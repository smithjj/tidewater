using System;
using System.Collections.Generic;
using System.Linq;

// Port of src/game/Orders.js: Joe's order of the day. Each morning (from day 2) he is asking for one species, a decent size or bigger, and pays a
// multiplier on it for as long as the day lasts. Pure rules; the state keeps the day's order (and how it went) and the HUD shows it.
//
//   orderFor( day, lobster )   -> Order | null   the same every time for a given day
//   orderMul( order, fish )    -> ORDER_MULT when the fish is the order (right species, big enough), else 1
namespace Tidewater.Game
{
	public sealed class Order
	{
		public int day;
		public string species;
		public double minKg;
		public int filled;
		public double bonus;
	}

	public static class Orders
	{
		public const double ORDER_MULT = 1.25; // what Joe pays on a fish that fills the order, on top of the day's market rate
		public const int ORDER_FIRST_DAY = 2;  // day 1 pays the standard rate, as the market does

		// 0..1, the same for a given day and salt (no random state in the save, like the market roll)
		static double hash01( double day, double salt )
		{
			double x = Math.Sin( day * 78.233 + salt * 12.9898 ) * 43758.5453;
			return x - Math.Floor( x );
		}

		// a species worth asking for: a fish of typical size fetches at least this much, so the multiplier is worth something
		public const double ORDER_MIN_VALUE = 5;
		static double typicalValue( Fish f ) => f.price * ( f.kgMin + f.kgMax ) / 2;

		// what Joe can ask for: the species that take a hook and are worth something, and lobster once there is a trap licence to catch one.
		// Not the night biters (tarpon): Joe shuts at dusk, so the order would be gone by the time one could be sold.
		public static List<string> orderPool( bool lobster = false ) =>
			FishTable.ALL.Where( f => ( f.id == "lobster" ? lobster : f.habitat.Length > 0 ) && f.time != "night" && typicalValue( f ) >= ORDER_MIN_VALUE ).Select( f => f.id ).ToList();

		// the commoner a fish, the likelier the ask (lobster has no bite rarity: it is asked for about as seldom as the rarest)
		static double weight( string id ) => 0.4 + FishTable.Get( id ).rarity;

		static int pick( int day, List<string> pool )
		{
			double total = 0;
			foreach ( var id in pool ) total += weight( id );
			double r = hash01( day, 1 ) * total;
			for ( int i = 0; i < pool.Count; i ++ )
			{
				r -= weight( pool[ i ] );
				if ( r <= 0 ) return i;
			}

			return pool.Count - 1;
		}

		public static Order orderFor( double dayIn, bool lobster = false )
		{
			if ( ! ( dayIn >= ORDER_FIRST_DAY ) ) return null;
			int day = ( int ) Math.Floor( dayIn );
			var pool = orderPool( lobster );
			// each day's pick, nudged on when it is yesterday's species (the walk from day 2 keeps "yesterday" the actual order, so no species is
			// asked for two days running; it runs about once per game day)
			int i = 0, yesterday = - 1;
			for ( int d = ORDER_FIRST_DAY; d <= day; d ++ )
			{
				i = pick( d, pool );
				if ( i == yesterday ) i = ( i + 1 ) % pool.Count;
				yesterday = i;
			}

			string species = pool[ i ];
			// big enough to be worth asking for, small enough to turn up: 5-35% of the way up the species' size range
			var f = FishTable.Get( species );
			double lo = f.kgMin, hi = f.kgMax;
			double raw = lo + ( hi - lo ) * ( 0.05 + 0.3 * hash01( day, 2 ) );
			double step = raw < 1 ? 0.05 : raw < 5 ? 0.1 : 0.5;
			double minKg = Math.Min( hi, Math.Max( lo, Tidewater.Engine.JS.Round( raw / step ) * step ) );
			return new Order { day = day, species = species, minKg = Tidewater.Engine.JS.Round( minKg * 100 ) / 100 };
		}

		public static double orderMul( Order order, string species, double kg ) =>
			order != null && species != null && species == order.species && kg >= order.minKg - 1e-9 ? ORDER_MULT : 1;

		public static string fmtKg( double kg ) => kg < 1 ? kg.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture ) + " kg"
			: ( Tidewater.Engine.JS.Round( kg * 10 ) / 10 ).ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ) + " kg";
	}
}
