using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tidewater.Engine;

// Port of src/game/Codex.js: the fish guide's knowledge rules, what the player has worked out about each species, from how many they have caught and
// sold. Pure functions over the fish table and a log entry (GameState.log[ species ]); the guide screen draws the result.
//
// Knowledge grows with catches (level 0..4) and the price with fish sold (0..3):
//
//   level 0  never caught: a silhouette
//   level 1  first one: the name, a rough size range (about a factor of two either way), where and when YOU caught it, a rough price
//   level 2  3 caught: the range tightens, its main habitat, how hard it fights, a coarse map of its water
//   level 3  6 caught: the range is close, all its habitats, when it bites, more of its story, a finer map
//   level 4  10 caught: the exact figures and the habitat map at full resolution
//
// A range never contradicts what you have already caught: it always includes your lightest and your best.
namespace Tidewater.Game
{
	// one remembered catch: the weight, when (day and hour), and where and in what water if it came from a spot
	public sealed class CatchRecord
	{
		public double kg; public int day; public double hour;
		public double? x, z; public string hab;
	}

	public sealed class FirstCatch { public int day; public double hour; }

	// a species' log entry (JS emptyEntry): the counts, the best and lightest, the first, the habitats and periods caught in, and what was sold
	public sealed class LogEntry
	{
		public int count;
		public double bestKg;
		public double? bestCm;
		public double minKg;
		public FirstCatch first;
		public List<CatchRecord> catches = new List<CatchRecord>();
		public Dictionary<string, int> habs = new Dictionary<string, int>();
		public Dictionary<string, int> periods = new Dictionary<string, int>();
		public int sold;
		public double earned;
	}

	public sealed class ValueRange { public double lo, hi; public bool exact; }
	public sealed class HabitatItem { public string key, label; public bool seen; public double? strength; }
	public sealed class HabitatKnowledge { public List<HabitatItem> list; public bool exact, none; }
	public sealed class TimeKnowledge { public List<string> seen; public string pref, text; }

	public sealed class Knowledge
	{
		public string id, name, sci, levelName, blurb;
		public int caught, level, soldLevel, sold, mapBlock;
		public int? toNext;
		public double earned;
		public ValueRange size, price;
		public HabitatKnowledge habitat;
		public TimeKnowledge time;
		public List<string> facts;
		public (double kg, double? cm)? best;
		public FirstCatch first;
		public List<CatchRecord> catches;
	}

	public sealed class MapBox { public double x0, z0, size; }

	public static class Codex
	{
		public const int CATCH_CAP = 40;                              // catches kept per species for the map and history (the counts keep going)
		public static readonly int[] LEVEL_AT = { 1, 3, 6, 10 };      // caught for knowledge level 1..4
		public static readonly int[] PRICE_LEVEL_AT = { 1, 3, 6 };    // fish sold for price level 1..3
		public static readonly string[] LEVEL_NAMES = { "Unknown", "Sighted", "Familiar", "Studied", "Mastered" };
		public static readonly int MAX_LEVEL = LEVEL_AT.Length;

		public static readonly Dictionary<string, string> HAB_LABEL = new Dictionary<string, string>
		{
			[ "shallows" ] = "Sandy shallows", [ "reef" ] = "Reef", [ "pier" ] = "Around the pier", [ "bay" ] = "Open bay", [ "deep" ] = "Deep water",
		};
		public static readonly Dictionary<string, string> PERIOD_LABEL = new Dictionary<string, string>
		{
			[ "dawn" ] = "dawn", [ "day" ] = "daytime", [ "dusk" ] = "dusk", [ "night" ] = "night",
		};

		public static int levelOf( int caught ) => LEVEL_AT.Count( n => caught >= n );
		public static int priceLevelOf( int sold ) => PRICE_LEVEL_AT.Count( n => sold >= n );
		// catches still needed for the next level, or null at the top
		public static int? catchesToNext( int caught )
		{
			foreach ( int n in LEVEL_AT ) if ( caught < n ) return n - caught;
			return null;
		}

