using System;
using System.Collections.Generic;
using Tidewater.Core;
using Tidewater.Engine;
using Tidewater.World;
using Tidewater.World.Boat;

// Port of src/player/Player.js: the first-person walker / swimmer / boat captain. Everything is in sim space (three.js axes) with the engine's
// double math, exactly as the JS; PlayerHost applies the camera pose to the Unity camera through Sim.
//   walk : capsule on terrain + walkable colliders, wading slows you down
//   swim : floats with the head at the surface; look down + W (or C) to dive, Space to rise
//   deck : aboard, walking in the boat's frame (it moves and rocks under you); E at the helm takes the wheel, E near a pier / the beach steps ashore
//   boat : at the helm, driving; V toggles helm (1st person) / chase (3rd person) camera, E stands up (the camera maths is BoatCamera)
// Differences from the JS: the camera is a SimCamera (position + quaternion in sim axes); the boat's model data (helm / board / exit points,
// colliders) is BoatController.boatModel; the audio is the IPlayerAudio hooks (null until the sound is ported).
namespace Tidewater.Player
{
	// the camera of the sim world (three.js: camera.position / camera.quaternion)
	public sealed class SimCamera
	{
		public readonly Engine.Vector3 position = new Engine.Vector3();
		public readonly Engine.Quaternion quaternion = new Engine.Quaternion();
		static readonly Matrix4 _m = new Matrix4();
		static readonly Engine.Vector3 _o = new Engine.Vector3();

		// Object3D.lookAt( position + forward ) with the given up
		public void lookAlong( Engine.Vector3 forward, Engine.Vector3 up )
		{
			_m.lookAt( _o.set( 0, 0, 0 ), forward, up );
			quaternion.setFromRotationMatrix( _m );
		}

		public Engine.Vector3 viewDir( Engine.Vector3 o ) => o.set( 0, 0, - 1 ).applyQuaternion( quaternion );
	}

	// what the player makes noise with (SoundScape.js); the sound is not ported yet
	public interface IPlayerAudio
	{
		void footstep( string surface );
		void splash( double strength, Engine.Vector3 at );
		void swimStroke();
		void submerge();
		void emerge();
		void engineStart();
		void engineStop();
	}

	public sealed class Prompt { public string action, text; }

	public sealed class Player
	{
		const double EYE = 1.62;
		const double SWIM_EYE = EYE * 0.1; // eyes above the body's float point while swimming
		const double RADIUS = 0.3;
		const double HEIGHT = 1.75;
		// water depth (mean level over the feet) where you start swimming / find your feet again
		const double SWIM_DEPTH = 1.35;
		const double STAND_DEPTH = 1.1;
		// walking on the boat
		const double DECK_RADIUS = 0.24;
		const double DECK_STEP = 0.36; // highest ledge you step up onto
		const double HELM_REACH = 0.75; // m from the helm seat to take the wheel

		static readonly Engine.Vector3 _v = new Engine.Vector3(), _v2 = new Engine.Vector3(), _fwd = new Engine.Vector3(), _right = new Engine.Vector3();
		static readonly Engine.Quaternion _q = new Engine.Quaternion();
		static readonly Euler _e = new Euler( 0, 0, 0, "YXZ" );
		static readonly Engine.Vector3 _yAxis = new Engine.Vector3( 0, 1, 0 );
		static readonly Engine.Quaternion _qa = new Engine.Quaternion(), _qb = new Engine.Quaternion(), _qc = new Engine.Quaternion();
		static readonly Engine.Vector3 _wish = new Engine.Vector3();
		// the action layer's movement, before it is turned into a direction: { x = strafe, y = forward }
		static readonly Engine.Vector2 _mv = new Engine.Vector2();

		static double clamp( double v, double lo, double hi ) => MathUtils.clamp( v, lo, hi );

		public sealed class AshoreTarget { public Engine.Vector3 @out; public double g; public Engine.Vector3 ep; }

