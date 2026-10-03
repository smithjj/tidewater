using System;
using static Tidewater.Util.MathX;

// Port of src/world/terrain/TerrainBake.js: CPU bakes of the terrain maps sampled by the terrain material
// (and the water):
//   normal RGBA8: nx * 0.5 + 0.5, nz * 0.5 + 0.5, rock mask, ambient occlusion
//   splat  RGBA8: loose sand, worn paths / trampled ground, gullies (land) or seagrass
//                 meadows (seabed), coral rubble / rock heads on the seabed
// The ambient occlusion combines a horizon-based term (4 m grid, 8 directions, up to ~110 m)
// with a small-scale cavity term from the height Laplacian.
// (buildShadowHeights, the input of the heightfield sun shadow, is not ported yet.)
namespace Tidewater.World.Terrain
{
	public static class TerrainBake
	{
		static readonly double[] AO_STEPS = { 4, 8, 13, 19, 27, 38, 52, 72, 100 };
		const int AO_DIRS = 8;

		public sealed class Maps
		{
			public byte[] normal, splat;
			public double aoMs, mapsMs;
		}

		public static Maps BakeTerrainMaps( TerrainData terrain )
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			int res = terrain.res;
			var H = terrain.heights; var rock = terrain.rock;
			double texel = terrain.texel, origin = terrain.origin;

			// ---- horizon AO on a coarse grid
			const int F = 4; // texels per AO cell
			int N = res / F;
			double cell = texel * F;
			var hc = new float[ N * N ];
			for ( int j = 0; j < N; j ++ ) for ( int i = 0; i < N; i ++ )
			{
				double s = 0;
				for ( int b = 0; b < F; b ++ )
				{
					int row = ( j * F + b ) * res + i * F;
					for ( int a = 0; a < F; a ++ ) s += H[ row + a ];
				}

				hc[ j * N + i ] = ( float ) ( s / ( F * F ) );
			}

			var ao = new float[ N * N ];
			for ( int i = 0; i < ao.Length; i ++ ) ao[ i ] = 1;
			var dirs = new double[ AO_DIRS ][];
			for ( int d = 0; d < AO_DIRS; d ++ )
			{
				double a = ( d + 0.5 ) / AO_DIRS * Math.PI * 2;
				dirs[ d ] = new[] { Math.Cos( a ), Math.Sin( a ) };
			}

			for ( int j = 1; j < N - 1; j ++ ) for ( int i = 1; i < N - 1; i ++ )
			{
				double h0 = hc[ j * N + i ];
				if ( h0 < - 25 ) continue;
				double vis = 0;
				for ( int d = 0; d < AO_DIRS; d ++ )
				{
					double dx = dirs[ d ][ 0 ], dz = dirs[ d ][ 1 ];
					double maxT = 0;
					for ( int s = 0; s < AO_STEPS.Length; s ++ )
					{
						double dist = AO_STEPS[ s ];
						int x = ( int ) Round( i + dx * dist / cell ), z = ( int ) Round( j + dz * dist / cell );
						if ( x < 0 || z < 0 || x >= N || z >= N ) break;
						double t = ( hc[ z * N + x ] - h0 - 0.3 ) / dist;
						if ( t > maxT ) maxT = t;
					}

					vis += 1 / ( 1 + maxT * maxT ); // cos^2 of the horizon elevation
				}

				ao[ j * N + i ] = ( float ) ( vis / AO_DIRS );
			}

			double t1ms = sw.Elapsed.TotalMilliseconds;

