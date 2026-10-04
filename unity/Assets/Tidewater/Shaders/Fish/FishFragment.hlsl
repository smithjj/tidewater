// Glue between HDRP's pass templates and the ported fish props: the vertex stage of FishMaterial.js propVertex (open jaw, curl, sag) and
// GetSurfaceAndBuiltinData, which runs FishSurface and fills HDRP's SurfaceData (modelled on the village's VillageFragment.hlsl).
//
// Mesh (FishPropsView): position / normal = the model with z mirrored (Unity z = -sim z), uv0 = aData.xy, uv1 = aData.zw, uv2 = ( rest x, y ),
// uv3 = ( rest z, 0 ). The object transform is position, rotation and uniform scale = the length of the fish.
//
// FISH_SWIM (the swimming fish of the schools, FishSchoolsView): the same mesh, drawn procedurally with the identity transform. The vertex stage
// finds its instance in the buffers (FishMaterial.js swimVertex), poses the model (fishSwimOffset: the travelling body wave, the sculling pectorals,
// the undulating or flapping ray discs, the turtle's flippers) and writes the world position itself; uv3.y carries the list entry to the fragment
// stage, which reads the record again (pattern + seed, length) and the level-of-detail fade of the entry.

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"
#include "FishSurface.hlsl"

float _AlphaCutoff;
float4 _TWFrame;              // time, wind speed, night, 0
#ifdef FISH_SWIM
// the schools' buffers (FishSchoolsView): 4 float4 per instance record ( x y z L, qx qy qz qw, phase amp bend pattern + seed * 0.9, motion xyz dPhase ),
// the lists of the instances drawn per model (the record index) and of those in a level-of-detail cross-fade (record index | share * 127 << 24 | outgoing << 31)
StructuredBuffer<float4> _FishInstances;
StructuredBuffer<uint> _FishList;
StructuredBuffer<uint> _FishFadeList;
// per draw (MaterialPropertyBlock): ( first list entry of this model, 1 = the fade list, 0, 0 )
float4 _FishDraw;

void FishSwimSetup() {}

float3 fishRotateQ( float4 q, float3 v ) { return v + cross( q.xyz, cross( q.xyz, v ) + v * q.w ) * 2.0; }

// Deformation as a function of the phase: fish: travelling body wave (amplitude grows toward the tail) plus the turning bend, sculling pectorals;
// rays: the disc margins undulate (stingray) or flap (eagle ray); turtle: the front flippers stroke, the hind ones paddle.
// d = aData, p = rest position, amp = wave amplitude, bend = turning bend at this vertex
float3 fishSwimOffset( float ph, float4 d, float3 p, float env, float amp, float bend, bool eagle )
{
	float u = d.x;
	float part = fishPartOf( d );
	bool isDisc = part == PA_DISC;
	bool isFlip = part == PA_FLIPPER;
	bool turtle = part > PA_WHIP + 0.5;
	float side = d.z; // rays: distance from the midline; flippers: along the flipper
	float lat = sin( ph - u * 5.6 ) * env * amp + bend;
	float flap = part == PA_PECTORAL ? sin( ph * 0.7 + 1.3 ) * d.z * 0.035 : 0.0;
	float3 fish = float3( lat + flap * sign( p.x ), 0.0, 0.0 );
	float k = eagle ? 1.2 : 8.0;
	float3 disc = float3( 0.0, sin( ph - u * k ) * pow( side, 1.6 ) * amp, 0.0 );
	bool front = d.w < 1.5;
	float3 stroke = float3( 0.0, sin( ph ) * ( front ? 0.3 : 0.07 ), cos( ph ) * ( front ? 0.14 : 0.0 ) ) * side;
	return isDisc ? disc : ( isFlip ? stroke : ( turtle ? float3( 0.0, 0.0, 0.0 ) : fish ) );
}
#else
// per draw (MaterialPropertyBlock): ( pattern + seed * 0.9, curl, sag, jaw ), ( cloudy eye, wet, dried, blood ), ( length, lod fade share, outgoing, 0 )
float4 _FishA;
float4 _FishB;
float4 _FishC;
#endif

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