		public readonly SimCamera camera;
		public readonly GameInput input;
		readonly Func<double, double, double> terrainHeightAt;
		public readonly Colliders colliders;
		public readonly IWaterQuery query;
		public BoatController boat;
		public readonly List<BoatController> boats;
		public Func<double, double, double> reefFloorHeightAt; // Reef.floorHeightAt (not ported yet)
		public IPlayerAudio audio;

		public string mode = "walk";
		public bool blockLeaveHelm; // set by Game while a catch card owns the E key
		public readonly BoatCamera cam = new BoatCamera();
		public readonly Engine.Vector3 position = new Engine.Vector3(), velocity = new Engine.Vector3();
		public double yaw, pitch = - 0.05;
		public bool grounded;
		public double bob, stepDist, waterH;
		public double? waterMean; // water level low-passed over the passing waves (mode decisions)
		public double wade;
		// eye height easing between modes (critically damped offset from the mode's eye height)
		double camOff, camOffV;
		double? _camY;
		public bool floating = true; // swimming at the surface (riding the waves) vs. free under water
		readonly int slot;
		public Prompt prompt;
		public string surface = "sand";
		bool wasUnder;

		// on deck: position and heading in the boat frame (+Z forward, yaw 0 looks forward)
		public readonly Engine.Vector3 deckPos = new Engine.Vector3(), deckVel = new Engine.Vector3();
		public double deckYaw;
		public bool deckGrounded = true;
		AshoreTarget _ashore; // cached step-ashore target
		double _ashoreT;
		// set by the fishing game: while a line is out the helm / step ashore prompts give way
		public bool busy;
		BoatController _boardable;

		public Player( SimCamera camera, GameInput input, Func<double, double, double> terrainHeightAt, Colliders colliders, IWaterQuery query, BoatController boat, List<BoatController> boats = null )
		{
			this.camera = camera;
			this.input = input;
			this.terrainHeightAt = terrainHeightAt;
			this.colliders = colliders;
			this.query = query;
			this.boat = boat;
			this.boats = boats ?? new List<BoatController> { boat }; // every boardable boat; `boat` is the one we're aboard

			var start = WorldLayout.StartPosition;
			position.set( start.x, 0, start.z );
			// standing on whatever is there (the boardwalk planks), not in it
			position.y = Math.Max( terrainHeightAt( position.x, position.z ), colliders != null ? colliders.groundHeightAt( position.x, position.z, 50 ) : double.NegativeInfinity );
			yaw = WorldLayout.StartYaw;
			slot = query.Allocate( "player", 1 );
		}

		// view direction in world space (for casting); works in every mode
		public Engine.Vector3 getViewDir( Engine.Vector3 o ) => camera.viewDir( o );

		// ------------------------------------------------------------------ helpers

		public double waterHeight()
		{
			if ( ! query.cpuValid ) return 0;
			double h = query.cpu[ slot * 4 ];
			return ! double.IsNaN( h ) && ! double.IsInfinity( h ) ? h : waterH;
		}

		public double groundAt( double x, double z, double maxY )
		{
			double g = terrainHeightAt( x, z );
			double c = colliders.groundHeightAt( x, z, maxY );
			if ( c > g ) g = c;
			if ( reefFloorHeightAt != null ) g = Math.Max( g, reefFloorHeightAt( x, z ) );
			return g;
		}

		public bool nearBoat()
		{
			// nearest boardable boat (there can be more than one in reach)
			BoatController best = null; double bd = double.PositiveInfinity;
			foreach ( var b in boats )
			{
				var bp = b.toWorld( b.boatModel.boardPoint, _v );
				double d = JS.Hypot( bp.x - position.x, bp.z - position.z );
				if ( d < 4.2 && Math.Abs( bp.y - position.y ) < 3.2 && d < bd ) { bd = d; best = b; }
			}

			_boardable = best;
			return best != null;
		}

		// ------------------------------------------------------------------ update

