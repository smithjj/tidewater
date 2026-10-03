// Glue between HDRP's pass templates and the ported terrain: the CDLOD vertex placement (the WGSL
// terrainLodSnapped / terrainLodMorph + terrainHeightAt of Terrain.js / CDLOD.js / TerrainGPU.js) and
// GetSurfaceAndBuiltinData, which runs the terrain surface (TerrainSurface.hlsl) and fills HDRP's SurfaceData.
// Modelled on HDRP's own TerrainLitData.hlsl.
//
// Coordinates: the simulation is x east, y up, z south and Unity's world mirrors z (Util/Sim.cs), so a sim
// position (x, y, z) is the Unity world position (x, y, -z). The terrain object sits at the origin with an
// identity matrix, so object space = Unity world space.

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"

// ---- terrain data (global shader properties, see TerrainGPU.SetGlobals / TerrainRenderer)
#include "TerrainHeight.hlsl"                                // _TWHeightTex (R32F heights, loaded), _TWTerrainParams, TWHeightAt
TEXTURE2D(_TWSplatTex); SAMPLER(sampler_TWSplatTex);      // sand, paths, gullies / seagrass, rubble / scarp
TEXTURE2D(_TWDetailTex); SAMPLER(sampler_TWDetailTex);    // tileable detail heights (aniso 4, repeat)
float4 _TWLodMorph[16];    // per LOD: morph start, 1 / morph range, grid spacing, 0
float4 _TWViewPos;         // view camera, sim space (the morph centre in every pass, shadows included)
float4 _TWWind;            // sim wind direction xz, speed (m/s)
float4 _TWGust;            // integrated gust offset xz

#include "TerrainShading.hlsl"
#include "TerrainSurface.hlsl"

struct TWLodVertex { float2 worldXZ; float spacing; float morphK; float lod; float size; };

// the vertex on its LOD's lattice
float2 TWLodSnapped( float4 node, float2 grid )
{
	float h = _TWLodMorph[ ( int ) node.w ].z;
	float2 p = node.xy + grid * node.z;
	return floor( p / h + 1e-3 ) * h;
}

// Morph in world space on the LOD's own vertex lattice (spacing h). Quarter nodes of a
// partially subdivided parent carry the parent's LOD, so their extra vertices first snap
// onto that lattice; every node covering a point then computes the same position.
TWLodVertex TWLodMorph( float4 node, float2 grid, float3 viewPos, float y0 )
{
	int lod = ( int ) node.w;
	float4 m = _TWLodMorph[ lod ];
	float h = m.z;
	float2 p = node.xy + grid * node.z;
	float2 idx = floor( p / h + 1e-3 );
	float2 snapped = idx * h;
	float dist = length( viewPos - float3( snapped.x, y0, snapped.y ) );
	float morphK = clamp( ( dist - m.x ) * m.y, 0.0, 1.0 );
	float2 odd = frac( idx * 0.5 ) * 2.0;
	TWLodVertex o;
	o.worldXZ = snapped - odd * h * morphK;
	o.spacing = h * ( morphK + 1.0 );
	o.morphK = morphK;
	o.lod = node.w;
	o.size = node.z;
	return o;
}

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

// per-instance CDLOD node: ( origin x, origin z, size, lod ) in sim space
UNITY_INSTANCING_BUFFER_START(TWTerrain)
UNITY_DEFINE_INSTANCED_PROP(float4, _TWNodeData)
UNITY_INSTANCING_BUFFER_END(TWTerrain)

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
	float4 node = UNITY_ACCESS_INSTANCED_PROP( TWTerrain, _TWNodeData );
	// CDLOD vertex (same lattice snapping and geomorph as the CDLOD module) morphing toward the view position
	float2 grid = input.positionOS.xz;
	float2 snapped = TWLodSnapped( node, grid );
	float y0 = TWHeightAt( snapped );
	TWLodVertex cv = TWLodMorph( node, grid, _TWViewPos.xyz, y0 );
	// sim (x, y, z) -> Unity (x, y, -z)
	input.positionOS = float3( cv.worldXZ.x, TWHeightAt( cv.worldXZ ), -cv.worldXZ.y );
	#ifdef ATTRIBUTES_NEED_NORMAL
		input.normalOS = float3( 0.0, 1.0, 0.0 );
	#endif
	#ifdef ATTRIBUTES_NEED_TANGENT
		input.tangentOS = float4( 1.0, 0.0, 0.0, 1.0 );
	#endif
	return input;
}

#endif // HAVE_MESH_MODIFICATION
#endif // !defined(SHADER_STAGE_RAY_TRACING)

// We don't use emission for terrain
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

#ifndef EDITOR_VISUALIZATION
	// lightmap uvs: the terrain has none
	input.texCoord1 = input.texCoord2 = input.texCoord0;
#endif

	// Unity world -> sim space
	float3 posWS = GetAbsolutePositionWS( input.positionRWS );
	float3 camWS = GetAbsolutePositionWS( float3( 0.0, 0.0, 0.0 ) );
	float3 p = float3( posWS.x, posWS.y, -posWS.z );
	float3 camPos = float3( camWS.x, camWS.y, -camWS.z );

	TWSurfaceOut s = TWTerrainSurface( p, camPos );

	float3 normalWS = normalize( float3( s.normal.x, s.normal.y, -s.normal.z ) );
	surfaceData.normalWS = normalWS;
	surfaceData.tangentWS = normalize( input.tangentToWorld[ 0 ].xyz );
	surfaceData.geomNormalWS = input.tangentToWorld[ 2 ];

	surfaceData.baseColor = s.albedo;
	surfaceData.perceptualSmoothness = 1.0 - s.roughness;
	surfaceData.metallic = 0.0;
	surfaceData.ambientOcclusion = s.ao;

	surfaceData.subsurfaceMask = 0;
	surfaceData.transmissionMask = 0;
	surfaceData.thickness = 1;
	surfaceData.diffusionProfileHash = 0;

	surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_STANDARD;

	// Init other parameters
	surfaceData.anisotropy = 0.0;
	surfaceData.specularColor = float3( 0.0, 0.0, 0.0 );
	surfaceData.coatMask = 0.0;
	surfaceData.iridescenceThickness = 0.0;
	surfaceData.iridescenceMask = 0.0;

	// Transparency parameters
	surfaceData.ior = 1.0;
	surfaceData.transmittanceColor = float3( 1.0, 1.0, 1.0 );
	surfaceData.atDistance = 1000000.0;
	surfaceData.transmittanceMask = 0.0;

	surfaceData.specularOcclusion = 1.0;

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, 1, bentNormalWS, 0, builtinData );

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}
