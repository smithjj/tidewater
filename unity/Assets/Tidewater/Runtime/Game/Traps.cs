using System;
using System.Collections.Generic;
using Tidewater.Player;
using Tidewater.World.Boat;
using Vector3 = Tidewater.Engine.Vector3;
using Quaternion = Tidewater.Engine.Quaternion;
using Euler = Tidewater.Engine.Euler;

// Port of src/game/Traps.js, the trap line: lobstering. Pots go over the side from the working boat, soak on the clock, and come back aboard with whatever crawled in. The
// rules at the top are pure functions (the oracle drives them with an injected rng); the class is the part of Traps.js that is not drawing:
//   - a set is one pot in the water: GameState.sets is the source of truth, `views` follows it (one pot, buoy and line per set; the buoy rides the sea through the water
//     query, the line is stretched from the seabed to it);
//   - the deck stack is one pot per pot aboard (up to the places the boat has), and the haul / set animations carry the one pot that moves, `holder`, along the same paths as
//     the JS (the hauler's block, over the deck and down into its place; or lifted, swung aft over the transom and dropped into the sea);
//   - TrapsView (the Unity objects) draws all of it.
// What is not here: the procedural pot and float the JS shows until the models load, and the fallback if they cannot be fetched (the baked meshes are in Resources).
namespace Tidewater.Game
{
	// the boat the trap line works from (a BoatController through TrapBoat, or a stand-in in the oracle)
	public interface ITrapBoat
	{
		Vector3 position { get; }
		Quaternion quaternion { get; }
		double sternZ { get; }
		Vector3 toWorld( Vector3 local, Vector3 outV );
		double sampleWaterAt( Vector3 p );
	}

	public sealed class TrapBoat : ITrapBoat
	{
		readonly BoatController c;
		public TrapBoat( BoatController c ) { this.c = c; }
		public Vector3 position => c.position;
		public Quaternion quaternion => c.quaternion;
		public double sternZ => c.model.sternZ ?? - 3.8;
		public Vector3 toWorld( Vector3 local, Vector3 outV ) => c.toWorld( local, outV );
		public double sampleWaterAt( Vector3 p ) => c.sampleWaterAt( p );
	}

	public sealed class Hauled { public string species; public double kg; }

	// one pot in the water: where its parts are (the sim frame) for the view to draw
	public sealed class TrapSetView
	{
		public int id, slot;
		public double yaw;
		public double x, z;
		public readonly Vector3 pot = new Vector3(), buoy = new Vector3(), rope = new Vector3();
		public double potYaw, buoyRotZ, ropeScaleY = 1;
	}

	public sealed class Traps
	{
		// m of the marker that sits below the surface. The float is ~0.65 m tall, so a third of it in the water is what gives it a waterline
		public const double BUOY_SINK = 0.22;

		// a pot needs a few hours to fish; a full soak fills it up
		public const double SOAK_MIN = 1.5; // game hours before anything worthwhile is aboard
		public const int MAX_KEEP = 3;      // most animals in one pot

		// Working the line from the boat (see TrapGame). A pot goes over the stern or comes up on the hauler only at a crawl: faster than this, the gear would be dragged.
		public const double TRAP_MAX_SPEED = 2; // m/s of the boat
		public const double SET_ASTERN = 0.9;   // m behind the transom a set pot goes into the water

		// Does the trap line fire this frame? `mode` is the player's; `interact` and `work` are the two buttons' edges this frame (E / A, and the cast button: LMB / RT); `live` says the
		// mouse is captured (or a pad is in hand), so the click that grabs the pointer does not also set a pot; `modeChanged` says the mode changed this frame (E had just brought the
		// player aboard, or off the helm).
		//   - at the helm E leaves it and is never also a pot (it used to be both, in the same frame);
		//   - the cast button works the pots there instead: nothing else uses it at the helm;
		//   - on deck it is E, as it always was.
		public static bool trapTriggered( string mode, bool interact, bool work, bool live, bool modeChanged )
		{
			if ( mode == "boat" ) return work && live;
			if ( mode == "deck" ) return interact && ! modeChanged;
			return false;
		}