		public void update( double dt )
		{
			var inp = input;
			query.SetPoint( slot, ( float ) position.x, ( float ) position.z );
			waterH = waterHeight();
			waterMean = waterMean == null ? waterH : waterMean + ( waterH - waterMean ) * ( 1 - Math.Exp( - dt / 4 ) );
			prompt = null;

			if ( mode == "boat" ) { updateBoat( dt ); return; }
			if ( mode == "deck" ) { updateDeck( dt ); return; }

			var look = inp.consumeLook();
			yaw -= look.x * 0.0022;
			pitch = clamp( pitch - look.y * 0.0022, - 1.5, 1.5 );

			// (not with a line out or a fish in hand: interact belongs to the fishing then)
			if ( nearBoat() && ! busy )
			{
				prompt = new Prompt { action = "interact", text = _boardable.boatModel.deck != null ? "Board boat" : "Take the helm" };
				if ( inp.actHit( "interact" ) ) { boardBoat(); return; }
			}

			string prevMode = mode;
			if ( mode == "walk" ) updateWalk( dt );
			else updateSwim( dt );

			// camera. Wading out of your depth, finding your feet again or climbing out on a ladder changes the eye height: ease the view there
			// (critically damped) instead of jumping
			var eye = position.clone();
			if ( mode == "walk" ) eye.y += EYE + Math.Sin( bob ) * 0.035 * ( 1 - 0.6 * wade );
			else eye.y += SWIM_EYE;
			if ( _camY == null || camera.position.y != _camY )
			{
				// something else drove the camera since our last frame (free camera, boat): start fresh
				camOff = 0;
				camOffV = 0;
			}
			else if ( mode != prevMode ) camOff = _camY.Value - eye.y;

			double w = 6, e = Math.Exp( - w * dt ), j = ( camOffV + w * camOff ) * dt;
			camOff = ( camOff + j ) * e;
			camOffV = ( camOffV - w * j ) * e;
			eye.y += camOff;
			camera.position.copy( eye );
			_camY = camera.position.y;
			camera.quaternion.setFromEuler( _e.set( pitch, yaw, 0 ) );
		}

		void updateWalk( double dt )
		{
			var inp = input;
			_fwd.set( - Math.Sin( yaw ), 0, - Math.Cos( yaw ) );
			_right.set( - _fwd.z, 0, _fwd.x );
			var mv = inp.move( _mv );
			var wish = _wish.set( 0, 0, 0 );
			if ( mv.y != 0 ) wish.addScaledVector( _fwd, mv.y );
			if ( mv.x != 0 ) wish.addScaledVector( _right, mv.x );
			// the magnitude a stick asks for is kept (a keyboard key asks for all of it)
			double mag = Math.Min( 1, JS.Hypot( mv.x, mv.y ) );
			if ( mag > 0 ) wish.normalize().multiplyScalar( mag );

			double depth = waterH - position.y; // water depth at the feet
			double wd = clamp( depth / 1.2, 0, 1 );
			wade = wd;
			bool sprint = inp.act( "sprint" );
			double speed = ( sprint ? 6.2 : 3.0 ) * MathUtils.lerp( 1, 0.42, wd );
			double accel = grounded ? 14 : 2.5;
			double k = 1 - Math.Exp( - accel * dt );
			velocity.x += ( wish.x * speed - velocity.x ) * k;
			velocity.z += ( wish.z * speed - velocity.z ) * k;

			if ( grounded && inp.actHit( "ascend" ) && depth < 0.9 )
			{
				velocity.y = 4.6;
				grounded = false;
			}

			velocity.y -= 9.81 * dt;
			// water drag while wading
			if ( depth > 0 ) velocity.multiplyScalar( Math.Exp( - dt * depth * 0.8 ) );

			var p = position;
			var old = p.clone();
			p.addScaledVector( velocity, dt );
			colliders.resolveCapsule( p, RADIUS, HEIGHT, 0.4 );
			double g = groundAt( p.x, p.z, p.y + 0.45 );
			if ( p.y <= g )
			{
				p.y = g;
				if ( velocity.y < 0 ) velocity.y = 0;
				grounded = true;
			}
			else grounded = p.y - g < 0.06;

			// head bob + footsteps
			double moved = JS.Hypot( p.x - old.x, p.z - old.z );
			if ( grounded )
			{
				bob += moved * 2.4;
				stepDist += moved;
				double stride = sprint ? 0.9 : 0.62;
				if ( stepDist > stride )
				{
					stepDist = 0;
					surface = surfaceType( depth );
					if ( audio != null ) audio.footstep( surface );
				}
			}

			// out of your depth -> swim. Wading in, the body lifts into the floating position at the surface and the view eases down to it
			// (update()); falling in off the pier plunges under and floats back up.
			if ( waterMean - p.y > SWIM_DEPTH )
			{
				bool wadedIn = grounded;
				mode = "swim";
				if ( wadedIn ) p.y = Math.Max( p.y, waterH - SWIM_EYE + 0.1 );
				velocity.y = wadedIn ? 0 : Math.Max( velocity.y * 0.3, - 2.5 );
				if ( audio != null ) audio.splash( wadedIn ? 0.2 : 0.5, p );
			}
		}

