using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using TerrainData = Tidewater.World.TerrainData;

// The placement of the scattered rocks (src/world/Rocks.js _place): boulders and blocks on the rocky shores ( some half submerged ), talus below cliffs, outcrops breaking through the hillsides, rubble
// around the sea stacks, a few decorative rocks where the beach meets the headlands and low rocks on the seabed. The random numbers are drawn in the JS's order, so the instances are the same rocks.
namespace Tidewater.World.Rocks
{
	public sealed class RockInstance
	{
		public double x, y, z, size, sy, radius;
		public int style;
		public double[] matrix; // three's Matrix4 elements ( column major ): the rock's TRS, in the sim frame
	}

	public static class RockPlacement
	{
		public const uint SEED = 4242;

		sealed class Opts { public double sink = 0.3, tilt = 0.25, align = 0.5, tumble = 0; }

		public static List<RockInstance> Place( TerrainData T, Tidewater.World.Village.Village village, uint seed = SEED )
		{
			var rand = new Mulberry32( seed );
			var outList = new List<RockInstance>();
			var clusterNoise = new Noise2D( 913 );
			const double cell = 2.5;
			var grid = new Dictionary<long, List<double[]>>();
			Func<int, int, long> key = ( i, j ) => ( ( long ) i << 32 ) ^ ( uint ) j;
			Func<double, double, double, bool> free = ( x, z, r ) =>
			{
				int i0 = ( int ) Math.Floor( ( x - r - 6 ) / cell ), i1 = ( int ) Math.Floor( ( x + r + 6 ) / cell );
				int j0 = ( int ) Math.Floor( ( z - r - 6 ) / cell ), j1 = ( int ) Math.Floor( ( z + r + 6 ) / cell );
				for ( int j = j0; j <= j1; j ++ )
					for ( int i = i0; i <= i1; i ++ )
					{
						if ( ! grid.TryGetValue( key( i, j ), out var list ) ) continue;
						foreach ( var o in list ) if ( MathX.Hypot( o[ 0 ] - x, o[ 1 ] - z ) < ( o[ 2 ] + r ) * 0.9 ) return false;
					}

				return true;
			};
			Action<double, double, double> occupy = ( x, z, r ) =>
			{
				long k = key( ( int ) Math.Floor( x / cell ), ( int ) Math.Floor( z / cell ) );
				if ( ! grid.TryGetValue( k, out var list ) ) grid[ k ] = list = new List<double[]>();
				list.Add( new[] { x, z, r } );
			};
			Func<double, double, double> rockAt = ( x, z ) =>
			{
				double fx = ( x - T.origin ) / T.texel - 0.5, fz = ( z - T.origin ) / T.texel - 0.5;
				int i = Math.Max( 0, Math.Min( T.res - 2, ( int ) Math.Floor( fx ) ) ), j = Math.Max( 0, Math.Min( T.res - 2, ( int ) Math.Floor( fz ) ) );
				return T.rock[ j * T.res + i ];
			};
			Func<double, double, double> slopeAt = ( x, z ) =>
			{
				double e = 1.5;
				double hx = T.HeightAt( x + e, z ) - T.HeightAt( x - e, z ), hz = T.HeightAt( x, z + e ) - T.HeightAt( x, z - e );
				return MathX.Hypot( hx, hz ) / ( 2 * e );
			};

			// keep-out zones
			var foot = village != null ? village.getFootprints() : new List<Tidewater.World.Village.Footprint>();
			double pierX = WorldLayout.Pier.x, pierZStart = WorldLayout.Pier.zStart, pierZEnd = WorldLayout.Pier.zEnd;
			double spawnX = WorldLayout.SpawnPosition.x, spawnZ = WorldLayout.SpawnPosition.z;
			double reefX = WorldLayout.Reef.x, reefZ = WorldLayout.Reef.z, reefR = WorldLayout.Reef.radius;
			var walk = new[] { new[] { 55.0, - 65 }, new[] { 54.6, - 72 }, new[] { 52.4, - 82 }, new[] { 48.4, - 92 }, new[] { 44.8, - 100.5 }, new[] { 42.6, - 107.2 } };
			Func<double[][], double, double, double> segDist = ( pts, x, z ) =>
			{
				double best = double.PositiveInfinity;
				for ( int k = 0; k < pts.Length - 1; k ++ )
				{
					var a = pts[ k ]; var b = pts[ k + 1 ];
					double abx = b[ 0 ] - a[ 0 ], abz = b[ 1 ] - a[ 1 ];
					double t = Math.Max( 0, Math.Min( 1, ( ( x - a[ 0 ] ) * abx + ( z - a[ 1 ] ) * abz ) / ( abx * abx + abz * abz ) ) );
					best = Math.Min( best, MathX.Hypot( x - a[ 0 ] - abx * t, z - a[ 1 ] - abz * t ) );
				}

				return best;
			};
			Func<double, double, double, double, bool> blocked = ( x, z, r, h ) =>
			{
				foreach ( var f in foot ) if ( MathX.Hypot( x - f.x, z - f.z ) < f.r + r + 2 ) return true;
				if ( segDist( walk, x, z ) < r + 4 ) return true;
				if ( Math.Abs( x - pierX ) < r + 9 && z > pierZStart - 8 && z < pierZEnd + 12 ) return true;
				if ( MathX.Hypot( x - spawnX, z - spawnZ ) < r + 10 ) return true;
				if ( MathX.Hypot( x - reefX, z - reefZ ) < reefR + r + 12 ) return true;
				if ( T.PathDistance( x, z ) < r + 1.5 ) return true;
				// the sandy beach of the bay stays clear above the waterline ( a few rocks at its ends )
				if ( x > - 125 && x < 155 && z > - 125 && h > - 1.2 && z < 40 ) return true;
				return false;
			};

			var yAxis = new Vector3( 0, 1, 0 );
			Func<double, double, double, int, Opts, bool> add = ( x, z, size, style, o ) =>
			{
				double h = T.HeightAt( x, z );
				if ( blocked( x, z, size, h ) || ! free( x, z, size ) ) return false;
				// T.normalAt( x, z, n ): ( -hx, 2 texel, -hz ) normalized, in doubles
				double e = T.texel;
				double nhx = T.HeightAt( x + e, z ) - T.HeightAt( x - e, z ), nhz = T.HeightAt( x, z + e ) - T.HeightAt( x, z - e );
				var n = new Vector3( - nhx, 2 * e, - nhz ).normalize();
				var st = RockGeometry.STYLES[ style ].scale;
				double sx = size * ( 0.8 + 0.4 * rand.Next() ), sy = size * ( 0.75 + 0.5 * rand.Next() ), sz = size * ( 0.8 + 0.4 * rand.Next() );
				// sit on the lowest ground under the rock, sunk by a fraction of its height
				double g = h;
				for ( int a = 0; a < 6; a ++ ) g = Math.Min( g, T.HeightAt( x + Math.Cos( a ) * size * 0.7, z + Math.Sin( a ) * size * 0.7 ) );
				double y = g - sy * st[ 1 ] * o.sink;
				// orientation: random yaw, a random tilt, partly following the ground
				var q = new Quaternion().setFromAxisAngle( yAxis, rand.Next() * Math.PI * 2 );
				var tq = new Quaternion().setFromUnitVectors( yAxis, new Vector3( 0, 1, 0 ).lerp( n, o.align ).normalize() );
				double tt = rand.Next() < o.tumble ? 2.2 : o.tilt; // some boulders rolled over: any face may be up
				double ex = ( rand.Next() - 0.5 ) * tt, ez = ( rand.Next() - 0.5 ) * tt;
				var rq = new Quaternion().setFromEuler( new Euler( ex, 0, ez ) );
				q.premultiply( rq ).premultiply( tq );
				var m = new Matrix4().compose( new Vector3( x, y, z ), q, new Vector3( sx, sy, sz ) );
				outList.Add( new RockInstance { x = x, y = y, z = z, size = size, sy = sy / size, style = style, matrix = ( double[] ) m.elements.Clone(), radius = Math.Max( sx, Math.Max( sy, sz ) ) * 1.5 } );
				occupy( x, z, size );
				return true;
			};

			Func<double[], int> pick = weights =>
			{
				double s = 0;
				foreach ( var w in weights ) s += w;
				double r = rand.Next() * s;
				for ( int i = 0; i < weights.Length; i ++ )
				{
					r -= weights[ i ];
					if ( r <= 0 ) return i;
				}

				return weights.Length - 1;
			};
			Func<double, double, double, double, Opts> O = ( sink, tilt, align, tumble ) => new Opts { sink = sink, tilt = tilt, align = align, tumble = tumble };

			// ---- rubble and boulders around the sea stacks
			foreach ( var s in T.rockSites )
			{
				if ( s.kind != "stack" ) continue;
				int count = ( int ) MathX.Round( 6 + s.r * 1.2 );
				for ( int k = 0; k < count * 3 && k < 80; k ++ )
				{
					double a = rand.Next() * Math.PI * 2, d = s.r * ( 0.9 + rand.Next() * 1.4 );
					double sz = 0.8 + rand.Next() * rand.Next() * 3.5;
					int st = pick( new[] { 3.0, 3, 1, 1 } );
					add( s.x + Math.Cos( a ) * d, s.z + Math.Sin( a ) * d, sz, st, O( 0.25, 0.6, 0.5, 0.4 ) );
				}
			}

			// ---- grid scan: shore boulders, talus, hillside outcrops, headland scatter
			double step = 2.2;
			for ( double z = - 880; z < 330; z += step )
			{
				for ( double x = - 640; x < 640; x += step )
				{
					double jx = x + ( rand.Next() - 0.5 ) * step, jz = z + ( rand.Next() - 0.5 ) * step;
					double h = T.HeightAt( jx, jz );
					if ( h < - 7 || h > 250 ) continue;
					double r = rockAt( jx, jz );
					double u = rand.Next();

					if ( h < 3.5 )
					{
						// rocky shore: boulder fields in the splash zone, partially submerged, in clusters ( sparser toward the bay so its ends stay readable )
						if ( r < 0.45 ) continue;
						double cl = MathX.Smoothstep( - 0.05, 0.45, clusterNoise.Noise( jx / 23, jz / 23 ) );
						double bay = Math.Abs( jx - 10 ) < 210 && jz > - 130 && jz < 80 ? 0.35 : 1;
						double p = 0.09 * cl * bay * MathX.Smoothstep( 0.45, 0.8, r ) * ( h > - 2.5 ? 1 : 0.35 );
						if ( u > p ) continue;
						double size = 0.6 + rand.Next() * rand.Next() * 3.4;
						add( jx, jz, size, pick( new[] { 4.0, 3, 2, 1 } ), O( h < 0 ? 0.2 : 0.22, 0.7, 0.3, 0.45 ) );
						continue;
					}

					double slope = slopeAt( jx, jz );
					if ( r > 0.35 && slope < 0.75 )
					{
						// talus / outcrops where bare rock meets gentler ground
						double p = 0.03 * MathX.Smoothstep( 0.35, 0.7, r ) * ( 1 - MathX.Smoothstep( 0.5, 0.75, slope ) );
						if ( u > p ) continue;
						bool big = rand.Next() < 0.25;
						double size = big ? 1.6 + rand.Next() * 3.2 : 0.4 + rand.Next() * rand.Next() * 1.6;
						add( jx, jz, size, pick( big ? new[] { 1.0, 3, 2, 1 } : new[] { 4.0, 2, 1, 0.5 } ), O( big ? 0.45 : 0.3, 0.5, big ? 0.8 : 0.4, 0 ) );
					}
					else if ( r < 0.35 && slope > 0.25 && slope < 0.9 && h > 6 )
					{
						// occasional outcrops breaking through the vegetated slopes
						if ( u > 0.0016 ) continue;
						double size = 1.5 + rand.Next() * 3.5;
						add( jx, jz, size, pick( new[] { 1.0, 2, 3, 0.5 } ), O( 0.55, 0.3, 0.9, 0 ) );
					}
				}
			}

			// ---- where the beach meets the headlands: a few boulder groups against the rocky ends, each a big anchor rock with smaller ones tumbled around it
			foreach ( int side in new[] { - 1, 1 } )
			{
				var seeds = new List<double[]>();
				for ( int k = 0; k < 400 && seeds.Count < 3; k ++ )
				{
					double x = side < 0 ? - 195 + rand.Next() * 60 : 145 + rand.Next() * 60;
					double z = - 70 + rand.Next() * 90;
					double h = T.HeightAt( x, z );
					if ( h < - 1.5 || h > 2.5 || rockAt( x, z ) < 0.25 ) continue;
					bool near = false;
					foreach ( var q in seeds ) if ( MathX.Hypot( q[ 0 ] - x, q[ 1 ] - z ) < 14 ) { near = true; break; }
					if ( near ) continue;
					seeds.Add( new[] { x, z } );
				}

				foreach ( var c in seeds )
				{
					double cx = c[ 0 ], cz = c[ 1 ];
					double asz = 1.8 + rand.Next() * 1.4;
					add( cx, cz, asz, pick( new[] { 3.0, 2, 1, 0 } ), O( 0.3, 0.4, 0.4, 0.3 ) );
					int n = 3 + ( int ) Math.Floor( rand.Next() * 5 );
					for ( int k = 0; k < n * 3; k ++ )
					{
						double a = rand.Next() * Math.PI * 2, d = 2.5 + rand.Next() * 5;
						double x = cx + Math.Cos( a ) * d, z = cz + Math.Sin( a ) * d;
						double h = T.HeightAt( x, z );
						if ( h < - 2 || h > 3 ) continue;
						double sz = 0.4 + rand.Next() * rand.Next() * 1.4;
						add( x, z, sz, pick( new[] { 4.0, 2, 1, 0 } ), O( 0.2, 0.6, 0.4, 0.5 ) );
					}
				}
			}

			// ---- seabed: low algae-covered rocks in the rubble patches ( 2 - 10 m deep )
			if ( T.rubble != null )
			{
				int res = T.res;
				for ( double z = - 120; z < 360; z += 3 )
					for ( double x = - 420; x < 420; x += 3 )
					{
						double jx = x + ( rand.Next() - 0.5 ) * 3, jz = z + ( rand.Next() - 0.5 ) * 3;
						int i = ( int ) Math.Floor( jx - T.origin ), j = ( int ) Math.Floor( jz - T.origin );
						if ( i < 0 || j < 0 || i >= res || j >= res ) continue;
						if ( T.rubble[ j * res + i ] < 170 || rand.Next() > 0.12 ) continue;
						double h = T.HeightAt( jx, jz );
						if ( h > - 2 || h < - 10 ) continue;
						double sz = 0.45 + rand.Next() * rand.Next() * 1.1;
						add( jx, jz, sz, pick( new[] { 3.0, 3, 1, 0 } ), O( 0.4, 0.5, 0.6, 0.5 ) );
					}
			}

			return outList;
		}

		// the emergent rocks collide ( submerged ones would snag boats that the eye cannot see )
		public static void AddColliders( List<RockInstance> rocks, Colliders colliders )
		{
			foreach ( var r in rocks )
			{
				double top = r.y + r.size * r.sy * 0.8;
				if ( r.size < 0.9 || top < - 0.3 ) continue;
				colliders.addCylinder( r.x, r.z, r.size * 0.75, r.y - r.size * 0.5, top, "rock" );
			}
		}
	}
}
