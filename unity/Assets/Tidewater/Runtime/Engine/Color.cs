using System;

// Port of src/engine/math/Color.js: RGB stored as linear floats; hex inputs are sRGB and get linearized.
namespace Tidewater.Engine
{
	public sealed class Color
	{
		public double r = 1, g = 1, b = 1;

		public Color() { }
		public Color( double hex ) { setHex( hex ); }
		public Color( double r, double g, double b ) { this.r = r; this.g = g; this.b = b; }

		public static double SRGBToLinear( double c ) => c < 0.04045 ? c * 0.0773993808 : Math.Pow( c * 0.9478672986 + 0.0521327014, 2.4 );
		public static double LinearToSRGB( double c ) => c < 0.0031308 ? c * 12.92 : 1.055 * Math.Pow( c, 0.41666 ) - 0.055;
		static double Clamp01( double v ) => Math.Max( 0, Math.Min( 1, v ) );
		static double Euclid( double n, double m ) => ( ( n % m ) + m ) % m;

		static double Hue2rgb( double p, double q, double t )
		{
			if ( t < 0 ) t += 1;
			if ( t > 1 ) t -= 1;
			if ( t < 1.0 / 6 ) return p + ( q - p ) * 6 * t;
			if ( t < 1.0 / 2 ) return q;
			if ( t < 2.0 / 3 ) return p + ( q - p ) * 6 * ( 2.0 / 3 - t );
			return p;
		}

		public Color set( double hex ) => setHex( hex );
		public Color set( Color c ) => copy( c );
		public Color setScalar( double s ) { r = s; g = s; b = s; return this; }

		public Color setHex( double hex, bool srgb = true )
		{
			long h = ( long ) Math.Floor( hex );
			return setRGB( ( ( h >> 16 ) & 255 ) / 255.0, ( ( h >> 8 ) & 255 ) / 255.0, ( h & 255 ) / 255.0, srgb );
		}

		public Color setRGB( double r, double g, double b, bool srgb = false )
		{
			if ( srgb ) { r = SRGBToLinear( r ); g = SRGBToLinear( g ); b = SRGBToLinear( b ); }
			this.r = r; this.g = g; this.b = b;
			return this;
		}

		public Color setHSL( double h, double s, double l, bool srgb = false )
		{
			h = Euler_( h ); s = Clamp01( s ); l = Clamp01( l );
			if ( s == 0 ) return setRGB( l, l, l, srgb );
			double p = l <= 0.5 ? l * ( 1 + s ) : l + s - l * s;
			double q = 2 * l - p;
			return setRGB( Hue2rgb( q, p, h + 1.0 / 3 ), Hue2rgb( q, p, h ), Hue2rgb( q, p, h - 1.0 / 3 ), srgb );
		}

		static double Euler_( double h ) => Euclid( h, 1 );

		public Color clone() => new Color( r, g, b );
		public Color copy( Color c ) { r = c.r; g = c.g; b = c.b; return this; }
		public Color copySRGBToLinear( Color c ) { r = SRGBToLinear( c.r ); g = SRGBToLinear( c.g ); b = SRGBToLinear( c.b ); return this; }
		public Color copyLinearToSRGB( Color c ) { r = LinearToSRGB( c.r ); g = LinearToSRGB( c.g ); b = LinearToSRGB( c.b ); return this; }

		public double getHex( bool srgb = true )
		{
			double R = r, G = g, B = b;
			if ( srgb ) { R = LinearToSRGB( R ); G = LinearToSRGB( G ); B = LinearToSRGB( B ); }
			return JS.Round( Clamp01( R ) * 255 ) * 65536 + JS.Round( Clamp01( G ) * 255 ) * 256 + JS.Round( Clamp01( B ) * 255 );
		}

		public void getHSL( out double h, out double s, out double l )
		{
			double max = Math.Max( r, Math.Max( g, b ) ), min = Math.Min( r, Math.Min( g, b ) );
			h = 0; s = 0; l = ( min + max ) / 2;
			if ( min != max )
			{
				double d = max - min;
				s = l <= 0.5 ? d / ( max + min ) : d / ( 2 - max - min );
				if ( max == r ) h = ( g - b ) / d + ( g < b ? 6 : 0 );
				else if ( max == g ) h = ( b - r ) / d + 2;
				else h = ( r - g ) / d + 4;
				h /= 6;
			}
		}

		public Color offsetHSL( double h, double s, double l ) { getHSL( out double ch, out double cs, out double cl ); return setHSL( ch + h, cs + s, cl + l ); }
		public Color add( Color c ) { r += c.r; g += c.g; b += c.b; return this; }
		public Color addColors( Color a, Color c ) { r = a.r + c.r; g = a.g + c.g; b = a.b + c.b; return this; }
		public Color addScalar( double s ) { r += s; g += s; b += s; return this; }
		public Color sub( Color c ) { r = Math.Max( 0, r - c.r ); g = Math.Max( 0, g - c.g ); b = Math.Max( 0, b - c.b ); return this; }
		public Color multiply( Color c ) { r *= c.r; g *= c.g; b *= c.b; return this; }
		public Color multiplyScalar( double s ) { r *= s; g *= s; b *= s; return this; }
		public Color lerp( Color c, double a ) { r += ( c.r - r ) * a; g += ( c.g - g ) * a; b += ( c.b - b ) * a; return this; }
		public Color lerpColors( Color a, Color c, double t ) { r = a.r + ( c.r - a.r ) * t; g = a.g + ( c.g - a.g ) * t; b = a.b + ( c.b - a.b ) * t; return this; }
		public UnityEngine.Color ToUnity() => new UnityEngine.Color( ( float ) r, ( float ) g, ( float ) b, 1f );
	}
}
