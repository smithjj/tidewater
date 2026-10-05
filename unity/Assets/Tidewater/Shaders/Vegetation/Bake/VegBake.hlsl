// The hash and noise of VegCommon.hlsl without the HDRP includes, for the bake shaders.
#ifndef VEG_BAKE_INCLUDED
#define VEG_BAKE_INCLUDED

float vegHash12( float2 p )
{
	float3 p3 = frac( float3( p.x, p.y, p.x ) * 0.1031 );
	p3 += dot( p3, p3.yzx + 33.33 );
	return frac( ( p3.x + p3.y ) * p3.z );
}

float vegNoise( float2 p )
{
	float2 i = floor( p );
	float2 fr = frac( p );
	float2 u = fr * fr * ( fr * - 2.0 + 3.0 );
	float a = vegHash12( i );
	float b = vegHash12( i + float2( 1.0, 0.0 ) );
	float c = vegHash12( i + float2( 0.0, 1.0 ) );
	float d = vegHash12( i + float2( 1.0, 1.0 ) );
	return lerp( lerp( a, b, u.x ), lerp( c, d, u.x ), u.y );
}

float vsm( float a, float b, float x ) { float t = saturate( ( x - a ) / ( b - a ) ); return t * t * ( 3.0 - 2.0 * t ); }

#endif
