// Shared by the ocean shaders: the cascade tile sizes and the shallow-water amplitude attenuation of the FFT cascades.
#ifndef TW_OCEAN_COMMON_INCLUDED
#define TW_OCEAN_COMMON_INCLUDED

float4 _TWOceanSizes[4];      // x = cascade tile size (m)

// per-cascade amplitude attenuation in shallow water (long waves feel the bottom first): long cascades vanish in shallow water,
// short ones persist until very shallow (WaterSurface.attenuationModule)
float WaterSurfaceCascadeAttenuation( int c, float depth )
{
	const float floorAmt[ 4 ] = { 0.0, 0.05, 0.25, 0.5 };
	float d0 = min( 40.0, _TWOceanSizes[ c ].x * 0.08 );
	float a = smoothstep( 0.0, d0, depth );
	return lerp( floorAmt[ c ] * smoothstep( 0.0, 0.6, depth ), 1.0, a );
}

#endif