		string surfaceType( double depth )
		{
			var p = position;
			if ( depth > 0.12 ) return "water";
			bool onWood = colliders.groundHeightAt( p.x, p.z, p.y + 0.1 ) > terrainHeightAt( p.x, p.z ) + 0.05;
			if ( onWood ) return "wood";
			double h = terrainHeightAt( p.x, p.z );
			if ( h < 0.6 ) return "wetsand";
			if ( h > 3.6 ) return "grass";
			return "sand";
		}

		void updateSwim( double dt )
		{
			var inp = input;
			var p = position;
			double surfaceY = waterH;
			// look-relative movement (diving follows the view)
			_fwd.set( 0, 0, - 1 ).applyEuler( _e.set( pitch, yaw, 0 ) );
			_right.set( - Math.Cos( yaw ), 0, Math.Sin( yaw ) ).negate();
			var mv = inp.move( _mv );
			var wish = _wish.set( 0, 0, 0 );
			if ( mv.y != 0 ) wish.addScaledVector( _fwd, mv.y );
			if ( mv.x != 0 ) wish.addScaledVector( _right, mv.x );
			bool ascend = inp.act( "ascend" ), descend = inp.act( "descend" );
			if ( ascend ) wish.y += 1;
			if ( descend ) wish.y -= 1;
			double mag = Math.Min( 1, JS.Hypot( mv.x, mv.y ) );
			if ( mag > 0 && wish.lengthSq() > 0 ) wish.normalize().multiplyScalar( Math.Max( mag, ascend || descend ? 1 : 0 ) );

			bool atSurface = floating && p.y > surfaceY - 0.45;
			// at the surface W along a level view keeps you on top; looking down dives
			if ( atSurface && wish.y > - 0.25 && ! descend ) wish.y = Math.Max( wish.y, 0 );

			bool sprint = inp.act( "sprint" );
			double speed = sprint ? 2.5 : 1.5;
			double k = 1 - Math.Exp( - dt * 3.0 );
			velocity.lerp( wish.multiplyScalar( speed ), k );

			// A swimmer at the surface floats with the head riding the waves; one who dives stays where they swim to (neutral buoyancy, nothing
			// pulls them back up) until they swim up to the surface again.
			double eyeTarget = surfaceY - SWIM_EYE + 0.1; // eyes ~10 cm above the water; waves still wash over
			bool diving = descend || ( inp.act( "forward" ) && pitch < - 0.35 ) || wish.y < - 0.1;
			if ( diving ) floating = false;
			else if ( p.y > eyeTarget - 0.15 ) floating = true;
			if ( floating )
			{
				p.y += ( eyeTarget - p.y ) * ( 1 - Math.Exp( - dt * 5 ) );
				if ( velocity.y > 0 ) velocity.y *= 0.5;
			}

			p.addScaledVector( velocity, dt );
			p.y = Math.Min( p.y, surfaceY + 0.05 );
			colliders.resolveCapsule( p, RADIUS, 1.0, 0 );
			double g = groundAt( p.x, p.z, p.y + 0.3 );
			if ( p.y < g + 0.25 ) p.y = g + 0.25;

			// strokes / bubbles
			stepDist += velocity.length() * dt;
			if ( stepDist > 1.3 )
			{
				stepDist = 0;
				if ( audio != null ) audio.swimStroke();
			}

			bool under = camera.position.y < surfaceY - 0.05;
			if ( under != wasUnder && audio != null )
			{
				if ( under ) audio.submerge();
				else audio.emerge();
			}

			wasUnder = under;

			// shallow enough to stand -> walk (update() eases the view up to standing height). The mean level decides, so a passing wave doesn't
			// flip you between swimming and standing.
			if ( waterMean - g < STAND_DEPTH )
			{
				mode = "walk";
				p.y = g;
				velocity.set( velocity.x, 0, velocity.z );
			}

			// ladders on the pier: climb out when swimming into them
			foreach ( var b in colliders.boxes )
			{
				if ( b.tag != "ladder" ) continue;
				if ( JS.Hypot( b.center.x - p.x, b.center.z - p.z ) < 1.1 )
				{
					prompt = new Prompt { action = "ascend", text = "Climb ladder" };
					if ( inp.act( "ascend" ) || inp.act( "forward" ) )
					{
						double top = colliders.groundHeightAt( b.center.x, b.center.z, 10 );
						if ( top > p.y )
						{
							p.y += dt * 1.6;
							if ( p.y > top - 1.0 )
							{
								// step onto the deck
								double deck = WorldLayout.Pier.deckHeight;
								p.set( b.center.x - JS.Sign( b.center.x - WorldLayout.Pier.x ) * 1.2, deck, b.center.z );
								mode = "walk";
								velocity.set( 0, 0, 0 );
							}
						}
					}

					break;
				}
			}
		}

