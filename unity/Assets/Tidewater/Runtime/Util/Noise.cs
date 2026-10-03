using System;

// Port of src/util/Noise.js: small, fast, seeded 2D noise utilities for CPU-side procedural generation.
//
// The JS runs every calculation in doubles and only rounds when a value is stored into a Float32Array,
// so the port keeps doubles for all arithmetic and float[] for stored grids. Where JS relies on
// 32-bit integer wrap-around (Math.imul, >>>) the C# uses unchecked int / uint arithmetic.
namespace Tidewater.Util
{
	public static class MathX
	{
		public static double Smoothstep( double a, double b, double x )
		{
			double t = Math.Min( 1, Math.Max( 0, ( x - a ) / ( b - a ) ) );
			return t * t * ( 3 - 2 * t );
		}

		public static double Clamp( double x, double a, double b ) => Math.Min( b, Math.Max( a, x ) );

		public static double Lerp( double a, double b, double t ) => a + ( b - a ) * t;

		// Math.imul
		public static int Imul( int a, int b ) => unchecked( a * b );

		// Math.round: halves round up (C# Math.Round rounds them to even)
		public static double Round( double x ) => Math.Floor( x + 0.5 );

		public static double Hypot( double a, double b ) => Math.Sqrt( a * a + b * b );
	}

	// mulberry32( seed ): returns a closure in JS, an object here
	public sealed class Mulberry32
	{
		uint a;

		public Mulberry32( uint seed ) { a = seed; }

		public double Next()
		{
			unchecked
			{
				a += 0x6D2B79F5u;
				uint t = a;
				t = ( uint ) MathX.Imul( ( int ) ( t ^ ( t >> 15 ) ), ( int ) ( t | 1 ) );
				t ^= t + ( uint ) MathX.Imul( ( int ) ( t ^ ( t >> 7 ) ), ( int ) ( t | 61 ) );
				return ( ( t ^ ( t >> 14 ) ) ) / 4294967296.0;
			}
		}
	}

	public sealed class Noise2D
	{
		readonly byte[] perm = new byte[ 512 ];
		readonly float[] grad = new float[ 512 ];

		public Noise2D( int seed = 1 )
		{
			var rand = new Mulberry32( unchecked( ( uint ) seed ) );
			var p = new byte[ 256 ];
			for ( int i = 0; i < 256; i ++ ) p[ i ] = ( byte ) i;
			for ( int i = 255; i > 0; i -- )
			{
				int j = ( int ) Math.Floor( rand.Next() * ( i + 1 ) );
				byte t = p[ i ]; p[ i ] = p[ j ]; p[ j ] = t;
			}

			for ( int i = 0; i < 512; i ++ ) perm[ i ] = p[ i & 255 ];
			for ( int i = 0; i < 256; i ++ )
			{
				double a = rand.Next() * Math.PI * 2;
				grad[ i * 2 ] = ( float ) Math.Cos( a );
				grad[ i * 2 + 1 ] = ( float ) Math.Sin( a );
			}
		}

		// gradient noise, roughly in [-1, 1]
		public double Noise( double x, double y )
		{
			double xi = Math.Floor( x ), yi = Math.Floor( y );
			double xf = x - xi, yf = y - yi;
			int X = ( int ) xi & 255, Y = ( int ) yi & 255;
			int h00 = perm[ X + perm[ Y ] ], h10 = perm[ X + 1 + perm[ Y ] ];
			int h01 = perm[ X + perm[ Y + 1 ] ], h11 = perm[ X + 1 + perm[ Y + 1 ] ];
			double d00 = grad[ h00 * 2 ] * xf + grad[ h00 * 2 + 1 ] * yf;
			double d10 = grad[ h10 * 2 ] * ( xf - 1 ) + grad[ h10 * 2 + 1 ] * yf;
			double d01 = grad[ h01 * 2 ] * xf + grad[ h01 * 2 + 1 ] * ( yf - 1 );
			double d11 = grad[ h11 * 2 ] * ( xf - 1 ) + grad[ h11 * 2 + 1 ] * ( yf - 1 );
			double u = xf * xf * xf * ( xf * ( xf * 6 - 15 ) + 10 );
			double v = yf * yf * yf * ( yf * ( yf * 6 - 15 ) + 10 );
			double a = d00 + ( d10 - d00 ) * u;
			double b = d01 + ( d11 - d01 ) * u;
			return ( a + ( b - a ) * v ) * 1.414;
		}

		public double Fbm( double x, double y, int octaves = 5, double lacunarity = 2.0, double gain = 0.5 )
		{
			double sum = 0, amp = 1, freq = 1, norm = 0;
			for ( int i = 0; i < octaves; i ++ )
			{
				sum += Noise( x * freq + i * 17.13, y * freq - i * 9.71 ) * amp;
				norm += amp;
				amp *= gain;
				freq *= lacunarity;
			}

			return sum / norm;
		}

		public double Ridged( double x, double y, int octaves = 5 )
		{
			double sum = 0, amp = 0.5, freq = 1, prev = 1;
			for ( int i = 0; i < octaves; i ++ )
			{
				double n = 1 - Math.Abs( Noise( x * freq + i * 31.7, y * freq + i * 11.3 ) );
				n *= n;
				sum += n * amp * prev;
				prev = n;
				amp *= 0.5;
				freq *= 2.03;
			}

			return sum;
		}
	}
}
