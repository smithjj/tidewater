#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
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

StructuredBuffer<float4> _TWWaterQuery;   // slot 0 = the camera ( height, nx, nz, sea floor )
TEXTURE2D_X_FLOAT(_UwDepthTex);           // the camera depth buffer, bound by UnderwaterCompositePass
TEXTURE2D_X(_UwSceneTex);                 // a copy of the lit scene colour
float4 _UwParams;                         // shafts strength, meniscus half-width (px), enabled, 0

#define UW_STRADDLE 0.35    // m: closer than this to the surface the near plane can cross it (LENS_REACH)
#define UW_BAND 16          // px: widest meniscus band searched
#define UW_IOR 1.333

// ---- the camera, in sim space
float3 UwCamSim() { float3 c = _WorldSpaceCameraPos.xyz; return float3( c.x, c.y, -c.z ); }

// world (Unity) direction of the ray through a pixel
float3 UwRayWorld( float2 pixel )
{
	float2 uv = pixel * _ScreenSize.zw;
	return normalize( ComputeWorldSpacePosition( uv, 0.5, UNITY_MATRIX_I_VP ) );
}

float3 UwToSim( float3 w ) { return float3( w.x, w.y, -w.z ); }

// distance along the ray (direction dirW) to the lens, the near clip plane
float UwLensAlong( float3 dirW ) { return _ProjectionParams.y / max( dot( dirW, -UNITY_MATRIX_V[ 2 ].xyz ), 1e-3 ); }

// medium at the lens for a pixel: 1 water, 0 air (underwaterMedium)
float UwMediumAt( float2 pixel, float3 camSim, float4 st )
{
	float camH = camSim.y - st.x;
	float water = camH < 0.0 ? 1.0 : 0.0;
	if ( abs( camH ) < UW_STRADDLE )
	{
		float3 dW = UwRayWorld( pixel );
		float3 q = camSim + UwToSim( dW ) * UwLensAlong( dW );
		float3 n = float3( st.y, sqrt( max( 1.0 - st.y * st.y - st.z * st.z, 0.01 ) ), st.z );
		float3 c0 = float3( camSim.x, st.x, camSim.z );
		water = dot( n, q - c0 ) < 0.0 ? 1.0 : 0.0;
	}
	return water;
}

// the scene medium integral between the eye and a point: light arriving at depth z is E0 exp(-sigT z / m); along the ray
// z(s) = zc - dir.y s, seen through exp(-sigT s). (exp(-a) - exp(-(a + k d))) / k with a = sigT zc / m, k = sigT (1 - dir.y / m):
// both exponents stay <= 0 while the ray is in the water, so it can't overflow looking up through deep water
float3 UwLit( float m, float3 sigT, float zc, float dirY, float dist )
{
	float3 a = sigT * zc / m;
	float3 kk = sigT * ( 1.0 - dirY / m );
	float3 e0 = exp( -a );
	float3 e1 = exp( -max( a + kk * dist, 0.0 ) );
	return abs( kk ) < 1e-4 ? e0 * dist : ( e0 - e1 ) / kk;
}

float UwIgn( float2 px ) { return frac( frac( dot( px, float2( 0.06711056, 0.00583715 ) ) ) * 52.9829189 ); }

float3 UwSceneAt( float2 pixel )
{
	int2 size = ( int2 ) _ScreenSize.xy;
	return LOAD_TEXTURE2D_X( _UwSceneTex, clamp( ( int2 ) pixel, 0, size - 1 ) ).rgb;
}

