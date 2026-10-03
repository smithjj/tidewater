// Port of the gradient noise of src/engine/render/wgsl/common.js: MaterialX (three's MaterialXNoise.js) bit for bit, the
// same Jenkins lookup3 hash, gradients, quintic fade and gradient scale (0.6616 in 2D), so the procedural patterns land
// where they did in the three.js version.
#ifndef TW_NOISE_INCLUDED
#define TW_NOISE_INCLUDED

uint TWMxRotl( uint x, uint k ) { return ( x << k ) | ( x >> ( 32u - k ) ); }

uint TWMxFinal( uint a0, uint b0, uint c0 )
{
	uint a = a0; uint b = b0; uint c = c0;
	c ^= b; c -= TWMxRotl( b, 14u );
	a ^= c; a -= TWMxRotl( c, 11u );
	b ^= a; b -= TWMxRotl( a, 25u );
	c ^= b; c -= TWMxRotl( b, 16u );
	a ^= c; a -= TWMxRotl( c, 4u );
	b ^= a; b -= TWMxRotl( a, 14u );
	c ^= b; c -= TWMxRotl( b, 24u );
	return c;
}

uint TWMxHash2( int x, int y )
{
	uint s = 0xdeadbeefu + ( 2u << 2u ) + 13u;
	return TWMxFinal( s + ( uint ) x, s + ( uint ) y, s );
}

float TWMxGrad2( uint hash, float x, float y )
{
	uint h = hash & 7u;
	float u = h < 4u ? x : y;
	float v = 2.0 * ( h < 4u ? y : x );
	return ( ( h & 1u ) != 0u ? -u : u ) + ( ( h & 2u ) != 0u ? -v : v );
}

float3 TWFade3( float3 t ) { return t * t * t * ( t * ( t * 6.0 - 15.0 ) + 10.0 ); }

float TWPerlin2( float2 p )
{
	float2 fl = floor( p );
	int X = ( int ) fl.x; int Y = ( int ) fl.y;
	float fx = p.x - fl.x; float fy = p.y - fl.y;
	float3 u = TWFade3( float3( fx, fy, 0.0 ) );
	float v0 = TWMxGrad2( TWMxHash2( X, Y ), fx, fy );
	float v1 = TWMxGrad2( TWMxHash2( X + 1, Y ), fx - 1.0, fy );
	float v2 = TWMxGrad2( TWMxHash2( X, Y + 1 ), fx, fy - 1.0 );
	float v3 = TWMxGrad2( TWMxHash2( X + 1, Y + 1 ), fx - 1.0, fy - 1.0 );
	float s1 = 1.0 - u.x;
	return ( ( 1.0 - u.y ) * ( v0 * s1 + v1 * u.x ) + u.y * ( v2 * s1 + v3 * u.x ) ) * 0.6616;
}

#endif