		// ---- what a log entry records (GameState.addFish writes it, this module only reads it)
		public static LogEntry emptyEntry() => new LogEntry();

		public static string periodOf( double hour )
		{
			double h = ( ( hour % 24 ) + 24 ) % 24;
			return h >= 5 && h < 8 ? "dawn" : h >= 8 && h < 17 ? "day" : h >= 17 && h < 20 ? "dusk" : "night";
		}

		// the habitat type a spot mostly is (weights from Bites.habitatAt), or null on sand / when nothing stands out
		public static string dominantHabitat( IEnumerable<KeyValuePair<string, double>> weights )
		{
			string best = null; double bw = 0.15;
			foreach ( var kv in weights ) if ( kv.Value > bw ) { bw = kv.Value; best = kv.Key; }
			return best;
		}

		// ---- rounding a range outward to a number of significant figures (0 = exact)
		static double floorSig( double x, int sig )
		{
			if ( sig == 0 || ! ( x > 0 ) ) return x;
			double m = Math.Pow( 10, Math.Floor( Math.Log10( x ) ) - ( sig - 1 ) );
			return Math.Floor( x / m + 1e-9 ) * m;
		}

		static double ceilSig( double x, int sig )
		{
			if ( sig == 0 || ! ( x > 0 ) ) return x;
			double m = Math.Pow( 10, Math.Floor( Math.Log10( x ) ) - ( sig - 1 ) );
			return Math.Ceiling( x / m - 1e-9 ) * m;
		}

		// Number( x.toPrecision( 6 ) )
		static double clean( double x ) => double.Parse( x.ToString( "E5", CultureInfo.InvariantCulture ), CultureInfo.InvariantCulture );

		// ---- size (kg): wider than the truth until you have caught enough of them
		static readonly double[] SIZE_SPREAD = { double.PositiveInfinity, 2.2, 1.6, 1.25, 1 };
		static readonly int[] SIZE_SIG = { 0, 1, 1, 2, 0 };

		public static ValueRange sizeKnown( string id, int level, LogEntry entry = null )
		{
			if ( level < 1 ) return null;
			var f = FishTable.Get( id );
			double w = SIZE_SPREAD[ level ];
			double lo = f.kgMin / w, hi = f.kgMax * w;
			if ( level < MAX_LEVEL && entry != null )
			{
				if ( entry.minKg > 0 ) lo = Math.Min( lo, entry.minKg );
				if ( entry.bestKg > 0 ) hi = Math.Max( hi, entry.bestKg );
			}

			int sig = SIZE_SIG[ level ];
			return new ValueRange { lo = clean( floorSig( lo, sig ) ), hi = clean( ceilSig( hi, sig ) ), exact = level >= MAX_LEVEL };
		}

		// ---- price ($ per kg at the stand): from fish sold, not from the fish you merely caught
		static readonly double[] PRICE_SPREAD = { 2.5, 1.6, 1.25, 1 };

		public static ValueRange priceKnown( string id, int level, int soldLevel )
		{
			if ( level < 1 ) return null;
			double p = FishTable.Get( id ).price;
			double w = PRICE_SPREAD[ soldLevel ];
			if ( soldLevel >= 3 ) return new ValueRange { lo = p, hi = p, exact = true };
			// 1 figure with no sales at all, 2 after the first, whole dollars after three
			int sig = soldLevel >= 2 ? 0 : soldLevel == 1 ? 2 : 1;
			double lo = p / w, hi = p * w;
			lo = sig != 0 ? floorSig( lo, sig ) : Math.Floor( lo );
			hi = sig != 0 ? ceilSig( hi, sig ) : Math.Ceiling( hi );
			return new ValueRange { lo = Math.Max( 1, clean( lo ) ), hi = clean( hi ), exact = false };
		}