float4 Frag( Varyings varyings ) : SV_Target
{
	float2 pixel = varyings.positionCS.xy;
	int2 pc = ( int2 ) pixel;
	int2 size = ( int2 ) _ScreenSize.xy;
	float4 st = _TWWaterQuery[ 0 ];
	float3 camSim = UwCamSim();
	float camH = camSim.y - st.x;
	float exposure = GetCurrentExposureMultiplier();
	float3 skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	float3 sunLight = _TWSunColor.rgb * exposure;

	float here = UwMediumAt( pixel, camSim, st );
	bool under = here > 0.5;
	int dbgMode = ( int ) _UwParams.w;
	// debug: 1 = ( medium here, camera water height / 10 + 0.5, camera height / 10 + 0.5 ), 2 = scene distance / 600, 3 = raw depth
	if ( dbgMode == 1 ) return float4( here, st.x * 0.1 + 0.5, camSim.y * 0.1 + 0.5, 1.0 );
	if ( dbgMode == 2 || dbgMode == 3 )
	{
		float rawD = LOAD_TEXTURE2D_X( _UwDepthTex, pc ).x;
		float3 pr = ComputeWorldSpacePosition( pixel * _ScreenSize.zw, rawD, UNITY_MATRIX_I_VP );
		return dbgMode == 2 ? float4( length( pr ) / 600.0, 0, 0, 1 ) : float4( rawD * 100.0, 0, 0, 1 );
	}

	// distance to the waterline on the lens in pixels, searched vertically (the line runs roughly across the screen: no camera
	// roll, and over the few cm of the lens the surface is a plane)
	float lineDist = 1e4;
	float lineDir = 0.0; // pixel y direction toward the other medium
	if ( abs( camH ) < UW_STRADDLE )
	{
		float flips = abs( UwMediumAt( pixel + float2( 0, -8 ), camSim, st ) - here ) + abs( UwMediumAt( pixel + float2( 0, 8 ), camSim, st ) - here )
			+ abs( UwMediumAt( pixel + float2( 0, -UW_BAND ), camSim, st ) - here ) + abs( UwMediumAt( pixel + float2( 0, UW_BAND ), camSim, st ) - here );
		if ( flips > 0.5 )
		{
			for ( int i = 1; i < UW_BAND + 1; i ++ )
			{
				bool up = abs( UwMediumAt( pixel + float2( 0, -i ), camSim, st ) - here ) > 0.5;
				bool down = abs( UwMediumAt( pixel + float2( 0, i ), camSim, st ) - here ) > 0.5;
				if ( lineDist > 1e3 && ( up || down ) )
				{
					lineDist = float( i ) - 0.5;
					lineDir = up ? -1.0 : 1.0;
				}
			}
		}
	}

	// ---- meniscus band: rounded water edge acting like a cylindrical lens; samples are pushed away from the line on both sides
	float bandW = _UwParams.y;
	float tBand = clamp( lineDist / bandW, 0.0, 1.0 );
	float bendPx = tBand * sqrt( max( 1.0 - tBand * tBand, 0.0 ) ) * bandW * 0.8;
	float3 base = UwSceneAt( pixel - float2( 0.0, lineDir * bendPx ) );
	float3 result = base;

	if ( under && _UwParams.z > 0.5 )
	{
		// scene distance along this pixel; the water between the eye and the lens is clipped away: the medium starts at the lens
		float3 dirW = UwRayWorld( pixel );
		float3 dir = UwToSim( dirW );
		float raw = LOAD_TEXTURE2D_X( _UwDepthTex, pc ).x;
		float sceneDist = 600.0;
		if ( raw != UNITY_RAW_FAR_CLIP_VALUE )
		{
			float2 uv = pixel * _ScreenSize.zw;
			sceneDist = min( length( ComputeWorldSpacePosition( uv, raw, UNITY_MATRIX_I_VP ) ), 600.0 );
		}
		float dist = max( sceneDist - UwLensAlong( dirW ), 0.0 );

		float zc = max( st.x - camSim.y, 0.0 ); // camera depth below the surface
		float3 sigA = _TWWaterAbsorption.rgb;
		float3 sigS = _TWWaterScattering.rgb;
		float3 sigT = sigA + sigS;

		float3 sunDir = normalize( _TWSunDir.xyz );
		float3 Ls = -refract( -sunDir, float3( 0.0, 1.0, 0.0 ), 1.0 / UW_IOR ); // toward the sun, underwater
		float mu = max( Ls.y, 0.15 );
		float cosPh = dot( dir, Ls );
		float g = 0.85;
		float phase = ( 1.0 - g * g ) / ( 4.0 * PI ) / pow( max( 1.0 + g * g - cosPh * 2.0 * g, 1e-4 ), 1.5 ) * 0.75 + 0.25 / ( 4.0 * PI );

		float3 sunE = sunLight * 0.96;
		float3 ambE = skyIrradiance * PI * 0.9;

		float bb = sigS.x * 0.035;
		float3 bbv = sigS * 0.035;
		float3 msAlb = bbv * 1.3 / ( sigA + bbv );
		float3 inSun = sunE * ( sigS * phase + msAlb * sigT * ( 1.0 / PI ) ) * UwLit( mu, sigT, zc, dir.y, dist );
		float3 inAmb = ambE * ( sigS * ( 1.0 / ( 4.0 * PI ) ) + msAlb * sigT * ( 1.0 / PI ) ) * UwLit( 0.8, sigT, zc, dir.y, dist );

		// caustic light shafts: the caustics of the sun along the view ray (the JS marches at half resolution, with the torch beam)
		float3 shafts = 0.0;
		if ( _UwParams.x > 0.0 && dist > 0.0 )
		{
			const int steps = 16;
			float maxD = min( dist, 22.0 );
			float ds = maxD / steps;
			// interleaved gradient noise on the pixel grid, moved every frame (Jimenez 2014)
			float jitter = UwIgn( pixel + float( _FrameCount % 64 ) * 5.588238 );
			// gusts / slicks vary over hundreds of metres: one sample for the whole march
			float detK = CausticsDetailK( camSim.xz + dir.xz * ( maxD * 0.5 ) );
			for ( int i = 0; i < steps; i ++ )
			{
				float s = ( float( i ) + jitter ) * ds;
				float3 p = camSim + dir * s;
				float z = max( st.x - p.y, 0.01 );
				float3 caus = CausticsSampleShaft( p, z, 1.5, detK );
				float3 Tl = exp( -sigT * ( s + z / mu ) );
				shafts += ( caus - 1.0 ) * Tl * ds;
			}
			shafts = max( shafts * sunE * sigS * phase * _UwParams.x * 2.5, 0.0 );
		}

		float3 T = exp( -sigT * dist );
		result = base * T + inSun + inAmb + shafts;
	}

	// meniscus contact line and bright rim
	float lineW = smoothstep( 2.0, 0.0, lineDist );
	float rim = smoothstep( bandW, bandW * 0.4, lineDist ) * smoothstep( 0.5, 2.5, lineDist );
	float3 withLine = result * ( 1.0 - lineW * 0.55 ) + rim * 0.15 * ( skyIrradiance * 2.5 + sunLight * 0.02 );
	return float4( withLine, 1.0 );
}
