// The terrain height map as seen by every shader that needs ground height (terrain, ocean, ...): the WGSL module
// of src/world/TerrainGPU.js, height part. Declares the global _TWHeightTex (R32F, loaded: manual bilinear) and
// _TWTerrainParams = ( origin, size, res, 0 ), set by TerrainGPU.SetGlobals.
#ifndef TW_TERRAIN_HEIGHT_INCLUDED
#define TW_TERRAIN_HEIGHT_INCLUDED

TEXTURE2D(_TWHeightTex);
TEXTURE2D(_TWNormalTex); SAMPLER(sampler_TWNormalTex);    // macro normal xz, rock mask, AO (mipmapped, trilinear, clamp)
float4 _TWTerrainParams;

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

// filtered macro normal (xz components, -1..1), rock mask, baked ambient occlusion at world xz (fragment only: implicit
// derivatives); the WGSL terrainNormalRock
float4 TWNormalRock( float2 xz )
{
	float2 uv = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y;
	float4 s = SAMPLE_TEXTURE2D( _TWNormalTex, sampler_TWNormalTex, uv );
	return float4( s.xy * 2.0 - 1.0, s.z, s.w );
}

#endif
