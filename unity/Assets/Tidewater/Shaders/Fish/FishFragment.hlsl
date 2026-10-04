// Glue between HDRP's pass templates and the ported fish props: the vertex stage of FishMaterial.js propVertex (open jaw, curl, sag) and
// GetSurfaceAndBuiltinData, which runs FishSurface and fills HDRP's SurfaceData (modelled on the village's VillageFragment.hlsl).
//
// Mesh (FishPropsView): position / normal = the model with z mirrored (Unity z = -sim z), uv0 = aData.xy, uv1 = aData.zw, uv2 = ( rest x, y ),
// uv3 = ( rest z, 0 ). The object transform is position, rotation and uniform scale = the length of the fish.

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"
#include "FishSurface.hlsl"

float _AlphaCutoff;
float4 _TWFrame;              // time, wind speed, night, 0
// per draw (MaterialPropertyBlock): ( pattern + seed * 0.9, curl, sag, jaw ), ( cloudy eye, wet, dried, blood ), ( length, lod fade share, outgoing, 0 )
float4 _FishA;
float4 _FishB;
float4 _FishC;

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
	float4 d = float4( input.uv0, input.uv1 );
	float pattern = floor( _FishA.x );
	// the model frame of the JS (z north to south mirrored back)
	float3 p = float3( input.positionOS.x, input.positionOS.y, - input.positionOS.z );
	float3 n = float3( input.normalOS.x, input.normalOS.y, - input.normalOS.z );

	// lower jaw: rotates down about the hinge at the corner of the mouth
	float2 hinge = fishRow( pattern, 7 ).zw;
	float a = _FishA.w * fishJawOf( d );
	float ca = cos( a ); float sa = sin( a );
	float dy = p.y - hinge.y; float dz = p.z - hinge.x;
	p.y = hinge.y + dy * ca - dz * sa;
	p.z = hinge.x + dy * sa + dz * ca;
	float ny = n.y * ca - n.z * sa; float nz = n.y * sa + n.z * ca;
	n.y = ny;
	n.z = nz;

	// body bent along circular arcs about its middle: sideways (curl), then up / down (sag)
	float k1 = _FishA.y + ( _FishA.y >= 0.0 ? 1e-4 : - 1e-4 );
	float t1 = k1 * p.z;
	float c1 = cos( t1 ); float s1 = sin( t1 ); float h1 = sin( t1 * 0.5 );
	float x1 = h1 * h1 * 2.0 / k1 + p.x * c1;
	float z1 = s1 / k1 - p.x * s1;
	float nx1 = n.x * c1 + n.z * s1; float nz1 = n.z * c1 - n.x * s1;
	float k2 = _FishA.z + ( _FishA.z >= 0.0 ? 1e-4 : - 1e-4 );
	float t2 = k2 * z1;
	float c2 = cos( t2 ); float s2 = sin( t2 ); float h2 = sin( t2 * 0.5 );
	float y2 = h2 * h2 * 2.0 / k2 + p.y * c2;
	float z2 = s2 / k2 - p.y * s2;
	float ny2 = n.y * c2 + nz1 * s2; float nz2 = nz1 * c2 - n.y * s2;

	input.positionOS = float3( x1, y2, - z2 );
	input.normalOS = float3( nx1, ny2, - nz2 );
	return input;
}

#endif // HAVE_MESH_MODIFICATION
#endif // !defined(SHADER_STAGE_RAY_TRACING)

// emission is not used by the fish
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

	float4 D = float4( input.texCoord0.xy, input.texCoord1.xy );

	// stochastic fin transparency (every pass) and the level-of-detail cross-fade (not in the shadow proxy)
	if ( ! fishPropKeep( D, input.positionSS.xy, _TWFrame.x ) ) discard;
#if SHADERPASS != SHADERPASS_SHADOWS
	if ( ! fishLodVisible( input.positionSS.xy, _FishC.y, _FishC.z > 0.5 ) ) discard;
#endif

	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );

	FishIn fi;
	fi.D = D;
	fi.Lp = float3( input.texCoord2.xy, input.texCoord3.x );
	fi.I = float4( floor( _FishA.x ), frac( _FishA.x ), _FishC.x, 0.0 );
	fi.flags = _FishB;
	fi.P = input.positionRWS;
	fi.N = normalWS;
	fi.V = V;
	fi.Pview = mul( ( float3x3 ) UNITY_MATRIX_V, input.positionRWS );

	FishOut s = FishSurface( fi );

	surfaceData.normalWS = normalize( s.normal );
	surfaceData.tangentWS = normalize( input.tangentToWorld[ 0 ].xyz );
	surfaceData.geomNormalWS = normalWS;

	surfaceData.baseColor = s.albedo;
	surfaceData.perceptualSmoothness = 1.0 - saturate( s.roughness );
	surfaceData.metallic = saturate( s.metalness );
	surfaceData.ambientOcclusion = 1.0;

	surfaceData.subsurfaceMask = 0;
	surfaceData.transmissionMask = 0;
	surfaceData.thickness = 1;
	surfaceData.diffusionProfileHash = 0;

	// the wet sheen of the props: HDRP's clear coat
	surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_STANDARD | ( s.coat > 0.0 ? MATERIALFEATUREFLAGS_LIT_CLEAR_COAT : 0 );
	surfaceData.coatMask = saturate( s.coat );
	surfaceData.anisotropy = 0.0;
	surfaceData.specularColor = float3( 0.0, 0.0, 0.0 );
	surfaceData.iridescenceThickness = 0.0;
	surfaceData.iridescenceMask = 0.0;

	surfaceData.ior = 1.5;
	surfaceData.transmittanceColor = float3( 1.0, 1.0, 1.0 );
	surfaceData.atDistance = 1000000.0;
	surfaceData.transmittanceMask = 0.0;

	surfaceData.specularOcclusion = 1.0;

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, 1.0, bentNormalWS, 0, builtinData );

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}