#ifdef FISH_SWIM

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
	// the instance: the entry of the draw's model in the list, then its record
#if UNITY_ANY_INSTANCING_ENABLED // (a procedural draw has PROCEDURAL_INSTANCING_ON, not INSTANCING_ON)
	uint inst = ( uint ) input.instanceID;
#else
	uint inst = 0u; // the draw is always instanced; the plain variant only has to compile
#endif
	uint e = ( uint ) _FishDraw.x + inst;
	bool fade = _FishDraw.y > 0.5;
	uint entry = fade ? _FishFadeList[ e ] : _FishList[ e ];
	uint ri = fade ? ( entry & 0xffffffu ) : entry;
	float4 r0 = _FishInstances[ ri * 4u ];
	float4 q = _FishInstances[ ri * 4u + 1u ];
	float4 r2 = _FishInstances[ ri * 4u + 2u ];

	float4 d = float4( input.uv0, input.uv1 );
	// the model frame of the JS (z north to south mirrored back)
	float3 p = float3( input.positionOS.x, input.positionOS.y, - input.positionOS.z );
	float3 n = float3( input.normalOS.x, input.normalOS.y, - input.normalOS.z );
	float u = d.x;
	float pattern = floor( r2.w );
	float env = ( u * u * 0.85 + 0.08 ) * ( sm( 0.0, 0.25, u ) * 0.7 + 0.3 );
	float bend = r2.z * ( u * u );
	bool eagle = pattern == PT_EAGLERAY;
	float3 off = fishSwimOffset( r2.x, d, p, env, r2.y, bend, eagle );

	// world (sim) position and normal; the Unity mesh space is the world mirrored in z
	float3 pw = fishRotateQ( q, ( p + off ) * r0.w ) + r0.xyz;
	float3 nw = fishRotateQ( q, n );
	// the draw's object matrix is the identity (or, with camera-relative rendering, only the camera's translation): the position is given in the
	// world, relative to the camera, minus whatever translation the matrix has
	input.positionOS = GetCameraRelativePositionWS( float3( pw.x, pw.y, - pw.z ) ) - UNITY_MATRIX_M._m03_m13_m23;
	input.normalOS = float3( nw.x, nw.y, - nw.z );
	input.uv2 = p.xy;
	input.uv3 = float2( p.z, ( float ) e );
	return input;
}

#else

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


#endif // FISH_SWIM
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

#ifdef FISH_SWIM
	// the record of this instance again (the entry came in uv3.y): pattern + seed, length; the level-of-detail cross-fade of the entry
	uint e = ( uint )( input.texCoord3.y + 0.5 );
	bool fadeDraw = _FishDraw.y > 0.5;
	uint entry = fadeDraw ? _FishFadeList[ e ] : _FishList[ e ];
	uint ri = fadeDraw ? ( entry & 0xffffffu ) : entry;
	float patternSeed = _FishInstances[ ri * 4u + 2u ].w;
	float fishLength = _FishInstances[ ri * 4u ].w;
	float4 fishFlags = float4( 0.0, 0.0, 0.0, 0.0 );
	if ( fadeDraw && ! fishLodVisible( input.positionSS.xy, float( ( entry >> 24u ) & 127u ) / 127.0, ( entry >> 31u ) != 0u ) ) discard;
#else
	float patternSeed = _FishA.x;
	float fishLength = _FishC.x;
	float4 fishFlags = _FishB;
	// stochastic fin transparency (every pass) and the level-of-detail cross-fade (not in the shadow proxy)
	if ( ! fishPropKeep( D, input.positionSS.xy, _TWFrame.x ) ) discard;
#if SHADERPASS != SHADERPASS_SHADOWS
	if ( ! fishLodVisible( input.positionSS.xy, _FishC.y, _FishC.z > 0.5 ) ) discard;
#endif
#endif

	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );

	FishIn fi;
	fi.D = D;
	fi.Lp = float3( input.texCoord2.xy, input.texCoord3.x );
	fi.I = float4( floor( patternSeed ), frac( patternSeed ), fishLength, 0.0 );
	fi.flags = fishFlags;
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
