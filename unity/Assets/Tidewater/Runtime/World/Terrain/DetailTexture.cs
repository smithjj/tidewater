using System;
using Tidewater.Util;
using static Tidewater.Util.MathX;
using static Tidewater.World.Terrain.TerrainNoise;

// Port of src/world/terrain/DetailTextures.js: tileable procedural detail heights shared by the terrain and
// rock materials (RGBA8, mipmapped, repeat wrapping). One texture, four height fields:
//   R  rock:  fractured plates (warped Voronoi cracks) with chipped facets and grain
//   G  soil:  leaf litter / clumps / small stones
//   B  sand:  grains, shell grit and scattered pebbles
//   A  fbm:   smooth multi-octave noise (macro variation when sampled at large scales)
// Generated once on the CPU and cached. (The JS closures are small classes here.)
namespace Tidewater.World.Terrain
{
	public static class DetailTexture
	{
		public const int S = 512;

		// periodic gradient noise over a P x P lattice (precomputed gradients), roughly [-1, 1]
		sealed class PeriodicNoise
		{
			readonly int P;
			readonly float[] gx, gy;

			public PeriodicNoise( int P, int seed )
			{
				this.P = P;
				gx = new float[ P * P ]; gy = new float[ P * P ];
				for ( int j = 0; j < P; j ++ ) for ( int i = 0; i < P; i ++ )
				{
					double a = Hash2( i, j, seed ) * 6.2831853;
					gx[ j * P + i ] = ( float ) Math.Cos( a );
					gy[ j * P + i ] = ( float ) Math.Sin( a );
				}
			}

			public double At( double x, double y )
			{
				double xi = Math.Floor( x ), yi = Math.Floor( y );
				double xf = x - xi, yf = y - yi;
				int x0 = ( ( ( int ) xi % P ) + P ) % P, y0 = ( ( ( int ) yi % P ) + P ) % P;
				int x1 = x0 + 1 == P ? 0 : x0 + 1, y1 = y0 + 1 == P ? 0 : y0 + 1;
				int k00 = y0 * P + x0, k10 = y0 * P + x1, k01 = y1 * P + x0, k11 = y1 * P + x1;
				double a = gx[ k00 ] * xf + gy[ k00 ] * yf;
				double b = gx[ k10 ] * ( xf - 1 ) + gy[ k10 ] * yf;
				double c = gx[ k01 ] * xf + gy[ k01 ] * ( yf - 1 );
				double d = gx[ k11 ] * ( xf - 1 ) + gy[ k11 ] * ( yf - 1 );
				double u = xf * xf * xf * ( xf * ( xf * 6 - 15 ) + 10 );
				double v = yf * yf * yf * ( yf * ( yf * 6 - 15 ) + 10 );
				return ( a + ( b - a ) * u + ( c - a + ( a - b - c + d ) * u ) * v ) * 1.414;
			}
		}

		sealed class Fbm
		{
			readonly PeriodicNoise[] oct;

			public Fbm( int P, int octaves, int seed )
			{
				oct = new PeriodicNoise[ octaves ];
				for ( int o = 0; o < octaves; o ++ ) oct[ o ] = new PeriodicNoise( P << o, seed + o * 17 );
			}

			public double At( double x, double y )
			{
				double s = 0, amp = 1, norm = 0, f = 1;
				for ( int o = 0; o < oct.Length; o ++ )
				{
					s += oct[ o ].At( x * f, y * f ) * amp;
					norm += amp;
					amp *= 0.5;
					f *= 2;
				}

				return s / norm;
			}
		}

		// periodic Voronoi over P x P cells. Feature points are stored with a 2-cell apron so the
		// search never wraps indices; the 5x5 search (R = 2) makes F2 exact, R = 1 is enough for F1.
		sealed class Worley
		{
			readonly int Q;
			readonly float[] px, py, pid;
			// the result of the last query (the JS returns one shared object per closure)
			public double f1, edge, id, dx, dy;

			public Worley( int P, int seed )
			{
				Q = P + 4;
				px = new float[ Q * Q ]; py = new float[ Q * Q ]; pid = new float[ Q * Q ];
				for ( int j = 0; j < Q; j ++ ) for ( int i = 0; i < Q; i ++ )
				{
					int wi = ( i - 2 + P ) % P, wj = ( j - 2 + P ) % P;
					px[ j * Q + i ] = ( float ) ( Hash2( wi, wj, seed ) + i - 2 );
					py[ j * Q + i ] = ( float ) ( Hash2( wi, wj, seed + 1 ) + j - 2 );
					pid[ j * Q + i ] = ( float ) Hash2( wi, wj, seed + 2 );
				}
			}

			// f1: distance to the nearest site, edge: distance to the nearest cell border (exact, so
			// cracks keep a constant width even between two nearly coincident sites), id: cell hash
			public Worley At( double x, double y, int R = 2 )
			{
				// x, y in [0, P)
				int xi = ( int ) Math.Floor( x ), yi = ( int ) Math.Floor( y );
				double d1 = 99, cid = 0; int kn = 0;
				for ( int j = - 1; j <= 1; j ++ )
				{
					int row = ( yi + j + 2 ) * Q + 2 + xi;
					for ( int i = - 1; i <= 1; i ++ )
					{
						int k = row + i;
						double ddx = px[ k ] - x, ddy = py[ k ] - y;
						double d = ddx * ddx + ddy * ddy;
						if ( d < d1 )
						{
							d1 = d; cid = pid[ k ]; kn = k;
						}
					}
				}

				double e2 = 99;
				if ( R > 1 )
				{
					double ax = px[ kn ], ay = py[ kn ];
					for ( int j = - 2; j <= 2; j ++ )
					{
						int row = ( yi + j + 2 ) * Q + 2 + xi;
						for ( int i = - 2; i <= 2; i ++ )
						{
							int k = row + i;
							if ( k == kn ) continue;
							double bx = px[ k ], by = py[ k ];
							double ex = bx - ax, ey = by - ay;
							double el = Math.Sqrt( ex * ex + ey * ey );
							double e = ( ( ax + bx ) * 0.5 - x ) * ex / el + ( ( ay + by ) * 0.5 - y ) * ey / el;
							if ( e < e2 ) e2 = e;
						}
					}
				}

				f1 = Math.Sqrt( d1 ); edge = e2; id = cid;
				dx = x - px[ kn ]; dy = y - py[ kn ];
				return this;
			}
		}

