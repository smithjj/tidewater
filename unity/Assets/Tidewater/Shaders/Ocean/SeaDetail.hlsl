// Port of the WGSL module of src/ocean/SeaDetail.js (prefix seaDetail): large-scale variation of the sea surface in world space.
//   SeaDetailSample SeaDetailSampleAt( xz ): rough = short-wave slope multiplier, gust 0..1, slick 0..1, streak 0..1
// SeaDetail.cs publishes _TWSeaDetailNoise, _TWSeaDetail = ( offset.xz, gustAmount, slickAmount ), _TWSeaDetail2 = ( streakAmount ).
// Needs the frame time in _TWShoreE.x and the wind in _TWWind = ( dir.xz, speed ) (the includer's globals).
#ifndef TW_SEA_DETAIL_INCLUDED
#define TW_SEA_DETAIL_INCLUDED

TEXTURE2D(_TWSeaDetailNoise); SAMPLER(sampler_TWSeaDetailNoise);
float4 _TWSeaDetail;     // offset.xz (accumulated wind drift, m), gustAmount, slickAmount
float4 _TWSeaDetail2;    // streakAmount

struct SeaDetailSampleOut { float rough; float gust; float slick; float streak; };

// hardware bilinear, repeat-wrapped
float4 SeaDetailLoad( float2 uv ) { return SAMPLE_TEXTURE2D_LOD( _TWSeaDetailNoise, sampler_TWSeaDetailNoise, uv, 0.0 ); }

SeaDetailSampleOut SeaDetailSampleAt( float2 xz )
{
	float2 p = xz - _TWSeaDetail.xy;
	float gustAmount = _TWSeaDetail.z, slickAmount = _TWSeaDetail.w, streakAmount = _TWSeaDetail2.x;

	// gusts: two octaves (~600 m and ~230 m features), the second slowly morphing
	float g1 = SeaDetailLoad( p / 620.0 ).x;
	float g2 = SeaDetailLoad( p / 230.0 + float2( _TWShoreE.x * 0.0009, 0.37 ) ).y;
	float gustRaw = g1 * 0.62 + g2 * 0.38;
	float gust = saturate( ( gustRaw - 0.5 ) * 2.4 * gustAmount + 0.5 );

	// wind-aligned frame, lightly domain-warped so bands meander
	float2 w = _TWWind.xy;
	float windSpeed = _TWWind.z;
	float along = dot( xz, w );
	float across = dot( xz, float2( -w.y, w.x ) ) + ( g2 - 0.5 ) * 26.0;

	// slicks: long bands, strongest in light wind, torn apart by gusts
	float sl = SeaDetailLoad( float2( along / 1100.0, across / 70.0 ) ).z;
	float calmWind = smoothstep( 13.0, 4.0, windSpeed );
	float slick = smoothstep( 0.64, 0.76, sl ) * ( 1.0 - gust * 0.8 ) * calmWind * slickAmount;

	// windrows: thin foam lines ~10 m apart that come and go along their length
	float st = SeaDetailLoad( float2( along / 380.0, across / 11.0 ) + float2( 0.13, 0.71 ) ).w;
	float breakUp = SeaDetailLoad( float2( along / 140.0, across / 40.0 ) + float2( 0.51, 0.29 ) ).x;
	float freshWind = smoothstep( 6.0, 12.0, windSpeed );
	float streak = smoothstep( 0.68, 0.82, st ) * smoothstep( 0.4, 0.62, breakUp ) * freshWind * streakAmount;

	// windrows show mostly as smooth lanes (surfactant and debris collect in the convergence lines and damp the ripples), with
	// only a trace of foam
	float rough = lerp( 0.5, 1.5, gust ) * ( 1.0 - slick * 0.8 ) * ( 1.0 - streak / max( streakAmount, 1e-3 ) * 0.45 );
	SeaDetailSampleOut o; o.rough = rough; o.gust = gust; o.slick = slick; o.streak = streak;
	return o;
}

#endif