		// ------------------------------------------------------------------ boat

		// step aboard from the pier / beach / water. Boats with a deck (`lines`) drop you on the cockpit sole; deckless boats (the mini fishing boat) take you
		// straight to the helm.
		public void boardBoat( BoatController b = null )
		{
			b = b ?? _boardable;
			boat = b;
			if ( b.boatModel.deck == null ) { takeHelm(); return; }

			mode = "deck";
			deckPos.copy( b.boatModel.boardPoint );
			deckVel.set( 0, 0, 0 );
			// keep looking where you looked (relative to the boat)
			deckYaw = yaw - ( b.getYaw() + Math.PI );
			deckGrounded = true;
			_ashore = null;
			_ashoreT = 0;
			velocity.set( 0, 0, 0 );
			_camY = null;
			deckToWorld();
		}

		// sit down at the helm and drive (the old "enter boat")
		public void takeHelm()
		{
			mode = "boat";
			boat.driven = true;
			boat.moored = false;
			cam.takeHelm( boat );
			if ( audio != null ) audio.engineStart();
		}

		// get up from the helm: stand beside the seat, looking forward (or get off a deckless boat)
		public void leaveHelm()
		{
			var b = boat;
			b.driven = false;
			b.throttle = 0;
			if ( b.boatModel.deck == null ) { exitBoat(); return; }

			mode = "deck";
			deckPos.set( b.boatModel.helmPointX + 0.45, b.boatModel.deck.deckY, b.boatModel.helmPointZ - 0.1 );
			deckVel.set( 0, 0, 0 );
			deckYaw = cam.helmYaw;
			pitch = cam.helmPitch;
			deckGrounded = true;
			_camY = null;
			if ( audio != null ) audio.engineStop();
			deckToWorld();
		}

