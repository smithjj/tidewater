// The humpback's rig and skin (src/world/marine/Whale.js: _rigModule, _createMaterial, _skinShading).
//
// Mesh (WhaleView): position / normal = the model as the JS has it (x to the whale's left, y up, z to the snout), uv0 = the skin uv (v flipped for Unity's textures),
// uv2 = ( rig.x = the rest axial z, rig.y = the part: 0 body, 1 left flipper, 2 right flipper ), uv3 = ( rig.z = the flipper tip factor, 0 ). The triangles are wound
// for the mirror below.
// The vertex stage poses every vertex from the spine frames (the CPU poses them each frame: _WhalePos / _WhaleRot, K = 40 frames; the second K are last frame's,
// unused here) and writes the world position itself, mirrored to Unity's z (Unity z = - sim z); the object matrix is the camera's translation at most.
// Skin: albedo with the roughness in alpha (sRGB, trilinear, anisotropic), the 16 bit relief in R / G (linear): it displaces the vertices (every level of detail)
// and, from three taps, bumps the normal in the fragment.

#ifndef WHALE_SURFACE_INCLUDED
#define WHALE_SURFACE_INCLUDED

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"

#define WHALE_K 40

float4 _WhalePos[ 2 * WHALE_K ];   // ( position xyz, rest y of the frame ), this frame then last frame's
float4 _WhaleRot[ 2 * WHALE_K ];   // the frames' quaternions
float4 _WhaleFlip[ 4 ];            // the flippers: ( axis xyz, angle ), this frame (left, right) then last frame's
float4 _WhaleA;                    // ( z of the head frame, spacing of the frames, relief minimum, relief range )
float4 _WhaleB;                    // ( water level, wet film (1 = just surfaced), 0, 0 )
float4 _WhalePecL;                 // the flipper roots (xyz)
float4 _WhalePecR;
TEXTURE2D( _WhaleAlbedo ); SAMPLER( sampler_WhaleAlbedo );
TEXTURE2D( _WhaleHeight ); SAMPLER( sampler_WhaleHeight );

float3 whaleRotateQ( float4 q, float3 v ) { return v + cross( q.xyz, cross( q.xyz, v ) + v * q.w ) * 2.0; }

// Rodrigues
float3 whaleRotAxis( float3 ax, float ang, float3 v )
{
	float c = cos( ang ); float s = sin( ang );
	return v * c + cross( ax, v ) * s + ax * ( dot( ax, v ) * ( 1.0 - c ) );
}

// pose a rest point (and its normal) with the frame set `o` (0 = this frame): the world (sim) position and normal
void whalePose( float3 p, float3 n, float2 rig, float tip, int o, out float3 world, out float3 normal )
{
	float part = rig.y;
	bool isL = part > 0.5 && part < 1.5;
	bool isR = part > 1.5 && part < 2.5;
	bool isPec = isL || isR;
	int fo = o != 0 ? 2 : 0;
	float4 fl = isL ? _WhaleFlip[ fo ] : _WhaleFlip[ fo + 1 ];
	float3 root = isL ? _WhalePecL.xyz : _WhalePecR.xyz;
	// flippers flex: the tip turns a little further than the root
	float ang = fl.w * ( tip * 0.35 + 0.8 ) * ( isPec ? 1.0 : 0.0 );
	float3 pp = root + whaleRotAxis( fl.xyz, ang, p - root );
	float fi = clamp( ( _WhaleA.x - rig.x ) / _WhaleA.y, 0.0, ( float )( WHALE_K - 1 ) - 0.001 );
	int i0 = ( int ) floor( fi ); float t = frac( fi );
	float4 P0 = _WhalePos[ i0 + o ]; float4 P1 = _WhalePos[ i0 + o + 1 ];
	float4 Q0 = _WhaleRot[ i0 + o ]; float4 Q1 = _WhaleRot[ i0 + o + 1 ];
	float4 q = normalize( lerp( Q0, Q1, t ) );
	float yc = lerp( P0.w, P1.w, t );
	float3 off = float3( pp.x, pp.y - yc, pp.z - rig.x );
	world = lerp( P0.xyz, P1.xyz, t ) + whaleRotateQ( q, off );
	normal = whaleRotateQ( q, whaleRotAxis( fl.xyz, ang, n ) );
}

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
	// real relief: tubercles, pleats and barnacles displace the mesh (rest space)
	float4 hRel = SAMPLE_TEXTURE2D_LOD( _WhaleHeight, sampler_WhaleHeight, input.uv0, 0.0 );
	float dRel = ( hRel.r * 0.99611 + hRel.g * 0.00389 ) * _WhaleA.w + _WhaleA.z;
	float3 p = input.positionOS + input.normalOS * dRel;

	float3 world; float3 normal;
	whalePose( p, input.normalOS, input.uv2, input.uv3.x, 0, world, normal );
	// the draw's object matrix is the identity (or, with camera-relative rendering, only the camera's translation): the position is given in the world, relative
	// to the camera, minus whatever translation the matrix has; the Unity world is the sim's mirrored in z
	input.positionOS = GetCameraRelativePositionWS( float3( world.x, world.y, - world.z ) ) - UNITY_MATRIX_M._m03_m13_m23;
	input.normalOS = float3( normal.x, normal.y, - normal.z );
	return input;
}

