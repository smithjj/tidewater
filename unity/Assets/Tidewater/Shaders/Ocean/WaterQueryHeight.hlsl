// The Eulerian water height of the full surface at an xz (WaterQuery.js waterQueryHeightAtXZ): shared by the water queries and the
// spray update. The includer provides Common.hlsl, TerrainHeight.hlsl, Noise.hlsl and ShoreWaves.hlsl first, and sets
// the inputs with WaterQuery.SetHeightInputs.
#ifndef TW_WATER_QUERY_HEIGHT_INCLUDED
#define TW_WATER_QUERY_HEIGHT_INCLUDED

Texture2DArray<float4> _TWOceanDisp;
SamplerState sampler_LinearRepeat;
float4 _TWOceanSizes[4];
float4 _QAmp;                 // x = WaterSurface amplitude, y = sea level, z = has terrain (1/0), w = has shore waves (1/0)

float CascadeAttenuation( int c, float depth )
{
	const float floorAmt[ 4 ] = { 0.0, 0.05, 0.25, 0.5 };
	float d0 = min( 40.0, _TWOceanSizes[ c ].x * 0.08 );
	float a = smoothstep( 0.0, d0, depth );
	return lerp( floorAmt[ c ] * smoothstep( 0.0, 0.6, depth ), 1.0, a );
}

// the displacement (vec3) of the full water surface at Lagrangian point x0
float3 WaterQueryDispAt( float2 x0, float depth )
{
	float3 d = 0.0;
	[unroll]
	for ( int c = 0; c < 4; c ++ )
	{
		d += _TWOceanDisp.SampleLevel( sampler_LinearRepeat, float3( x0 / _TWOceanSizes[ c ].x, c ), c == 3 ? 2.0 : 0.0 ).xyz * CascadeAttenuation( c, depth );
	}

	d *= _QAmp.x;
	if ( _QAmp.w > 0.5 ) { d += ShoreEvaluateNoNormal( x0, depth, TWHeightAt( x0 ) ).disp; }
	// (wakeDisplacement( x0 ) is added here once ported)
	return d;
}

// Eulerian water height at xz
float WaterQueryHeightAtXZ( float2 p )
{
	float depth = _QAmp.z > 0.5 ? _QAmp.y - TWHeightAt( p ) : 500.0;
	float2 x0 = p;
	for ( int i = 0; i < 2; i ++ )
	{
		x0 = p - WaterQueryDispAt( x0, depth ).xz;
	}

	return _QAmp.y + WaterQueryDispAt( x0, depth ).y;
}

#endif
