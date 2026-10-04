using System;
using System.Collections.Generic;
using Tidewater.World.Fish;

// Port of src/game/Sonar.js: the fish finder's reading: the schools the reef is actually simulating (World/Fish/FishSchools.cs), not the habitat
// model it used to sample. Given the FishSchools group list and a point, it says which schools are in range, where they are, how deep and how many
// fish, nearest first.
//
// Pure (no scene, no GPU) so the reading can be tested on its own: the groups are read, never touched. FishSchools freezes groups beyond SIM_RANGE
// but keeps their positions, so a frozen school is still reported; it just is not swimming.
namespace Tidewater.Game
{
	public sealed class SchoolReading
	{
		public double x, z, depth, count, radius, dist;
		public string model, name;
	}

	// the shoal a cast is sitting on, as an influence the bite model can use
	public sealed class SchoolBiteInfo
	{
		public string model, name;
		public double dist, count, influence, bite, bias;
	}

	public static class Sonar
	{
		// the whale's escorts sit at a seed point until a whale exists (Fish.js attachEscort), so they are not fish to be found yet
		static readonly HashSet<string> SKIP = new HashSet<string> { "escort", "remora" };

		public const double SONAR_RANGE = 65; // m: the simulation's own range (Fish.js SIM_RANGE): what is being simulated
		public const double SONAR_FULL = 250; // fish in range that fill the HUD gauge

		// Every school within r of ( x, z ), nearest first. max > 0 returns at most that many, skipping any that comes closer than minSep to one
		// already picked (so two rings on the map do not sit on top of each other); max = 0 returns all of them.
		public static List<SchoolReading> fishNear( IList<FishGroup> groups, double x, double z, double r, int max = 0, double minSep = 0 )
		{
			var @out = new List<SchoolReading>();
			if ( groups == null ) return @out;
			foreach ( var g in groups )
			{
				if ( g == null || g.center == null ) continue;
				if ( g.sp != null && SKIP.Contains( g.sp.mode ) ) continue;
				double d = Tidewater.Engine.JS.Hypot( g.center.x - x, g.center.z - z );
				if ( d > r ) continue;
				var fish = g.sp != null && g.sp.model != null ? FishTable.Get( g.sp.model ) : null;
				@out.Add( new SchoolReading
				{
					x = g.center.x, z = g.center.z,
					depth = Math.Max( 0, - g.center.y ), // below the surface, metres
					count = g.count,
					radius = g.radius,
					dist = d,
					// FishTable is keyed by model and holds only catchable species, so a ray or turtle falls back to its behaviour name
					model = g.sp != null ? g.sp.model : null, // the FishTable key: what a cast from here could catch
					name = fish != null ? fish.name : g.sp != null && ! string.IsNullOrEmpty( g.sp.name ) ? g.sp.name : "fish",
				} );
			}

			// a stable sort by distance (Array.prototype.sort is stable)
			var order = new List<KeyValuePair<int, SchoolReading>>();
			for ( int i = 0; i < @out.Count; i ++ ) order.Add( new KeyValuePair<int, SchoolReading>( i, @out[ i ] ) );
			order.Sort( ( a, b ) => { int c = a.Value.dist.CompareTo( b.Value.dist ); return c != 0 ? c : a.Key.CompareTo( b.Key ); } );
			@out = order.ConvertAll( kv => kv.Value );
			if ( max == 0 ) return @out;
			var picked = new List<SchoolReading>();
			foreach ( var q in @out )
			{
				if ( minSep > 0 && picked.Exists( o => Tidewater.Engine.JS.Hypot( o.x - q.x, o.z - q.z ) < minSep ) ) continue;
				picked.Add( q );
				if ( picked.Count >= max ) break;
			}

			return picked;
		}

		// The shoal a cast is sitting on, as an influence the bite model can use: the nearest school within r, scaled by how close it is (1 under the
		// shoal, 0 at the edge) and how many fish are in it (a pair of grouper is not a bait ball). Null when there is nothing there. This is what
		// makes casting into a school you can see - or found with the finder - actually pay.
		public static SchoolBiteInfo schoolBite( IList<FishGroup> groups, double x, double z, double r = 12 )
		{
			var list = fishNear( groups, x, z, r, 1 );
			if ( list.Count == 0 ) return null;
			var near = list[ 0 ];
			double closeness = 1 - near.dist / r;
			double size = Math.Min( 1, Math.Max( 0.35, near.count / 40 ) );
			return new SchoolBiteInfo
			{
				model = near.model, name = near.name, dist = near.dist, count = near.count,
				influence = closeness * size, // 0..1, for biteDelay
				bite = 1 + 0.9 * closeness * size, // the rate the wait is divided by
				bias = 1 + 2.5 * closeness * size, // the weight its species is multiplied by
			};
		}
	}
}
