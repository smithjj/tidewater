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

		public static string Give( string species, double kg, double hour = 12 ) { var f = Game().state.addFish( species, kg, hour ); return f == null ? "hold full" : $"#{f.id} {f.species} {f.kg} kg ${f.value}"; }
		public static string Money( double v ) { Game().state.money = v; Game().state.emit(); return State(); }
		public static string Hour( double h ) { Game().clock.Set( h ); return State(); }

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