		// enterBoat() kept for callers: straight to the helm
		public void enterBoat() { boardBoat(); takeHelm(); }

		// target: an ashoreTarget() spot; side: +1 / -1 jumps overboard on the starboard / port side
		public void exitBoat( AshoreTarget target = null, double side = 0 )
		{
			var b = boat;
			bool wasDriving = b.driven;
			b.driven = false;
			b.throttle = 0;
			// the exit point closest to something walkable (pier deck / sand)
			AshoreTarget best = side != 0 ? null : ashoreTarget();

			var dock = b.homeDock.position;
			// near the berth it is tied up where it lies; with the anchor down the anchor holds it, and it stays down
			if ( b.position.distanceTo( dock ) < 14 && b.speed < 1.5 && ! b.anchor.down )
			{
				b.moored = true;
				b.mooringAnchor.set( b.position.x, 0, b.position.z );
				b.mooringHeading = b.getYaw();
			}

			if ( target != null ) best = target;
			if ( best != null )
			{
				position.set( best.@out.x, best.g, best.@out.z );
				mode = "walk";
			}
			else
			{
				var w = b.toWorld( new Engine.Vector3( 2.2 * ( side != 0 ? side : 1 ), 0, 0 ), new Engine.Vector3() );
				// (the boat floats at the water line: the walker's water height is stale while aboard)
				waterH = b.position.y; waterMean = waterH;
				position.set( w.x, b.position.y - 0.2, w.z );
				mode = "swim";
				if ( audio != null ) audio.splash( 0.8, position );
			}

			velocity.set( 0, 0, 0 );
			yaw = b.getYaw() + Math.PI;
			_camY = null;
			if ( audio != null && wasDriving ) audio.engineStop();
		}

		// best walkable spot next to the boat (pier deck / sand / shallows), or null. Looks straight out from the rail at each exit point (square to
		// the hull, a few reaches: the boat swings on its mooring) for ground from a little below the rail up to a pier deck a climb above it.
		public AshoreTarget ashoreTarget()
		{
			var b = boat;
			double water = query.cpuValid ? query.cpu[ 0 ] : 0;
			// the boat's starboard (local +x) direction in the world, level
			var sx = _v2.set( 1, 0, 0 ).applyQuaternion( b.quaternion ); sx.y = 0; sx.normalize();
			double rx = sx.x, rz = sx.z;
			AshoreTarget best = null; double bestScore = double.PositiveInfinity;
			foreach ( var ep in b.boatModel.exitPoints )
			{
				var w = b.toWorld( ep, new Engine.Vector3() );
				double sgn = JS.Or( JS.Sign( ep.x ), 1 );
				foreach ( double reach in new[] { 0.9, 1.4, 2.0 } )
				{
					var o = new Engine.Vector3( w.x + rx * sgn * reach, w.y, w.z + rz * sgn * reach );
					double g = groundAt( o.x, o.z, w.y + 2.5 );
					double up = g - w.y;
					if ( up > 1.7 || up < - 1.2 || g < water - 0.3 ) continue;
					double score = Math.Abs( up ) + reach * 0.2;
					if ( score < bestScore ) { bestScore = score; best = new AshoreTarget { @out = o, g = g, ep = ep }; }
				}
			}

			return best;
		}

		// ------------------------------------------------------------------ deck

		// camera base orientation on the boat: its heading, with roll and pitch half stabilised
		Engine.Quaternion deckBase( Engine.Quaternion o )
		{
			var b = boat;
			_qa.setFromAxisAngle( _yAxis, b.getYaw() + Math.PI );
			_qb.copy( b.quaternion ).multiply( _qc.setFromAxisAngle( _yAxis, Math.PI ) );
			return o.copy( _qb ).slerp( _qa, 0.55 ); // slerpQuaternions( _qb, _qa, 0.55 )
		}

		// world position / heading of the player from the deck state (feet)
		void deckToWorld()
		{
			var b = boat;
			b.toWorld( deckPos, position );
			yaw = b.getYaw() + Math.PI + deckYaw;
		}