		// Which of the deck stack's `slots` are showing: one pot per pot aboard (up to the slots there are), and `held` -- the slot a hauled pot is still on its way to -- not yet.
		public static bool[] stackVisible( int aboard, int slots, int held = - 1 )
		{
			var o = new bool[ slots ];
			for ( int i = 0; i < slots; i ++ ) o[ i ] = i < aboard && i != held;
			return o;
		}

		// game hours a set has been soaking
		public static double soakHours( TrapSet set, double hour, int day ) => Math.Max( 0, ( day * 24 + hour ) - ( set.day * 24 + set.clock ) );

		// What is in the pot when it comes up: { species, kg } (empty on barren ground, or too soon). Lobsters want a bit of depth and hard ground; anything else that wanders in is
		// whatever lives there, taken from the same habitat table the rod fishes.
		public static List<Hauled> haulYield( double soak, double depth, Habitat habitat, double hour, Func<double> rng )
		{
			var o = new List<Hauled>();
			if ( ! ( soak >= SOAK_MIN ) ) return o;
			double want = Math.Min( MAX_KEEP, soak / 3.5 );
			int n = ( int ) Math.Floor( want );
			if ( rng() < want - n ) n ++;
			for ( int i = 0; i < n; i ++ )
			{
				double lobsterChance = depth < 2 ? 0.15 : depth > 35 ? 0.3 : 0.7;
				string species = rng() < lobsterChance ? "lobster" : Bites.pickSpecies( habitat, hour, rng );
				if ( species == null ) continue; // nothing down there: the pot comes up light
				o.Add( new Hauled { species = species, kg = Bites.rollWeight( species, rng ) } );
			}
			return o;
		}

		// ---- the line

		readonly GameState state;
		readonly Func<double, double, double> heightAt;
		readonly IWaterQuery query;
		readonly int slot = - 1;
		readonly double deckY;
		public readonly int stackCount;              // the places on the deck (0: no working boat to carry them)
		public readonly bool[] stackShown;           // which of them hold a pot now
		public Action onSplash;                      // a pot hits the water (the sound is the caller's)
		public readonly List<TrapSetView> views = new List<TrapSetView>(); // one per set, in the order of the gear
		readonly Dictionary<int, TrapSetView> byId = new Dictionary<int, TrapSetView>();
		readonly Action unsubscribe;
		int held = - 1;                              // the stack place a hauled pot is still on its way to

		// the pot on the hauler / over the stern, in the sim frame
		public bool holderVisible;
		public readonly Vector3 holderPos = new Vector3();
		public readonly Quaternion holderQuat = new Quaternion();

		sealed class Anim { public string kind; public double t, dur; public ITrapBoat boat; public int slot; public Vector3 end; public Fall fall; }
		sealed class Fall { public double y, v, x, z, spin; }
		Anim anim;

		public Traps( GameState state, Func<double, double, double> terrainHeightAt, IWaterQuery query, double? deckY )
		{
			this.state = state; heightAt = terrainHeightAt; this.query = query;
			if ( query != null )
			{
				try { slot = query.Allocate( "traps", Gear.TRAP_LIMIT ); }
				catch ( Exception e ) { UnityEngine.Debug.LogWarning( "Traps: no water query slots, buoys will sit at sea level: " + e.Message ); }
			}

			stackCount = deckY.HasValue ? DeckGear.TRAPS.Length : 0;
			this.deckY = deckY ?? 0;
			stackShown = new bool[ stackCount ];
			unsubscribe = state.onChange( _ => sync() );
			sync();
		}

		public void Dispose() { unsubscribe?.Invoke(); }

