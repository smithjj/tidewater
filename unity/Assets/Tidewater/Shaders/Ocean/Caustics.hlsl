// Port of the WGSL module of src/ocean/Caustics.js (prefix caustics): the caustic light factor (mean ~1) at a world point
// `depth` metres below the surface.
//   float3 CausticsSample( P, depth, slope, foam, gdx, gdy )   gdx / gdy = ddx / ddy of P.xz (the pixel footprint)
//   float3 CausticsSampleLevel( P, depth, level )               flat surface, fixed blur level
//   float3 CausticsSampleBaked( P, depth, slope, foam, gdx, gdy, detailK )   the gust / slick factor precomputed
//   float3 CausticsSampleBakedMono( ... )                       same without the chromatic dispersion
//   float3 CausticsSampleShaft( P, depth, level, detailK )      flat surface, one fine lookup, for ray marches
//   float CausticsDetailK( xz )                                 the gust / slick factor
//   slope: slope (dh/dx, dh/dz) of the long waves above (swell, shore waves): their refraction tilts the light, so the whole
//          network sways as each wave passes.   foam: surface foam / bubble coverage above (0..1): diffuses the light.
// Needs SeaDetail.hlsl and _TWSunDir (toward the sun, sim space). Caustics.cs publishes the textures and
// _TWCausticsParams = ( strength, fine tile, broad tile ).
#ifndef TW_CAUSTICS_INCLUDED
#define TW_CAUSTICS_INCLUDED

#define CAUSTICS_FINE_RES 512.0
#define CAUSTICS_BROAD_RES 256.0

TEXTURE2D(_TWCausticsFine); SAMPLER(sampler_TWCausticsFine);
TEXTURE2D(_TWCausticsBroad); SAMPLER(sampler_TWCausticsBroad);
float4 _TWCausticsParams;   // strength, fine tile (m), broad tile (m)

// the blur level is the least filtering; with a footprint, each gradient is stretched to at least that level's texel size
float2 CausticsStretch( float2 g, float minLen ) { return g * max( minLen / max( length( g ), 1e-9 ), 1.0 ); }

float4 CausticsFetchFine( float2 uv, float lvl, float2 gdx, float2 gdy, bool useGrad )
{
	if ( ! useGrad ) { return SAMPLE_TEXTURE2D_LOD( _TWCausticsFine, sampler_TWCausticsFine, uv, lvl ); }
	float minLen = exp2( lvl ) / CAUSTICS_FINE_RES;
	return SAMPLE_TEXTURE2D_GRAD( _TWCausticsFine, sampler_TWCausticsFine, uv, CausticsStretch( gdx / _TWCausticsParams.y, minLen ), CausticsStretch( gdy / _TWCausticsParams.y, minLen ) );
}

float4 CausticsFetchBroad( float2 uv, float lvl, float2 gdx, float2 gdy, bool useGrad )
{
	if ( ! useGrad ) { return SAMPLE_TEXTURE2D_LOD( _TWCausticsBroad, sampler_TWCausticsBroad, uv, lvl ); }
	float minLen = exp2( lvl ) / CAUSTICS_BROAD_RES;
	return SAMPLE_TEXTURE2D_GRAD( _TWCausticsBroad, sampler_TWCausticsBroad, uv, CausticsStretch( gdx / _TWCausticsParams.z, minLen ), CausticsStretch( gdy / _TWCausticsParams.z, minLen ) );
}

// gust / slick factor of the caustics at xz
float CausticsDetailK( float2 xz )
{
	SeaDetailSampleOut det = SeaDetailSampleAt( xz );
	return lerp( 0.55, 1.25, det.gust ) * ( 1.0 - det.slick * 0.6 );
}

// mono: one fine lookup instead of three (no chromatic dispersion); detailK < 0: evaluated here
float3 CausticsSampleImpl( float3 P, float depth, float level, float2 slope, float foam, bool hasFoam, float2 gdx, float2 gdy, bool useGrad, bool mono, float detailK )
{
	float3 sunDir = normalize( _TWSunDir.xyz );
	// the light reaching this point entered the water up-sun along the refracted sun ray
	float3 n = normalize( float3( -slope.x, 1.0, -slope.y ) );
	float3 Ls = refract( -sunDir, n, 1.0 / 1.333 );
	float tDown = max( -Ls.y, 0.15 );
	float2 entry = P.xz - Ls.xz * ( depth / tDown );

	// deeper -> softer (finite sun disk + forward scattering)
	float blur = level >= 0.0 ? level : clamp( depth * 0.4 - 0.2, 0.0, 3.0 );
	float wD = saturate( ( depth - 1.2 ) / 2.8 ); // blend between the two focal planes

	float2 uvF = entry / _TWCausticsParams.y;
	float4 tg = CausticsFetchFine( uvF, blur, gdx, gdy, useGrad );
	float g = lerp( tg.x, tg.y, wD );
	float r = g;
	float b = g;
	if ( ! mono )
	{
		// slight chromatic dispersion: each color lands a little apart along the sun direction
		float2 disp = normalize( Ls.xz + float2( 1e-4, 0.0 ) ) * ( depth * 0.0035 );
		float4 tr = CausticsFetchFine( uvF + disp / _TWCausticsParams.y, blur, gdx, gdy, useGrad );
		float4 tb = CausticsFetchFine( uvF - disp / _TWCausticsParams.y, blur, gdx, gdy, useGrad );
		r = lerp( tr.x, tr.y, wD );
		b = lerp( tb.x, tb.y, wD );
	}
	float4 broad = CausticsFetchBroad( entry / _TWCausticsParams.z, 1.5, gdx, gdy, useGrad );
	float br = lerp( broad.x, broad.y, saturate( depth / 9.0 ) );
	float3 c = float3( r, g, b ) * lerp( 1.0, br, 0.6 );

	// no caustics right at the surface, strongest in the first metres, fading with depth
	float k = smoothstep( 0.03, 0.5, depth ) * exp( depth * -0.06 ) * _TWCausticsParams.x;
	k *= detailK >= 0.0 ? detailK : CausticsDetailK( entry );
	if ( hasFoam ) { k *= 1.0 - saturate( foam ); }
	float3 result = lerp( 1.0, c, k );
	// foam and bubble clouds scatter the light back up: the floor under them is shaded
	return hasFoam ? result * ( 1.0 - saturate( foam ) * 0.6 ) : result;
}

float3 CausticsSample( float3 P, float depth, float2 slope, float foam, float2 gdx, float2 gdy )
{
	return CausticsSampleImpl( P, depth, -1.0, slope, foam, true, gdx, gdy, true, false, -1.0 );
}

float3 CausticsSampleBaked( float3 P, float depth, float2 slope, float foam, float2 gdx, float2 gdy, float detailK )
{
	return CausticsSampleImpl( P, depth, -1.0, slope, foam, true, gdx, gdy, true, false, detailK );
}

// without the chromatic dispersion (one fine lookup)
float3 CausticsSampleBakedMono( float3 P, float depth, float2 slope, float foam, float2 gdx, float2 gdy, float detailK )
{
	return CausticsSampleImpl( P, depth, -1.0, slope, foam, true, gdx, gdy, true, true, detailK );
}

float3 CausticsSampleLevel( float3 P, float depth, float level )
{
	return CausticsSampleImpl( P, depth, level, 0.0, 0.0, false, 0.0, 0.0, false, false, -1.0 );
}

float3 CausticsSampleShaft( float3 P, float depth, float level, float detailK )
{
	return CausticsSampleImpl( P, depth, level, 0.0, 0.0, false, 0.0, 0.0, false, true, detailK );
}

#endif