			// ---- per texel maps
			var normal = new byte[ res * res * 4 ];
			var splat = new byte[ res * res * 4 ];
			var sand = terrain.sand; var path = terrain.path; var gully = terrain.gully;
			var seagrass = terrain.seagrass; var rubble = terrain.rubble; var scarp = terrain.scarp;
			double inv2t = 1 / ( 2 * texel );
			for ( int j = 0; j < res; j ++ )
			{
				int jm = j > 0 ? j - 1 : 0, jp = j < res - 1 ? j + 1 : res - 1;
				int j2m = j > 1 ? j - 2 : 0, j2p = j < res - 2 ? j + 2 : res - 1;
				// AO grid coordinate (cell centres at (i + 0.5) * F texels)
				double fz = ( j + 0.5 ) / F - 0.5;
				fz = fz < 0 ? 0 : fz > N - 1.001 ? N - 1.001 : fz;
				int az = ( int ) fz; double tz = fz - az;
				for ( int i = 0; i < res; i ++ )
				{
					int k = j * res + i;
					int im = i > 0 ? i - 1 : 0, ip = i < res - 1 ? i + 1 : res - 1;
					double hx = ( ( double ) H[ j * res + ip ] - H[ j * res + im ] ) * inv2t;
					double hz = ( ( double ) H[ jp * res + i ] - H[ jm * res + i ] ) * inv2t;
					double il = 1 / Math.Sqrt( hx * hx + 1 + hz * hz );
					double nx = - hx * il, nz = - hz * il;

					// cavity from the Laplacian over a 2 m baseline (negative = convex)
					int i2m = i > 1 ? i - 2 : 0, i2p = i < res - 2 ? i + 2 : res - 1;
					double h = H[ k ];
					double lap = ( ( double ) H[ j * res + i2m ] + H[ j * res + i2p ] + H[ j2m * res + i ] + H[ j2p * res + i ] - 4 * h ) * 0.25;
					double cav = 0.5 + lap * 0.9;
					cav = cav < 0 ? 0 : cav > 1 ? 1 : cav;

					double fx = ( i + 0.5 ) / F - 0.5;
					fx = fx < 0 ? 0 : fx > N - 1.001 ? N - 1.001 : fx;
					int ax = ( int ) fx; double tx = fx - ax;
					int q = az * N + ax;
					double aoL = ( ao[ q ] * ( 1 - tx ) + ao[ q + 1 ] * tx ) * ( 1 - tz ) + ( ao[ q + N ] * ( 1 - tx ) + ao[ q + N + 1 ] * tx ) * tz;
					double aoT = aoL * ( 1 - Math.Max( 0, cav - 0.5 ) * 0.7 );
					aoT = aoT < 0 ? 0 : aoT;

					int o = k * 4;
					normal[ o ] = ( byte ) ( ( nx * 0.5 + 0.5 ) * 255 + 0.5 );
					normal[ o + 1 ] = ( byte ) ( ( nz * 0.5 + 0.5 ) * 255 + 0.5 );
					normal[ o + 2 ] = ( byte ) ( rock[ k ] * 255 + 0.5 );
					normal[ o + 3 ] = ( byte ) ( aoT * 255 + 0.5 );
					splat[ o ] = sand[ k ];
					splat[ o + 1 ] = path[ k ];
					splat[ o + 2 ] = Math.Max( gully[ k ], seagrass[ k ] );
					// alpha: seabed rubble below the sea, the eroded beach scarp face on land
					splat[ o + 3 ] = Math.Max( rubble[ k ], scarp[ k ] );
				}
			}

			// trampled ground around the building pads carved by the village
			foreach ( var p in terrain.pads )
			{
				double R = p.radius + 3.5;
				int i0 = ( int ) Math.Max( 0, Math.Floor( ( p.x - R - origin ) / texel ) ), i1 = ( int ) Math.Min( res - 1, Math.Ceiling( ( p.x + R - origin ) / texel ) );
				int j0 = ( int ) Math.Max( 0, Math.Floor( ( p.z - R - origin ) / texel ) ), j1 = ( int ) Math.Min( res - 1, Math.Ceiling( ( p.z + R - origin ) / texel ) );
				for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
				{
					double x = origin + ( i + 0.5 ) * texel, z = origin + ( j + 0.5 ) * texel;
					double d = Hypot( x - p.x, z - p.z );
					double a = Math.Atan2( z - p.z, x - p.x );
					// ragged edge: worn patches reach out unevenly around each building
					double reach = 1.2 + 1.8 * ( 0.5 + 0.5 * Math.Sin( a * 3 + p.x ) * Math.Sin( a * 5 - p.z ) );
					double t = d < p.radius ? 1 : Math.Max( 0, 1 - ( d - p.radius ) / reach );
					int o = ( j * res + i ) * 4 + 1;
					int v = ( int ) Round( t * t * 150 );
					if ( v > splat[ o ] ) splat[ o ] = ( byte ) v;
				}
			}

			return new Maps { normal = normal, splat = splat, aoMs = t1ms, mapsMs = sw.Elapsed.TotalMilliseconds - t1ms };
		}
	}
}
