using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Core;
using Tidewater.Player;
using Tidewater.World;
using UnityEngine;

// Hosts the economy on top of the world (the money half of src/game/Game.js): owns the GameState (the save), the world clock, the two traders and
// their panels, burns the fuel at the helm, applies the engine upgrade, and rolls the day over at midnight. PlayerHost ticks it after the player,
// where the JS App calls Game.update. Fishing (the rod, bites, the catch card), the trap line, the anchor, the guide and the minimap are Game.js too
// and come with their own rows; until the rod is ported, catches come from the Editor tools (GameDebug).
//
// The panels and the readouts are IMGUI (like the prompts), until the HTML UI (GameHUD.js, ui/) is ported.
namespace Tidewater.Game
{
	public sealed class GameHost : MonoBehaviour
	{
		public static GameHost instance { get; private set; }

		public GameState state { get; private set; }
		public readonly WorldClock clock = new WorldClock();
		public Vendor stand { get; private set; }
		public Vendor chandlery { get; private set; }
		public IReadOnlyList<Vendor> vendors => _vendors;
		public bool saveToFile = true;      // false: the state lives in memory only (tools)
		public bool showHud = true;
		public Vendor openVendor { get; private set; }   // the trader whose panel is open (JS hud.standOpen / hud.vendor)
		public bool inventoryOpen { get; private set; }  // the cooler panel (I)

		readonly List<Vendor> _vendors = new List<Vendor>();
		PlayerHost host;
		Tidewater.Player.BoatController lobster;
		double baseMaxThrust, basePitchSpeed;
		bool fuelOut, wasDriven;
		double clockT = 20;
		// built once Ensure has made the state (a field would survive an Editor reload that drops the state, so it is derived from it)
		bool built => state != null && lobster != null;

		void Awake() { instance = this; }
		void OnDestroy() { if ( instance == this ) instance = null; }

		// builds the state and the world half once the player's world exists (PlayerHost.Build): false until then
		public bool Ensure( PlayerHost h )
		{
			if ( built ) return true;
			if ( h == null || h.terrainData == null || h.colliders == null || h.driver == null || h.driver.controller == null ) return false;
			host = h;
			_vendors.Clear();
			state = new GameState( saveToFile ? SafeStore() : new MemorySaveStore() );
			state.load();
			// the day resumes where it was left
			if ( state.clock != null ) clock.hour = state.clock.Value;
			var vs = Stalls.Create( h.terrainData.HeightAt, h.colliders );
			stand = vs[ 0 ]; chandlery = vs[ 1 ];
			_vendors.Add( stand ); _vendors.Add( chandlery );
			StallsView.Build( transform, h.terrainData.HeightAt, stand, chandlery );
			// the rebuilt engine is the lobster boat's: always target it, not whichever boat is active
			lobster = h.driver.controller;
			baseMaxThrust = lobster.maxThrust; basePitchSpeed = lobster.pitchSpeed;
			ApplyGear();
			state.onChange( _ => ApplyGear() );
			return true;
		}

		static ISaveStore SafeStore() { try { return new FileSaveStore(); } catch ( Exception ) { return null; } }

		// Game.applyGear: the engine upgrade (speedMul), and the floodlights when the lights port brings them
		public void ApplyGear()
		{
			if ( lobster == null || state == null ) return;
			var g = state.stats;
			lobster.maxThrust = baseMaxThrust * g.speedMul * g.speedMul;
			lobster.pitchSpeed = basePitchSpeed * g.speedMul;
		}

		public void Toast( string text, float seconds = 2.6f ) { if ( host != null ) host.Toast( text, seconds ); else Debug.Log( "[game] " + text ); }

		// ---- the actions the panels and the keys call (Game.buy / buyTraps / refuel / sellAll)

		public GearLevel Buy( string key )
		{
			var r = state.buy( key );
			if ( r != null ) Toast( $"{Gear.Track( key ).name}: {r.label}" );
			return r;
		}

		public bool BuyTraps( int n = 1 )
		{
			bool r = state.buyTraps( n );
			if ( r ) Toast( n == 1 ? $"Trap aboard · ${Gear.TRAP_PRICE}" : $"{n} traps aboard · ${Gear.TRAP_PRICE * n}" );
			return r;
		}

		public double Refuel()
		{
			double l = state.refuel();
			if ( l > 0 ) { fuelOut = false; Toast( $"Filled up · {Math.Round( l )} L" ); }
			return l;
		}

		public SaleResult SellAll() => Sell( null );

