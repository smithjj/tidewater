// The body of the underwater lighting custom pass (see UnderwaterLighting.shader): the factor that multiplies the lit colour of a
// pixel that is below the water surface, and a debug summary of its terms.
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"

float4 _TWWind;
float4 _TWSunDir;             // toward the sun, sim space
float4 _TWSunColor;           // sun illuminance (lux) x colour
float4 _TWWaterAbsorption;
float4 _TWWaterScattering;
float4 _TWShoreE;
#include "../Common/Noise.hlsl"
#include "SeaDetail.hlsl"
#include "Caustics.hlsl"
#include "UnderwaterMap.hlsl"

TEXTURE2D_X_FLOAT(_UwDepthTex);     // the camera depth buffer, bound by UnderwaterLightingPass
TEXTURE2D_X(_UwNormalTex);          // the camera normal buffer

float UwLum( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

// returns the factor; dbg receives ( factor.g, baked foam, caustic factor.g, 1 ) for the debug view.
// Special values: ( 0, 0, 1 ) the sky, ( 0, 1, 0 ) above the reach of the water, ( 0.3, 0.3, 0 ) not under water.
float4 UwFactor( Varyings varyings, out float4 dbg )
{
	dbg = float4( 0.0, 0.0, 0.0, 1.0 );
	float depth = LOAD_TEXTURE2D_X( _UwDepthTex, ( uint2 ) varyings.positionCS.xy ).x;
	if ( depth == UNITY_RAW_FAR_CLIP_VALUE ) { dbg = float4( 0.0, 0.0, 1.0, 1.0 ); return 1.0; }

	float2 uv = varyings.positionCS.xy * _ScreenSize.zw;
	float3 posRWS = ComputeWorldSpacePosition( uv, depth, UNITY_MATRIX_I_VP );
	float3 posWorld = posRWS + _WorldSpaceCameraPos.xyz;
	float3 P = float3( posWorld.x, posWorld.y, -posWorld.z );   // sim space
	float seaLevel = _TWUwParams.y;
	// cheap reject: above anything the water reaches
	if ( P.y >= seaLevel + UwReach() ) { dbg = float4( 0.0, 1.0, 0.0, 1.0 ); return 1.0; }

	UwMapSample lw = UwMapLookup( P.xz, true );
	float d = max( lw.height - P.y, 0.0 );
	if ( d <= 0.0 ) { dbg = float4( 0.3, 0.3, 0.0, 1.0 ); return 1.0; }

	float under = smoothstep( 0.0, 0.08, d );
	float3 L = normalize( _TWSunDir.xyz );
	float3 Ls = refract( -L, float3( 0.0, 1.0, 0.0 ), 1.0 / 1.333 );
	float mu = max( -Ls.y, 0.15 );
	float3 sigT = _TWWaterAbsorption.rgb + _TWWaterScattering.rgb;
	float3 atten = exp( -sigT * d / mu );
	// the pixel's footprint on the ground plane, to filter the caustics over it
	float2 gdx = ddx( P.xz ), gdy = ddy( P.xz );
	float3 caust = CausticsSampleBaked( P, d, lw.slope, lw.foam, gdx, gdy, lw.detailK );
	float3 sunMod = lerp( 1.0, atten * caust, under );

	// diffuse downwelling light: effective path ~1.2x depth, plus a little in-scattered blue
	float dm = max( UwMapLookup( P.xz, false ).mean - P.y, 0.0 );
	float underA = smoothstep( 0.0, 0.1, dm );
	float3 attenA = exp( -sigT * dm * 1.2 ) * 0.85 + float3( 0.0, 0.02, 0.04 ) * exp( dm * -0.1 );
	float3 ambMod = lerp( 1.0, attenA, underA );

	// the share of this pixel's light that comes from the sun
	NormalData nd;
	DecodeFromNormalBuffer( LOAD_TEXTURE2D_X( _UwNormalTex, ( uint2 ) varyings.positionCS.xy ), nd );
	float3 Nw = nd.normalWS;
	float3 Nsim = float3( Nw.x, Nw.y, -Nw.z );
	float exposure = GetCurrentExposureMultiplier();
	float sunTerm = UwLum( _TWSunColor.rgb ) * exposure * max( dot( Nsim, L ), 0.0 ) * INV_PI;
	float ambTerm = UwLum( EvaluateAmbientProbe( Nw ) ) * exposure;
	float s = sunTerm / ( sunTerm + ambTerm + 1e-6 );
	float4 result = float4( lerp( ambMod, sunMod, s ), 1.0 );
	dbg = float4( result.g, lw.foam, caust.g, 1.0 );
	return result;
}

float4 Frag( Varyings varyings ) : SV_Target
{
	float4 dbg;
	return UwFactor( varyings, dbg );
}

float4 FragDebug( Varyings varyings ) : SV_Target
{
	float4 dbg;
	UwFactor( varyings, dbg );
	return dbg;
}