		// ---- where it lives: what you found it in, then what you have worked out
		public static HabitatKnowledge habitatKnown( string id, int level, LogEntry entry = null )
		{
			if ( level < 1 ) return null;
			var table = FishTable.Get( id ).habitat;
			double W( string k ) => table.FirstOrDefault( h => h.key == k ).w;
			var observed = entry != null ? entry.habs.Where( kv => kv.Value > 0 ).Select( kv => kv.Key ).ToList() : new List<string>();
			var keys = new List<string>( observed.Distinct() );   // a Set: insertion order, no repeats
			void Add( string k ) { if ( ! keys.Contains( k ) ) keys.Add( k ); }
			var ranked = table.Select( h => h.key ).OrderByDescending( W ).ToList();   // stable, like Array.sort
			if ( level >= 2 && ranked.Count > 0 ) Add( ranked[ 0 ] );
			if ( level >= 3 ) foreach ( var k in ranked ) if ( W( k ) >= 0.3 ) Add( k );
			if ( level >= MAX_LEVEL ) foreach ( var k in ranked ) Add( k );
			var list = keys.OrderByDescending( W )
				.Select( key => new HabitatItem { key = key, label = HAB_LABEL.TryGetValue( key, out var l ) ? l : key, seen = observed.Contains( key ), strength = level >= MAX_LEVEL ? W( key ) : ( double? ) null } ).ToList();
			return new HabitatKnowledge { list = list, exact = level >= MAX_LEVEL, none = ranked.Count == 0 };
		}

		// ---- when it bites: when you caught it, then what the table says
		static readonly Dictionary<string, string> TIME_TEXT = new Dictionary<string, string>
		{
			[ "night" ] = "Bites at night only",
			[ "dawnDusk" ] = "Bites best at dawn and dusk",
			[ "day" ] = "A daytime feeder",
			[ "any" ] = "Bites at any hour",
		};

		public static TimeKnowledge timeKnown( string id, int level, LogEntry entry = null )
		{
			if ( level < 1 ) return null;
			var periods = new[] { "dawn", "day", "dusk", "night" }.Where( p => entry != null && entry.periods.TryGetValue( p, out var n ) && n > 0 ).ToList();
			string t = FishTable.Get( id ).time;
			return new TimeKnowledge { seen = periods, pref = level >= 3 ? t : null, text = level >= 3 && TIME_TEXT.TryGetValue( t, out var s ) ? s : null };
		}

		// ---- the story: a line of flavour, and facts that come out as you learn the fish
		static readonly Dictionary<string, string> BLURB = new Dictionary<string, string>
		{
			[ "silverside" ] = "Tiny schooling baitfish that glitter in the shallows. Everything bigger eats them.",
			[ "mullet" ] = "Grazes the sandy bottom and leaps clear of the water when startled.",
			[ "needlefish" ] = "A long, toothy surface hunter that skims and leaps after small fish.",
			[ "sergeant" ] = "A bold, banded damselfish that crowds around pilings and reef edges.",
			[ "grunt" ] = "Grinds its teeth to make the sound it is named for. Schools in the shade of piles and reef.",
			[ "yellowtail" ] = "A slim snapper with a bright yellow stripe and tail, hunting above the reef.",
			[ "chromis" ] = "A small, electric-blue reef fish that feeds in clouds above the coral.",
			[ "tang" ] = "A blue surgeonfish with a scalpel-sharp spine at the base of its tail. It grazes the reef.",
			[ "wrasse" ] = "A pig-snouted wrasse that roots shellfish out of the sand. Prized at the table.",
			[ "parrot" ] = "Crunches coral with its beak-like teeth, and sleeps in a cocoon of its own mucus at night.",
			[ "angel" ] = "A regal reef angelfish with a crown-like marking on its forehead.",
			[ "jack" ] = "A powerful, hard-running predator that hunts in packs around the bay.",
			[ "barracuda" ] = "A silver ambusher with a mouthful of fangs. It strikes at flash and glitter.",
			[ "grouper" ] = "A big, long-lived grouper that ambushes from caves. Slow-growing, and protected in many places.",
			[ "redSnapper" ] = "A deep-water snapper, red from head to tail, that keeps to ledges and wrecks.",
			[ "tuna" ] = "A small, fast, warm-blooded tuna that chases baitfish offshore.",
			[ "mahi" ] = "Brilliant gold and blue, always on the move. It follows floating debris offshore.",
			[ "tarpon" ] = "The silver king: a huge, armoured fish that leaps and runs. It hunts in the dark.",
			[ "lobster" ] = "A clawless spiny lobster that hides in crevices by day. It only walks into pots.",
		};