#endif // HAVE_MESH_MODIFICATION
#endif // !defined(SHADER_STAGE_RAY_TRACING)

// emission is not used by the whale
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

	float3 N = normalize( input.tangentToWorld[ 2 ] );
	float3 P = input.positionRWS;
	float waterY = _WhaleB.x;
	float above = smoothstep( -0.1, 0.2, GetAbsolutePositionWS( P ).y - waterY );
	float film = above * _WhaleB.y;

	float2 uvA = input.texCoord0.xy;
	float2 dux = ddx( uvA ); float2 duy = ddy( uvA );
	float3 dpx = ddx( P ); float3 dpy = ddy( P );
	float4 albedo = SAMPLE_TEXTURE2D( _WhaleAlbedo, sampler_WhaleAlbedo, uvA );
	// matte skin (0.55-0.75) in the water; a thin glossy film only where it is out of the water and freshly wet
	float roughness = lerp( albedo.a, albedo.a * 0.48, film );
	float4 t0 = SAMPLE_TEXTURE2D( _WhaleHeight, sampler_WhaleHeight, uvA );
	float4 t1 = SAMPLE_TEXTURE2D( _WhaleHeight, sampler_WhaleHeight, uvA + dux );
	float4 t2 = SAMPLE_TEXTURE2D( _WhaleHeight, sampler_WhaleHeight, uvA + duy );
	float hh0 = ( t0.r * 0.99611 + t0.g * 0.00389 ) * _WhaleA.w;
	float dhx = ( ( t1.r * 0.99611 + t1.g * 0.00389 ) * _WhaleA.w - hh0 ) * 1.6;
	float dhy = ( ( t2.r * 0.99611 + t2.g * 0.00389 ) * _WhaleA.w - hh0 ) * 1.6;
	// the surface-gradient bump of the three height taps, in world space
	float3 r1 = cross( dpy, N ); float3 r2 = cross( N, dpx );
	float det = dot( dpx, r1 );
	float3 grad = ( r1 * dhx + r2 * dhy ) * sign( det );
	float3 normalWS = normalize( abs( det ) * N - grad );

	surfaceData.normalWS = normalWS;
	surfaceData.tangentWS = normalize( input.tangentToWorld[ 0 ].xyz );
	surfaceData.geomNormalWS = N;

	surfaceData.baseColor = albedo.rgb;
	surfaceData.perceptualSmoothness = 1.0 - saturate( roughness );
	surfaceData.metallic = 0.0;
	surfaceData.ambientOcclusion = 1.0;

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

	// in water the skin's Fresnel reflectance is far weaker than in air (n 1.33 vs 1.0)
	surfaceData.specularOcclusion = lerp( 0.45, 1.0, above );

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, 1.0, bentNormalWS, 0, builtinData );

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}

#endif // WHALE_SURFACE_INCLUDED
