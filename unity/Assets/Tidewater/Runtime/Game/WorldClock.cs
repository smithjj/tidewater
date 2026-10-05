using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// The world clock of src/App.js: settings.timeOfDay (hours on the 24 h clock) runs at timeSpeed hours per real second (0.02: a day in 20 real minutes),
// T pauses it (App.toggleTime), and crossing midnight is a new day (App.newDay -> Game.newDay). The sun, sky and weather read it in the JS; here they
// join when the sky is ported (the sun light is still fixed).
namespace Tidewater.Game
{
	public sealed class WorldClock
	{
		public double hour = 16.2;       // App.settings.timeOfDay
		public double timeSpeed = 0.02;  // hours per real second (24 h in 20 real minutes); 0 pauses the day
		double savedSpeed;               // App._timeSpeed: what T resumes to

		// App.frame: advance the clock; true when the day rolled over (midnight)
		public bool Tick( double dt )
		{
			if ( timeSpeed == 0 ) return false;
			double prev = hour;
			hour = ( ( prev + dt * timeSpeed + 24 ) % 24 + 24 ) % 24; // JS % keeps the sign of the dividend; the sum is positive, so it is the same
			return hour < prev;
		}

		// T: let the day run or stop it; true when it now runs
		public bool Toggle()
		{
			if ( timeSpeed != 0 ) { savedSpeed = timeSpeed; timeSpeed = 0; }
			else timeSpeed = savedSpeed != 0 ? savedSpeed : 0.02;
			return timeSpeed != 0;
		}

		// Debug.js time( h )
		public void Set( double h ) { hour = ( ( h % 24 ) + 24 ) % 24; }
	}

	// the strings the HUD, the panels and the toasts build from the game's numbers (GameHUD.js, Game.js)
	public static class GameText
	{
		static string Inv( double x ) => x.ToString( CultureInfo.InvariantCulture );

		// a clock hour as a person would say it: 6 -> "6 am", 18.5 -> "6:30 pm" (Game.js hourLabel)
		public static string hourLabel( double h )
		{
			int hr = ( int ) Math.Floor( h ) % 24;
			int m = ( int ) Tidewater.Engine.JS.Round( ( h - Math.Floor( h ) ) * 60 );
			int h12 = hr % 12 == 0 ? 12 : hr % 12;
			string ap = hr < 12 ? "am" : "pm";
			return m != 0 ? $"{h12}:{m:D2} {ap}" : $"{h12} {ap}";
		}

		// the clock, 24 h: 6.75 -> "06:45" (GameHUD.js fmtClock)
		public static string fmtClock( double hours )
		{
			int hr = ( int ) Math.Floor( hours ) % 24, m = ( int ) Math.Floor( ( hours - Math.Floor( hours ) ) * 60 );
			return $"{hr:D2}:{m:D2}";
		}

		// the pieces of a fish's price tag: the price, then the market mark and the order star (GameHUD.js priceTag)
		public static string priceTag( GameState s, InventoryFish f )
		{
			double m = s.mulFor( f.species );
			string mark = m > 1.001 ? " ▲" : m < 0.999 ? " ▼" : "";
			string order = s.orderMulFor( f ) > 1 ? " ★" : "";
			return "$" + Inv( s.priceOf( f ) ) + mark + order;
		}

		// Joe's order of the day: what he is asking for, the multiplier, and how it has gone so far (GameHUD.js orderBoard), as UIKit markup; "" when there is none
		public static string orderBoard( GameState s )
		{
			var o = s.todaysOrder;
			if ( o == null ) return "";
			var f = FishTable.Get( o.species );
			string done = o.filled > 0 ? $" <c tone=\"aqua\">filled {( o.filled == 1 ? "once" : o.filled + " times" )} · +${Inv( o.bonus )}</c>" : "";
			return $"<c tone=\"ink3\">Joe wants</c> <b>{f.name}</b>, {Orders.fmtKg( o.minKg )} or bigger <c tone=\"sun\">★ ×{Inv( Orders.ORDER_MULT )}</c>{done}";
		}

		// Joe's board: the day's movers (the top payers and the one that fell out of favour) (GameHUD.js marketBoard), one UIKit markup string per mover; empty when the market is flat
		public static List<string> marketRows( GameState s )
		{
			var ranked = FishTable.ALL.Where( f => f.habitat.Length > 0 ).Select( f => ( id: f.id, mul: s.mulFor( f.id ) ) ).OrderByDescending( r => r.mul ).ToList();   // stable, like Array.sort
			var best = ranked.Where( r => r.mul > 1.001 ).Take( 3 ).ToList();
			var worst = ranked.Count > 0 ? ranked[ ranked.Count - 1 ] : ( id: ( string ) null, mul: 1.0 );
			if ( best.Count == 0 && ( worst.id == null || worst.mul >= 0.999 ) ) return new List<string>();
			var rows = best.Select( r => $"<b>{FishTable.Get( r.id ).name}</b> <c tone=\"aqua\">▲ {Inv( JS_Round( ( r.mul - 1 ) * 100 ) )}%</c>" ).ToList();
			if ( worst.id != null && worst.mul < 0.999 ) rows.Add( $"<b>{FishTable.Get( worst.id ).name}</b> <c tone=\"coral\">▼ {Inv( JS_Round( ( 1 - worst.mul ) * 100 ) )}%</c>" );
			return rows;
		}

		static double JS_Round( double x ) => Tidewater.Engine.JS.Round( x );
	}
}