		// the facts unlocked at this level, in the order they came out
		public static List<string> factsFor( string id, int level )
		{
			var f = FishTable.Get( id );
			var o = new List<string>();
			void add( int at, string text ) { if ( level >= at ) o.Add( text ); }
			if ( f.kind == "lobster" || f.habitat.Length == 0 ) add( 1, "Never takes a hook: it only comes up in a pot" );
			add( 2, f.fight >= 0.8 ? "Fights ferociously: long runs, and it takes a lot to tire" : f.fight >= 0.55 ? "A strong fighter" : f.fight <= 0.2 ? "Gives up without much of a fight" : "A fair fight on light gear" );
			if ( f.time == "night" ) add( 3, "Joe is shut by the time it bites: sell it the next day" );
			add( 3, f.rarity > 0 && f.rarity <= 0.3 ? "Uncommon: it takes patience to find one" : f.rarity >= 0.9 ? "Common: you will not go long without one" : "Neither common nor rare" );
			if ( f.kind == "lobster" ) add( 3, "Pots do best in 2–35 m of water: shallower and deeper ground holds fewer" );
			if ( f.price >= 15 ) add( 3, "Fetches a good price for its weight" );
			else if ( f.price <= 5 ) add( 3, "Worth little per kg: only the big ones pay" );
			return o;
		}

		public static string blurbFor( string id ) => BLURB.TryGetValue( id, out var s ) ? s : "";

		// ---- the habitat map: how well a spot suits a species, and a version of it blurred by how little is known
		// `sample( x, z )` returns the habitat weights at a point (Bites.habitatAt) or null where there is no water.
		public static double suitability( string id, IReadOnlyDictionary<string, double> weights )
		{
			if ( weights == null ) return 0;
			double s = 0;
			foreach ( var h in FishTable.Get( id ).habitat ) s += h.w * ( weights.TryGetValue( h.key, out var w ) ? w : 0 );
			return Math.Min( 1, s );
		}

		// an n x n grid over the square { x0, z0, size } (row-major, z down), 0..1
		public static float[] habitatGrid( string id, MapBox box, int n, Func<double, double, IReadOnlyDictionary<string, double>> sample )
		{
			var g = new float[ n * n ];
			double cell = box.size / n;
			for ( int j = 0; j < n; j ++ ) for ( int i = 0; i < n; i ++ )
				g[ j * n + i ] = ( float ) suitability( id, sample( box.x0 + ( i + 0.5 ) * cell, box.z0 + ( j + 0.5 ) * cell ) );
			return g;
		}

		// cells per block the map is drawn in at each level (0: no map). The finest is the grid itself.
		public static readonly int[] MAP_BLOCK = { 0, 0, 12, 4, 1 };

		// average the grid over block x block cells and paint the average back, so a low-level map is blocky
		public static float[] coarsen( float[] grid, int n, int block )
		{
			if ( block <= 1 ) return grid;
			var o = new float[ n * n ];
			for ( int bj = 0; bj < n; bj += block ) for ( int bi = 0; bi < n; bi += block )
			{
				float sum = 0; int cnt = 0;
				for ( int j = bj; j < Math.Min( n, bj + block ); j ++ ) for ( int i = bi; i < Math.Min( n, bi + block ); i ++ ) { sum += grid[ j * n + i ]; cnt ++; }
				float v = sum / cnt;
				for ( int j = bj; j < Math.Min( n, bj + block ); j ++ ) for ( int i = bi; i < Math.Min( n, bi + block ); i ++ ) o[ j * n + i ] = v;
			}

			return o;
		}