		static byte[] cached;

		// RGBA8, S x S, row-major (row 0 = v 0)
		public static byte[] Get()
		{
			if ( cached != null ) return cached;

			var warpA = new Fbm( 4, 2, 11 ); var warpB = new Fbm( 4, 2, 23 );
			var rockCells = new Worley( 7, 101 ); var rockChips = new Worley( 23, 211 );
			var rockGrain = new Fbm( 16, 3, 307 );
			var leafCells = new Worley( 26, 401 ); var stoneCells = new Worley( 12, 601 );
			var soilFbm = new Fbm( 10, 3, 503 );
			var grainA = new PeriodicNoise( 180, 701 ); var grainB = new PeriodicNoise( 90, 709 );
			var pebbles = new Worley( 18, 801 );
			var macro = new Fbm( 4, 5, 907 );

			var data = new byte[ S * S * 4 ];
			for ( int j = 0; j < S; j ++ )
			{
				double v = ( double ) j / S;
				for ( int i = 0; i < S; i ++ )
				{
					double u = ( double ) i / S;
					int o = ( j * S + i ) * 4;

					// ---- rock: plates split by warped cracks, each plate offset, chipped edges, grain
					double wu = u + warpA.At( u * 4, v * 4 ) * 0.035, wv = v + warpB.At( u * 4, v * 4 ) * 0.035;
					wu -= Math.Floor( wu );
					wv -= Math.Floor( wv );
					// fractured faces: every block is a tilted facet (neighbours catch the light differently),
					// blocks are split into smaller chips. The per-cell tilt / offset fades to zero toward the
					// joints (bevelled edges) so the height stays continuous across cells: a step there would
					// light up as a bright line in the bump.
					var w = rockCells.At( wu * 7, wv * 7 );
					double ta = w.id * 40.0;
					double bevel = Smoothstep( 0.0, 0.2, w.edge );
					double facet = ( ( w.dx * Math.Cos( ta ) + w.dy * Math.Sin( ta ) ) * 0.5 + ( w.id - 0.5 ) * 0.22 ) * bevel;
					// joints: soft grooves, deeper for some blocks
					double joint = Smoothstep( 0.0, 0.1 + 0.08 * w.id, w.edge );
					var chips = rockChips.At( wu * 23, wv * 23, 2 );
					double tb = chips.id * 40.0;
					double chipB = Smoothstep( 0.0, 0.14, chips.edge );
					double chip = ( ( chips.dx * Math.Cos( tb ) + chips.dy * Math.Sin( tb ) ) * 0.22 + ( chips.id - 0.5 ) * 0.1 ) * chipB;
					double rg = rockGrain.At( u * 16, v * 16 );
					double r = ( 0.45 + facet + chip * 0.6 + 0.16 * rg ) * ( 0.66 + 0.34 * joint ) * ( 0.94 + 0.06 * chipB );

					// ---- soil / litter: flat leaf blobs, clumps and a few stones
					w = leafCells.At( u * 26, v * 26, 1 );
					double leaf = w.id < 0.6 ? ( 1 - Smoothstep( 0.1 + 0.15 * w.id, 0.45, w.f1 ) ) * ( 0.4 + 0.6 * w.id ) : 0;
					double clump = soilFbm.At( u * 10, v * 10 );
					var st = stoneCells.At( u * 12, v * 12, 1 );
					double stone = st.id < 0.15 ? ( 1 - Smoothstep( 0.06, 0.28, st.f1 ) ) : 0;
					double g = 0.34 + 0.3 * clump + 0.26 * leaf + 0.45 * stone;

					// ---- sand: fine grains, grit and sparse pebbles
					double grain = grainA.At( u * 180, v * 180 ) * 0.55 + grainB.At( u * 90, v * 90 ) * 0.45;
					var pw = pebbles.At( u * 18, v * 18, 1 );
					double pq = pw.f1 / ( 0.14 + 0.12 * pw.id * 5 );
					double pebble = pw.id < 0.2 ? Math.Max( 0, 1 - Math.Pow( pq, 2 ) ) : 0;
					double b = 0.45 + 0.2 * grain + 0.5 * Math.Sqrt( pebble );

					// ---- generic fbm
					double a = 0.5 + 0.55 * macro.At( u * 4, v * 4 );

					r = r < 0 ? 0 : r > 1 ? 1 : r;
					g = g < 0 ? 0 : g > 1 ? 1 : g;
					b = b < 0 ? 0 : b > 1 ? 1 : b;
					a = a < 0 ? 0 : a > 1 ? 1 : a;
					data[ o ] = ( byte ) ( r * 255 ); data[ o + 1 ] = ( byte ) ( g * 255 ); data[ o + 2 ] = ( byte ) ( b * 255 ); data[ o + 3 ] = ( byte ) ( a * 255 );
				}
			}

			cached = data;
			return data;
		}
	}
}
