// The vegetation's vertex stage and surface function (src/world/vegetation/VegMaterials.js). One of VEG_PLANT (palms, understory, bananas, broadleaf plants),
// VEG_CANOPY (tree and shrub leaf cards) or VEG_IMPOSTOR (the far crowns) is defined by the shader.
//
// The draw is procedural (Graphics.DrawMeshInstancedProcedural, the object matrix the identity): the instance is _VegList[ SV_InstanceID ], its record is in _VegInst. The
// vertex stage deforms in the sim frame and writes the world position itself (camera relative, z mirrored); it passes the record's index on in uv2.x, the plant's stem
// height / axis in uv1.x and the tangent, for the surface to read the instance again.

#ifndef VEG_SURFACE_INCLUDED
#define VEG_SURFACE_INCLUDED

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"

float4 _TWFrame;   // ( time, wind speed, night, 0 )
float4 _TWWind;    // ( wind direction x, z, speed, 0 )
float4 _TWSunDir;  // the sun's direction (sim)

#include "VegCommon.hlsl"
#if defined(VEG_PLANT)
#include "VegPlant.hlsl"
#endif
#if defined(VEG_CANOPY) || defined(VEG_IMPOSTOR)
#include "VegCanopy.hlsl"
#endif
#if defined(VEG_GRASS)
#include "VegGrass.hlsl"
#endif
#if defined(VEG_ROCK)
#include "Assets/Tidewater/Shaders/Terrain/TerrainHeight.hlsl"
#include "Assets/Tidewater/Shaders/Terrain/TerrainShading.hlsl"
#include "VegRock.hlsl"
#endif

void VegSetup() {}

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
#if UNITY_ANY_INSTANCING_ENABLED // (a procedural draw has PROCEDURAL_INSTANCING_ON, not INSTANCING_ON)
	uint inst = ( uint ) input.instanceID;
#else
	uint inst = 0u; // the draw is always instanced; the plain variant only has to compile
#endif
#if defined(VEG_GRASS)
	// the grass: the instance is a cell of the camera's grid; the blade's channels are the patch mesh's ( GrassField.cs )
	float4 side4 = float4( input.uv0.x, 0.0, input.uv0.y, input.uv1.x );
	VegGrassOut gv = vegGrassDeform( input.positionOS, input.normalOS, side4, input.color, input.tangentOS, _GrassCells[ inst ] );
	input.positionOS = GetCameraRelativePositionWS( vegM( gv.pos ) ) - UNITY_MATRIX_M._m03_m13_m23;
	input.normalOS = vegM( gv.normal );
	input.tangentOS = float4( 0.0, 1.0, 0.0, 1.0 );
	input.color = gv.col;
	input.uv0 = gv.uv0;
	input.uv1 = gv.uv1;
	input.uv2 = gv.uv2;
	return input;
#elif defined(VEG_ROCK)
	// a rock: the instance's 3 x 4 matrix ( the sim frame ) and its level of detail fade
	float4 rr0 = _RockInst[ inst * 4u ], rr1 = _RockInst[ inst * 4u + 1u ], rr2 = _RockInst[ inst * 4u + 2u ], rlod = _RockInst[ inst * 4u + 3u ];
	float3 rp = input.positionOS, rn = input.normalOS;
	float3 rcA = float3( rr0.x, rr1.x, rr2.x ), rcB = float3( rr0.y, rr1.y, rr2.y ), rcC = float3( rr0.z, rr1.z, rr2.z );
	float3 rw = float3( dot( rr0.xyz, rp ) + rr0.w, dot( rr1.xyz, rp ) + rr1.w, dot( rr2.xyz, rp ) + rr2.w );
	float3 rnw = normalize( cross( rcB, rcC ) * rn.x + cross( rcC, rcA ) * rn.y + cross( rcA, rcB ) * rn.z );
	input.positionOS = GetCameraRelativePositionWS( vegM( rw ) ) - UNITY_MATRIX_M._m03_m13_m23;
	input.normalOS = vegM( rnw );
	input.tangentOS = float4( 0.0, 1.0, 0.0, 1.0 );
	input.uv1 = rlod.xy;
	return input;
#else
	uint ri = _VegList[ inst ];
	float4 iPos = _VegInst[ ri * 3u ];
	float4 iDat = _VegInst[ ri * 3u + 1u ];
	float4 ext = _VegInst[ ri * 3u + 2u ];
	float3 p = input.positionOS;
	float3 n = input.normalOS;
	float4 veg = input.tangentOS;
	float4 aMat = input.color;
	float4 aLobe = float4( input.uv1, input.uv2 );

#if defined(VEG_PLANT)
	VegPlant pl = vegPlantDeform( vegInstanceP( iPos, ext, p ), vegInstanceN( iPos, ext, n ), iPos, iDat, veg, aMat, aLobe, _VegLod.xyz );
	float3 pos = pl.pos; float3 nrm = pl.normal;
	input.tangentOS = float4( vegM( pl.trunkT ), 1.0 );
	input.uv1 = float2( pl.trunkY, veg.x );
#elif defined(VEG_CANOPY)
	VegCanopyOut cv = vegCanopyDeform( p, n, iPos, iDat, ext, veg, aMat, aLobe );
	float3 pos = cv.pos; float3 nrm = cv.normal;
	input.tangentOS = float4( 0.0, 1.0, 0.0, 1.0 );
	input.uv1 = float2( 0.0, veg.x );