		// the square on the map (world metres) that frames your catches, or a default around the pier head
		public static MapBox mapBox( IEnumerable<CatchRecord> catches, double extent = 1280, double x0 = - 640, double z0 = - 820, double homeX = 55, double homeZ = 20, double min = 220 )
		{
			double loX = double.PositiveInfinity, loZ = double.PositiveInfinity, hiX = double.NegativeInfinity, hiZ = double.NegativeInfinity;
			foreach ( var c in catches ?? new CatchRecord[ 0 ] )
			{
				if ( c.x == null || c.z == null ) continue;
				loX = Math.Min( loX, c.x.Value ); loZ = Math.Min( loZ, c.z.Value );
				hiX = Math.Max( hiX, c.x.Value ); hiZ = Math.Max( hiZ, c.z.Value );
			}

			if ( double.IsPositiveInfinity( loX ) ) { loX = hiX = homeX; loZ = hiZ = homeZ; }
			double cx = ( loX + hiX ) / 2, cz = ( loZ + hiZ ) / 2;
			double size = Math.Min( extent, Math.Max( min * 2, Math.Max( hiX - loX, hiZ - loZ ) * 1.35 + 120 ) );
			// keep the box inside the baked map
			double bx = Math.Min( x0 + extent - size, Math.Max( x0, cx - size / 2 ) );
			double bz = Math.Min( z0 + extent - size, Math.Max( z0, cz - size / 2 ) );
			return new MapBox { x0 = bx, z0 = bz, size = size };
		}

		// ---- everything the guide shows for one species, in one object
		public static Knowledge knowledge( string id, LogEntry entry = null )
		{
			var f = FishTable.Get( id );
			if ( f == null ) return null;
			var e = entry ?? emptyEntry();
			int caught = e.count;
			int level = levelOf( caught );
			int soldLevel = priceLevelOf( e.sold );
			return new Knowledge
			{
				id = id, name = level >= 1 ? f.name : "???", sci = level >= 1 ? f.sci : "",
				caught = caught, level = level, levelName = LEVEL_NAMES[ level ], toNext = catchesToNext( caught ),
				sold = e.sold, soldLevel = soldLevel, earned = e.earned,
				blurb = level >= 1 ? blurbFor( id ) : "",
				size = sizeKnown( id, level, e ), price = priceKnown( id, level, soldLevel ),
				habitat = habitatKnown( id, level, e ), time = timeKnown( id, level, e ), facts = factsFor( id, level ),
				best = caught > 0 ? ( e.bestKg, e.bestCm ) : ( ( double, double? )? ) null,
				first = e.first, catches = e.catches, mapBlock = MAP_BLOCK[ level ],
			};
		}

		public static string[] GUIDE_IDS => FishTable.FISH_IDS;

		// ---- text for the numbers
		static string str( double x ) => x.ToString( "R", CultureInfo.InvariantCulture );
		static string num( double n ) => n < 1 ? str( clean( JS.Round( n * 100 ) / 100 ) ) : n < 10 ? str( clean( JS.Round( n * 10 ) / 10 ) ) : str( JS.Round( n ) );

		public static string sizeText( ValueRange s ) => s == null ? "" : $"{( s.exact ? "" : "~" )}{num( s.lo )}–{num( s.hi )} kg";

		public static string priceText( ValueRange p )
		{
			if ( p == null ) return "";
			return p.exact ? $"${str( p.lo )}/kg" : $"~${str( p.lo )}–${str( p.hi )}/kg";
		}
	}
}
