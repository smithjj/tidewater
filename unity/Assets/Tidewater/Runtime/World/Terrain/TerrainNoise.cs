using System;
using Tidewater.Util;

// Port of src/world/terrain/TerrainNoise.js: fast CPU helpers for terrain generation: integer hashes,
// sin/cos, slope-aligned erosion noise, separable resampling and blurs over float grids.
namespace Tidewater.World.Terrain
{
	public static class TerrainNoise
	{
		// integer lattice hash -> [0, 1)
		public static double Hash2( int i, int j, int s )
		{
			unchecked
			{
				int h = MathX.Imul( i, 374761393 ) + MathX.Imul( j, 668265263 ) + MathX.Imul( s, 1274126177 );
				h = MathX.Imul( h ^ ( int ) ( ( uint ) h >> 13 ), 1274126177 );
				h ^= ( int ) ( ( uint ) h >> 16 );
				return ( uint ) h / 4294967296.0;
			}
		}

		// 0 below 0, quadratic fillet up to 2k, then linear (x - k)
		public static double SoftRamp( double x, double k ) => x <= 0 ? 0 : x < 2 * k ? x * x / ( 4 * k ) : x - k;

		const double TAU = Math.PI * 2;

		// cos / sin with ~1e-4 absolute error (range reduced polynomial)
		static void SinCos( double a, out double cos, out double sin )
		{
			a -= TAU * MathX.Round( a / TAU ); // [-pi, pi]
			// cos via even polynomial on [-pi, pi] using half angle: cos a = 1 - 2 sin^2(a/2)
			double h = a * 0.5; // [-pi/2, pi/2]
			double h2 = h * h;
			// sin(h) minimax-ish (Taylor to h^9 is accurate to 4e-6 on [-pi/2, pi/2])
			double s = h * ( 1 - h2 * ( 1.0 / 6 - h2 * ( 1.0 / 120 - h2 * ( 1.0 / 5040 - h2 / 362880 ) ) ) );
			double c = 1 - h2 * ( 0.5 - h2 * ( 1.0 / 24 - h2 * ( 1.0 / 720 - h2 / 40320 ) ) );
			cos = 1 - 2 * s * s;
			sin = 2 * s * c;
		}

		// Slope-aligned gully noise (after Clay John's "eroded terrain noise"). Each octave sums
		// windowed plane waves oriented along the local fall line; the gradient of the octaves so far
		// bends the next ones so the gullies branch. Returns a zero-mean relief offset in meters.
		//   gx, gz: slope of the terrain being eroded (dh/dx, dh/dz)
		public const int EROSION_OCTAVES = 5;
		public const double EROSION_CELL = 160, EROSION_AMP = 36, EROSION_GAIN = 0.5, EROSION_LAC = 2,
			EROSION_SLOPE = 1.1, EROSION_BRANCH = 1.0, EROSION_MAXSLOPE = 0.9;

		public static double ErosionNoise( double x, double z, double gx, double gz, int octaves = EROSION_OCTAVES, double cell0 = EROSION_CELL, double amp0 = EROSION_AMP )
		{
			double h = 0, hx = 0, hz = 0;
			double a = amp0, cs = cell0;
			const double R2 = 1.5625; // kernel radius 1.25 cells -> a 3x3 neighbourhood is exact
			for ( int o = 0; o < octaves; o ++ )
			{
				double sx = gx + hx * EROSION_BRANCH, sz = gz + hz * EROSION_BRANCH;
				double sl = Math.Sqrt( sx * sx + sz * sz );
				if ( sl > EROSION_MAXSLOPE )
				{
					sx *= EROSION_MAXSLOPE / sl; sz *= EROSION_MAXSLOPE / sl;
				}

				// stripes vary along the contour direction -> they run downhill
				double dx = sz * EROSION_SLOPE, dz = - sx * EROSION_SLOPE;
				double px = x / cs, pz = z / cs;
				double ixd = Math.Floor( px ), izd = Math.Floor( pz );
				int ix = ( int ) ixd, iz = ( int ) izd;
				double fx = px - ixd, fz = pz - izd;
				double v = 0, vx = 0, vz = 0, wt = 0;
				for ( int j = - 1; j <= 1; j ++ ) for ( int i = - 1; i <= 1; i ++ )
				{
					int cx = ix + i, cz = iz + j;
					// feature point: cell centre +- 0.25
					double ox = fx - i - 0.25 - Hash2( cx, cz, 11 + o ) * 0.5;
					double oz = fz - j - 0.25 - Hash2( cx, cz, 57 + o ) * 0.5;
					double d2 = ox * ox + oz * oz;
					if ( d2 >= R2 ) continue;
					double q = 1 - d2 / R2;
					double w = q * q * q;
					SinCos( ( ox * dx + oz * dz ) * TAU, out double c, out double s );
					v += c * w;
					vx -= s * dx * w;
					vz -= s * dz * w;
					wt += w;
				}

				if ( wt > 1e-6 )
				{
					v /= wt; vx /= wt; vz /= wt;
				}
				else v = 1;

				// subtract the expected value of the windowed waves for this frequency so the carving is
				// zero-mean: ridges rise above the envelope as much as the gullies cut below it
				double vm = Math.Exp( - 3.4 * ( dx * dx + dz * dz ) );
				h += a * ( v - vm );
				hx += a * vx * TAU / cs;
				hz += a * vz * TAU / cs;
				a *= EROSION_GAIN;
				cs /= EROSION_LAC;
			}

			return h * 0.5;
		}