		public SaleResult Sell( ICollection<int> ids )
		{
			var r = state.sell( ids );
			if ( r.count > 0 ) Toast( $"Sold {r.count} fish for ${r.total}{( r.bonus > 0 ? $" · Joe's order +${r.bonus}" : "" )}" );
			return r;
		}

		// midnight: the day ticks over, and the world clock goes into the save (Game.newDay)
		public void NewDay()
		{
			state.advanceDay();
			state.setClock( clock.hour );
			state.save();
			state.emit();
			Toast( $"Day {state.day}", 3.4f );
		}

		public void ToggleInventory( bool? force = null )
		{
			inventoryOpen = force ?? ! inventoryOpen;
			if ( inventoryOpen ) { CloseStand(); host.input.ReleaseLock(); }
			host.input.menuMode = inventoryOpen || openVendor != null;
		}

		public void OpenStand( Vendor v )
		{
			openVendor = v;
			inventoryOpen = false;
			host.input.ReleaseLock();
			host.input.menuMode = true;
		}

		public void CloseStand()
		{
			openVendor = null;
			if ( host != null && host.input != null ) host.input.menuMode = inventoryOpen;
		}

		// ---- per frame (after the player update: Game.update)
		public void Tick( double dt )
		{
			if ( ! built ) return;
			var p = host.player; var inp = host.input;

			// App.frame: the world clock runs, and midnight is a new day
			if ( clock.Tick( dt ) ) NewDay();
			if ( inp.actHit( "pauseTime" ) ) Toast( clock.Toggle() ? "Time running" : "Time paused" );

			if ( inp.actHit( "cooler" ) ) ToggleInventory();
			if ( inp.actHit( "cancel" ) ) { ToggleInventory( false ); CloseStand(); }

			UpdateBoat( dt, p );

			// the world clock rides along in the save (every 20 s, and at midnight)
			clockT -= dt;
			if ( clockT <= 0 ) { clockT = 20; state.setClock( clock.hour ); state.save(); }

			// the traders
			foreach ( var v in _vendors ) v.update( dt, p.mode == "walk" ? p.position : null );
			UpdateVendors( inp, p );
		}

		// fuel burn at the helm (the engine stops when the tank is dry); the fish finder comes with the HUD
		void UpdateBoat( double dt, Tidewater.Player.Player p )
		{
			var b = p.boat; var s = state;
			if ( b != null && b.driven )
			{
				double left = s.burn( Gear.fuelBurn( b.rpm ) * dt );
				if ( left <= 0 )
				{
					b.throttle = 0;
					if ( ! fuelOut ) Toast( "Out of fuel · buy diesel at the chandlery by the boathouse", 4f );
					fuelOut = true;
				}
			}
			else if ( wasDriven ) s.save();
			wasDriven = b != null && b.driven;
		}

		void UpdateVendors( GameInput inp, Tidewater.Player.Player p )
		{
			Vendor near = null, shut = null;
			double hour = clock.hour;
			if ( p.mode == "walk" )
				foreach ( var v in _vendors )
				{
					if ( ! v.inRange( p.position ) ) { v.shutTold = false; continue; }
					if ( v.openAt( hour ) ) near = v;
					else if ( ! v.shutTold ) { v.shutTold = true; shut = v; }
				}

			// the stalls keep island hours: closed at night, say so once as you walk up
			if ( shut != null ) Toast( $"{shut.shortName} is shut — back at {GameText.hourLabel( shut.hours[ 0 ] )}", 3.2f );
			foreach ( var v in _vendors ) v.talking = openVendor == v;
			if ( openVendor != null && ( near == null || near != openVendor ) ) CloseStand();
			if ( near == null ) return;
			if ( p.prompt == null ) p.prompt = new Prompt { action = "interact", text = openVendor != null ? "Leave" : $"Talk to {near.shortName}" };
			if ( inp.actHit( "interact" ) )
			{
				if ( openVendor != null ) CloseStand();
				else OpenStand( near );
			}
		}

		// ---- the readouts and the panels (IMGUI, until the HTML UI is ported)

		GUIStyle label, small, title, rowKey;
		Vector2 scroll;

		void Styles()
		{
			if ( label != null ) return;
			label = new GUIStyle( GUI.skin.label ) { fontSize = 15, fontStyle = FontStyle.Bold }; label.normal.textColor = Color.white;
			small = new GUIStyle( GUI.skin.label ) { fontSize = 12 }; small.normal.textColor = new Color( 1, 1, 1, 0.75f );
			title = new GUIStyle( GUI.skin.label ) { fontSize = 20, fontStyle = FontStyle.Bold }; title.normal.textColor = Color.white;
			rowKey = new GUIStyle( GUI.skin.label ) { fontSize = 14 }; rowKey.normal.textColor = Color.white;
		}

		void OnGUI()
		{
			if ( ! built || ! Application.isPlaying || host == null || host.player == null ) return;
			Styles();
			var s = state;
			if ( showHud )
			{
				// the purse: money, the day and the hour, the cooler (Game.update's hud.update)
				var st = s.stats;
				GUI.Label( new Rect( 12, 8, 420, 24 ), $"${s.money:N0}    Day {s.day}  {GameText.fmtClock( clock.hour )}{( clock.timeSpeed == 0 ? " (paused)" : "" )}", label );
				GUI.Label( new Rect( 12, 30, 420, 20 ), $"{( s.upgrades[ "hold" ] > 0 ? "Hold" : "Cooler" )} {s.holdKg:F1} / {st.holdKg} kg", small );
				bool aboard = host.player.mode == "boat" || host.player.mode == "deck";
				if ( aboard ) GUI.Label( new Rect( 12, 48, 420, 20 ), $"Fuel {s.fuelL:F0} / {st.fuelL} L", small );
				if ( s.mayTrap || s.sets.Count > 0 ) GUI.Label( new Rect( 12, aboard ? 66 : 48, 420, 20 ), $"Traps {s.sets.Count} set · {s.traps} aboard", small );
			}

			if ( openVendor != null ) DrawVendor( openVendor );
			else if ( inventoryOpen ) DrawInventory();
		}

		Rect PanelRect() { float w = Mathf.Min( 560, Screen.width - 40 ), h = Mathf.Min( 520, Screen.height - 80 ); return new Rect( ( Screen.width - w ) / 2, ( Screen.height - h ) / 2, w, h ); }

		static string Fmt( double kg ) => kg.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture );

