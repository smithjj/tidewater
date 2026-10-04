using System;
using UnityEngine;

// Port of makeLaceTexture / laceData of src/ocean/SurfFoam.js: the tileable lace texture shared by the shore simulation and
// the surf foam (and the foam left on the sand), generated once on the CPU with CPU mipmaps.
//   R: distance to the nearest bubble strand (warped cell edges + noise contours), 0 on a strand, 1 in the middle of a hole:
//      thresholding it by the foam amount gives a dense mat, foam with holes, lace and thin strands as the foam decays
//   G: small bubbles clustered along the strands
//   B: soft mottling (foam density variation)
//   A: per-cell random value (staggers when stranded foam pops)
// RGBA8, 512^2, repeat; `texture` is sampled with filtering and mips, `nearest` is the same pattern read with loads.
namespace Tidewater.Ocean
{
	public static class LaceTexture
	{
		public const float LACE_TILE = 3.5f; // metres per tile of the lace texture

		static double Hash( int x, int y, int s )
		{
			unchecked
			{
				int h = x * 374761393 + y * 668265263 + s * ( int ) 2246822519u;
				h = Tidewater.Util.MathX.Imul( h ^ ( int ) ( ( uint ) h >> 13 ), 1274126177 );
				h ^= ( int ) ( ( uint ) h >> 16 );
				return ( uint ) h / 4294967296.0;
			}
		}

		static int W( int a, int n ) => ( ( a % n ) + n ) % n;

		static double Vnoise( double x, double y, int n, int s )
		{
			double fi = Math.Floor( x ), fj = Math.Floor( y );
			int i = ( int ) fi, j = ( int ) fj;
			double fx = x - fi, fy = y - fj;
			double ux = fx * fx * ( 3 - 2 * fx ), uy = fy * fy * ( 3 - 2 * fy );
			double a = Hash( W( i, n ), W( j, n ), s ), b = Hash( W( i + 1, n ), W( j, n ), s );
			double c = Hash( W( i, n ), W( j + 1, n ), s ), d = Hash( W( i + 1, n ), W( j + 1, n ), s );
			return ( a * ( 1 - ux ) + b * ux ) * ( 1 - uy ) + ( c * ( 1 - ux ) + d * ux ) * uy;
		}

		static double Fbm( double x, double y, int n, int s, int oct = 3 )
		{
			double v = 0, a = 0.5, t = 0;
			for ( int o = 0; o < oct; o ++ )
			{
				v += Vnoise( x * ( 1 << o ), y * ( 1 << o ), n * ( 1 << o ), s + o * 17 ) * a;
				t += a;
				a *= 0.5;
			}

			return v / t;
		}

		// Voronoi F1, F2 and nearest cell id on an n x n periodic jittered grid (coordinates in cells)
		static void Voronoi( double x, double y, int n, int s, out double f1, out double f2, out double id )
		{
			int i = ( int ) Math.Floor( x ), j = ( int ) Math.Floor( y );
			f1 = 9; f2 = 9; id = 0;
			for ( int dj = -1; dj <= 1; dj ++ ) for ( int di = -1; di <= 1; di ++ )
			{
				int ci = i + di, cj = j + dj;
				int wi = W( ci, n ), wj = W( cj, n );
				double px = ci + 0.15 + 0.7 * Hash( wi, wj, s ), py = cj + 0.15 + 0.7 * Hash( wi, wj, s + 1 );
				double d = Math.Sqrt( ( px - x ) * ( px - x ) + ( py - y ) * ( py - y ) );
				if ( d < f1 )
				{
					f2 = f1; f1 = d; id = Hash( wi, wj, s + 2 );
				}
				else if ( d < f2 ) f2 = d;
			}
		}

		static double Sstep( double a, double b, double x )
		{
			double t = Math.Min( 1, Math.Max( 0, ( x - a ) / ( b - a ) ) );
			return t * t * ( 3 - 2 * t );
		}