		// bilinear sample of a grid whose texel centres are at o + (i + 0.5) * t
		public static double SampleGrid( float[] g, int n, double o, double t, double x, double z )
		{
			double fx = ( x - o ) / t - 0.5, fz = ( z - o ) / t - 0.5;
			fx = fx < 0 ? 0 : fx > n - 1.001 ? n - 1.001 : fx;
			fz = fz < 0 ? 0 : fz > n - 1.001 ? n - 1.001 : fz;
			int i = ( int ) fx, j = ( int ) fz;
			double tx = fx - i, tz = fz - j;
			int k = j * n + i;
			return ( g[ k ] * ( 1 - tx ) + g[ k + 1 ] * tx ) * ( 1 - tz ) + ( g[ k + n ] * ( 1 - tx ) + g[ k + n + 1 ] * tx ) * tz;
		}

		// Upsample an n x n grid by 2 (texel centres aligned as in SampleGrid) with Catmull-Rom
		// (interpolating) or bilinear filtering. Separable.
		public static float[] Upsample2( float[] src, int n, bool cubic = true )
		{
			int m = n * 2;
			var tmp = new float[ m * n ];
			var outp = new float[ m * m ];
			// new texel centre i maps to source coordinate (i + 0.5) / 2 - 0.5 = i / 2 - 0.25
			// even i: frac 0.75 of (i/2 - 1), odd i: frac 0.25 of (i - 1) / 2
			double[] W0 = cubic ? Catmull( 0.75 ) : Linear( 0.75 ), W1 = cubic ? Catmull( 0.25 ) : Linear( 0.25 );
			int ClampI( int i ) => i < 0 ? 0 : i >= n ? n - 1 : i;
			for ( int j = 0; j < n; j ++ )
			{
				int row = j * n;
				for ( int i = 0; i < m; i ++ )
				{
					int bs = ( i & 1 ) != 0 ? ( i - 1 ) >> 1 : ( i >> 1 ) - 1;
					double[] w = ( i & 1 ) != 0 ? W1 : W0;
					tmp[ j * m + i ] = ( float ) ( src[ row + ClampI( bs - 1 ) ] * w[ 0 ] + src[ row + ClampI( bs ) ] * w[ 1 ] + src[ row + ClampI( bs + 1 ) ] * w[ 2 ] + src[ row + ClampI( bs + 2 ) ] * w[ 3 ] );
				}
			}

			for ( int j = 0; j < m; j ++ )
			{
				int bs = ( j & 1 ) != 0 ? ( j - 1 ) >> 1 : ( j >> 1 ) - 1;
				double[] w = ( j & 1 ) != 0 ? W1 : W0;
				int r0 = ClampI( bs - 1 ) * m, r1 = ClampI( bs ) * m, r2 = ClampI( bs + 1 ) * m, r3 = ClampI( bs + 2 ) * m;
				int o = j * m;
				for ( int i = 0; i < m; i ++ )
				{
					outp[ o + i ] = ( float ) ( tmp[ r0 + i ] * w[ 0 ] + tmp[ r1 + i ] * w[ 1 ] + tmp[ r2 + i ] * w[ 2 ] + tmp[ r3 + i ] * w[ 3 ] );
				}
			}

			return outp;
		}

		static double[] Catmull( double t )
		{
			double t2 = t * t, t3 = t2 * t;
			return new[] { - 0.5 * t3 + t2 - 0.5 * t, 1.5 * t3 - 2.5 * t2 + 1, - 1.5 * t3 + 2 * t2 + 0.5 * t, 0.5 * t3 - 0.5 * t2 };
		}

		static double[] Linear( double t ) => new[] { 0.0, 1 - t, t, 0.0 };

		// separable box blur (radius r texels)
		public static float[] BoxBlur( float[] src, int w, int h, int r, float[] outp = null )
		{
			outp = outp ?? new float[ w * h ];
			var tmp = new float[ w * h ];
			double inv = 1.0 / ( 2 * r + 1 );
			for ( int j = 0; j < h; j ++ )
			{
				int o = j * w;
				double acc = 0;
				for ( int k = - r; k <= r; k ++ ) acc += src[ o + Math.Min( w - 1, Math.Max( 0, k ) ) ];
				for ( int i = 0; i < w; i ++ )
				{
					tmp[ o + i ] = ( float ) ( acc * inv );
					acc += ( double ) src[ o + Math.Min( w - 1, i + r + 1 ) ] - src[ o + Math.Max( 0, i - r ) ];
				}
			}

			for ( int i = 0; i < w; i ++ )
			{
				double acc = 0;
				for ( int k = - r; k <= r; k ++ ) acc += tmp[ Math.Min( h - 1, Math.Max( 0, k ) ) * w + i ];
				for ( int j = 0; j < h; j ++ )
				{
					outp[ j * w + i ] = ( float ) ( acc * inv );
					acc += ( double ) tmp[ Math.Min( h - 1, j + r + 1 ) * w + i ] - tmp[ Math.Max( 0, j - r ) * w + i ];
				}
			}

			return outp;
		}
	}
}
