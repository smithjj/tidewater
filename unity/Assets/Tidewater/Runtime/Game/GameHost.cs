using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.World;
using UnityEngine;

// Hosts the economy on top of the world (the money half of src/game/Game.js): owns the GameState (the save), the world clock, the two traders and
// their panels, burns the fuel at the helm, applies the engine upgrade, and rolls the day over at midnight. PlayerHost ticks it after the player,
// where the JS App calls Game.update. Fishing (the rod, bites, the catch card), the trap line, the guide and the minimap are Game.js too
// and have their own rows (FishingGame, TrapGame, Guide, Minimap); the Editor tools (GameDebug) can also drive them.
//
// The purse, the panels, the toasts and the prompts are IMGUI in the web UI's look (GameHUD, UIKit); the settings rail and the start overlay (ui/) are not ported.
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
		public static bool noGuide;         // the tools that drive the Editor turn the first-play intro and tips off (a static: it must not be saved into the scene)
		public Vendor openVendor { get; private set; }   // the trader whose panel is open (JS hud.standOpen / hud.vendor)
		public bool inventoryOpen { get; private set; }  // the cooler panel (I)
		public Weather weather { get; private set; }     // the sea-state walk (null until the world is built)
		public FishingGame fishing { get; private set; } // the rod, the bite, the fight and the catch card (null until the world is built)
		public TrapGame trap { get; private set; }       // the trap line: pots set and hauled from the working boat (Traps.js, Game.js; null until the world is built)
		public readonly BoatGauges gauges = new BoatGauges(); // bottom left at the helm: throttle, rpm and speed, compass (UI.setBoatGauges)
		public AnchorGame anchor { get; private set; }   // X aboard a boat: the anchor down or up, its toasts, and the gear on the seabed (Game.toggleAnchor, AnchorGear.js)
		public readonly DepthSounder depth = new DepthSounder(); // left while diving: the sounding tape (UI.setDepth)
		public readonly MinimapView minimap = new MinimapView(); // lower right: the island, the markers; N opens the large map (Minimap.js)
		public Guide guide { get; private set; }         // the first-play intro and the one-time tips (Guide.js; null until the world is built)
		public FishGuide fishGuide { get; private set; } // the J fish guide (FishGuide.js; null until the world is built)
		public bool fishGuideOpen => fishGuide != null && fishGuide.open;
		public GameInput Input => host != null ? host.input : null;
		public Tidewater.Player.Player Player => host != null ? host.player : null;
		public ControlsSheet controls { get; private set; } // the F1 sheet (UI.js help; null until the world is built)
		public GameHUD hud { get; private set; }         // the purse and the panels (GameHUD.js; null until the world is built)
		public SettingsUI settings { get; private set; } // the settings rail and panel (UI.js; null until the world is built)
		public PlayerHost Host => host;
		public readonly WildlifeCam wildlife = new WildlifeCam(); // G: visit the rays and the turtle (Unity only)
		public bool photoMode => ( settings != null && settings.photo ) || wildlife.active; // the interface is hidden (UI.setPhotoMode)

		readonly List<Vendor> _vendors = new List<Vendor>();
		PlayerHost host;
		Tidewater.Player.BoatController lobster;
		double baseMaxThrust, basePitchSpeed;
		bool fuelOut, wasDriven;
		double clockT = 20;
		// built once Ensure has made the state (a field would survive an Editor reload that drops the state, so it is derived from it)
		bool built => state != null && lobster != null;

		void Awake() { instance = this; }
		void OnDestroy()
		{
			if ( instance == this ) instance = null;
			if ( fishing != null ) fishing.Dispose();
			if ( trap != null ) trap.Dispose();
			if ( anchor != null ) anchor.Dispose();
			if ( fishGuide != null ) fishGuide.Dispose();
			minimap.Dispose();
			gauges.Dispose();
			// the Editor's sea state slider works again once the game is gone
			if ( weather != null && OceanRenderer.instance != null ) OceanRenderer.instance.weatherDriven = false;
		}

		// builds the state and the world half once the player's world exists (PlayerHost.Build): false until then
		public bool Ensure( PlayerHost h )
		{
			if ( built ) return true;
			if ( h == null || h.terrainData == null || h.colliders == null || h.driver == null || h.driver.controller == null ) return false;
			host = h;
			_vendors.Clear();
			var store = saveToFile ? SafeStore() : new MemorySaveStore();
			state = new GameState( store );
			state.load();
			// the day resumes where it was left
			if ( state.clock != null ) clock.hour = state.clock.Value;
			// then the weather takes over the sea state (App.js: new Weather, onAnnounce, restore)
			var ocean = OceanRenderer.instance;
			if ( ocean != null )
			{
				weather = new Weather( () => clock.hour, () => clock.timeSpeed, ocean.windDirection, ocean.ApplyConditions, new System.Random().Next( 1000000000 ) );
				weather.onAnnounce = text => Toast( text, 3.6f );
				weather.restore( state.weather );
				ocean.weatherDriven = true;
			}
			var vs = Stalls.Create( h.terrainData.HeightAt, h.colliders );
			stand = vs[ 0 ]; chandlery = vs[ 1 ];
			_vendors.Add( stand ); _vendors.Add( chandlery );
			StallsView.Build( transform, h.terrainData.HeightAt, stand, chandlery );
			// the rebuilt engine is the lobster boat's: always target it, not whichever boat is active
			lobster = h.driver.controller;
			baseMaxThrust = lobster.maxThrust; basePitchSpeed = lobster.pitchSpeed;
			// only the boats you have bought can be boarded; the rest wait at their moorings
			h.player.owns = c => state.ownsBoat( BoatId( c ) );
			fishing?.Dispose();
			fishing = new FishingGame( this, h, transform );
			trap?.Dispose();
			trap = new TrapGame( this, h, lobster, transform );
			anchor?.Dispose();
			anchor = new AnchorGame( this, h, transform );
			ApplyGear();
			state.onChange( _ => { ApplyGear(); if ( fishGuide != null ) fishGuide.Refresh(); } ); // (the guide: the log or today's order changed)
			fishGuide = new FishGuide( this, h.terrainData.HeightAt );
			hud = new GameHUD( this );
			controls = new ControlsSheet( this );
			settings = new SettingsUI( this );
			SettingsTabs.Build( settings, this );
			// the first-play guide keeps its own seen state in a file, even where the game save is in memory (the dev scene): the intro shows once
			guide = new Guide( this, h, minimap, SafeStore() ?? new MemorySaveStore(), ! noGuide );
			return true;
		}

		// where the rebound controls and the pad options are kept (Bindings.Attach): beside the save, or nowhere when the state lives in memory
		public ISaveStore ControlsStore() => saveToFile ? SafeStore() : null;

		static ISaveStore SafeStore() { try { return new FileSaveStore(); } catch ( Exception ) { return null; } }

		// Game.applyGear: the engine upgrade (speedMul), and the deck floodlights (the lights upgrade)
		public void ApplyGear()
		{
			if ( lobster == null || state == null ) return;
			var g = state.stats;
			lobster.maxThrust = baseMaxThrust * g.speedMul * g.speedMul;
			lobster.pitchSpeed = basePitchSpeed * g.speedMul;
			if ( fishing != null ) fishing.ApplyGear();
			var lamps = LocalLightsView.instance;
			if ( lamps != null ) lamps.SetDeckLights( g.deckLights );
		}

		// the intro takes the mouse and the keys while it is up (Guide.js)
		public void GuideOpened( bool open )
		{
			if ( host == null ) return;
			if ( open ) host.input.ReleaseLock();
			SyncMenu();
		}

		// a panel (or the intro) owns the keys and the mouse
		public void SyncMenu()
		{
			if ( host != null && host.input != null ) host.input.menuMode = inventoryOpen || openVendor != null || fishGuideOpen || ( guide != null && guide.open ) || ( controls != null && controls.open );
		}

		// the controllers of the boats you own (the guide's "your boat" tip)
		public IEnumerable<Tidewater.Player.BoatController> OwnedBoats()
		{
			if ( host == null || state == null ) yield break;
			if ( lobster != null && state.ownsBoat( "lobster" ) ) yield return lobster;
			if ( host.pelagicDriver != null && state.ownsBoat( "pelagic" ) ) yield return host.pelagicDriver.controller;
			if ( host.miniDriver != null && state.ownsBoat( "mini" ) ) yield return host.miniDriver.controller;
		}

		public void Toast( string text, float seconds = 2.6f ) { if ( host != null ) host.Toast( text, seconds ); else Debug.Log( "[game] " + text ); }

		// ---- the actions the panels and the keys call (Game.buy / buyTraps / refuel / sellAll)

		// which of the three boats a controller is (the ids of Gear.BOATS)
		public string BoatId( Tidewater.Player.BoatController c )
		{
			if ( c == null || host == null ) return null;
			return c == lobster ? "lobster" : host.pelagicDriver != null && c == host.pelagicDriver.controller ? "pelagic" : host.miniDriver != null && c == host.miniDriver.controller ? "mini" : null;
		}

		public BoatDef BuyBoat( string id )
		{
			var r = state.buyBoat( id );
			if ( r != null ) Toast( $"{r.name} is yours · she's at her mooring by the pier" );
			return r;
		}

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

		public SaleResult SellAll() { var r = Sell( null ); if ( host != null && host.sound != null && host.sound.Alive ) host.sound.scape.coin(); return r; } // (Game.sellAll: the coins)

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
			if ( weather != null ) state.setWeather( weather.state() );
			state.save();
			state.emit();
			Toast( $"Day {state.day}", 3.4f );
		}

		public void ToggleInventory( bool? force = null )
		{
			inventoryOpen = force ?? ! inventoryOpen;
			if ( inventoryOpen ) { CloseStand(); if ( fishGuide != null ) fishGuide.Toggle( false ); host.input.ReleaseLock(); }
			SyncMenu();
		}

		public void OpenStand( Vendor v )
		{
			openVendor = v;
			inventoryOpen = false;
			if ( fishGuide != null ) fishGuide.Toggle( false );
			host.input.ReleaseLock();
			SyncMenu();
		}

		public void CloseStand()
		{
			openVendor = null;
			SyncMenu();
		}

		// ---- per frame (after the player update: Game.update)
		public void Tick( double dt )
		{
			if ( ! built ) return;
			var p = host.player; var inp = host.input;

			// App.frame: the world clock runs, and midnight is a new day
			if ( clock.Tick( dt ) ) NewDay();
			// the weather walks the sea state on in-game time (a no-op while the clock is paused)
			if ( weather != null ) weather.update( dt );
			if ( inp.actHit( "pauseTime" ) ) Toast( clock.Toggle() ? "Time running" : "Time paused" );

			// the intro owns the keys while it is up (Guide.js: capture phase), F1 asks for it again
			bool intro = guide != null && guide.open;
			// the interface commands (AppUI.update -> UI.command): the panel, photo mode and the sheet; the intro owns the keys while it is up
			if ( ! intro && inp.actHit( "settings" ) ) settings.Command( "settings" );
			if ( ! intro && inp.actHit( "photo" ) ) settings.Command( "photo" );
			if ( inp.actHit( "controls" ) && ! intro ) { if ( settings.photo ) settings.SetPhoto( false ); controls.Toggle(); }
			if ( ! intro && inp.actHit( "cooler" ) ) ToggleInventory();
			if ( ! intro && inp.actHit( "codex" ) ) fishGuide.Toggle();
			if ( inp.actHit( "cancel" ) && ! settings.Cancel() ) { ToggleInventory( false ); CloseStand(); fishGuide.Toggle( false ); controls.Toggle( false ); }
			fishGuide.Tick( dt );
			hud.Tick( dt );
			controls.Tick( dt );
			settings.Tick( dt );

			// the rod, the bite, the fight and the landed fish
			fishing.Update( dt );

			// the minimap (Game.update: after the rest; the catch card owns the screen while it is up, the map steps aside)
			minimap.Tick( dt, this, host, fishing.catchOpen );
			gauges.Tick( dt, this );
			depth.Tick( dt, this );
			if ( guide != null ) guide.Update( dt );

			UpdateBoat( dt, p );

			// the world clock and the weather ride along in the save (every 20 s, and at midnight)
			clockT -= dt;
			if ( clockT <= 0 ) { clockT = 20; state.setClock( clock.hour ); if ( weather != null ) state.setWeather( weather.state() ); state.save(); }

			// the traders
			foreach ( var v in _vendors ) v.update( dt, p.mode == "walk" ? p.position : null );
			UpdateVendors( inp, p );

			// the trap line (the pots ride the sea; setting and hauling are E on the working boat, the cast button at its helm)
			trap.Update( dt, inp, p );
			anchor.Update( dt, inp, p ); // (X: the anchor down or up aboard a boat)

			// prompts when the player has nothing to say
			trap.ApplyPrompt( p, inp );
			if ( p.prompt == null ) p.prompt = fishing.Prompt();
			else if ( p.mode == "boat" && fishing.SeatedWithRod )
			{
				// seated with the rod out: the fishing prompts, and the way out of the seat
				var fp = fishing.Prompt();
				if ( fp != null ) p.prompt = new Prompt { action = fp.action, key = fp.key, text = fp.text + "   ·   " + host.input.label( "interact" ) + "  leave helm" };
			}
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
			if ( near == null || fishing.blocksVendors ) return;
			if ( p.prompt == null ) p.prompt = new Prompt { action = "interact", text = openVendor != null ? "Leave" : $"Talk to {near.shortName}" };
			if ( inp.actHit( "interact" ) )
			{
				if ( openVendor != null ) CloseStand();
				else OpenStand( near );
			}
		}

		// ---- the readouts and the panels (GameHUD)

		void OnGUI()
		{
			if ( ! built || ! Application.isPlaying || host == null || host.player == null ) return;
			GUI.skin.font = UIFonts.Inter; // the plain labels and buttons (the web UI's body face)
			bool hudOn = showHud && ! photoMode; // (photo mode: only its hint)
			if ( hudOn ) minimap.OnGUI();
			if ( hudOn ) gauges.OnGUI();
			if ( hudOn ) depth.OnGUI();
			if ( hudOn && fishing != null ) fishing.OnGUI();
			if ( hudOn && hud != null ) hud.OnGUI();
			if ( hudOn && fishGuide != null ) fishGuide.OnGUI();
			if ( settings != null && ! wildlife.active ) settings.OnGUI();
			if ( hudOn && guide != null ) guide.OnGUI();
			if ( hudOn && controls != null ) controls.OnGUI();
			wildlife.OnGUI();
			if ( settings != null ) settings.OnGUIOverlay(); // (the open dropdown and the tooltip: above everything)
		}
	}
}
