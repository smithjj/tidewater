// Glue between HDRP's pass templates and the ported boat materials: the vertex animation (ensign, whip antennas) and
// GetSurfaceAndBuiltinData, which runs the surface of BoatSurface.hlsl and fills HDRP's SurfaceData. Modelled on HDRP's LitData.hlsl.
//
// Handedness: the boat model is built in the JS frame (+X port, right-handed); the Unity meshes mirror x, so the JS model position of a
// vertex is ( -x, y, z ) of its object-space position.

#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMapping.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/MaterialUtilities.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"
#include "BoatSurface.hlsl"

float _BoatKind;              // 0 hull, 1 gelcoat, 2 wood, 3 fittings, 4 glass, 5 glow, 6 trap, 7 glTF factors (the mini fishing boat, the Pelagic 30), 8 game props (the rod)
float4 _FacColor;             // kind 7: base colour (linear)
float4 _FacPbr;               // kind 7: roughness, metalness, clear coat, clear coat roughness
float4 _FacEmissive;          // kind 7: emissive colour (linear, emissive strength applied)
float _AlphaCutoff;
float4 _FlagPivot;            // the ensign's hoist (boat frame)
float4 _FlagDir;              // streaming direction (boat frame, unit)
float _FlagWind;              // 0 limp .. 1 stiff
float _NavOn;
float4 _RodBend;              // kind 8: xyz bend direction (rod space), w bend (FishingRod.js rodBend)
float4 _RodShape;             // x: bend exponent (fast action)
float4 _ReelAnim;             // rotor angle, bail open 0..1, crank angle, spool angle
float4 _ReelAnim2;            // spool oscillation (m), line fill
float4 _TWFrame;              // time, wind speed, night, 0
float4 _TWSunDir;
float4 _TWSunColor;

#if !defined(SHADER_STAGE_RAY_TRACING)
#ifdef HAVE_MESH_MODIFICATION

