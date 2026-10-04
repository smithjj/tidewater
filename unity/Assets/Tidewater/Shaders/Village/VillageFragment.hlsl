// Glue between HDRP's pass templates and the ported village materials: the vertex animation (flags, laundry, nets) and
// GetSurfaceAndBuiltinData, which runs the surface of VillageSurface.hlsl and fills HDRP's SurfaceData. Modelled on HDRP's LitData.hlsl.
//
// Mesh data (VillageView): position, normal, uv0 = the GeoBuilder uv (metres), vertex colour = the per-vertex tint (linear),
// uv1 = ( vdata.x, vdata.y ), uv2 = ( vdata.z, vdata.w ). The meshes are the sim geometry with z mirrored (Unity z = -sim z).

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"
#include "VillageSurface.hlsl"

// the material kind is a compile time constant (one keyword per kind; no keyword = wood; the net shader defines _KIND_NET):
// 0 wood, 1 hard, 2 roofMetal, 3 thatch, 4 stone, 5 fabric, 6 net
#if defined(_KIND_HARD)
	#define VILLAGE_KIND 1
#elif defined(_KIND_ROOF)
	#define VILLAGE_KIND 2
#elif defined(_KIND_THATCH)
	#define VILLAGE_KIND 3
#elif defined(_KIND_STONE)
	#define VILLAGE_KIND 4
#elif defined(_KIND_FABRIC)
	#define VILLAGE_KIND 5
#elif defined(_KIND_NET)
	#define VILLAGE_KIND 6
#else
	#define VILLAGE_KIND 0
#endif

float _VillageKind;           // (unused: the kind is VILLAGE_KIND)
float _AlphaCutoff;
float4 _TWFrame;              // time, wind speed, night, 0
float4 _TWWind;               // sim wind direction (x, z), speed
float4 _TWSunDir;
float4 _TWSunColor;

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
#if VILLAGE_KIND >= 5
	// sway of cloth and nets, and the pennant flags streaming from their poles (VillageMaterials.js SWAY / fabric vertex snippet).
	// The JS works in sim space; the mesh is the sim geometry with z mirrored.
	float4 vd = float4( input.uv1, input.uv2 );
	float3 sp = float3( input.positionOS.x, input.positionOS.y, - input.positionOS.z );
	float t = _TWFrame.x;
	float w = vd.y;
	float ph = t * 1.7 + sp.x * 0.6 + sp.z * 0.45 + vd.x * 20.0;
	float gust = sin( ph ) * 0.6 + sin( ph * 2.3 + 1.7 ) * 0.3 + 0.55;
	float flutter = sin( t * 7.0 + sp.y * 9.0 + vd.x * 50.0 ) * 0.25;
	float3 swayPos = sp + float3( _TWWind.x, flutter * 0.3, _TWWind.y ) * ( ( gust + flutter ) * ( _TWWind.z * 0.011 * w ) );
	#if VILLAGE_KIND == 5
		float isFlag = step( 5000.0, vd.z );
		// flag: oriented downwind around its pole
		float a = vd.y;
		float3 dir = normalize( float3( _TWWind.x, 0.0, _TWWind.y ) );
		float3 perp = float3( - dir.z, 0.0, dir.x );
		float strength = smoothstep( 0.5, 9.0, _TWWind.z );
		float phase = t * 7.5 - a * 5.0 + vd.x * 30.0;
		float wave = sin( phase ) * ( a * 0.09 ) * ( strength * 0.7 + 0.3 );
		float droop = ( 1.0 - strength ) * a * 0.55;
		float3 flagPos = float3( vd.z - 10000.0, sp.y - droop, vd.w ) + dir * ( a * ( 1.0 - droop * 0.4 ) ) + perp * wave;
		sp = lerp( swayPos, flagPos, isFlag );
	#else
		sp = swayPos;
	#endif
	input.positionOS = float3( sp.x, sp.y, - sp.z );
#endif
	return input;
}

#endif // HAVE_MESH_MODIFICATION
#endif // !defined(SHADER_STAGE_RAY_TRACING)

// emission is set from the surface (in pre-exposed units) below
#define _EmissiveColor float3(0,0,0)
#define _AlbedoAffectEmissive 0
#define _EmissiveExposureWeight 0
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitBuiltinData.hlsl"
#undef _EmissiveColor
#undef _AlbedoAffectEmissive
#undef _EmissiveExposureWeight

void GetSurfaceAndBuiltinData( inout FragInputs input, float3 V, inout PositionInputs posInput, out SurfaceData surfaceData, out BuiltinData builtinData RAY_TRACING_OPTIONAL_PARAMETERS )
{
	ZERO_INITIALIZE( SurfaceData, surfaceData );
	ZERO_INITIALIZE( BuiltinData, builtinData );

	const int kind = VILLAGE_KIND;
	float exposure = GetCurrentExposureMultiplier();

	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );
	// double-sided materials (fabric, nets): the normal faces the viewer
	if ( VILLAGE_KIND >= 5 ) { normalWS = input.isFrontFace ? normalWS : -normalWS; }

	VillageIn vi;
	vi.vdata = float4( input.texCoord1.xy, input.texCoord2.xy );
	vi.tint = input.color.rgb;
	vi.uv = input.texCoord0.xy;
	// sim-space world position (x east, y up, z south): the world mirrors z
	float3 pw = GetAbsolutePositionWS( input.positionRWS );
	vi.p = float3( pw.x, pw.y, -pw.z );
	vi.P = input.positionRWS;
	vi.N = normalWS;
	vi.pixel = input.positionSS.xy;
	vi.front = input.isFrontFace;
	vi.wind = _TWWind;
	vi.time = _TWFrame.x;
	vi.night = _TWFrame.z;
	vi.skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	vi.sunColor = _TWSunColor.rgb * exposure;
	vi.sunDirY = normalize( _TWSunDir.xyz ).y;

	VillageOut s = VillageSurface( kind, vi );

#if defined(_ALPHATEST_ON)
	clip( s.alpha - _AlphaCutoff );
#endif

	surfaceData.normalWS = normalize( s.normal );
	surfaceData.tangentWS = normalize( input.tangentToWorld[ 0 ].xyz );
	surfaceData.geomNormalWS = normalWS;

	surfaceData.baseColor = s.albedo;
	surfaceData.perceptualSmoothness = 1.0 - saturate( s.roughness );
	surfaceData.metallic = saturate( s.metalness );
	surfaceData.ambientOcclusion = s.ao;

	surfaceData.subsurfaceMask = 0;
	surfaceData.transmissionMask = 0;
	surfaceData.thickness = 1;
	surfaceData.diffusionProfileHash = 0;

	surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_STANDARD;
	surfaceData.coatMask = 0.0;
	surfaceData.anisotropy = 0.0;
	surfaceData.specularColor = float3( 0.0, 0.0, 0.0 );
	surfaceData.iridescenceThickness = 0.0;
	surfaceData.iridescenceMask = 0.0;

	// transparency parameters
	surfaceData.ior = 1.5;
	surfaceData.transmittanceColor = float3( 1.0, 1.0, 1.0 );
	surfaceData.atDistance = 1000000.0;
	surfaceData.transmittanceMask = 0.0;

	surfaceData.specularOcclusion = 1.0;

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, s.alpha, bentNormalWS, 0, builtinData );
	// emissive surfaces (lit windows, lanterns) in pre-exposed units
	builtinData.emissiveColor = s.emissive * exposure;

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}
