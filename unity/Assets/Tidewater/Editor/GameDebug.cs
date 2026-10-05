using System.Linq;
using Tidewater.Game;
using Tidewater.Ocean;
using Tidewater.Player;
using UnityEditor;
using UnityEngine;

// Editor tools for the economy: run the PlayerHost frame by frame in the Editor (it does not tick when not playing), press keys, stand the player
// somewhere, hand the state a catch, read the state back. The save is kept in memory (never the player's file).
//   GameDebug.Ensure();  GameDebug.At( 52, -72, 0 );  GameDebug.Run( 0.5 );  GameDebug.Give( "grouper", 6.1, 12 );  GameDebug.Tap( "KeyE" );  GameDebug.Run( 0.2 );
namespace Tidewater.EditorTools
{
	public static class GameDebug
	{
		static PlayerHost Host() => Object.FindAnyObjectByType<PlayerHost>();
		public static GameHost Game() => Object.FindAnyObjectByType<GameHost>();

		public static string Ensure()
		{
			var g = GameObject.Find( "Game" ) ?? new GameObject( "Game" );
			var host = g.GetComponent<GameHost>() ?? g.AddComponent<GameHost>();
			host.saveToFile = false;
			GameHost.noGuide = true;
			var p = GameObject.Find( "Player" ) ?? new GameObject( "Player" );
			if ( p.GetComponent<PlayerHost>() == null ) p.AddComponent<PlayerHost>();
			return "game + player objects ready";
		}

		// frames of App.frame: the sea advances, the water queries are read back at once, the host steps
		public static string Run( double seconds, double dt = 1.0 / 30 )
		{
			var ocean = Object.FindAnyObjectByType<OceanRenderer>();
			var host = Host();
			if ( ocean == null || ocean.query == null ) return "no ocean query yet (render a frame first)";
			if ( host == null ) return "no PlayerHost";
			var q = ocean.query;
			for ( double t = 0; t < seconds - 1e-9; t += dt )
			{
				ocean.Advance( ( float ) dt, ( float ) dt );
				q.Update(); q.Flush();
				host.Step( dt );
			}

			return State();
		}

		public static string Tap( string code ) { var h = Host(); if ( h == null || h.input == null ) return "not built yet (Run first)"; h.input.Tap( code ); return "tapped " + code; }

		public static string Mouse( int button, bool down ) { var h = Host(); if ( h == null || h.input == null ) return "not built yet (Run first)"; h.input.Press( button, down ); return ( down ? "down " : "up " ) + button; }

		public static string Rod()
		{
			var f = Game().fishing; var r = f.rod;
			return $"rod {r.state} equipped {r.equipped} power {r.power:F2} lineOut {r.lineOut:F1} depth {r.depth:F1} bobber ({r.bobber.x:F1},{r.bobber.y:F2},{r.bobber.z:F1}) bite {( f.bite != null ? f.bite.phase + " " + f.bite.species + " " + f.bite.kg.ToString( "F2" ) + " t=" + f.bite.t.ToString( "F1" ) : "-" )} fight {( f.fight != null ? f.fight.state + " tension " + f.fight.tension.ToString( "F2" ) + " dist " + f.fight.distance.ToString( "F1" ) + " stamina " + f.fight.stamina.ToString( "F2" ) : "-" )} landing {f.landing != null}";
		}

		// a player stand-in for real-time runs: strikes on the take, holds the reel while the line is slack enough, lets go when it is tight
		static bool auto;
		public static string AutoFish( bool on )
		{
			if ( on && ! auto ) EditorApplication.update += AutoStep;
			if ( ! on && auto ) EditorApplication.update -= AutoStep;
			auto = on;
			return "autofish " + on;
		}

		static void AutoStep()
		{
			var g = Game(); var h = Host();
			if ( g == null || g.fishing == null || h == null || h.input == null ) return;
			var f = g.fishing;
			if ( f.fight != null ) h.input.Press( 0, f.fight.tension < 0.62 );
			else if ( f.bite != null && f.bite.phase == "take" && f.rod.state == "floating" ) { h.input.Press( 0, false ); h.input.Press( 0, true ); }
			else if ( f.rod.state != "idle" && f.rod.state != "windup" ) h.input.Press( 0, false );
		}

		// stand the player on foot at a sim point, facing yaw (radians, 0 = north... the Player's own yaw)
		public static string At( double x, double z, double yaw = 0 )
		{
			var h = Host();
			if ( h == null || h.player == null ) return "not built yet (Run first)";
			var p = h.player;
			p.mode = "walk"; p.yaw = yaw; p.pitch = 0;
			p.position.set( x, h.terrainData.HeightAt( x, z ) + 0.05, z );
			p.velocity.set( 0, 0, 0 );
			return State();
		}