AttributesMesh ApplyMeshModification( AttributesMesh input, float3 timeParameters )
{
	// the fittings material animates pattern 3 (whip antennas sway) and pattern 2 (the ensign streams from its staff)
	if ( ( int ) _BoatKind == 3 )
	{
		float4 aux = float4( input.uv1, input.uv2 );
		float3 p = float3( -input.positionOS.x, input.positionOS.y, input.positionOS.z );
		float t = _TWFrame.x;
		float wA = aux.w * aux.w;
		float phase = p.x * 3.1 + p.z * 1.7;
		float gust = _TWFrame.y * 0.06 + 0.5;
		float3 sway = float3( sin( t * 1.9 + phase ), 0.0, sin( t * 1.37 + phase * 1.3 ) * 0.6 ) * ( wA * 0.12 * gust );

		float3 rel = p - _FlagPivot.xyz;
		float along = max( -rel.z, 0.0 );
		float fu = aux.w; // 0 at the hoist .. 1 at the fly
		float droop = ( 1.0 - _FlagWind ) * 1.15 * ( fu * 0.5 + 0.5 );
		float3 lat = float3( _FlagDir.z, 0.0, -_FlagDir.x );
		float flutter = sin( fu * 9.0 - t * ( _FlagWind * 9.0 + 4.0 ) + rel.y * 4.0 ) * fu * ( _FlagWind * 0.05 + 0.015 );
		float3 flagPos = float3( _FlagPivot.x, 0.0, _FlagPivot.z )
			+ _FlagDir.xyz * ( along * cos( droop ) )
			+ float3( 0.0, rel.y - along * sin( droop ), 0.0 )
			+ lat * flutter;

		float3 np = lerp( p + sway * BoatIsPattern( aux.z, 3.0 ), flagPos, BoatIsPattern( aux.z, 2.0 ) );
		input.positionOS = float3( -np.x, np.y, np.z );
	}

	// the fishing rod (FishingRod.js vertex snippet): moving parts tagged in aux.w ( 1 rotor, 2 bail, 3 crank, 4 spool, 5 braid ), then the blank
	// bends toward the line. Rod space is the JS frame ( +Y along the blank, the reel toward -Z, +X right ): the mesh is mirrored in x.
	if ( ( int ) _BoatKind == 8 )
	{
		const float ROD_L = 2.13, BLANK_START = 0.535, REEL_Z = -0.092, BODY_Y = 0.327, PIVOT_Y = 0.403;
		float part = input.uv2.y;
		float3 P = float3( -input.positionOS.x, input.positionOS.y, input.positionOS.z );
		float3 Nn = float3( -input.normalOS.x, input.normalOS.y, input.normalOS.z );
		float3 axisC = float3( 0.0, 0.0, REEL_Z );
		if ( part > 0.5 )
		{
			if ( part < 2.5 )
			{
				if ( part > 1.5 )
				{
					// the bail flips back about the line through its two pivots
					float a = -_ReelAnim.y * 1.95;
					float3 c = float3( 0.0, PIVOT_Y, REEL_Z );
					float ca = cos( a ); float sa = sin( a );
					float3 q = P - c;
					P = c + float3( q.x, q.y * ca - q.z * sa, q.y * sa + q.z * ca );
					Nn = float3( Nn.x, Nn.y * ca - Nn.z * sa, Nn.y * sa + Nn.z * ca );
				}
				// rotor (and the bail on it) turn about the reel axis
				float a = _ReelAnim.x;
				float ca = cos( a ); float sa = sin( a );
				float3 q = P - axisC;
				P = float3( q.x * ca + q.z * sa, P.y, -q.x * sa + q.z * ca ) + float3( 0.0, 0.0, axisC.z );
				Nn = float3( Nn.x * ca + Nn.z * sa, Nn.y, -Nn.x * sa + Nn.z * ca );
			}
			else if ( part < 3.5 )
			{
				// crank handle about its shaft (along x)
				float a = _ReelAnim.z;
				float3 c = float3( 0.0, BODY_Y, REEL_Z );
				float ca = cos( a ); float sa = sin( a );
				float3 q = P - c;
				P = c + float3( q.x, q.y * ca - q.z * sa, q.y * sa + q.z * ca );
				Nn = float3( Nn.x, Nn.y * ca - Nn.z * sa, Nn.y * sa + Nn.z * ca );
			}
			else
			{
				// spool: in and out with the crank, turning back when the drag slips; the braid on it shrinks as line goes out
				float3 q = P - axisC;
				if ( part > 4.5 )
				{
					float r = length( q.xz );
					float r2 = lerp( 0.0205, r, _ReelAnim2.y );
					q = float3( q.x * r2 / max( r, 1e-5 ), q.y, q.z * r2 / max( r, 1e-5 ) );
				}
				float a = _ReelAnim.w;
				float ca = cos( a ); float sa = sin( a );
				P = float3( q.x * ca + q.z * sa, P.y + _ReelAnim2.x, -q.x * sa + q.z * ca ) + float3( 0.0, 0.0, axisC.z );
				Nn = float3( Nn.x * ca + Nn.z * sa, Nn.y, -Nn.x * sa + Nn.z * ca );
			}
		}
		// the blank bends toward the line (fast action: _RodShape.x = exponent of the deflection)
		float span = ROD_L - BLANK_START;
		float s = clamp( ( P.y - BLANK_START ) / span, 0.0, 1.0 );
		float pw = _RodShape.x;
		float lat = _RodBend.w * ROD_L * pow( s, pw );
		float slope = _RodBend.w * ROD_L * pw * pow( max( s, 1e-4 ), pw - 1.0 ) / span;
		float drop = 0.5 * lat * lat / ( max( P.y - BLANK_START, 0.0 ) + 0.06 );
		P = P + _RodBend.xyz * lat - float3( 0.0, drop, 0.0 );
		Nn = normalize( Nn - float3( 0.0, slope * dot( Nn, _RodBend.xyz ), 0.0 ) );
		input.positionOS = float3( -P.x, P.y, P.z );
		input.normalOS = float3( -Nn.x, Nn.y, Nn.z );
	}

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


	int kind = ( int ) _BoatKind;
	float exposure = GetCurrentExposureMultiplier();

	float3 posOS = TransformWorldToObject( input.positionRWS );
	float3 normalWS = normalize( input.tangentToWorld[ 2 ] );
	// double-sided materials (glass, traps, the glTF boat): the normal faces the viewer
	if ( kind == 4 || kind == 6 || kind == 7 ) { normalWS = input.isFrontFace ? normalWS : -normalWS; }

	BoatIn bi;
	bi.aux = float4( input.texCoord1.xy, input.texCoord2.xy );
	bi.color = input.color.rgb;
	bi.uv = input.texCoord0.xy;
	bi.p = float3( -posOS.x, posOS.y, posOS.z );
	bi.rest = float3( input.texCoord0.xy, input.texCoord3.x );
	bi.P = input.positionRWS;
	bi.N = normalWS;
	bi.time = _TWFrame.x;
	bi.night = _TWFrame.z;
	bi.skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	bi.sunColor = _TWSunColor.rgb * exposure;
	bi.sunDirY = normalize( _TWSunDir.xyz ).y;
	bi.navOn = _NavOn;

	BoatOut s = BoatSurface( kind, bi );
	if ( kind == 7 )
	{
		// a factor-based glTF material (MiniFishingBoat.js / Pelagic30.js _material): colour (its alpha is the opacity of the windshield glass, 1
		// otherwise), roughness, metalness, emissive, clear coat
		s = BoatDefaults( bi, _FacPbr.x );
		s.albedo = _FacColor.rgb; s.alpha = _FacColor.a; s.metalness = _FacPbr.y; s.emissive = _FacEmissive.rgb; s.clearcoat = _FacPbr.z; s.coatRoughness = _FacPbr.w;
	}

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

	// the hull (and a glTF material with a clear coat) is clear-coated
	if ( kind == 0 || kind == 8 || ( kind == 7 && _FacPbr.z > 0.0 ) )
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

	// transparency parameters
	surfaceData.ior = 1.5;
	surfaceData.transmittanceColor = float3( 1.0, 1.0, 1.0 );
	surfaceData.atDistance = 1000000.0;
	surfaceData.transmittanceMask = 0.0;

	surfaceData.specularOcclusion = 1.0;

	float3 bentNormalWS = surfaceData.normalWS;

	GetBuiltinData( input, V, posInput, surfaceData, s.alpha, bentNormalWS, 0, builtinData );
	// emissive surfaces (lights, screens, dials) in pre-exposed units
	builtinData.emissiveColor = s.emissive * exposure;

	RAY_TRACING_OPTIONAL_ALPHA_TEST_PASS
}
