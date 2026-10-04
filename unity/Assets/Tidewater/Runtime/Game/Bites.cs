using System;
using System.Collections;
using System.Collections.Generic;

// Port of src/game/Bites.js: where the bobber is and what lives there, and the bite model (which species, how big, how soon).
namespace Tidewater.Game
{
	// the weights per water type at a spot (they overlap: a spot can be both pier and bay), in the order the JS object lists them
	public sealed class Habitat : IEnumerable<KeyValuePair<string, double>>
	{
		public static readonly string[] KEYS = { "shallows", "reef", "pier", "bay", "deep" };
		public double shallows, reef, pier, bay, deep;

		public double this[ string key ]
		{
			get
			{
				switch ( key )
				{
					case "shallows": return shallows;
					case "reef": return reef;
					case "pier": return pier;
					case "bay": return bay;
					case "deep": return deep;
					default: return 0;
				}
			}
		}

		public IEnumerator<KeyValuePair<string, double>> GetEnumerator()
		{
			foreach ( var k in KEYS ) yield return new KeyValuePair<string, double>( k, this[ k ] );
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	// a shoal of one species under the bobber (Sonar.schoolBite) multiplies that species' weight
	public sealed class SpeciesBias { public string id; public double k; }

	public static class Bites
	{
		// depth: water depth under the bobber (m); reefDist: horizontal distance to the reef edge (m, < 0 inside it); pierDist: to the pier (piles / head)
		public static Habitat habitatAt( double depth, double reefDist, double pierDist )
		{
			var h = new Habitat();
			if ( depth < 0.25 ) return h; // on the sand
			h.shallows = smooth( 3.5, 1.0, depth );
			h.reef = smooth( 12, - 6, reefDist ) * smooth( 0.8, 2.5, depth );
			h.pier = smooth( 9, 2, pierDist ) * smooth( 0.6, 2, depth );
			h.bay = smooth( 1.5, 4, depth ) * ( 1 - smooth( 18, 30, depth ) );
			h.deep = smooth( 16, 28, depth );
			return h;
		}

		// 1 at the species' favourite time, less at others; hour 0..24
		public static double activity( string pref, double hour )
		{
			double dawn = Math.Exp( - ( Math.Pow( hour - 6.5, 2 ) ) / 2.5 ), dusk = Math.Exp( - ( Math.Pow( hour - 18.5, 2 ) ) / 2.5 );
			double night = hour < 5.5 || hour > 19.5 ? 1 : 0;
			double day = hour > 7 && hour < 18 ? 1 : 0.35;
			switch ( pref )
			{
				case "day": return 0.25 + 0.75 * day * ( 1 - night );
				case "dawnDusk": return 0.3 + 0.7 * Math.Max( dawn, dusk ) + 0.1 * day;
				case "night": return 0.15 + 0.85 * Math.Max( night, dusk * 0.8 );
				default: return 0.8 + 0.2 * Math.Max( dawn, dusk );
			}
		}

		// Weighted pick of the species that bites here; null when nothing lives here. rng: () => [0, 1)
		public static string pickSpecies( Habitat habitat, double hour, Func<double> rng, SpeciesBias bias = null )
		{
			double total = 0;
			var ids = FishTable.FISH_IDS;
			var w = new double[ ids.Length ];
			for ( int i = 0; i < ids.Length; i ++ )
			{
				var f = FishTable.Get( ids[ i ] );
				double hw = 0;
				foreach ( var hb in f.habitat ) hw += hb.w * habitat[ hb.key ];
				double x = hw * f.rarity * activity( f.time, hour );
				if ( bias != null && bias.id == ids[ i ] ) x *= bias.k;
				w[ i ] = x;
				total += x;
			}

			if ( total < 1e-4 ) return null;
			double r = rng() * total;
			for ( int i = 0; i < w.Length; i ++ )
			{
				r -= w[ i ];
				if ( r <= 0 ) return ids[ i ];
			}

			// rounding can leave `r` a hair above zero after the last subtraction: hand back the likeliest species here rather than the last in the
			// table (a trap-only species has no weight at all, and must never come back from a cast, whatever the arithmetic does)
			int best = 0;
			for ( int i = 1; i < w.Length; i ++ ) if ( w[ i ] > w[ best ] ) best = i;
			return ids[ best ];
		}

		// Weight in kg: skewed toward the small end (most fish are small, trophies are rare)
		public static double rollWeight( string id, Func<double> rng )
		{
			var f = FishTable.Get( id );
			double t = Math.Pow( rng(), 2.2 );
			return f.kgMin + ( f.kgMax - f.kgMin ) * t;
		}

		// Seconds until the next bite at this spot: richer water bites sooner, and a shoal under the bobber sooner still (school: 0..1, see
		// Sonar.schoolBite). Infinity when barren.
		public static double biteDelay( Habitat habitat, double hour, Func<double> rng, double school = 0 )
		{
			double rich = 0;
			foreach ( var kv in habitat ) rich += kv.Value;
			if ( rich < 0.05 ) return double.PositiveInfinity;
			// open water between the named habitats still has fish: never much slower than a poor spot
			rich = Math.Max( rich, 0.35 );
			double light = hour > 6 && hour < 19 ? 1 : 0.8;
			double mean = 8 / ( Math.Min( rich, 1.6 ) * light * ( 1 + 0.9 * school ) );
			return 2 + - Math.Log( 1 - rng() * 0.98 ) * mean * 0.6;
		}

		static double smooth( double e0, double e1, double x )
		{
			double t = Math.Min( 1, Math.Max( 0, ( x - e0 ) / ( e1 - e0 ) ) );
			return t * t * ( 3 - 2 * t );
		}
	}
}
