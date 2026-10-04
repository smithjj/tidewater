// Port of the WGSL module of src/ocean/ShoreSim.js (prefix shoreSim): the Eulerian state over the main beach, updated every
// frame on the GPU:
//   r = foam carried by the water (made by the bore roller, the plunge point, the swash front and the spray falling back;
//       advected with the actual flow: bores, uprush, backwash; thinned where the flow spreads it out)
//   g = sand wetness (1 while covered, dries over ~half a minute)
//   b = foam stranded on the sand when the water drains away (pops over a few seconds)
//   a = depth-averaged flow speed along the local wave direction (m/s): carries the foam pattern (SurfFoam flow map) and gives
//       the divergence that thins the foam
//
//   ShoreSimUvOf( xz ), ShoreSimInside( uv ) (fade at the region border), ShoreSimStateAt( uv ) (bilinear from 4 loads),
//   ShoreSimState( xz ) (raw state, faded at the border), ShoreSimSample( xz ) = ( foam on the water, sand wetness, foam left on
//   the sand, flow speed ), ShoreSimLaceLoad( q ), ShoreSimSandFoam( xz, s, h )
// Loads only (no sampler bindings). ShoreSim.cs publishes _TWShoreSimState (the state), _TWShoreSimLace (the lace pattern,
// nearest) and _TWShoreSimParams = ( min.x, min.z, size, enabled ).

#ifndef TW_SHORE_SIM_INCLUDED
#define TW_SHORE_SIM_INCLUDED

#define SHORE_SIM_RES 768.0
#define SHORE_SIM_LACE_TILE 3.5
#define SHORE_SIM_LACE_N 512

TEXTURE2D(_TWShoreSimState);   // RGBA16F, loaded (manual bilinear)
TEXTURE2D(_TWShoreSimLace);    // the lace pattern, nearest, loaded (manual bilinear)
float4 _TWShoreSimParams;      // min.x, min.z, size, enabled

float2 ShoreSimUvOf( float2 xz ) { return ( xz - _TWShoreSimParams.xy ) / _TWShoreSimParams.z; }

float ShoreSimInside( float2 uv )
{
	return smoothstep( 0.0, 0.02, uv.x ) * smoothstep( 1.0, 0.98, uv.x ) * smoothstep( 0.0, 0.02, uv.y ) * smoothstep( 1.0, 0.98, uv.y );
}

// bilinear state at texture coordinate uv, from 4 loads
float4 ShoreSimStateAt( float2 uv )
{
	float2 fp = clamp( uv * SHORE_SIM_RES - 0.5, 0.0, SHORE_SIM_RES - 1.001 );
	int2 i = ( int2 ) floor( fp );
	float2 t = frac( fp );
	float4 a = _TWShoreSimState.Load( int3( i, 0 ) );
	float4 b = _TWShoreSimState.Load( int3( i + int2( 1, 0 ), 0 ) );
	float4 c = _TWShoreSimState.Load( int3( i + int2( 0, 1 ), 0 ) );
	float4 d = _TWShoreSimState.Load( int3( i + int2( 1, 1 ), 0 ) );
	return lerp( lerp( a, b, t.x ), lerp( c, d, t.x ), t.y );
}

// raw state (foam, wetness, residue amount, lace offset), faded out at the region border
float4 ShoreSimState( float2 xz )
{
	float2 uv = ShoreSimUvOf( xz );
	float4 outp = 0.0;
	if ( uv.x > 0.0 && uv.x < 1.0 && uv.y > 0.0 && uv.y < 1.0 )
	{
		outp = ShoreSimStateAt( uv ) * ShoreSimInside( uv );
	}
	return outp;
}

// vec4( foam amount on the water, sand wetness, foam amount left on the sand, flow speed ). Used by the water (x, w), the
// underwater lighting (x) and the terrain (y, z; for the lacy look of the foam left on the sand use ShoreSimSandFoam()). No
// sampler bindings.
float4 ShoreSimSample( float2 xz ) { return ShoreSimState( xz ); }

// bilinear lace lookup from 4 loads of the nearest-filtered copy (no sampler binding needed)
float4 ShoreSimLaceLoad( float2 q )
{
	float2 fp = q / SHORE_SIM_LACE_TILE * ( float ) SHORE_SIM_LACE_N - 0.5;
	int2 i = ( int2 ) floor( fp );
	float2 t = frac( fp );
	int m = SHORE_SIM_LACE_N - 1;
	float4 a = _TWShoreSimLace.Load( int3( i & m, 0 ) );
	float4 b = _TWShoreSimLace.Load( int3( ( i + int2( 1, 0 ) ) & m, 0 ) );
	float4 c = _TWShoreSimLace.Load( int3( ( i + int2( 0, 1 ) ) & m, 0 ) );
	float4 d = _TWShoreSimLace.Load( int3( ( i + int2( 1, 1 ) ) & m, 0 ) );
	return lerp( lerp( a, b, t.x ), lerp( c, d, t.x ), t.y );
}

// Foam left on the sand (0..1): thin bubble lines and single bubbles where the draining water left its foam, popping patch by
// patch as it dries. Static on the sand (world space). s: ShoreSimSample( xz ). (h: the ground height at xz, unused.)
float ShoreSimSandFoam( float2 xz, float4 s, float h )
{
	float r = s.z;
	// (screen-space footprint first: derivatives before the branch)
	float fp = length( fwidth( xz ) ) / SHORE_SIM_LACE_TILE * ( float ) SHORE_SIM_LACE_N;
	float outp = 0.0;
	if ( r > 0.01 )
	{
		float4 lace = ShoreSimLaceLoad( xz + 11.3 );
		// fade to the average where a pixel covers several strands (no mipmaps on the load path)
		float nearK = smoothstep( 3.0, 1.2, fp );
		float keep = smoothstep( lace.w * 0.55, lace.w * 0.55 + 0.08, r ); // staggered popping
		// thin bubble lines where the strands were, a little wider where more foam was left
		float lw = r * 0.1 + 0.06;
		float strand = ( 1.0 - smoothstep( lw, lw + 0.07, lace.x ) ) * ( lace.z * 0.5 + 0.6 );
		float lines = max( strand * smoothstep( 0.02, 0.25, r ) * keep, lace.y * keep * 0.8 );
		outp = lerp( smoothstep( 0.08, 0.6, r ) * 0.12, lines, nearK ) * 0.85;
	}
	return outp;
}

#endif