		void DrawInventory()
		{
			var s = state; var r = PanelRect();
			GUI.Box( r, GUIContent.none );
			GUILayout.BeginArea( new Rect( r.x + 14, r.y + 10, r.width - 28, r.height - 20 ) );
			GUILayout.Label( s.upgrades[ "hold" ] > 0 ? "Fish hold" : "Cooler", title );
			GUILayout.Label( $"{s.inventory.Count} fish · {s.holdKg:F1} of {s.stats.holdKg} kg · worth ${s.holdValue}", small );
			scroll = GUILayout.BeginScrollView( scroll, GUILayout.Height( r.height - 130 ) );
			if ( s.inventory.Count == 0 ) GUILayout.Label( "Nothing yet. Cast from the pier, the beach or the boat.", small );
			foreach ( var f in s.inventory.ToList() )
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label( FishTable.Get( f.species ).name + ( f.record ? "  (record)" : "" ), rowKey, GUILayout.Width( 220 ) );
				GUILayout.Label( $"{f.cm:F0} cm", rowKey, GUILayout.Width( 60 ) );
				GUILayout.Label( $"{Fmt( f.kg )} kg", rowKey, GUILayout.Width( 70 ) );
				GUILayout.Label( GameText.priceTag( s, f ), rowKey, GUILayout.Width( 70 ) );
				if ( GUILayout.Button( "Release", GUILayout.Width( 70 ) ) ) s.release( f.id );
				GUILayout.EndHorizontal();
			}

			foreach ( var kv in s.log.Where( k => FishTable.Has( k.Key ) ) )
				GUILayout.Label( $"{FishTable.Get( kv.Key ).name}: {kv.Value.count} caught, best {Fmt( kv.Value.bestKg )} kg · {( kv.Value.bestCm ?? Math.Round( FishTable.fishLengthCm( kv.Key, kv.Value.bestKg ) ) ):F0} cm", small );
			GUILayout.EndScrollView();
			GUILayout.BeginHorizontal();
			GUILayout.Label( "Sell at the fish stand by the pier", small );
			GUILayout.FlexibleSpace();
			if ( GUILayout.Button( $"Close ({host.input.label( "cooler" )})", GUILayout.Width( 120 ) ) ) ToggleInventory( false );
			GUILayout.EndHorizontal();
			GUILayout.EndArea();
		}

		void DrawVendor( Vendor v )
		{
			var s = state; var r = PanelRect();
			GUI.Box( r, GUIContent.none );
			GUILayout.BeginArea( new Rect( r.x + 14, r.y + 10, r.width - 28, r.height - 20 ) );
			GUILayout.Label( v.name, title );
			if ( v.kind == "shop" ) DrawShop( v ); else DrawStand( v );
			GUILayout.EndArea();
		}