		// one pot + buoy + line per set; called whenever the trap line changes
		public void sync()
		{
			var sets = state.sets;
			var want = new HashSet<int>();
			foreach ( var s in sets ) want.Add( s.id );
			for ( int i = views.Count - 1; i >= 0; i -- )
				if ( ! want.Contains( views[ i ].id ) ) { byId.Remove( views[ i ].id ); views.RemoveAt( i ); }

			int index = 0;
			foreach ( var s in sets )
			{
				int at = index ++;
				if ( byId.ContainsKey( s.id ) ) continue;
				var v = new TrapSetView
				{
					id = s.id, x = s.x, z = s.z,
					// one readback slot per pot, by position in the gear (they wrap at the limit)
					slot = slot >= 0 ? slot + ( at % Gear.TRAP_LIMIT ) : - 1,
					yaw = ( hash( s.id ) % 997 ) / 997.0 * Math.PI * 2,
				};
				v.potYaw = v.yaw;
				v.pot.set( s.x, heightAt( s.x, s.z ) + 0.05, s.z );
				views.Add( v ); byId[ s.id ] = v;
			}

			syncStack();
		}

		// the pot being set or hauled right now: leave the others alone until it is done
		public bool busy => anim != null;

		// the deck stack shows what is aboard: a pot per pot, so setting one empties a place and hauling one fills it (when it lands)
		void syncStack()
		{
			var show = stackVisible( state.traps, stackCount, held );
			for ( int i = 0; i < stackCount; i ++ ) stackShown[ i ] = show[ i ];
		}

		public void update( double dt )
		{
			sync(); // cheap when nothing changed; keeps up with traps set or hauled by other means

			foreach ( var s in state.sets )
			{
				if ( ! byId.TryGetValue( s.id, out var v ) ) continue;
				double bed = heightAt( s.x, s.z );
				double y = 0;
				if ( v.slot >= 0 && query != null )
				{
					query.SetPoint( v.slot, ( float ) s.x, ( float ) s.z );
					double h = query.cpu[ v.slot * 4 ];
					if ( ! double.IsNaN( h ) && ! double.IsInfinity( h ) ) y = h;
				}

				v.buoy.set( s.x, y - BUOY_SINK, s.z );
				v.buoyRotZ = Math.Sin( ( s.id + v.yaw ) * 3.1 ) * 0.12;
				double len = Math.Max( 0.15, y - bed );
				v.rope.set( s.x, ( bed + y ) * 0.5, s.z );
				v.ropeScaleY = len;
			}

			if ( anim != null ) step( dt );
		}

		// ---- the haul: the pot comes up on the hauler and lands on the aft deck

		// where a pot lies in stack place i, in the boat's own frame (the same place the view puts it)
		public Vector3 slotAt( int i, Vector3 outV )
		{
			var t = DeckGear.TRAPS[ i ];
			return outV.set( t[ 0 ], deckY + 0.03 + t[ 1 ] * ( TRAP.H + 0.035 ), t[ 2 ] );
		}

		public double slotYaw( int i ) => DeckGear.TRAPS[ i ][ 3 ];

		// Bring a pot up on the hauler and swing it onto the stack, lowering it into the next empty place (the catch is the caller's job). Call it after the haul is in the state, so
		// `traps aboard` counts this pot.
		public bool haulVisual( ITrapBoat boat )
		{
			if ( stackCount == 0 ) return false;
			int place = Math.Min( state.traps, stackCount ) - 1;
			held = state.traps <= stackCount ? place : - 1; // an occupied place just takes it
			syncStack();
			holderVisible = true;
			slotAt( Math.Max( place, 0 ), _end );
			anim = new Anim { kind = "haul", t = 0, dur = 3.6, boat = boat, slot = Math.Max( place, 0 ), end = _end.clone() };
			return true;
		}

		// Put a pot over the stern: it lifts off the stack (the place it came from, `place`), swings aft over the transom and drops into the water, which is where the caller's splash
		// lands. Call it after the set is in the state, so the stack has already lost it.
		public bool setVisual( ITrapBoat boat, int place )
		{
			if ( stackCount == 0 ) return false;
			place = Math.Max( 0, Math.Min( place, stackCount - 1 ) );
			holderVisible = true;
			slotAt( place, _end );
			anim = new Anim { kind = "set", t = 0, dur = 1.15, boat = boat, slot = place, end = _end.clone() };
			return true;
		}