		// highest walkable box top under the point (boat frame), not above maxY; the sole otherwise
		double deckGroundAt( double x, double z, double maxY )
		{
			var L = boat.boatModel.deck;
			double g = L.deckY;
			foreach ( var c in boat.boatModel.colliders )
			{
				if ( ! c.walkable ) continue;
				if ( Math.Abs( x - c.center.x ) > c.half.x || Math.Abs( z - c.center.z ) > c.half.z ) continue;
				double top = c.center.y + c.half.y;
				if ( top <= maxY && top > g ) g = top;
			}

			return g;
		}

		void updateDeck( double dt )
		{
			var inp = input;
			var b = boat;
			var model = b.boatModel;
			var L = model.deck;
			var look = inp.consumeLook();
			deckYaw -= look.x * 0.0022;
			pitch = clamp( pitch - look.y * 0.0022, - 1.5, 1.5 );

			// movement in the boat frame (camera base looks along +Z at deckYaw 0)
			double sy = Math.Sin( deckYaw ), cy = Math.Cos( deckYaw );
			_fwd.set( sy, 0, cy );
			_right.set( - cy, 0, sy );
			_wish.set( 0, 0, 0 );
			var dmv = inp.move( _mv );
			if ( dmv.y != 0 ) _wish.addScaledVector( _fwd, dmv.y );
			if ( dmv.x != 0 ) _wish.addScaledVector( _right, dmv.x );
			double dmag = Math.Min( 1, JS.Hypot( dmv.x, dmv.y ) );
			if ( dmag > 0 ) _wish.normalize().multiplyScalar( dmag );
			double speed = inp.act( "sprint" ) ? 2.6 : 1.6;
			double k = 1 - Math.Exp( - 12 * dt );
			var v = deckVel;
			v.x += ( _wish.x * speed - v.x ) * k;
			v.z += ( _wish.z * speed - v.z ) * k;
			if ( deckGrounded && inp.actHit( "ascend" ) )
			{
				v.y = 3.2;
				deckGrounded = false;
			}

			v.y -= 9.81 * dt;
			var p = deckPos;
			double oldX = p.x, oldZ = p.z;
			p.addScaledVector( v, dt );

			// walls: push out of the solid boxes you can't step onto (boat frame, axis aligned)
			for ( int iter = 0; iter < 2; iter ++ ) foreach ( var c in model.colliders )
			{
				if ( ! c.solid ) continue;
				double top = c.center.y + c.half.y, bot = c.center.y - c.half.y;
				if ( top <= p.y + DECK_STEP || bot >= p.y + HEIGHT ) continue;
				double ex = c.half.x + DECK_RADIUS, ez = c.half.z + DECK_RADIUS;
				double dx = p.x - c.center.x, dz = p.z - c.center.z;
				if ( Math.Abs( dx ) >= ex || Math.Abs( dz ) >= ez ) continue;
				double px = ex - Math.Abs( dx ), pz = ez - Math.Abs( dz );
				if ( px < pz ) { p.x += JS.Or( JS.Or( JS.Sign( dx ), JS.Sign( oldX - c.center.x ) ), 1 ) * px; v.x = 0; }
				else { p.z += JS.Or( JS.Or( JS.Sign( dz ), JS.Sign( oldZ - c.center.z ) ), 1 ) * pz; v.z = 0; }
			}

			// stay inside the hull (the bulwarks, plus a margin fore and aft)
			p.z = clamp( p.z, L.zAft + L.shell + DECK_RADIUS, L.zFwd ?? 4.0 );
			double halfIn = Math.Max( 0.15, L.halfBreadth( L.tAtSheerZ( p.z ), Math.Max( p.y, L.deckY ) ) - L.shell - DECK_RADIUS );
			p.x = clamp( p.x, - halfIn, halfIn );

			double g = deckGroundAt( p.x, p.z, p.y + DECK_STEP );
			if ( p.y <= g )
			{
				p.y = g;
				if ( v.y < 0 ) v.y = 0;
				deckGrounded = true;
			}
			else deckGrounded = p.y - g < 0.04;

			// footsteps on the deck
			double moved = JS.Hypot( p.x - oldX, p.z - oldZ );
			if ( deckGrounded )
			{
				bob += moved * 2.4;
				stepDist += moved;
				if ( stepDist > 0.6 )
				{
					stepDist = 0;
					if ( audio != null ) audio.footstep( "wood" );
				}
			}

			deckToWorld();

			// prompts: take the helm, or step ashore
			double hx = model.helmPointX, hz = model.helmPointZ;
			bool nearHelm = JS.Hypot( p.x - hx, p.z - hz ) < HELM_REACH && ! busy;
			_ashoreT -= dt;
			if ( _ashoreT <= 0 )
			{
				_ashoreT = 0.25;
				_ashore = b.speed < 2.5 ? ashoreTarget() : null;
			}

			if ( nearHelm )
			{
				prompt = new Prompt { action = "interact", text = "Take the helm" };
				if ( inp.actHit( "interact" ) ) { takeHelm(); return; }
			}
			else if ( ! busy )
			{
				// at the rail: step ashore where there is ground on that side, else jump into the sea
				var ep = _ashore != null ? _ashore.ep : null;
				bool atRail = model.exitPoints.Exists( e => JS.Hypot( p.x - e.x, p.z - e.z ) < 1.3 );
				if ( ep != null && JS.Hypot( p.x - ep.x, p.z - ep.z ) < 1.3 )
				{
					prompt = new Prompt { action = "interact", text = "Step ashore" };
					if ( inp.actHit( "interact" ) ) { exitBoat( _ashore ); return; }
				}
				else if ( atRail )
				{
					prompt = new Prompt { action = "interact", text = "Jump overboard" };
					if ( inp.actHit( "interact" ) ) { exitBoat( null, JS.Or( JS.Sign( p.x ), 1 ) ); return; }
				}
			}

			// camera: eye above the feet, following the boat's motion
			var eyeL = _v.set( p.x, p.y + EYE + Math.Sin( bob ) * 0.02, p.z );
			b.toWorld( eyeL, camera.position );
			_camY = camera.position.y;
			deckBase( _q );
			camera.quaternion.copy( _q ).multiply( _qa.setFromEuler( _e.set( pitch, deckYaw, 0 ) ) );
		}

