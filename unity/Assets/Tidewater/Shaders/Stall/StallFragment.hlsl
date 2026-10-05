// The stalls' surfaces: createStallMaterial of src/game/StallKit.js (WGSL) as HLSL, run on HDRP's SurfaceData. Modelled on BoatFragment.hlsl.
//
// Frame: the stall meshes are the JS meshes with z mirrored (Unity world = ( x, y, -z ) of the sim), so the position the JS noise reads is
// ( x, y, -z ) of the absolute world position and the patterns land where the JS puts them. The tangent frame (JS: from derivatives, image v runs down)
// comes with the mesh as a tangent, built by the loader in the same convention.
//
// vertex colour = ( tint or plain colour rgb, layer ), uv0 = surface / prop uv, uv1 = aUV2
//   layer 0..3 tiling surface, 100+i prop texture set i, -1 ice, -2 sign, -3 chalkboard, -4 plain, -5 rope, -6 float, -7 scale dial

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"

TEXTURE2D_ARRAY( _SkSurfA ); SAMPLER( sampler_SkSurfA );
TEXTURE2D_ARRAY( _SkSurfN );
TEXTURE2D_ARRAY( _SkSurfR );
TEXTURE2D_ARRAY( _SkPropA ); SAMPLER( sampler_SkPropA );
TEXTURE2D_ARRAY( _SkPropN );
TEXTURE2D_ARRAY( _SkPropR );
TEXTURE2D( _SkSigns ); SAMPLER( sampler_SkSigns );
float4 _TWFrame;              // time, wind speed, night, 0

float SkHash( float3 p ) { return frac( sin( dot( p, float3( 127.1, 311.7, 74.7 ) ) ) * 43758.5453 ); }
float SkNoise( float3 p )
{
	float3 i = floor( p ); float3 f = frac( p ); float3 u = f * f * ( 3.0 - 2.0 * f );
	float a = lerp( lerp( SkHash( i ), SkHash( i + float3( 1, 0, 0 ) ), u.x ), lerp( SkHash( i + float3( 0, 1, 0 ) ), SkHash( i + float3( 1, 1, 0 ) ), u.x ), u.y );
	float b = lerp( lerp( SkHash( i + float3( 0, 0, 1 ) ), SkHash( i + float3( 1, 0, 1 ) ), u.x ), lerp( SkHash( i + float3( 0, 1, 1 ) ), SkHash( i + float3( 1, 1, 1 ) ), u.x ), u.y );
	return lerp( a, b, u.z );
}
float SkFbm( float3 p ) { return SkNoise( p ) * 0.55 + SkNoise( p * 2.13 + 7.1 ) * 0.3 + SkNoise( p * 4.7 + 3.3 ) * 0.15; }
// 2D cellular noise: ( distance to the nearest feature, to the second nearest )
float2 SkCells( float2 p )
{
	float2 i = floor( p ); float2 f = frac( p );
	float d1 = 8.0; float d2 = 8.0;
	for ( int y = -1; y <= 1; y ++ )
	{
		for ( int x = -1; x <= 1; x ++ )
		{
			float2 g = float2( x, y );
			float2 h = float2( SkHash( float3( i + g, 1.7 ) ), SkHash( float3( i + g, 9.3 ) ) );
			float d = length( g + h - f );
			if ( d < d1 ) { d2 = d1; d1 = d; } else if ( d < d2 ) { d2 = d; }
		}
	}
	return float2( d1, d2 );
}
// perturbed normal from a scalar height (screen-space derivatives; the result does not depend on which way the screen's y runs)
float3 SkBump( float3 P, float3 N, float h )
{
	float3 dPdx = ddx( P ); float3 dPdy = ddy( P );
	float3 r1 = cross( dPdy, N ); float3 r2 = cross( N, dPdx );
	float det = dot( dPdx, r1 );
	float3 grad = sign( det ) * ( ddx( h ) * r1 + ddy( h ) * r2 );
	return normalize( abs( det ) * N - grad + N * 1e-12 );
}
// the same with the normal and the result in the Unity frame ( P is in the JS frame: z mirrored )
float3 SkBumpU( float3 P, float3 N, float h )
{
	float3 r = SkBump( P, float3( N.x, N.y, - N.z ), h );
	return float3( r.x, r.y, - r.z );
}