		void finish()
		{
			holderVisible = false;
			anim = null;
			held = - 1;
			syncStack();
		}

		void step( double dt )
		{
			var a = anim;
			a.t += dt;
			var b = a.boat;
			if ( b == null ) { finish(); return; }
			double yaw = DeckGear.TRAPS[ a.slot ][ 3 ];

			if ( a.kind == "haul" )
			{
				double u = Math.Min( 1, a.t / a.dur );
				// up out of the water under the davit, over the deck, and down into its place
				double up = smooth( 0, 0.4, u ), over = smooth( 0.4, 0.75, u ), down = smooth( 0.75, 1, u );
				_pos.copy( HAUL_FROM ).lerp( HAUL_TOP, up );
				_above.copy( a.end ); _above.y += 0.75;
				_pos.lerp( _above, over ).lerp( a.end, down );
				b.toWorld( _pos, holderPos );
				_eul.set( 0.12 * Math.Sin( u * 5 ) * ( 1 - u ), 0.35 * Math.Sin( u * 7 ) * ( 1 - u ) + yaw * over, 0 );
				holderQuat.copy( b.quaternion ).multiply( _quat.setFromEuler( _eul ) );
				if ( u >= 1 ) finish();
				return;
			}

			// setting: lift off the stack, swing aft over the transom, then let go and fall in the world
			if ( a.fall == null )
			{
				double u = Math.Min( 1, a.t / a.dur );
				double lift = smooth( 0, 0.3, u ), aft = smooth( 0.3, 1, u );
				_above.copy( a.end ); _above.y += 0.7;
				_stern.set( 0, _above.y + 0.5, b.sternZ - 0.45 );
				_pos.copy( a.end ).lerp( _above, lift ).lerp( _stern, aft );
				b.toWorld( _pos, holderPos );
				_eul.set( 0, yaw * ( 1 - aft ), 0 );
				holderQuat.copy( b.quaternion ).multiply( _quat.setFromEuler( _eul ) );
				if ( u >= 1 ) a.fall = new Fall { y = holderPos.y, v = 0, x = holderPos.x, z = holderPos.z, spin = 0 };
				return;
			}

			// let go: straight down in the world (the boat is under way, at a crawl), a little tumble, until the sea
			var f = a.fall;
			f.v += 9.81 * dt;
			f.y -= f.v * dt;
			f.spin += dt * 2.2;
			holderPos.set( f.x, f.y, f.z );
			_eul.set( 0, 0, f.spin );
			holderQuat.setFromEuler( _eul );
			_water.set( f.x, 0, f.z );
			double sea = b.sampleWaterAt( _water );
			if ( f.y <= sea - 0.1 )
			{
				onSplash?.Invoke();
				finish();
			}
		}

		static readonly Vector3 _end = new Vector3(), _pos = new Vector3(), _above = new Vector3(), _stern = new Vector3(), _water = new Vector3();
		static readonly Euler _eul = new Euler();
		static readonly Quaternion _quat = new Quaternion();
		// the hauler's snatch block (boat frame: starboard, just aft of the wheelhouse): where a pot comes up
		static readonly Vector3 HAUL_FROM = new Vector3( - 1.05, - 1.25, - 0.8 ), HAUL_TOP = new Vector3( - 1.0, 1.25, - 0.8 );

		// Math.imul( ( n | 0 ) ^ 0x9e3779b9, 2246822519 ) >>> 0
		static uint hash( int n ) => unchecked( ( uint ) ( ( n ^ ( int ) 0x9e3779b9 ) * ( int ) 2246822519u ) );

		static double smooth( double e0, double e1, double x )
		{
			double t = Math.Min( 1, Math.Max( 0, ( x - e0 ) / ( e1 - e0 ) ) );
			return t * t * ( 3 - 2 * t );
		}
	}
}
