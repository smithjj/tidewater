// Port of src/ocean/WaterSurface.js and src/ocean/WaterMaterial.js (see Water.shader for the structure). All the
// maths is in SIM space (x east, y up, z south; see Util/Sim.cs): the vertex stage mirrors z into the Unity world
// for rasterisation, and the fragment converts only where it talks to HDRP (sky cubemap, projection, fog).
//
// Units: values taken from HDRP (the colour pyramid, the sky cubemap, the ambient probe, fog) are in exposed units;
// the sun is given as lux (_TWSunColor) and the shader applies the exposure itself, so the same white surface gets the
// same colour here as in the lit terrain next to it.

float4 _TWWind;               // sim wind direction xz, speed (m/s)
float4 _TWSunDir;             // toward the sun, sim space
#include "../Terrain/TerrainHeight.hlsl"
#include "../Common/CDLOD.hlsl"
#include "OceanCommon.hlsl"
#include "../Common/Noise.hlsl"
#include "ShoreWaves.hlsl"
#include "ShoreSim.hlsl"
#include "SurfFoam.hlsl"
#include "Wake.hlsl"

float4 _TWFrame;   // time, wind speed, night, 0 (OceanRenderer.PublishSun)
#include "SeaDetail.hlsl"

TEXTURE2D_ARRAY(_TWOceanDisp);  SAMPLER(sampler_TWOceanDisp);    // (Dx, Dy, Dz, foam) per cascade, mipmapped
TEXTURE2D_ARRAY(_TWOceanDeriv); SAMPLER(sampler_TWOceanDeriv);   // (dDy/dx, dDy/dz, dDx/dx, dDz/dz), aniso 4
TEXTURE2D(_TWHullMask);                                           // camera distance of the nearest hull-volume face per pixel (HullMask.cs, 0 = none)
float4 _TWHullParams;                                             // active, width, height
TEXTURE2D(_TWFoamTex); SAMPLER(sampler_TWFoamTex);               // the tileable foam pattern (aniso)

float4 _TWOceanParams;        // choppiness, foamBias, cascades, 0
float4 _TWOceanLodMorph[16];  // per LOD: morph start, 1 / morph range, grid spacing, 0
float4 _TWViewPos;            // view camera, sim space (the morph centre)

// WaterSurface params: amplitude, slopeScale, foamCoverage, foamSharpness
float4 _TWWaterA;
// foamScale (pattern repeats per metre), backscatter, sss, foamIntensity
float4 _TWWaterB;
// waterRoughness, reflectionStrength, ssr (on/off), seaLevel
float4 _TWWaterC;
float4 _TWWaterAbsorption;    // frame.waterAbsorption (1/m)
float4 _TWWaterScattering;    // frame.waterScattering (1/m)
float4 _TWSunColor;           // sun illuminance (lux) x colour
float4 _TWDebug;              // x = debug view (see the end of Frag)
float4 _TWCamera;             // xyz = sim camera position
// the local lights (LocalLightsView.PublishWater): the torch first, then the lamps nearest the camera, packed as in LocalLights.js
// pos = ( sim position, range^2 ), col = colour x intensity in lux ( + cos inner ), dir = ( sim axis, cos outer )
float4 _TWLampPos[8];
float4 _TWLampCol[8];
float4 _TWLampDir[8];
float4 _TWLampN;              // x = count
StructuredBuffer<float4> _TWWaterQuery;   // WaterQuery results: slot 0 = the camera ( height, nx, nz, sea floor )

#define TW_IOR 1.333
#define TW_WATER_BEHIND 0.05   // a refracted sample is usable when it lies this far behind the water surface (view depth, m)

UNITY_INSTANCING_BUFFER_START(TWWater)
UNITY_DEFINE_INSTANCED_PROP(float4, _TWNodeData)   // ( origin x, origin z, size, lod ) in sim space
UNITY_INSTANCING_BUFFER_END(TWWater)

// ------------------------------------------------------------------ helpers