struct StallOut
{
	float3 albedo; float roughness; float metalness; float ao; float3 normal; float3 emissive; float clearcoat;
};

// P: the JS-frame position ( x, y, -z of the world ), N: the world normal ( Unity frame ), T / B: the uv frame in the Unity frame ( B runs the way image v runs down )
StallOut StallSurface( float layer, float3 tint, float2 uv, float2 uv2, float3 P, float3 N0, float3 T, float3 B, float time, float night )
{
	StallOut s;
	float L = round( layer );
	// the derivatives (uniform control flow here, the branches below only sample with them)
	// the textures: the JS samples with v running down the image, Unity's v runs up: flip v and the gradients' v for every sample ( the procedural parts keep the JS uv )
	float2 duv1 = ddx( uv ) * float2( 1, - 1 ); float2 duv2 = ddy( uv ) * float2( 1, - 1 );
	float2 g2x = ddx( uv2 ) * float2( 1, - 1 ); float2 g2y = ddy( uv2 ) * float2( 1, - 1 );
	float2 tuv = float2( uv.x, 1.0 - uv.y );
	float2 tuv2 = float2( uv2.x, 1.0 - uv2.y );
	float3 alb = tint;
	float rough = 0.8;
	float metal = 0.0;
	float ao = 1.0;
	float3 nrm = N0;
	float3 emissive = float3( 0, 0, 0 );
	float clearcoat = 0.0;

	if ( L > -0.5 )
	{
		// scanned texture set: a prop (512 px) or a tiling surface (1K)
		float4 a; float4 n; float4 r;
		if ( L > 99.5 )
		{
			int li = ( int )( L - 100.0 );
			a = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkPropA, sampler_SkPropA, tuv, li, duv1, duv2 );
			n = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkPropN, sampler_SkPropA, tuv, li, duv1, duv2 );
			r = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkPropR, sampler_SkPropA, tuv, li, duv1, duv2 );
		}
		else
		{
			int li = ( int ) L;
			a = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfA, sampler_SkSurfA, tuv, li, duv1, duv2 );
			n = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfN, sampler_SkSurfA, tuv, li, duv1, duv2 );
			r = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfR, sampler_SkSurfA, tuv, li, duv1, duv2 );
		}
		// the corrugated sheets are corrugated in the geometry too: keep only a little of the ribs
		float strength = abs( L - 2.0 ) < 0.5 ? 0.35 : 1.0;
		float3 tn = n.xyz * 2.0 - 1.0;
		tn = float3( tn.xy * strength, max( tn.z, 0.05 ) );
		nrm = normalize( T * tn.x + B * tn.y + N0 * tn.z );
		alb = a.rgb * tint;
		ao = r.r; rough = r.g; metal = r.b;
		// the lantern's glass (prop layer 7: wooden_lantern_01_1): sooty panes that glow warm from dusk, as the village lanterns do (the stall's local
		// light switches on over the same range)
		if ( abs( L - 107.0 ) < 0.5 )
		{
			float lum = dot( a.rgb, float3( 0.3, 0.55, 0.15 ) );
			float pane = smoothstep( 0.32, 0.16, lum ); // the dark panes, not the frame rims
			float nightOn = smoothstep( 0.15, 0.75, night );
			float flicker = sin( time * 9.0 + P.x * 40.0 ) * sin( time * 5.3 + P.z * 13.0 ) * 0.12 + 0.9;
			// lit from inside: the flame shows through the soot, brighter in the middle of each pane
			float soot = lerp( 1.0, 0.35, smoothstep( 0.05, 0.22, lum ) );
			// the flame sits ~19 cm up in the middle: a hot core behind the glass, dim toward the frame
			float2 q = ( uv2 - float2( 0.0, 0.19 ) ) / float2( 0.05, 0.075 );
			float core = exp( - dot( q, q ) );
			float3 glow = float3( 1.0, 0.5, 0.18 ) * 0.35 + float3( 1.0, 0.72, 0.4 ) * 3.0 * core;
			emissive = glow * flicker * nightOn * pane * soot;
			// clean glass over the soot: glossy, a little lighter than the scan by day
			alb = lerp( alb, alb * 1.6 + 0.02, pane );
			rough = lerp( rough, 0.12, pane );
			metal = 0.0;
		}
		// sun-bleached and salty on the upward faces of the stall timber
		if ( L < 1.5 )
		{
			float up = smoothstep( 0.3, 0.95, N0.y );
			float lum = dot( alb, float3( 0.3, 0.55, 0.15 ) );
			alb = lerp( alb, float3( lum, lum, lum ) * 1.25, 0.28 * up + 0.12 );
			rough = clamp( rough + 0.06, 0.0, 1.0 );
		}
	}
	else if ( L > -1.5 )
	{
		// crushed ice: packed chunks with glassy facets, clear meltwater in the gaps
		float2 c = SkCells( P.xz * 34.0 + float2( P.y * 11.0, P.y * 11.0 ) );
		float chunk = smoothstep( 0.0, 0.55, c.y - c.x );
		float frost = SkFbm( P * 90.0 );
		float hgt = chunk * 0.004 + frost * 0.0008;
		nrm = SkBumpU( P, N0, hgt );
		alb = lerp( float3( 0.52, 0.62, 0.66 ), float3( 0.86, 0.92, 0.95 ), chunk * 0.8 + frost * 0.2 );
		rough = lerp( 0.03, 0.28, frost * chunk );
		clearcoat = 1.0;
	}
	else if ( L > -3.5 || ( L > -7.5 && L < -6.5 ) )
	{
		// painted: a sign on peeling timber, a chalkboard, or the scale's enamel dial
		float4 paint = SAMPLE_TEXTURE2D_GRAD( _SkSigns, sampler_SkSigns, tuv2, g2x, g2y );
		if ( L > -2.5 )
		{
			float4 a = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfA, sampler_SkSurfA, tuv, 3, duv1, duv2 );
			float4 n = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfN, sampler_SkSurfA, tuv, 3, duv1, duv2 );
			float4 r = SAMPLE_TEXTURE2D_ARRAY_GRAD( _SkSurfR, sampler_SkSurfA, tuv, 3, duv1, duv2 );
			// the board was painted over: a dark ground coat, the lettering, both chipped and sun-faded
			// chipped through where the old timber shows (its own peeling paint pattern) and at random
			float wear = SkFbm( float3( uv2 * float2( 90.0, 45.0 ), 3.1 ) );
			float chips = smoothstep( 0.64, 0.74, wear + ( 0.45 - dot( a.rgb, float3( 0.33, 0.33, 0.33 ) ) ) * 0.35 );
			float3 ground = lerp( float3( 0.03, 0.062, 0.068 ) * lerp( 0.8, 1.15, wear ), a.rgb * tint, chips );
			float letters = paint.a * ( 1.0 - chips * 0.8 ) * lerp( 0.78, 1.0, SkNoise( float3( uv2 * 400.0, 0.0 ) ) );
			alb = lerp( ground, paint.rgb * 0.82, letters );
			rough = lerp( r.g, 0.62, ( 1.0 - chips ) * 0.8 );
			ao = r.r;
			float3 tn = n.xyz * 2.0 - 1.0;
			float2 txy = tn.xy * lerp( 1.0, 0.45, 1.0 - chips );
			nrm = normalize( T * txy.x + B * txy.y + N0 * tn.z );
		}
		else if ( L > -3.5 )
		{
			// slate with smeared chalk dust, the prices in chalk
			float dust = SkFbm( float3( uv2 * float2( 18.0, 9.0 ), 0.0 ) );
			float chalk = paint.a * lerp( 0.55, 1.0, SkNoise( float3( uv2 * 900.0, 1.0 ) ) );
			alb = lerp( float3( 0.028, 0.034, 0.031 ) + dust * 0.035, float3( 0.72, 0.72, 0.68 ), chalk );
			rough = 0.9;
		}
		else
		{
			// enamel dial face, a little crazed and yellowed
			float crazing = SkNoise( float3( uv2 * 900.0, 2.0 ) );
			alb = lerp( float3( 0.72, 0.68, 0.58 ), paint.rgb, paint.a ) * lerp( 0.92, 1.0, crazing );
			rough = 0.22;
			clearcoat = 1.0;
		}
	}
	else if ( L > -4.5 )
	{
		// plain: colour, roughness and metalness from the vertex data; a little grime and pitting
		float g = SkFbm( P * 14.0 );
		alb = tint * lerp( 0.78, 1.04, g );
		rough = clamp( uv2.x + ( g - 0.5 ) * 0.2, 0.05, 1.0 );
		metal = uv2.y;
		if ( metal > 0.5 )
		{
			float rust = smoothstep( 0.6, 0.8, SkFbm( P * 40.0 + 3.0 ) );
			alb = lerp( alb, float3( 0.2, 0.09, 0.04 ), rust * 0.8 );
			metal = lerp( metal, 0.0, rust );
			rough = lerp( rough, 0.9, rust );
		}
	}
	else if ( L > -5.5 )
	{
		// three-strand laid rope: twisted strands, fibres, grime
		float strand = sin( ( uv.y * 3.0 + uv.x * 22.0 ) * 6.2831853 ) * 0.5 + 0.5;
		float fibre = SkNoise( float3( uv.x * 900.0, uv.y * 40.0, 0.0 ) );
		alb = tint * lerp( 0.55, 1.05, strand ) * lerp( 0.85, 1.05, fibre ) * lerp( 0.8, 1.0, SkFbm( P * 6.0 ) );
		rough = 0.95;
		nrm = SkBumpU( P, N0, strand * 0.0025 + fibre * 0.0003 );
	}
	else
	{
		// sun-faded painted float or fender: chalky, chipped to the white foam, streaky grime
		float fade = SkFbm( P * 9.0 );
		float chip = smoothstep( 0.66, 0.74, SkFbm( P * 55.0 + 2.0 ) );
		float streak = SkFbm( float3( P.x * 30.0, P.y * 3.0, P.z * 30.0 ) );
		float3 chalky = lerp( tint, float3( 1, 1, 1 ) * ( dot( tint, float3( 0.33, 0.33, 0.33 ) ) * 1.1 + 0.08 ), 0.3 + fade * 0.2 );
		alb = lerp( chalky, float3( 0.78, 0.76, 0.7 ), chip ) * lerp( 0.62, 1.0, smoothstep( 0.35, 0.7, streak ) );
		rough = lerp( 0.55, 0.85, fade );
		nrm = SkBumpU( P, N0, - chip * 0.0015 + fade * 0.0006 );
	}

	s.albedo = alb; s.roughness = rough; s.metalness = metal; s.ao = ao; s.normal = nrm; s.emissive = emissive; s.clearcoat = clearcoat;
	return s;
}

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

	float exposure = GetCurrentExposureMultiplier();

	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );
	float3 tangentWS = input.tangentToWorld[ 0 ].xyz;
	float3 bitangentWS = input.tangentToWorld[ 1 ].xyz;
	// the JS position: the sim frame ( x, y, -z )
	float3 aw = GetAbsolutePositionWS( input.positionRWS );
	float3 P = float3( aw.x, aw.y, - aw.z );

	StallOut s = StallSurface( input.color.a, input.color.rgb, input.texCoord0.xy, input.texCoord1.xy, P, normalWS, normalize( tangentWS ), normalize( bitangentWS ), _TWFrame.x, _TWFrame.z );

	surfaceData.normalWS = normalize( s.normal );
	surfaceData.tangentWS = normalize( tangentWS );
	surfaceData.geomNormalWS = normalWS;

	surfaceData.baseColor = s.albedo;
	surfaceData.perceptualSmoothness = 1.0 - saturate( s.roughness );
	surfaceData.metallic = saturate( s.metalness );
	surfaceData.ambientOcclusion = s.ao;

	surfaceData.subsurfaceMask = 0;
	surfaceData.transmissionMask = 0;
	surfaceData.thickness = 1;
	surfaceData.diffusionProfileHash = 0;

	// the ice and the enamel dial are clear-coated
	if ( s.clearcoat > 0.0 )
	{
		surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_CLEAR_COAT;
		surfaceData.coatMask = s.clearcoat;
	}
	else
	{
		surfaceData.materialFeatures = MATERIALFEATUREFLAGS_LIT_STANDARD;
		surfaceData.coatMask = 0.0;
	}

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
	// the lantern's glass, in pre-exposed units
	builtinData.emissiveColor = s.emissive * exposure;

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}