		public static byte[] LaceData( int size )
		{
			var data = new byte[ size * size * 4 ];

			// pass 1: warped coordinates and the contour noise at every texel
			int N = size * size;
			var UU = new float[ N ]; var VV = new float[ N ]; var NC = new float[ N ];
			for ( int py = 0; py < size; py ++ ) for ( int px = 0; px < size; px ++ )
			{
				double u = ( px + 0.5 ) / size, v = ( py + 0.5 ) / size;
				// two-level domain warp: organic, curvy strands
				double w1x = ( Fbm( u * 3, v * 3, 3, 3 ) - 0.5 ) * 0.16, w1y = ( Fbm( u * 3 + 5.2, v * 3 + 1.3, 3, 7 ) - 0.5 ) * 0.16;
				double w2x = ( Fbm( ( u + w1x ) * 9, ( v + w1y ) * 9, 9, 13 ) - 0.5 ) * 0.07, w2y = ( Fbm( ( u + w1x ) * 9 + 2.7, ( v + w1y ) * 9, 9, 17 ) - 0.5 ) * 0.07;
				int k = py * size + px;
				UU[ k ] = ( float ) ( u + w1x + w2x );
				VV[ k ] = ( float ) ( v + w1y + w2y );
				NC[ k ] = ( float ) Fbm( UU[ k ] * 6.0, VV[ k ] * 6.0, 6, 91, 3 );
			}

			// pass 2: distance to the nearest strand
			const int N1 = 16;
			const double half = 0.5 / N1;
			var D = new float[ N ];
			for ( int py = 0; py < size; py ++ ) for ( int px = 0; px < size; px ++ )
			{
				double u = ( px + 0.5 ) / size, v = ( py + 0.5 ) / size;
				int k = py * size + px;
				double uu = UU[ k ], vv = VV[ k ];
				// strands 1: edges of a warped cell network (distance to the edge ~ (F2 - F1) / 2, in cells)
				Voronoi( uu * N1, vv * N1, N1, 11, out double a1, out double b1, out double id1 );
				double dCells = ( b1 - a1 ) * 0.5 / N1;
				// strands 2: contour loops of the warped noise; distance ~ |n - c| / |grad n| (texture units)
				double n1 = NC[ k ];
				int xl = ( px + size - 1 ) % size, xr = ( px + 1 ) % size, yd = ( py + size - 1 ) % size, yu = ( py + 1 ) % size;
				double gx = ( ( double ) NC[ py * size + xr ] - NC[ py * size + xl ] ) * size * 0.5;
				double gy = ( ( double ) NC[ yu * size + px ] - NC[ yd * size + px ] ) * size * 0.5;
				double g = Math.Max( Math.Sqrt( gx * gx + gy * gy ), 0.5 );
				double dLoops = Math.Min( Math.Min( Math.Abs( n1 - 0.5 ), Math.Abs( n1 - 0.36 ) ), Math.Abs( n1 - 0.64 ) ) / g;
				// normalised by half a cell: 0 on a strand, ~1 in the middle of a hole. Irregular hole edges (fine noise) and
				// places where the strands break up (gaps).
				double hi = Fbm( u * 24, v * 24, 24, 5, 2 );
				double gap = Sstep( 0.52, 0.36, Fbm( u * 10 + 3.1, v * 10, 10, 31 ) );
				double d = Math.Min( dCells * 1.35 + 0.004, dLoops ) / half * ( 0.75 + 0.5 * hi ) + gap * 0.22;
				double mott = Fbm( u * 3, v * 3, 3, 71, 4 );
				// bubbles: small dots, clustered near the strands
				const int N3 = 120;
				Voronoi( u * N3, v * N3, N3, 61, out double a3, out double _, out double id3 );
				double rad = 0.1 + 0.22 * id3;
				double dot = Sstep( rad, rad * 0.35, a3 ) * ( id3 > 0.45 ? 1 : 0 );
				double bub = dot * ( 0.25 + 0.75 * Sstep( 0.5, 0.1, d ) );
				D[ k ] = ( float ) Math.Min( 1, d );
				data[ k * 4 + 1 ] = ( byte ) Tidewater.Util.MathX.Round( Math.Min( 1, bub ) * 255 );
				data[ k * 4 + 2 ] = ( byte ) Tidewater.Util.MathX.Round( mott * 255 );
				data[ k * 4 + 3 ] = ( byte ) Tidewater.Util.MathX.Round( id1 * 255 );
			}

			// pass 3: rounded holes: blurred distance inside the holes, the exact one near the strands
			var D0 = ( float[] ) D.Clone();
			var T = new float[ N ];
			for ( int it = 0; it < 2; it ++ )
			{
				for ( int py = 0; py < size; py ++ ) for ( int px = 0; px < size; px ++ )
				{
					double s = 0;
					for ( int o = -2; o <= 2; o ++ ) s += D[ py * size + ( ( px + o + size ) % size ) ];
					T[ py * size + px ] = ( float ) ( s / 5 );
				}

				for ( int py = 0; py < size; py ++ ) for ( int px = 0; px < size; px ++ )
				{
					double s = 0;
					for ( int o = -2; o <= 2; o ++ ) s += T[ ( ( py + o + size ) % size ) * size + px ];
					D[ py * size + px ] = ( float ) ( s / 5 );
				}
			}

			for ( int k = 0; k < N; k ++ ) data[ k * 4 ] = ( byte ) Tidewater.Util.MathX.Round( ( D0[ k ] + ( D[ k ] - D0[ k ] ) * Sstep( 0.12, 0.45, D0[ k ] ) ) * 255 );
			return data;
		}