		// the fish stand (GameHUD.renderStand)
		void DrawStand( Vendor v )
		{
			var s = state;
			GUILayout.Label( s.inventory.Count > 0 ? ( v.greeting != "" ? v.greeting : "Let's see what you caught." ) : ( v.idle != "" ? v.idle : "Come back when you've got fish." ), small );
			string order = GameText.orderBoard( s ), market = GameText.marketBoard( s );
			if ( order != "" ) GUILayout.Label( order, label );
			if ( market != "" ) GUILayout.Label( market, small );
			scroll = GUILayout.BeginScrollView( scroll, GUILayout.Height( 260 ) );
			if ( s.inventory.Count == 0 ) GUILayout.Label( "Your cooler is empty.", small );
			foreach ( var f in s.inventory.ToList() )
			{
				GUILayout.BeginHorizontal();
				GUILayout.Label( FishTable.Get( f.species ).name, rowKey, GUILayout.Width( 220 ) );
				GUILayout.Label( $"{f.cm:F0} cm", rowKey, GUILayout.Width( 60 ) );
				GUILayout.Label( $"{Fmt( f.kg )} kg", rowKey, GUILayout.Width( 70 ) );
				GUILayout.Label( GameText.priceTag( s, f ), rowKey, GUILayout.Width( 70 ) );
				if ( GUILayout.Button( "Sell", GUILayout.Width( 60 ) ) ) Sell( new[] { f.id } );
				GUILayout.EndHorizontal();
			}

			GUILayout.EndScrollView();
			GUILayout.BeginHorizontal();
			if ( GUILayout.Button( $"Leave ({host.input.label( "interact" )})", GUILayout.Width( 120 ) ) ) CloseStand();
			GUILayout.FlexibleSpace();
			GUI.enabled = s.inventory.Count > 0;
			if ( GUILayout.Button( $"Sell all · ${s.holdValue}", GUILayout.Width( 160 ) ) ) SellAll();
			GUI.enabled = true;
			GUILayout.EndHorizontal();
		}

		// the chandlery (GameHUD.renderShop): fuel, the trap line, then every upgrade track
		void DrawShop( Vendor v )
		{
			var s = state;
			GUILayout.Label( $"{v.greeting} · You have ${s.money:N0}", small );
			scroll = GUILayout.BeginScrollView( scroll, GUILayout.Height( 340 ) );
			double missing = s.stats.fuelL - s.fuelL;
			ShopRow( $"Diesel · ${Gear.FUEL_PRICE:F2} / L", $"Tank: {s.fuelL:F0} of {s.stats.fuelL} L",
				missing > 0.5 ? $"Fill · ${s.refuelCost()}" : null, missing > 0.5 && s.money >= Gear.FUEL_PRICE, "Full", () => Refuel() );
			bool licensed = s.mayTrap;
			var lic = Gear.nextLevel( s.upgrades, "trapLicence" );
			ShopRow( $"{Gear.Track( "trapLicence" ).name}: {( licensed ? "held" : "none" )}",
				licensed ? $"{s.traps} aboard · {s.sets.Count} of {Gear.TRAP_LIMIT} in the water" : $"Set and haul lobster pots (max {Gear.TRAP_LIMIT} in the water)",
				lic != null ? $"${lic.cost}" : null, lic != null && lic.cost <= s.money, "Held", () => Buy( "trapLicence" ) );
			ShopRow( $"Lobster traps · ${Gear.TRAP_PRICE} each", licensed ? $"{s.traps} aboard (max {Gear.TRAP_LIMIT})" : "Licence required",
				licensed && s.traps < Gear.TRAP_LIMIT ? $"Buy 1 · ${Gear.TRAP_PRICE}" : null, s.money >= Gear.TRAP_PRICE, licensed ? "Full" : "Licence", () => BuyTraps( 1 ) );
			foreach ( var t in Gear.UPGRADES_LIST.Where( t => t.key != "trapLicence" ) )
			{
				var cur = t.levels[ s.upgrades[ t.key ] ]; var next = Gear.nextLevel( s.upgrades, t.key );
				string key = t.key;
				ShopRow( $"{t.name}: {( next != null ? next.label : cur.label )}", $"Now: {cur.label}", next != null ? $"${next.cost}" : null, next != null && next.cost <= s.money, "Top of the line", () => Buy( key ) );
			}

			GUILayout.EndScrollView();
			GUILayout.BeginHorizontal();
			GUILayout.Label( "Upgrades take effect at once", small );
			GUILayout.FlexibleSpace();
			if ( GUILayout.Button( $"Leave ({host.input.label( "interact" )})", GUILayout.Width( 120 ) ) ) CloseStand();
			GUILayout.EndHorizontal();
		}

		void ShopRow( string name, string detail, string button, bool enabled, string done, Action click )
		{
			GUILayout.BeginHorizontal();
			GUILayout.BeginVertical();
			GUILayout.Label( name, rowKey );
			GUILayout.Label( detail, small );
			GUILayout.EndVertical();
			GUILayout.FlexibleSpace();
			if ( button == null ) GUILayout.Label( done, small, GUILayout.Width( 130 ) );
			else
			{
				GUI.enabled = enabled;
				if ( GUILayout.Button( button, GUILayout.Width( 130 ) ) ) click();
				GUI.enabled = true;
			}

			GUILayout.EndHorizontal();
		}
	}
}