float TWLuminance3( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

float TWSat( float x ) { return saturate( x ); }

// LocalLights.js localLightsSpotProfile: hot centre, soft edge, faint spill
float WaterLampSpot( float cd, float cosInner, float cosOuter )
{
	float m = smoothstep( cosOuter, cosInner, cd );
	return max( m * m, smoothstep( cosOuter - 0.55, cosOuter, cd ) * 0.05 );
}

float3 SimToWorld( float3 p ) { return float3( p.x, p.y, -p.z ); }  // sim <-> Unity world (an involution)

// exact unpolarized dielectric Fresnel, cosI > 0, eta = n2/n1
float FresnelDielectric( float cosI, float eta )
{
	float c = clamp( cosI, 0.0, 1.0 );
	float g2 = eta * eta - 1.0 + c * c;
	bool tir = g2 < 0.0;
	float g = sqrt( max( g2, 0.0 ) );
	float a = ( g - c ) / ( g + c );
	float b = ( c * ( g + c ) - 1.0 ) / ( c * ( g - c ) + 1.0 );
	return tir ? 1.0 : 0.5 * ( a * a ) * ( b * b + 1.0 );
}

// Henyey-Greenstein
float WaterPhaseHG( float cosT, float g )
{
	float g2 = g * g;
	return ( ( 1.0 - g2 ) / ( 4.0 * PI ) ) / pow( max( 1.0 + g2 - cosT * 2.0 * g, 1e-4 ), 1.5 );
}

float WaterDGGX( float NdH, float a2 )
{
	float d = NdH * NdH * ( a2 - 1.0 ) + 1.0;
	return a2 / ( d * d * PI );
}

float WaterVSmithGGX( float NdL, float NdV, float a2 )
{
	float gv = NdL * sqrt( NdV * NdV * ( 1.0 - a2 ) + a2 );
	float gl = NdV * sqrt( NdL * NdL * ( 1.0 - a2 ) + a2 );
	return 0.5 / max( gv + gl, 1e-5 );
}

// the sky as the HDRP environment sees it; direction in sim space. The cubemap is in physical units: the exposure is
// applied here (as HDRP does when a surface reflects it).
float3 TWSkyRadiance( float3 dirSim )
{
	return SampleSkyTexture( SimToWorld( dirSim ), 0.0, 0 ).rgb * GetCurrentExposureMultiplier();
}

// ------------------------------------------------------------------ vertex

struct Attributes
{
	float3 positionOS : POSITION;
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
	float4 positionCS : SV_POSITION;
	float3 positionRWS : TEXCOORD0;
	float2 lagXZ : TEXCOORD1;
	float4 misc : TEXCOORD2;   // wave height, sea depth, foam, shore (surf) foam
	float4 shoreNS : TEXCOORD3; // shore normal xyz, swash
	float2 surfMask : TEXCOORD4; // clear plunging face, whitewater roller relief (m)
	UNITY_VERTEX_INPUT_INSTANCE_ID
};

static const float TW_FOAM_WEIGHTS[ 4 ] = { 0.35, 0.45, 0.5, 0.25 };   // per-cascade contribution to the foam coverage

Varyings Vert( Attributes input )
{
	Varyings o;
	UNITY_SETUP_INSTANCE_ID( input );
	UNITY_TRANSFER_INSTANCE_ID( input, o );

	float4 node = UNITY_ACCESS_INSTANCED_PROP( TWWater, _TWNodeData );
	float seaLevel = _TWWaterC.w;
	TWCdlodVertex lod = TWCdlodMorph( node, input.positionOS.xz, _TWOceanLodMorph[ ( int ) node.w ], _TWViewPos.xyz, 0.0 );
	float2 worldXZ = lod.worldXZ;
	float spacing = lod.spacing;
	float ground = TWHeightAt( worldXZ );
	float depth = seaLevel - ground;

	float3 disp = 0.0;
	float foam = 0.0;
	[unroll]
	for ( int c = 0; c < 4; c ++ )
	{
		float L = _TWOceanSizes[ c ].x;
		float texel = L / 256.0;
		// band-limit to the mesh spacing to avoid aliasing / swimming
		float level = max( log2( spacing / texel ) + 0.7, 0.0 );
		float att = WaterSurfaceCascadeAttenuation( c, depth );
		float2 uv = worldXZ / L;
		float4 s = SAMPLE_TEXTURE2D_ARRAY_LOD( _TWOceanDisp, sampler_TWOceanDisp, uv, c, level );
		disp += s.xyz * att;
		// foam coverage is smooth enough to evaluate per vertex (sampled at a fixed detail level, the displacement
		// sample itself from there on)
		float fv = s.w;
		if ( level < 1.5 ) { fv = SAMPLE_TEXTURE2D_ARRAY_LOD( _TWOceanDisp, sampler_TWOceanDisp, uv, c, 1.5 ).w; }
		foam += fv * TW_FOAM_WEIGHTS[ c ] * att;
	}

	disp *= _TWWaterA.x; // amplitude

	float3 extra = 0.0;
	float3 shoreN = float3( 0.0, 1.0, 0.0 );
	float shoreFoam = 0.0;
	float swash = 0.0;
	float2 surfMask = 0.0; // clear plunging face, whitewater roller relief (m)

	// Offshore of WATER_SHORE_DEEP the shore waves have faded out completely (their envelope is 0 from 26 m of depth, see
	// ShoreWaves) and there is no swash: most of the sea skips their evaluation.
	bool nearShore = depth < 26.0;
	float swashLevel = -1e4;
	if ( nearShore )
	{
		ShoreSample sw = ShoreEvaluate( worldXZ, depth, ground );
		extra += sw.disp;
		shoreN = clamp( sw.nShore, -1.0, 1.0 );
		// (the foam line on the swash front is added per pixel in the water shader: on this coarse mesh it would end short
		// of the front and follow the triangles)
		shoreFoam = sw.foam;
		surfMask = float2( sw.face, sw.roller );
		swashLevel = sw.swashLevel;
	}
	extra += WakeDisplacement( worldXZ );

	float3 total = disp + extra;
	float y = seaLevel + total.y;
	if ( nearShore )
	{
		// thin run-up sheet on the sand: take whichever surface is higher (smooth max)
		float k = 0.04;
		// no run-up sheet on steep rock (cliffs, sea stacks): waves break against it instead
		float4 nr = TWNormalRockLevel( worldXZ, 0.0 );
		float gentle = smoothstep( 0.45, 0.25, length( nr.xy ) );
		float hmx = TWSat( ( swashLevel - y ) / k * 0.5 + 0.5 ) * gentle;
		float smax = lerp( y, swashLevel, hmx ) + hmx * ( 1.0 - hmx ) * k;
		swash = smoothstep( -0.02, 0.03, swashLevel - y );
		y = smax;
		// Where the sheet is the surface it is the sheet that is seen, not the wave below it: the sheet lies on the sand (the
		// sand's slope, no horizontal wave motion, no plunging face / roller). Otherwise the backwash sheet over the lower
		// beach face, exposed by the trough of the next wave, keeps the trough's tilted normal and motion and reads as a
		// separate dark strip between the sea and the thin film further up.
		shoreN = normalize( lerp( shoreN, float3( nr.x, 1.0, nr.y ), hmx ) );
		float still = 1.0 - hmx;
		total = float3( total.x * still, total.y, total.z * still );
		surfMask *= still;
	}

	// hide the water sheet below dry land (beyond the swash zone)
	float below = depth < -3.0 ? min( ground - 2.0, seaLevel - 1.0 ) : ground - 0.06;
	y = y < ground ? min( y, below ) : y;

	float3 posSim = float3( worldXZ.x + total.x, y, worldXZ.y + total.z );
	float3 posWS = SimToWorld( posSim );
	o.positionRWS = GetCameraRelativePositionWS( posWS );
	o.positionCS = TransformWorldToHClip( o.positionRWS );
	o.lagXZ = worldXZ;
	o.misc = float4( total.y, depth, foam, shoreFoam );
	o.shoreNS = float4( shoreN, swash );
	o.surfMask = surfMask;
	return o;
}

// ------------------------------------------------------------------ surface (WaterSurface.fragment)

struct WaterSurfaceFrag
{
	float3 normal;
	float foam;
	float coverage;
	float2 slopes;
	float jacobian;
	float rough;
	float aeration;
	float gust;
	float slick;
	SurfFoamInfo foamInfo;
};

float2 Rot( float2 v, float a )
{
	float c = cos( a ), s = sin( a );
	return float2( v.x * c - v.y * s, v.x * s + v.y * c );
}

// extraFoam: foam carried by the water (ShoreSim; not ported yet); surfMask: clear face of a plunging wave, whitewater roller
// relief (from the vertex stage)
WaterSurfaceFrag WaterSurfaceFragment( float2 lagXZ, float footprint, float depth, float vertexFoam, float3 shoreN, float shoreFoam, float extraFoam, float4 simState, float2 surfMask, float3 P )
{
	float4 d = 0.0;
	float foamSum = 0.0;
	// the clear concave face of a plunging wave overhangs the trough: the foam carried by the (depth-averaged, world-space)
	// shore simulation below it is not on the face
	float face = TWSat( surfMask.x );
	// (some of it stays: the lace of the previous wave is drawn up the face)
	float simFoam = extraFoam * ( 1.0 - face * 0.72 );
	foamSum += simFoam;
	// bubbles mixed into the water (milky, turquoise, hides the bottom): surf and wake
	float aeration = 0.0;
	// world-space gusts / slicks modulate the short wind waves (non-repeating dark and bright patches)
	SeaDetailSampleOut det = SeaDetailSampleAt( lagXZ );
	float rough = det.rough;

	[unroll]
	for ( int c = 0; c < 4; c ++ )
	{
		float att = WaterSurfaceCascadeAttenuation( c, depth );
		if ( c >= 2 ) att *= rough;
		else if ( c == 1 ) att *= lerp( 1.0, rough, 0.4 );
		d += SAMPLE_TEXTURE2D_ARRAY( _TWOceanDeriv, sampler_TWOceanDeriv, lagXZ / _TWOceanSizes[ c ].x, c ) * att;
	}

	d *= _TWWaterA.x;
	float2 slopes = float2( d.x / max( d.z + 1.0, 0.2 ), d.y / max( d.w + 1.0, 0.2 ) );

	// Near-field capillary ripples. Within a few metres of the camera a pixel covers less than the finest cascade's
	// texel (~3 cm), so the surface looks glassy. Re-sample that cascade at ~1 m and ~2.3 m tiles (rotated, so they
	// never line up with it) wherever the footprint is small. Explicit LOD: this runs in a branch.
	float near = smoothstep( 0.04, 0.01, footprint ) * rough;
	if ( near > 0.002 )
	{
		const float k1 = 7.3, k2 = 3.1;
		float Lf = _TWOceanSizes[ 3 ].x;
		float texel1 = Lf / k1 / 256.0, texel2 = Lf / k2 / 256.0;
		float4 c1 = SAMPLE_TEXTURE2D_ARRAY_LOD( _TWOceanDeriv, sampler_TWOceanDeriv, Rot( lagXZ, 0.63 ) * ( k1 / Lf ), 3, max( log2( footprint / texel1 ), 0.0 ) );
		float4 c2 = SAMPLE_TEXTURE2D_ARRAY_LOD( _TWOceanDeriv, sampler_TWOceanDeriv, Rot( lagXZ, 2.14 ) * ( k2 / Lf ), 3, max( log2( footprint / texel2 ), 0.0 ) );
		// gradients back into world axes (transpose of the rotation)
		float2 g = Rot( c1.xy, -0.63 ) * 0.55 + Rot( c2.xy, -2.14 ) * 0.35;
		slopes += g * near;
	}

	float jac = ( d.z + 1.0 ) * ( d.w + 1.0 );
	{
		WakeFrag w = WakeFragment( lagXZ, _TWFrame.x );
		slopes += w.slopes;
		foamSum += w.foam;
		aeration += w.aeration;
	}

	// base normal: large shoreline waves (per-vertex, can overhang) perturbed by FFT detail
	float3 normal;
	float3 Ns_base;
	{
		// On a coarse mesh the shore normal can flip between the vertices of a folding crest: the interpolated vector then
		// cancels out (or is NaN). Keep it finite and facing up; NaN would otherwise surface as a white-hot cell after the
		// output clamp.
		float3 sn = clamp( shoreN, -1.0, 1.0 ) + float3( 0.0, 1e-3, 0.0 );
		float3 Ns0 = sn / max( length( sn ), 1e-4 );
		float3 Ns = normalize( float3( Ns0.x, max( Ns0.y, 0.12 ), Ns0.z ) );
		Ns_base = Ns;
		// the ripples and chop ride on the wave: the detail normal is rotated onto the tilted face (reoriented normal
		// mapping) instead of being flattened by it, so a steep face keeps the full texture of the sea surface rather than
		// turning into smooth plastic
		float3 nd = normalize( float3( -slopes.x, 1.0, -slopes.y ) );
		float3 tq = Ns + float3( 0.0, 1.0, 0.0 );
		float3 uq = float3( slopes.x, 1.0, slopes.y ) * nd.y;
		normal = normalize( tq * ( dot( tq, uq ) / tq.y ) - uq );
		foamSum += shoreFoam * 0.55; // ShoreSim carries the rest of the surf foam
		// the roller and the water behind the plunge point are full of bubbles, decaying behind the bore with the foam it
		// sheds; the clear face of a plunging wave is not
		aeration += TWSat( shoreFoam * 1.2 + simFoam * 0.7 ) * ( 1.0 - face ) * smoothstep( -0.1, 0.3, depth );
	}

	// whitecaps: persistent (per vertex) + fresh where the surface is compressed right now
	float fresh = TWSat( ( _TWOceanParams.y - 0.15 - jac ) * 2.0 );
	float whitecaps = vertexFoam + fresh;
	// more of them inside gusts, plus windrow lines in fresh wind
	whitecaps = whitecaps * lerp( 0.5, 1.5, det.gust ) + det.streak * 0.5;
	float coverage = TWSat( ( foamSum + whitecaps ) * _TWWaterA.z );

	// foam pattern: an irregular bubbly mat thresholded by coverage, so foam grows, tears into lace and dissolves
	// naturally
	float2 fuv = lagXZ * _TWWaterB.x;
	float4 p1 = SAMPLE_TEXTURE2D( _TWFoamTex, sampler_TWFoamTex, fuv );
	// second layer at another scale, rotated, to break repetition
	float2 r2 = float2( fuv.x * 0.8 - fuv.y * 0.6, fuv.x * 0.6 + fuv.y * 0.8 );
	float4 p2 = SAMPLE_TEXTURE2D( _TWFoamTex, sampler_TWFoamTex, r2 * 2.37 + float2( 0.31, 0.77 ) );
	float pattern = p1.x * 0.62 + p2.x * 0.38;
	float thresh = 1.05 - coverage * 1.1;
	float soft = 0.06 + footprint * 0.1;
	float detail = smoothstep( thresh - soft, thresh + soft, pattern ) * ( p1.y * 0.25 + 0.8 );
	// at distance the pattern averages out -> use coverage directly
	float farK = smoothstep( 0.15, 1.2, footprint );
	float foam = lerp( detail, coverage * 0.85, farK );

	WaterSurfaceFrag o;
	// foam look (surf zone whitewater / lace, see SurfFoam)
	SurfFoamArgs fa;
	fa.coverage = coverage; fa.foam = foam; fa.footprint = footprint; fa.depth = depth; fa.bubbles = p1.y;
	fa.lagXZ = lagXZ; fa.normal = normal; fa.baseNormal = Ns_base;
	fa.fresh = shoreFoam; fa.sim = simFoam; fa.simState = simState; fa.roller = surfMask.y; fa.P = P;
	fa.seaLevel = _TWWaterC.w; fa.sunDir = normalize( _TWSunDir.xyz );
	o.foamInfo = SurfFoamShading( fa );
	foam = o.foamInfo.foam;

	o.normal = normal;
	o.foam = foam;
	o.coverage = coverage;
	o.slopes = slopes;
	o.jacobian = jac;
	o.rough = rough;
	o.aeration = TWSat( aeration );
	o.gust = det.gust;
	o.slick = det.slick;
	return o;
}

// ------------------------------------------------------------------ screen-space reflection
// March the reflected ray through the opaque depth (view space, geometric steps, then a short bisection). Returns
// ( color, weight ): weight fades at screen edges, for rays heading back toward the camera and at the end of the search
// range. y0, ry: world height of the start and the ray's rise per metre. A hit beyond 260 m, or below the water on a
// descending ray, is weighted 0, so the march stops once the last miss is there.
// View space here is HDRP's (right handed, -z forward), the same convention as the JS.

float2 WaterProject( float3 pV )
{
	float4 clip = mul( UNITY_MATRIX_P, float4( pV, 1.0 ) );
	float2 ndc = clip.xy / max( clip.w, 1e-4 );
	float2 uv = float2( ndc.x * 0.5 + 0.5, ndc.y * 0.5 + 0.5 );
#if UNITY_UV_STARTS_AT_TOP
	uv.y = 1.0 - uv.y;
#endif
	return uv;
}

float3 WaterSceneColorAt( float2 uv )
{
	uint2 px = ( uint2 ) ( clamp( uv, 0.0, 0.9999 ) * _ScreenSize.xy );
	return LoadCameraColor( px );
}

// linear view Z of the opaque scene (negative, in front of the camera)
float WaterSceneZAt( float2 uv )
{
	uint2 px = ( uint2 ) ( clamp( uv, 0.0, 0.9999 ) * _ScreenSize.xy );
	return -LinearEyeDepth( LoadCameraDepth( px ), _ZBufferParams );
}

float4 WaterSSR( float3 posV, float3 Rv, float y0, float ry, float seaLevel, float4x4 invView )
{
	bool hit = false;
	// steps grow with the distance: far away the first ones would all land in the same pixel
	float stepScale = max( -posV.z / 60.0, 1.0 );
	float t = 0.15 * stepScale;
	float dt = 0.25 * stepScale;
	float prevT = 0.0;
	[loop]
	for ( int i = 0; i < 11; i ++ )
	{
		prevT = t;
		if ( prevT >= 260.0 || ( ry <= 0.0 && y0 + ry * prevT < seaLevel - 0.2 ) ) { break; }
		t += dt;
		dt *= 1.7;
		float3 p = posV + Rv * t;
		float2 uv = WaterProject( p );
		if ( uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || p.z > -0.1 ) { break; }
		float sz = WaterSceneZAt( uv );
		// behind the visible surface, within a thickness covering the last step
		if ( p.z < sz && sz - p.z < max( dt * 1.3, max( t * 0.08, 0.3 ) ) )
		{
			hit = true;
			break;
		}
	}

	float3 color = 0.0;
	float weight = 0.0;
	if ( hit )
	{
		// refine between the last miss and the hit
		float a = prevT; float b = t;
		for ( int k = 0; k < 3; k ++ )
		{
			float m = ( a + b ) * 0.5;
			float3 p = posV + Rv * m;
			bool behind = p.z < WaterSceneZAt( WaterProject( p ) );
			b = behind ? m : b;
			a = behind ? a : m;
		}

		float hitT = b;
		float3 hitV = posV + Rv * b;
		float2 uv = WaterProject( hitV );
		// after refining, the ray must really touch the surface there (a ray that only passed far behind a thin or
		// distant object is a false hit)
		float gap = abs( WaterSceneZAt( uv ) - hitV.z );
		float touch = smoothstep( max( b * 0.04, 0.4 ), max( b * 0.02, 0.2 ), gap );
		// anything under the surface (seabed seen through the water, submerged hull) is not visible to a reflected
		// ray: those rays run into the next wave instead
		float3 hitWorldRWS = mul( invView, float4( hitV, 1.0 ) ).xyz;
		float hitY = ( SimToWorld( hitWorldRWS + _WorldSpaceCameraPos.xyz ) ).y; // sim y == Unity y
		color = WaterSceneColorAt( uv );
		float edge = smoothstep( 0.0, 0.06, uv.x ) * smoothstep( 1.0, 0.94, uv.x ) * smoothstep( 0.0, 0.06, uv.y ) * smoothstep( 1.0, 0.94, uv.y );
		float facing = smoothstep( 0.5, 0.1, Rv.z ); // rays toward the camera leave the screen
		weight = edge * facing * touch * smoothstep( 260.0, 120.0, hitT ) * smoothstep( seaLevel - 0.15, seaLevel + 0.35, hitY );
	}
	return float4( color, weight );
}

// ------------------------------------------------------------------ shading (WaterMaterial output snippet)

float4 Frag( Varyings input, bool front : SV_IsFrontFace ) : SV_Target0
{
	UNITY_SETUP_INSTANCE_ID( input );

	float exposure = GetCurrentExposureMultiplier();
	float seaLevel = _TWWaterC.w;

	float3 posRWS = input.positionRWS;
	float3 posWorld = posRWS + _WorldSpaceCameraPos.xyz;   // Unity world
	float3 pos = SimToWorld( posWorld );                    // sim
	float3 camPos = _TWCamera.xyz;                          // sim
	float2 screenUV = input.positionCS.xy * _ScreenSize.zw;
	float surfEye = input.positionCS.w;                     // eye depth of the surface (positive)

	// No sea inside a hull: the surface behind the nearest face of the hull volume is water the hull keeps out (without this it
	// shows through the cockpit sole when the stern squats or the boat heels)
	if ( _TWHullParams.x > 0.5 && front )
	{
		uint2 hpx = ( uint2 ) ( clamp( screenUV, 0.0, 0.9999 ) * _TWHullParams.yz );
		float hullDist = LOAD_TEXTURE2D( _TWHullMask, hpx ).x;
		if ( hullDist > 0.01 && length( posRWS ) > hullDist - 0.02 ) discard;
	}
	float2 lagXZ = input.lagXZ;
	float vDepth = input.misc.y;
	float vHeight = input.misc.x;
	// footprint of this pixel on the surface (m): for filtering / roughness (uniform control flow)
	float footprint = max( length( fwidth( lagXZ ) ), 1e-4 );

	float3 toCam = camPos - pos;
	float dist = length( toCam );
	float3 V = toCam / dist;
	float3 L = normalize( _TWSunDir.xyz );
	// the sun light reaching the surface (sun colour x shadow maps x clouds x the island's own shadow; none of the
	// shadows is ported yet)
	float3 sunLight = _TWSunColor.rgb * exposure;
	// the ambient probe is in physical units (HDRP applies the exposure when it lights a surface)
	float3 skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	float3 absorption = _TWWaterAbsorption.rgb;
	float3 scattering = _TWWaterScattering.rgb;

	// water film thickness at this pixel and the distance to the swash front (ShoreWaves.swashEdge): the sheet ends exactly on
	// its analytic leading edge, not on the mesh triangles
	float groundH = TWHeightAt( pos.xz );
	float thickness = pos.y - groundH;
	float frontD = 1e3;
	float swTau = 0.0;
	float swRt = 0.0;
	if ( vDepth < 1.0 )
	{
		float tRaw = thickness;
		float4 se = ShoreSwashEdge( pos.xz, thickness );
		thickness = se.x; frontD = se.y; swTau = se.z; swRt = se.w;
		// The draining sheet has no rounded front: it thins out over decimetres and breaks up where the sand drains faster.
		// The analytic front runs parallel to the shoreline; kept as a hard, smooth edge (with the uprush's meniscus, rim and
		// contact shadow) it read as a dark line ruled along the beach between the foam and the wet sand.
		float backwash = smoothstep( 0.32, 0.46, swTau );
		if ( backwash > 0.0 && swRt > 0.0 && frontD < 3.0 )
		{
			frontD += ( TWPerlin2( pos.xz * 1.1 ) * 0.35 + TWPerlin2( pos.xz * 3.7 + float2( 5.3, 1.9 ) ) * 0.15 ) * backwash;
			thickness = min( tRaw, frontD * lerp( 0.08, 0.025, backwash ) );
		}
	}
	// the foam line riding the swash front, per pixel: a dense bubbly bead right at the edge while the sheet runs up, a
	// thinning lace behind it; weaker in the backwash (it sinks into the sand)
	float uprush = smoothstep( 0.46, 0.32, swTau );
	float bead = smoothstep( -0.01, 0.05, frontD ) * smoothstep( 0.6, 0.12, frontD );
	float trail = smoothstep( -0.01, 0.25, frontD ) * smoothstep( 2.2, 0.3, frontD );
	// patchy along the front (dense bunches and thin stretches), not an even white rope (only where the edge foam below can
	// be non-zero: it is weighted by the run-up and the shallow depth)
	float edgePatch = 1.0;
	if ( swRt > 0.0 && vDepth < 0.4 )
	{
		edgePatch = smoothstep( -0.45, 0.55, TWPerlin2( pos.xz * 0.42 ) ) * 0.7 + smoothstep( -0.3, 0.6, TWPerlin2( pos.xz * 1.7 + float2( 3.1, 7.7 ) ) ) * 0.3;
	}
	float edgeFoam = ( bead * lerp( 0.45, 1.1, uprush ) * lerp( 0.35, 1.0, edgePatch ) + trail * lerp( 0.12, 0.4, uprush ) * edgePatch ) * smoothstep( 0.0, 1.0, swRt ) * smoothstep( 0.4, -0.2, vDepth );
	// the meniscus: the last decimetre of the advancing sheet bends down to the sand
	float lipW = ( 1.0 - smoothstep( 0.0, 0.14, frontD ) ) * uprush;

	float4 simState = ShoreSimSample( pos.xz );
	WaterSurfaceFrag surf = WaterSurfaceFragment( lagXZ, footprint, vDepth, input.misc.z, input.shoreNS.xyz, input.misc.w + edgeFoam, simState.x, simState, input.surfMask, pos );
	float foam = surf.foam;
	// (the whale's churned white water and slick: not ported yet)

	// Which medium is the view ray in before it reaches this fragment? The water surface is a closed interface: a
	// front face (its air side towards the camera) is seen from the air, a back face from the water. For the visible
	// (nearest) fragment this is the medium the ray starts in at the near clip plane, which is exactly how the clip
	// plane slices the water. The winding can't be trusted in folds of the choppy / breaking surface: there, and well
	// above or below the surface, the camera's own medium decides.
	float camH = camPos.y - _TWWaterQuery[ 0 ].x;   // frame.cameraWaterHeight (this frame's GPU result)
	bool folded = surf.jacobian < 0.1 || normalize( input.shoreNS.xyz ).y < 0.35;
	bool nearSurface = abs( camH ) < 1.5;
	bool viewFromBelow = ( nearSurface && ! folded ) ? ! front : camH < 0.0;
	// shading normal on the viewer's side of the interface. Triangle winding can't be trusted (tiny self-intersections
	// of the choppy FFT surface render as back faces seen from above), so pick the side from the camera and bend facets
	// that face away to grazing instead of flipping them (a flipped normal turns a fold into a white sky-mirror patch).
	float3 Nup = normalize( surf.normal );
	float3 Nside = viewFromBelow ? -Nup : Nup;
	float3 Nview = normalize( Nside + V * max( -dot( Nside, V ) + 0.03, 0.0 ) );

	// roughness from unresolved slope variance (Cox-Munk: mss = 0.003 + 0.00512 U)
	float mss = ( 0.003 + _TWWind.z * 0.00512 ) * _TWWaterA.y;
	float kpx = PI / footprint;
	float unresolved = TWSat( log2( 110.0 / kpx ) / 9.0 );
	float roughVar = surf.rough * surf.rough;
	float waterRoughness = _TWWaterC.x;
	float alpha2 = waterRoughness * waterRoughness + mss * 2.0 * unresolved * roughVar + foam * 0.2 + surf.aeration * 0.03;
	// slope spread the mesh / normal maps can't show at this distance (for the reflection)
	float sigmaUnres = sqrt( mss * unresolved * roughVar );

	float3 outCol = 0.0;
	// debug views (OceanRenderer.debugView): the terms of the shading, see the end of Frag
	float3 dbgTransmitted = 0.0, dbgRefl = 0.0, dbgSpec = 0.0, dbgScene = 0.0, dbgInSun = 0.0, dbgInAmb = 0.0;
	float dbgF = 0.0, dbgPath = 0.0;

	if ( ! viewFromBelow )
	{
		// ================= ABOVE WATER =================
		// near the leading edge the surface bends down to meet the sand like a rounded bead (meniscus), tilting the
		// normal toward dry land
		float edgeW = max( ( 1.0 - smoothstep( 0.0, 0.006, thickness ) ) * uprush, lipW );
		float4 nr = TWNormalRock( pos.xz );
		float2 uphill = normalize( -float2( nr.x, nr.y ) + float2( 1e-5, 0.0 ) );
		float3 N = normalize( Nview + float3( uphill.x, 0.0, uphill.y ) * ( edgeW * edgeW * 0.7 ) );
		float NdV = max( dot( N, V ), 1e-4 );
		float F = FresnelDielectric( NdV, TW_IOR );

		// ---- reflection
		float3 Rraw = reflect( -V, N );
		// unresolved facets tilt the average reflection toward the higher, darker sky: rough patches (gusts) darken
		// toward the horizon, slicks stay bright and mirror-like
		float Rup = max( Rraw.y, 0.004 ) + sigmaUnres * 1.3 * ( 1.0 - max( Rraw.y, 0.0 ) );
		float3 R = normalize( float3( Rraw.x, Rup, Rraw.z ) );
		// reflections pointing below the horizon hit other waves: fade toward a dark sea color
		float horizonOcc = max( smoothstep( -0.12, 0.08, Rraw.y ), smoothstep( 0.25, 0.06, thickness ) );
		float3 skyRefl = 0.0;
		if ( horizonOcc > 0.0 || frontD < 0.1 ) { skyRefl = TWSkyRadiance( R ); }
		float3 horizonColor = 0.0;
		if ( horizonOcc < 1.0 )
		{
			// frame.horizonColor: the sky just above the horizon, averaged over the four compass directions
			horizonColor = ( TWSkyRadiance( normalize( float3( 1.0, 0.03, 0.0 ) ) ) + TWSkyRadiance( normalize( float3( -1.0, 0.03, 0.0 ) ) )
				+ TWSkyRadiance( normalize( float3( 0.0, 0.03, 1.0 ) ) ) + TWSkyRadiance( normalize( float3( 0.0, 0.03, -1.0 ) ) ) ) * 0.25;
		}
		float3 reflCol = lerp( horizonColor * 0.35, skyRefl, horizonOcc );

		// objects (pier, boat, hills, village) reflected from the screen; only rays close to the horizon can hit
		// anything, so steep reflections skip the march entirely (looking down, F is tiny: the reflection can't be
		// seen, skip the march)
		if ( Rraw.y < 0.45 && F > 0.05 && _TWWaterC.z > 0.5 )
		{
			float3 posV = mul( ( float3x3 ) UNITY_MATRIX_V, posRWS );
			float3 Rv = mul( ( float3x3 ) UNITY_MATRIX_V, SimToWorld( Rraw ) );
			// (rays toward the camera get no weight: see facing in WaterSSR)
			if ( Rv.z < 0.5 )
			{
				float4 r = WaterSSR( posV, Rv, pos.y, Rraw.y, seaLevel, UNITY_MATRIX_I_V );
				reflCol = lerp( reflCol, r.rgb, r.a );
			}
		}

		reflCol *= _TWWaterC.y;

		// ---- sun specular (GGX), sun light already includes shadowing
		float3 H = normalize( L + V );
		float NdL = max( dot( N, L ), 0.0 );
		float NdH = max( dot( N, H ), 0.0 );
		float VdH = max( dot( V, H ), 0.0 );
		float Fs = FresnelDielectric( VdH, TW_IOR );
		float spec = WaterDGGX( NdH, alpha2 ) * WaterVSmithGGX( NdL, NdV, alpha2 ) * Fs * NdL;
		// physically the glint is ~1e5x brighter than the sky; clamp to stay inside fp16 range
		float3 sunSpec = sunLight * min( spec, 400.0 );

		// ---- refraction / water volume
		// Trace the refracted view ray (Snell) to the sea floor instead of using the straight screen ray: at grazing
		// angles the straight ray overestimates the water path ~10x. View ray inside the water (unit, downward).
		// Facets of a curling crest can refract it upward on a coarse mesh; keep it heading down into the water body.
		float3 Tr = refract( -V, N, 1.0 / TW_IOR );
		float3 Tv = normalize( float3( Tr.x, min( Tr.y, -0.08 ), Tr.z ) );
		float tDown = max( -Tv.y, 0.04 );

		// water column below the surface along the refracted ray (terrain, 2 refinements)
		float L0 = max( pos.y - groundH, 0.0 ) / tDown;
		// deep water: the end point is capped at 80 m and the column is opaque long before, so the refinements can't
		// change the result
		float Lt = L0;
		if ( L0 < 100.0 )
		{
			float L1 = max( pos.y - TWHeightAt( pos.xz + Tv.xz * min( L0, 200.0 ) ), 0.0 ) / tDown;
			Lt = max( pos.y - TWHeightAt( pos.xz + Tv.xz * min( L1 * 0.5 + L0 * 0.5, 200.0 ) ), 0.0 ) / tDown;
		}
		float Lter = clamp( Lt, 0.0, 400.0 );
		// thin breaking crests: the refracted ray leaves through the back of the wave into the sky
		float crestT = ShoreCrestPath( lagXZ, vDepth, Tv );
		bool thruCrest = crestT < Lter;

		// project the refracted end point to the screen
		float3 pEnd = pos + Tv * min( Lter, 80.0 );
		float3 pEndRWS = GetCameraRelativePositionWS( SimToWorld( pEnd ) );
		float2 uvR = ComputeNormalizedDeviceCoordinates( pEndRWS, UNITY_MATRIX_VP );
		bool onScreen = all( uvR > 0.0 ) && all( uvR < 1.0 );

		// The refraction pass (what lies below the water only) is not ported yet: the opaque scene copy is used,
		// where the refracted sample lies behind the water surface, else the unrefracted pixel
		uint2 pxR = ( uint2 ) ( clamp( uvR, 0.0, 0.9999 ) * _ScreenSize.xy );
		uint2 pxS = ( uint2 ) ( clamp( screenUV, 0.0, 0.9999 ) * _ScreenSize.xy );
		float dO = LoadCameraDepth( pxR );
		float sceneEyeO = LinearEyeDepth( dO, _ZBufferParams );
		bool valid = onScreen && sceneEyeO - surfEye > TW_WATER_BEHIND;
		float2 uvF = valid ? uvR : screenUV;
		float dR = valid ? dO : LoadCameraDepth( pxS );
		float3 sceneCol = WaterSceneColorAt( uvF );
		if ( thruCrest ) { sceneCol = TWSkyRadiance( normalize( float3( Tv.x, max( abs( Tv.y ), 0.03 ), Tv.z ) ) ); }

		// objects in front of the sea floor (pylons, rocks, reef) shorten the path
		float3 qWS = ComputeWorldSpacePosition( uvF, dR, UNITY_MATRIX_I_VP ); // camera relative
		float qDist = length( qWS - posRWS );
		float pathLen = clamp( min( Lter, qDist ), 0.0, 400.0 );
		pathLen = min( pathLen, crestT );

		// bubbles mixed into the water (the surf behind breakers, wakes): a strong scatterer, the water turns milky
		// turquoise and the bottom disappears (WaterSurface.fragment aeration)
		float aer = surf.aeration;
		// sand stirred up where the bores have just passed (the foam they left marks that water): clouds of sediment, not a
		// uniform tint
		float sandK = saturate( simState.x * 2.5 ) * 1.8 + 0.45;
		// surf zone: sand and bubbles stirred up by the breakers (see ShoreWaves.surfMedium)
		ShoreMedium surfMed = ShoreSurfMedium( pos.xz, vDepth );
		float3 sigA = absorption + surfMed.absorb * sandK;
		// (bubble plumes are shallow and patchy: a moderate scatterer, milky turquoise rather than a glow)
		float3 sigS = scattering + surfMed.scatter * sandK + aer * 1.6;
		float3 sigT = sigA + sigS;

		// refracted sun direction
		float3 Ls = -refract( -L, float3( 0.0, 1.0, 0.0 ), 1.0 / TW_IOR ); // toward the sun from underwater
		float muS = max( Ls.y, 0.1 );
		float muV = max( -Tv.y, 0.15 );

		float3 Tview = exp( -sigT * pathLen );

		// in-scattered light along the view ray (single scattering sun + ambient), analytic
		// light at depth z: E0 * exp(-sigT * z / mu). Along the view ray z = s * muV.
		float3 sunIn = sunLight * ( 1.0 - FresnelDielectric( max( L.y, 0.02 ), TW_IOR ) );
		float3 kSun = sigT * ( 1.0 + muV / muS );
		float3 kAmb = sigT * ( 1.0 + muV / 0.75 );
		float cosPh = dot( Tv, Ls );
		float phase = WaterPhaseHG( cosPh, 0.86 ) * 0.7 + 0.3 / ( 4.0 * PI );
		float3 bb = sigS * lerp( _TWWaterB.y, 0.06, TWSat( aer * 2.0 ) );
		// multiple-scattering boosted backscatter (Gordon R = 0.33 bb/(a+bb))
		float3 albedoMS = bb * ( 0.33 * 4.0 ) / ( sigA + bb );
		float3 inSun = sunIn * ( sigS * phase + albedoMS * sigT * INV_PI ) * ( 1.0 - exp( -kSun * pathLen ) ) / kSun;
		float3 inAmb = skyIrradiance * ( sigS * 0.25 + albedoMS * sigT ) * ( 1.0 - exp( -kAmb * pathLen ) ) / kAmb;

		// crest translucency (sun shining through thin wave tips)
		float2 vH = normalize( float2( V.x, V.z ) );
		float2 lH = normalize( float2( L.x, L.z ) + 1e-5 );
		// (light entering the top and back of a thin crest scatters out of the face over a broad lobe: side-lit waves
		// glow green too, not only when looking straight into the sun)
		float backK = pow( TWSat( dot( vH, -lH ) * 0.6 + 0.4 ), 2.5 );
		float crest = TWSat( vHeight * 0.9 + 0.1 ) * ( TWSat( ( 1.0 - N.y ) * 4.0 ) + 0.25 );
		float3 sssCol = float3( 0.12, 0.55, 0.45 ) * 0.06;
		float3 sss = sunLight * sssCol * backK * crest * _TWWaterB.z * smoothstep( 0.0, 0.25, L.y );

		// the bead of the meniscus shades the sand right under it
		float3 transmitted = sceneCol * Tview * ( 1.0 - 0.3 * lipW ) + inSun + inAmb + sss;
		dbgTransmitted = transmitted; dbgRefl = reflCol; dbgSpec = sunSpec; dbgScene = sceneCol; dbgInSun = inSun; dbgInAmb = inAmb; dbgF = F; dbgPath = pathLen;

		// ---- foam
		// foam: bright diffuse scatterer (albedo ~0.85), wrapped sun + sky irradiance (skyIrradiance = E/PI)
		float3 foamLit = SurfFoamLight( surf.foamInfo, N, L, V, sunLight, pos, skyIrradiance );
		float3 foamCol = foamLit * _TWWaterB.w;

		// a thin bright rim just behind the edge: the rounded bead catches the sky
		float rim = smoothstep( 0.0, 0.025, frontD ) * smoothstep( 0.1, 0.035, frontD ) * uprush;
		// ---- local lights on the sea surface (the torch, the deck floods, the lanterns)
		// The water is outside the lighting model, so nothing lights it from HDRP's lights; this reads the lamps directly with the
		// same maths as the lit materials. Two terms: the reflected glint (the broken path leading back to the source) and the
		// light that enters the water and scatters back out, tinted by the water's own body. Free by day: the count is 0 until dusk.
		float3 lampSpec = 0.0;
		float3 lampGlow = 0.0;
		int lampN = ( int ) _TWLampN.x;
		for ( int li = 0; li < lampN; li ++ )
		{
			float4 lp = _TWLampPos[ li ];
			float3 ld = lp.xyz - pos;
			float ld2 = dot( ld, ld );
			if ( ld2 < lp.w )
			{
				float4 lc = _TWLampCol[ li ];
				float4 ls = _TWLampDir[ li ];
				float3 Lw = ld * rsqrt( max( ld2, 1e-6 ) );
				float lx = ld2 / lp.w;
				float win = TWSat( 1.0 - lx * lx );
				float spotK = WaterLampSpot( dot( -Lw, ls.xyz ), lc.w, ls.w );
				float3 lcol = lc.xyz * exposure * ( win * win * spotK / ( ld2 + 0.15 ) );
				float NdLw = max( dot( N, Lw ), 0.0 );
				// the glint: the water's own GGX with this light's half vector
				float3 Hw = normalize( Lw + V );
				float Fw = FresnelDielectric( max( dot( V, Hw ), 0.0 ), TW_IOR );
				float specW = WaterDGGX( max( dot( N, Hw ), 0.0 ), alpha2 ) * WaterVSmithGGX( NdLw, NdV, alpha2 ) * Fw * NdLw;
				lampSpec += lcol * min( specW, 400.0 );
				// the lit patch: the backscattered fraction of the water body
				lampGlow += lcol * NdLw * ( sigS / sigT ) * INV_PI;
			}
		}

		float3 lamp = lampSpec + lampGlow;
		float3 water = lerp( transmitted, reflCol, F ) + sunSpec + lamp + skyRefl * ( 0.22 * rim );
		float3 shaded = lerp( water, foamCol + sunSpec * 0.05 + lamp * 0.5, TWSat( foam ) );
		// fade into the sand right at the leading edge (anti-aliased by the film thickness)
		float edgeAA = smoothstep( 0.0, max( fwidth( thickness ) * 1.5, 0.004 ), thickness );
		outCol = shaded;
		if ( edgeAA < 1.0 )
		{
			// contact shadow: the sand just ahead of the advancing edge is darkened (the bead's shadow and the wetting front),
			// fading within ~15 cm
			float contact = smoothstep( -0.16, -0.005, frontD ) * ( 1.0 - edgeAA ) * uprush;
			float3 sandC = WaterSceneColorAt( screenUV ) * ( 1.0 - 0.3 * contact );
			outCol = lerp( sandC, shaded, edgeAA );
		}
	}
	else
	{
		// ================= BELOW WATER (looking up at the surface) =================
		float3 N = Nview;
		float NdV = max( dot( N, V ), 1e-4 );
		// from water (n=1.333) into air: eta = 1/1.333
		float F = FresnelDielectric( NdV, 1.0 / TW_IOR );
		float3 Tt = refract( -V, N, TW_IOR );
		bool tValid = dot( Tt, Tt ) > 0.5;
		float3 Td = normalize( tValid ? Tt : float3( 0.0, 1.0, 0.0 ) );
		// sky through Snell's window; the sun disk is bounded so grazing refractions of it far away cannot bloom
		// through the fog
		float3 skyT = min( TWSkyRadiance( Td ), 60.0 );

		// total internal reflection mirrors the lit water body below: the radiance of an infinitely long view ray
		// through the medium in the reflected direction
		float3 sigA = absorption; float3 sigS = scattering; float3 sigT = sigA + sigS;
		float3 bb = sigS * _TWWaterB.y;
		float3 albedoMS = bb * ( 0.33 * 4.0 ) / ( sigA + bb );
		float3 Rr = reflect( -V, N );
		float3 LsU = -refract( -L, float3( 0.0, 1.0, 0.0 ), 1.0 / TW_IOR );
		float muU = max( LsU.y, 0.15 );
		float phR = WaterPhaseHG( dot( Rr, LsU ), 0.86 ) * 0.7 + 0.3 / ( 4.0 * PI );
		float3 kS = sigT * ( 1.0 - min( Rr.y, 0.0 ) / muU );
		float3 kA = sigT * ( 1.0 - min( Rr.y, 0.0 ) / 0.8 );
		float3 eSunU = sunLight * ( 1.0 - FresnelDielectric( max( L.y, 0.02 ), TW_IOR ) );
		float3 deepCol = eSunU * ( sigS * phR + albedoMS * sigT * INV_PI ) / kS
			+ skyIrradiance * PI * ( sigS * ( 1.0 / ( 4.0 * PI ) ) + albedoMS * sigT * INV_PI ) / kA;

		// objects above the water seen through Snell's window (from the viewport)
		uint2 pxS = ( uint2 ) ( clamp( screenUV, 0.0, 0.9999 ) * _ScreenSize.xy );
		float sceneEyeC = LinearEyeDepth( LoadCameraDepth( pxS ), _ZBufferParams );
		bool hasObj = surfEye - sceneEyeC > 0.0 && sceneEyeC < _ProjectionParams.z * 0.9;
		float3 objCol = LoadCameraColor( pxS );
		float3 transmittedU = hasObj ? objCol : skyT;

		float3 foamUnder = ( skyIrradiance + sunLight * 0.5 ) * 0.25;
		outCol = lerp( transmittedU * ( 1.0 - F ) + deepCol * F, foamUnder, TWSat( foam ) * 0.7 );
	}

	float3 res = min( outCol, 16000.0 );
	int dbg = ( int ) _TWDebug.x;
	// views 3..13 show the luminance of the term as a magnitude code: R = v, G = v / 10, B = v / 100 (each clamped to 1)
	#define DBG_ENC( v ) float3( saturate( v ), saturate( ( v ) / 10.0 ), saturate( ( v ) / 100.0 ) )
	if ( dbg == 1 ) res = surf.normal * 0.5 + 0.5;
	else if ( dbg == 2 ) res = foam;
	else if ( dbg == 3 ) res = DBG_ENC( TWLuminance3( dbgTransmitted ) );
	else if ( dbg == 4 ) res = DBG_ENC( TWLuminance3( dbgRefl ) );
	else if ( dbg == 5 ) res = DBG_ENC( TWLuminance3( dbgSpec ) );
	else if ( dbg == 6 ) res = DBG_ENC( TWLuminance3( dbgScene ) );
	else if ( dbg == 7 ) res = DBG_ENC( TWLuminance3( dbgInSun ) );
	else if ( dbg == 8 ) res = DBG_ENC( TWLuminance3( dbgInAmb ) );
	else if ( dbg == 9 ) res = dbgF;
	else if ( dbg == 10 ) res = dbgPath * 0.02;
	else if ( dbg == 11 ) res = DBG_ENC( TWLuminance3( skyIrradiance ) );
	else if ( dbg == 12 ) res = DBG_ENC( TWLuminance3( sunLight ) );
	else if ( dbg == 13 ) res = DBG_ENC( exposure * 1000000.0 );
	if ( dbg > 0 ) return float4( res, 1.0 );

	// HDRP fog (aerial perspective, volumetric fog) on top, like any transparent
	PositionInputs posInput = GetPositionInput( input.positionCS.xy, _ScreenSize.zw, input.positionCS.z, input.positionCS.w, posRWS );
	float3 V_hdrp = GetWorldSpaceNormalizeViewDir( posRWS );
	float3 fogColor, fogOpacity;
	if ( EvaluateAtmosphericScattering( posInput, V_hdrp, fogColor, fogOpacity ) )
	{
		res = res * ( 1.0 - fogOpacity ) + fogColor;
	}

	return float4( res, 1.0 );
}