		void updateBoat( double dt )
		{
			var inp = input;
			var b = boat;
			var look = inp.consumeLook();
			double wheel = inp.consumeWheel();

			if ( inp.actHit( "boatCamera" ) ) cam.toggle();
			// (E also closes the catch card a haul leaves up: Game sets blockLeaveHelm while it is open)
			if ( inp.actHit( "interact" ) && ! blockLeaveHelm ) { leaveHelm(); return; }

			// Throttle and rudder are continuous, so the stick drives them straight through the same curve the keys always gave: W 0.7, Shift+W 1.0,
			// S -0.6, full lock on the rudder.
			double fwd = inp.axis( "forward" );
			double throttle = fwd >= 0 ? fwd * ( inp.act( "sprint" ) ? 1 : 0.7 ) : fwd * 0.6;
			double steer = - inp.axis( "strafe" );
			b.setInput( throttle, steer, dt );
			prompt = new Prompt { action = "interact", text = "Leave helm   ·   " + inp.label( "boatCamera" ) + "  camera" };

			// keep the player attached (for audio / queries)
			b.toWorld( b.boatModel.helmEye, position );
			position.y -= EYE;

			// the helm camera (the head half-stabilises against the boat's roll and pitch) or the chase orbit: BoatCamera, the JS maths
			cam.update( b, b.boatModel.helmEye, look, wheel, dt, out var eye, out var forward, out var up );
			camera.position.copy( eye );
			camera.lookAlong( forward, up );
		}
	}
}
