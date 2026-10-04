// MaterialX gradient noise (three's MaterialXNoise.js, as src/engine/render/wgsl/common.js has it): the same Jenkins lookup3 hash,
// gradients, quintic fade and gradient scales (0.6616 in 2D, 0.982 in 3D), so the procedural patterns land where they did in the
// three.js version.
//   float mx_noise_float3( float3 p ), mx_noise_float2( float2 p ), mx_fractal_noise_float3( p, octaves, lacunarity, diminish )
#ifndef TW_MATERIALX_NOISE_INCLUDED
#define TW_MATERIALX_NOISE_INCLUDED

uint MxRotl( uint x, uint k ) { return ( x << k ) | ( x >> ( 32u - k ) ); }

uint MxFinal( uint a0, uint b0, uint c0 )
{
	uint a = a0, b = b0, c = c0;
	c ^= b; c -= MxRotl( b, 14u );
	a ^= c; a -= MxRotl( c, 11u );
	b ^= a; b -= MxRotl( a, 25u );
	c ^= b; c -= MxRotl( b, 16u );
	a ^= c; a -= MxRotl( c, 4u );
	b ^= a; b -= MxRotl( a, 14u );
	c ^= b; c -= MxRotl( b, 24u );
	return c;
}

uint MxHash2( int x, int y ) { uint s = 0xdeadbeefu + ( 2u << 2u ) + 13u; return MxFinal( s + ( uint ) x, s + ( uint ) y, s ); }
uint MxHash3( int x, int y, int z ) { uint s = 0xdeadbeefu + ( 3u << 2u ) + 13u; return MxFinal( s + ( uint ) x, s + ( uint ) y, s + ( uint ) z ); }

float MxGrad2( uint hash, float x, float y )
{
	uint h = hash & 7u;
	float u = h < 4u ? x : y;
	float v = 2.0 * ( h < 4u ? y : x );
	return ( ( h & 1u ) != 0u ? -u : u ) + ( ( h & 2u ) != 0u ? -v : v );
}

float MxGradDot3( uint h, float3 p )
{
	uint hh = h & 15u;
	float u = hh < 8u ? p.x : p.y;
	float v = hh < 4u ? p.y : ( ( hh == 12u || hh == 14u ) ? p.x : p.z );
	return ( ( hh & 1u ) != 0u ? -u : u ) + ( ( hh & 2u ) != 0u ? -v : v );
}

float3 MxFade3( float3 t ) { return t * t * t * ( t * ( t * 6.0 - 15.0 ) + 10.0 ); }
uint MxH3( int3 i ) { return MxHash3( i.x, i.y, i.z ); }

float MxPerlin3( float3 p )
{
	float3 fl = floor( p );
	int3 i = ( int3 ) fl;
	float3 f = p - fl;
	float3 u = MxFade3( f );
	float n000 = MxGradDot3( MxH3( i ), f );
	float n100 = MxGradDot3( MxH3( i + int3( 1, 0, 0 ) ), f - float3( 1.0, 0.0, 0.0 ) );
	float n010 = MxGradDot3( MxH3( i + int3( 0, 1, 0 ) ), f - float3( 0.0, 1.0, 0.0 ) );
	float n110 = MxGradDot3( MxH3( i + int3( 1, 1, 0 ) ), f - float3( 1.0, 1.0, 0.0 ) );
	float n001 = MxGradDot3( MxH3( i + int3( 0, 0, 1 ) ), f - float3( 0.0, 0.0, 1.0 ) );
	float n101 = MxGradDot3( MxH3( i + int3( 1, 0, 1 ) ), f - float3( 1.0, 0.0, 1.0 ) );
	float n011 = MxGradDot3( MxH3( i + int3( 0, 1, 1 ) ), f - float3( 0.0, 1.0, 1.0 ) );
	float n111 = MxGradDot3( MxH3( i + int3( 1, 1, 1 ) ), f - float3( 1.0, 1.0, 1.0 ) );
	float x0 = lerp( lerp( n000, n100, u.x ), lerp( n010, n110, u.x ), u.y );
	float x1 = lerp( lerp( n001, n101, u.x ), lerp( n011, n111, u.x ), u.y );
	return lerp( x0, x1, u.z ) * 0.982;
}

float MxPerlin2( float2 p )
{
	float2 fl = floor( p );
	int X = ( int ) fl.x; int Y = ( int ) fl.y;
	float fx = p.x - fl.x; float fy = p.y - fl.y;
	float3 u = MxFade3( float3( fx, fy, 0.0 ) );
	float v0 = MxGrad2( MxHash2( X, Y ), fx, fy );
	float v1 = MxGrad2( MxHash2( X + 1, Y ), fx - 1.0, fy );
	float v2 = MxGrad2( MxHash2( X, Y + 1 ), fx, fy - 1.0 );
	float v3 = MxGrad2( MxHash2( X + 1, Y + 1 ), fx - 1.0, fy - 1.0 );
	float s1 = 1.0 - u.x;
	return ( ( 1.0 - u.y ) * ( v0 * s1 + v1 * u.x ) + u.y * ( v2 * s1 + v3 * u.x ) ) * 0.6616;
}

float mx_noise_float3( float3 p ) { return MxPerlin3( p ); }
float mx_noise_float2( float2 p ) { return MxPerlin2( p ); }

float mx_fractal_noise_float3( float3 p, int octaves, float lacunarity, float diminish )
{
	float r = 0.0; float amp = 1.0; float q3 = 0.0; float3 q = p;
	for ( int i = 0; i < octaves; i ++ ) { r += amp * MxPerlin3( q ); amp *= diminish; q *= lacunarity; }
	return r;
}

#endif