#else
	// impostor: a camera-facing quad around the crown centre, swaying with the wind
	float3 base = iPos.xyz;
	float sy = abs( iDat.y );
	bool g1Flag = iDat.y < 0.0;
	float R = g1Flag ? _VegGroup1.y : _VegGroup0.y;
	float Rh = g1Flag ? _VegGroup1.z : _VegGroup0.z;
	float Hv = g1Flag ? _VegGroup1.w : _VegGroup0.w;
	float Cy = g1Flag ? _VegGroup1.x : _VegGroup0.x;
	// LOD window: from the near plant's hand-over distance to the fade-out, else collapsed
	float d = length( _VegCam.xyz - base );
	float vis = ( d >= ( g1Flag ? _VegNear.y : _VegNear.x ) * ( 1.0 - VEG_LOD_BAND / 2.0 ) && d < ( g1Flag ? VEG_IMP_SHRUB_MAX : _VegLod.z ) ) ? 1.0 : 0.0;
	// far away the forest is thinned out: fewer, proportionally larger crowns ( grown about the base ) keep the canopy closed
	float thin = vsm( VEG_IMP_THIN_0, VEG_IMP_THIN_1, d );
	float keepI = frac( iDat.w * 91.7 ) < thin * VEG_IMP_THIN_FRACTION ? 0.0 : 1.0;
	float grow = thin * ( 1.0 / sqrt( 1.0 - VEG_IMP_THIN_FRACTION ) - 1.0 ) + 1.0;
	float sI = iPos.w * grow;
	float3 Cc = base + float3( 0.0, Cy * sI * sy, 0.0 );
	// sway of the whole crown ( matches the near plants' trunk sway amplitude )
	float w = vegWindStrength();
	float g = vegGustAt( base.xz );
	float ph0 = iDat.w * 6.2832;
	float swayK = ( w * w * 0.009 * ( g * 0.8 + 0.3 ) + sin( _TWFrame.x * 0.9 + ph0 ) * w * 0.0045 * ( g + 0.4 ) ) * iDat.z * 0.45;
	float3 swayV = vegWindDir3() * swayK;
	float3 Cs = Cc + swayV;
	float3 toCam = normalize( _VegCam.xyz - Cs );
	float3 right = normalize( cross( VEG_UP, toCam ) + float3( 1e-4, 0.0, 0.0 ) );
	float3 up = cross( toCam, right );
	// quad fitted to the plant's projected extent: its horizontal radius across, from above the crown disc, from the side the ( stretched ) height
	float k = sI * vis * keepI;
	float ty = abs( toCam.y );
	float halfW = Rh * k;
	float halfH = ( Hv * sy * sqrt( max( 1.0 - ty * ty, 0.0 ) ) + Rh * ty ) * k;
	float3 pos = Cs + right * ( p.x * halfW ) + up * ( p.y * halfH );
	float3 nrm = n;
	input.tangentOS = float4( 0.0, 1.0, 0.0, 1.0 );
	input.color = float4( swayV, sI );
#endif
	// the draw's object matrix is the identity (or, with camera-relative rendering, only the camera's translation): the position is given in the world, relative to the
	// camera, minus whatever translation the matrix has; the Unity world is the sim's mirrored in z
	input.positionOS = GetCameraRelativePositionWS( vegM( pos ) ) - UNITY_MATRIX_M._m03_m13_m23;
	input.normalOS = vegM( nrm );
	input.uv2 = float2( ( float ) ri, 0.0 );
#if defined(VEG_IMPOSTOR)
	input.uv1 = float2( 0.0, veg.x );
#endif
	return input;
#endif // VEG_GRASS
}

#endif // HAVE_MESH_MODIFICATION
#endif // !defined(SHADER_STAGE_RAY_TRACING)

// emission is not used by the vegetation
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

	// the surface (the mask discards in here: a function of its own breaks the DXC compile, as the fish's)
#if defined(VEG_PLANT)
#include "VegBodyPlant.hlsl"
#elif defined(VEG_GRASS)
#include "VegBodyGrass.hlsl"
#elif defined(VEG_ROCK)
#include "VegBodyRock.hlsl"
#elif defined(VEG_CANOPY)
#include "VegBodyCanopy.hlsl"
#else
#include "VegBodyImpostor.hlsl"
#endif

	surfaceData.normalWS = normalize( vegM( s.normal ) );
	surfaceData.tangentWS = normalize( input.tangentToWorld[ 0 ].xyz );
	surfaceData.geomNormalWS = vegM( Ns );

	surfaceData.baseColor = s.albedo;
	surfaceData.perceptualSmoothness = 1.0 - saturate( s.roughness );
	surfaceData.metallic = 0.0;
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

	surfaceData.ior = 1.5;
	surfaceData.transmittanceColor = float3( 1.0, 1.0, 1.0 );
	surfaceData.atDistance = 1000000.0;
	surfaceData.transmittanceMask = 0.0;

	surfaceData.specularOcclusion = 1.0;

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, 1.0, bentNormalWS, 0, builtinData );

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}

#endif // VEG_SURFACE_INCLUDED
