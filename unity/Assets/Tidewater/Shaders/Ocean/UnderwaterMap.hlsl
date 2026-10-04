// Port of the `uwMap` WGSL module of src/ocean/UnderwaterLighting.js: the baked wave maps (UnderwaterLighting.cs, UnderwaterLight.compute).
//   UwMapSample UwMapLookup( xz, waves ): bilinear lookup of the baked maps at xz (near map first, then far; flat sea beyond).
//   waves: map A (height, slope, foam) is only read for the full direct term.
// _TWUwOrigins = ( origin0.xz, origin1.xz ), _TWUwParams = ( reach, sea level, 0, 0 ).
#ifndef TW_UNDERWATER_MAP_INCLUDED
#define TW_UNDERWATER_MAP_INCLUDED

#define UW_MAP_N 512.0
#define UW_TEXEL0 0.25     // 128 m over 512 texels
#define UW_TEXEL1 2.0      // 1024 m over 512 texels

TEXTURE2D(_TWUwWaves0); SAMPLER(sampler_TWUwWaves0);
TEXTURE2D(_TWUwLevel0); SAMPLER(sampler_TWUwLevel0);
TEXTURE2D(_TWUwWaves1); SAMPLER(sampler_TWUwWaves1);
TEXTURE2D(_TWUwLevel1); SAMPLER(sampler_TWUwLevel1);
float4 _TWUwOrigins;
float4 _TWUwParams;

struct UwMapSample { float height; float2 slope; float foam; float mean; float detailK; };

// the highest the water can reach on the shore: the lighting's cheap reject
float UwReach() { return _TWUwParams.x; }

UwMapSample UwMapLookup( float2 xz, bool waves )
{
	float4 a = 0.0;
	float4 b = float4( 0.0, 0.9, 0.0, 0.0 );
	float2 st0 = ( xz - _TWUwOrigins.xy ) / UW_TEXEL0;
	float2 st1 = ( xz - _TWUwOrigins.zw ) / UW_TEXEL1;
	if ( all( st0 > 0.5 ) && all( st0 < UW_MAP_N - 0.5 ) )
	{
		float2 uv = st0 / UW_MAP_N;
		if ( waves ) { a = SAMPLE_TEXTURE2D_LOD( _TWUwWaves0, sampler_TWUwWaves0, uv, 0.0 ); }
		b = SAMPLE_TEXTURE2D_LOD( _TWUwLevel0, sampler_TWUwLevel0, uv, 0.0 );
	}
	else if ( all( st1 > 0.5 ) && all( st1 < UW_MAP_N - 0.5 ) )
	{
		float2 uv = st1 / UW_MAP_N;
		if ( waves ) { a = SAMPLE_TEXTURE2D_LOD( _TWUwWaves1, sampler_TWUwWaves1, uv, 0.0 ); }
		b = SAMPLE_TEXTURE2D_LOD( _TWUwLevel1, sampler_TWUwLevel1, uv, 0.0 );
	}

	UwMapSample o;
	o.height = _TWUwParams.y + a.x; o.slope = a.yz; o.foam = a.w; o.mean = _TWUwParams.y + b.x; o.detailK = b.y;
	return o;
}

#endif
