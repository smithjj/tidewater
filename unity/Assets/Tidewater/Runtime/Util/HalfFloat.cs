using System;

// Port of toHalfFloat of src/engine/math/DataUtils.js (three.js DataUtils-compatible): a TRUNCATING float -> IEEE half
// conversion (no rounding of the dropped mantissa bits), overflow clamped to +-65504.
namespace Tidewater.Util
{
	public static class HalfFloat
	{
		public static ushort ToHalf( double val )
		{
			val = Math.Max( -65504, Math.Min( 65504, val ) );
			uint x = ( uint ) BitConverter.SingleToInt32Bits( ( float ) val );
			uint sign = ( x >> 16 ) & 0x8000;
			uint exp = ( x >> 23 ) & 0xff;
			uint mant = x & 0x7fffff;

			if ( exp == 0xff ) return ( ushort ) ( sign | 0x7c00 | ( mant >> 13 ) ); // NaN / Inf
			int e = ( int ) exp - 127 + 15;
			if ( e >= 0x1f ) return ( ushort ) ( sign | 0x7c00 );
			if ( e <= 0 ) return e < -10 ? ( ushort ) sign : ( ushort ) ( sign | ( ( mant | 0x800000 ) >> ( 14 - e ) ) ); // subnormal
			return ( ushort ) ( sign | ( ( uint ) e << 10 ) | ( mant >> 13 ) );
		}
	}
}