		// CPU mip chain: the 2x2 box with the JS rounding
		public static byte[][] Mips( byte[] data, int size )
		{
			var mips = new System.Collections.Generic.List<byte[]> { data };
			byte[] src = data; int s = size;
			while ( s > 1 )
			{
				int h = s >> 1;
				var dst = new byte[ h * h * 4 ];
				for ( int y = 0; y < h; y ++ ) for ( int x = 0; x < h; x ++ ) for ( int c = 0; c < 4; c ++ )
				{
					int a = src[ ( ( 2 * y ) * s + 2 * x ) * 4 + c ], b = src[ ( ( 2 * y ) * s + 2 * x + 1 ) * 4 + c ];
					int d = src[ ( ( 2 * y + 1 ) * s + 2 * x ) * 4 + c ], e = src[ ( ( 2 * y + 1 ) * s + 2 * x + 1 ) * 4 + c ];
					dst[ ( y * h + x ) * 4 + c ] = ( byte ) ( ( a + b + d + e + 2 ) >> 2 );
				}

				mips.Add( dst );
				src = dst;
				s = h;
			}

			return mips.ToArray();
		}

		public sealed class Lace
		{
			public Texture2D texture, nearest;
			public int size;
			public byte[] data;
		}

		static Lace cached;

		public static Lace Make( int size = 512 )
		{
			if ( cached != null && cached.texture != null ) return cached;
			var data = LaceData( size );
			var mips = Mips( data, size );
			// sampled filtered with mips and repeat
			var tex = new Texture2D( size, size, TextureFormat.RGBA32, mips.Length, true ) { name = "surfLace", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
			for ( int m = 0; m < mips.Length; m ++ ) tex.SetPixelData( mips[ m ], m );
			tex.Apply( false, true );
			// the same pattern for shaders with no sampler to spare: nearest, read with loads, filtered by hand
			var near = new Texture2D( size, size, TextureFormat.RGBA32, false, true ) { name = "surfLaceNearest", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
			near.SetPixelData( data, 0 );
			near.Apply( false, true );
			cached = new Lace { texture = tex, nearest = near, size = size, data = data };
			return cached;
		}
	}
}
