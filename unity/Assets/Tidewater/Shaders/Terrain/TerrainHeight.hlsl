// The terrain height map as seen by every shader that needs ground height (terrain, ocean, ...): the WGSL module
// of src/world/TerrainGPU.js, height part. Declares the global _TWHeightTex (R32F, loaded: manual bilinear) and
// _TWTerrainParams = ( origin, size, res, 0 ), set by TerrainGPU.SetGlobals.
#ifndef TW_TERRAIN_HEIGHT_INCLUDED
#define TW_TERRAIN_HEIGHT_INCLUDED

TEXTURE2D(_TWHeightTex);
TEXTURE2D(_TWNormalTex); SAMPLER(sampler_TWNormalTex);    // macro normal xz, rock mask, AO (mipmapped, trilinear, clamp)
float4 _TWTerrainParams;
TEXTURE2D(_TWShoreTex);          // shore field: RGBA32F ( T, dirX * exposure, dirZ * exposure, shoreline T ), loaded
float4 _TWShoreParams;           // x = shore field resolution

// exact bilinear height at world xz (sim space; matches TerrainData.HeightAt)
float TWHeightAt( float2 xz )
{
	float res = _TWTerrainParams.z;
	float2 f = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y * res - 0.5;
	float2 fc = clamp( f, 0.0, res - 1.001 );
	float2 i = floor( fc );
	float2 t = frac( fc );
	int2 ii = ( int2 ) i;
	float a = _TWHeightTex.Load( int3( ii, 0 ) ).x;
	float b = _TWHeightTex.Load( int3( ii + int2( 1, 0 ), 0 ) ).x;
	float c = _TWHeightTex.Load( int3( ii + int2( 0, 1 ), 0 ) ).x;
	float d = _TWHeightTex.Load( int3( ii + int2( 1, 1 ), 0 ) ).x;
	float h = lerp( lerp( a, b, t.x ), lerp( c, d, t.x ), t.y );
	// outside the domain: deep ocean floor
	bool outside = f.x < 0.0 || f.y < 0.0 || f.x > res - 1.0 || f.y > res - 1.0;
	return outside ? -90.0 : h;
}

// shore field ( T, dirX, dirZ, exposure ), bilinear via loads (float32 data); the WGSL terrainShoreSample
float4 TWShoreSample( float2 xz )
{
	float res = _TWShoreParams.x;
	float2 f = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y * res - 0.5;
	float2 fc = clamp( f, 0.0, max( res - 1.001, 0.0 ) );
	float2 i = floor( fc );
	float2 t = frac( fc );
	int2 ii = ( int2 ) i;
	int2 mx = ( int2 ) max( ( int ) res - 1, 0 );
	float4 a = _TWShoreTex.Load( int3( ii, 0 ) );
	float4 b = _TWShoreTex.Load( int3( min( ii + int2( 1, 0 ), mx ), 0 ) );
	float4 c = _TWShoreTex.Load( int3( min( ii + int2( 0, 1 ), mx ), 0 ) );
	float4 d = _TWShoreTex.Load( int3( min( ii + int2( 1, 1 ), mx ), 0 ) );
	return lerp( lerp( a, b, t.x ), lerp( c, d, t.x ), t.y );
}

// explicit mip (e.g. in the vertex stage); the WGSL terrainNormalRockLevel
float4 TWNormalRockLevel( float2 xz, float level )
{
	float2 uv = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y;
	float4 s = SAMPLE_TEXTURE2D_LOD( _TWNormalTex, sampler_TWNormalTex, uv, level );
	return float4( s.xy * 2.0 - 1.0, s.z, s.w );
}

#ifndef TW_NO_NORMAL_ROCK  // (compute shaders define this: the lookup needs implicit derivatives)
// filtered macro normal (xz components, -1..1), rock mask, baked ambient occlusion at world xz (fragment only: implicit
// derivatives); the WGSL terrainNormalRock
float4 TWNormalRock( float2 xz )
{
	float2 uv = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y;
	float4 s = SAMPLE_TEXTURE2D( _TWNormalTex, sampler_TWNormalTex, uv );
	return float4( s.xy * 2.0 - 1.0, s.z, s.w );
}
#endif

#endif