		// land a fish as if the fight had been won: the catch card with its portrait comes up (the rod is taken out first, the player must be on foot)
		public static string Land( string species, double kg )
		{
			var f = Game().fishing;
			if ( ! f.rod.equipped ) f.rod.equip( true );
			f.Land( species, kg );
			return $"landing {species} {kg} kg: rod {f.rod.state}";
		}

		public static string Give( string species, double kg, double hour = 12 ) { var f = Game().state.addFish( species, kg, hour ); return f == null ? "hold full" : $"#{f.id} {f.species} {f.kg} kg ${f.value}"; }
		public static string Money( double v ) { Game().state.money = v; Game().state.emit(); return State(); }
		// the boats you own; with ids ("all" or "pelagic,lobster") hand them over without paying (the browser's __tw.boats)
		public static string Boats( string ids = null )
		{
			var st = Game().state;
			if ( ! string.IsNullOrEmpty( ids ) )
			{
				var want = ids.Split( ',' ).Select( x => x.Trim() ).ToList();
				if ( want.Contains( "all" ) ) want = Tidewater.Game.Gear.BOAT_IDS.ToList();
				st.boats = Tidewater.Game.Gear.BOAT_IDS.Where( id => st.ownsBoat( id ) || want.Contains( id ) ).ToList();
				st.save(); st.emit();
			}

			return string.Join( ",", st.boats );
		}

		// the trap line: a licence and n pots aboard (Marta's licence and traps, without paying)
		public static string Traps( int n = 4 )
		{
			var st = Game().state;
			st.upgrades[ "trapLicence" ] = 1; st.money = System.Math.Max( st.money, 1000 ); st.buyTraps( n ); st.emit();
			return State();
		}

		// step aboard the lobster boat: on its deck, or at the helm ("helm"), wherever it lies
		public static string Aboard( string where = "deck" )
		{
			var h = Host();
			if ( h == null || h.player == null ) return "not built yet (Run first)";
			Boats( "all" );
			h.player.boardBoat( h.driver.controller );
			if ( where == "helm" ) h.player.takeHelm();
			return State() + $" boat at {h.driver.controller.position.x:F1}, {h.driver.controller.position.z:F1} depth {- h.terrainData.HeightAt( h.driver.controller.position.x, h.driver.controller.position.z ):F1}";
		}

		// the pot actions (what E does): over the stern / up on the hauler
		public static string SetPot() { var r = Game().trap.SetTrap(); return r == null ? "refused" : $"set #{r.id} at {r.x:F1}, {r.z:F1}"; }
		public static string HaulPot() { var r = Game().trap.HaulTrap(); return r == null ? "nothing hauled" : $"hauled {r.Count} animals: " + string.Join( ", ", r.Select( a => $"{a.species} {a.kg:F2}" ) ); }

		public static string Hour( double h ) { Game().clock.Set( h ); return State(); }

		// the sea state: a rung of the ladder, or the name of the one we are on (Debug.js weather)
		public static string Weather( string name = null )
		{
			var w = Game()?.weather;
			if ( w == null ) return "no weather yet";
			if ( name == null ) return $"{w.name} level={w.level:0.###} target={w.target} hold={w.HoldHours:0.##}h wind={w.windDir:0.#}";
			int i = System.Array.IndexOf( Tidewater.Ocean.Conditions.CONDITIONS, name );
			if ( i < 0 ) return string.Join( " | ", Tidewater.Ocean.Conditions.CONDITIONS );
			w.Jump( i );
			return name;
		}

		public static string State()
		{
			var g = Game(); var h = Host();
			if ( g == null || g.state == null ) return "game not built yet";
			var s = g.state;
			string prompt = h?.player?.prompt != null ? h.player.prompt.text : "-";
			return $"${s.money} day {s.day} {GameText.fmtClock( g.clock.hour )} inv {s.inventory.Count} ({s.holdKg:F1}/{s.stats.holdKg} kg, ${s.holdValue}) fuel {s.fuelL:F1}/{s.stats.fuelL} traps {s.traps}/{s.sets.Count} " +
				$"| vendor {( g.openVendor != null ? g.openVendor.shortName : "-" )} cooler {g.inventoryOpen} prompt '{prompt}' mode {h?.player?.mode} menu {h?.input?.menuMode}";
		}
	}
}
