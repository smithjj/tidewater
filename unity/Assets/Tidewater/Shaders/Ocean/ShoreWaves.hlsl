// Port of the WGSL module of src/ocean/ShoreWaves.js (prefix shore): depth-aware shoreline waves.
//
// Wave phase comes from the precomputed travel-time field (refraction around headlands, fronts aligning with the depth
// contours). Each individual wave m has its own height (sets + along-shore variation). Height grows in shallow water
// (Green's law) until H > gamma * depth. The wave then plunges: the front face turns into a vertical, concave wall under
// the crest (the thrown lip is a separate sheet, see Breakers), the tube collapses where the lip lands and the wave
// continues as a turbulent bore, which finally runs up the beach as a thin swash sheet whose leading edge advances and
// retreats (vertical run-up R(t) compared against the sand height).
//
// Functions (sim space; same names as the WGSL with the shore prefix capitalised):
//   ShoreSample ShoreEvaluate( xz, depth, groundH )        with the normal: the water mesh
//   ShoreSample ShoreEvaluateNoNormal( xz, depth, groundH ) no normal: queries
//   ShoreSample ShoreEvaluateWorld( xz, depth, groundH )   a fixed world point: ShoreSim
//   ShorePhase ShorePhaseAt( xz ), ShoreWaveAmp( m, along ), ShoreShape / ShoreShapePair / ShoreWorld, ShoreBore, ShoreCrest,
//   ShoreBreakDepth, ShoreDirAt, ShoreSurfMedium, ShoreCrestPath, ShoreSwashRunup, ShoreSwashEdge, ShoreSwashClip
// Needs TWHeightAt / TWShoreSample (TerrainHeight.hlsl) and TWPerlin2 (Common/Noise.hlsl); the includer declares
// _TWShoreA..E, the direction texture and defines the sea level (_TWShoreE.y).

#ifndef TW_SHORE_WAVES_INCLUDED
#define TW_SHORE_WAVES_INCLUDED

// ShoreParams (ShoreWaves.cs publishes them):
float4 _TWShoreA;   // period, phase (accumulated in periods), amplitude (offshore, H/2), variation
float4 _TWShoreB;   // gamma, breakSpan, curl, runup
float4 _TWShoreC;   // enabled, turbidity, dirOn, dirSize
float4 _TWShoreD;   // dirMin.xy
float4 _TWShoreE;   // x = frame.time (s), y = sea level
TEXTURE2D(_TWShoreDirTex);   // wave direction + exposure over a region (rgba32f), loaded; see ShoreWaves.BuildDirTexture

#define shoreP_period _TWShoreA.x
#define shoreP_phase _TWShoreA.y
#define shoreP_amplitude _TWShoreA.z
#define shoreP_variation _TWShoreA.w
#define shoreP_gamma _TWShoreB.x
#define shoreP_breakSpan _TWShoreB.y
#define shoreP_curl _TWShoreB.z
#define shoreP_runup _TWShoreB.w
#define shoreP_enabled _TWShoreC.x
#define shoreP_turbidity _TWShoreC.y
#define shoreP_dirOn _TWShoreC.z
#define shoreP_dirSize _TWShoreC.w
#define shoreP_dirMin _TWShoreD.xy

#define SHORE_GRAVITY 9.81
#define SHORE_TAU 6.283185307179586
#define SHORE_BEACH_SLOPE 0.066    // run-up is converted to a horizontal excursion with this slope
#define SHORE_SWASH_UP 0.4         // fractions of the period: uprush,
#define SHORE_SWASH_DOWN 0.55      // backwash
#define SHORE_SWASH_OVERSHOOT 1.2  // the mesh sheet reaches this far (m) past the leading edge (> the mesh spacing, so the
                                   // per-pixel front, not the triangles, always decides where the sheet ends)

struct ShoreSample
{
	float3 disp;
	float3 nShore;
	float env;
	float foam;
	float breaking;
	float u;
	float2 dir;
	float exposure;
	float swashLevel;
	float swashCovered;
	float thick;
	float swashFoam;
	float runup;
	float inland;
	float dRdt;
	float tau;
	float2 flow;
	float flowSpeed;
	float face;
	float roller;
};

struct ShoreMedium { float3 scatter; float3 absorb; };

struct ShorePhase { float4 sh; float T; float2 dir; float exposure; float along; float s; };

struct ShoreBreak
{
	float db, b, Ash, p, meanP, crestPeak, yc, yt;
	float H, wBore, Xi, Hb, ycB, ytB, Xc, Wt;
};

struct ShoreProfile { float x, y, b, foam, face, roller; };

struct ShorePair { float4 s0; float4 s1; };

struct ShoreRunup { float tau, Rt, inland, RhMax, su, sb; bool isUp; };

// (the per-wave random: sin() of a few hundred times 43758 amplifies the last bits of sin, so the waves' heights differ between
// GPUs. TW_SHORE_ORACLE swaps in an exact integer hash, only for the oracle comparison with the JS: tools/dump-shore-query.mjs)
#ifdef TW_SHORE_ORACLE
uint ShorePcg( uint v )
{
	uint state = v * 747796405u + 2891336453u;
	uint word = ( ( state >> ( ( state >> 28u ) + 4u ) ) ^ state ) * 277803737u;
	return ( word >> 22u ) ^ word;
}
float ShoreHash1( float x ) { return float( ShorePcg( asuint( x ) ) >> 8u ) * ( 1.0 / 16777216.0 ) + ( 0.5 / 16777216.0 ); }
#else
float ShoreHash1( float x ) { return frac( sin( x * 127.1 + 311.7 ) * 43758.5453 ); }
#endif

// Bathymetry along the beach that the travel-time field doesn't resolve: a bar with rip channels cut through it every
// ~100 m (irregular spacing and width). Over the bar the waves are bigger and break first and farther out (the peaks); in
// the channels they are much smaller and roll through unbroken almost to the shorebreak, so a set never closes out along
// the whole beach at once. Returns 1 over the bar, less in a channel; rip (0..1) is the channel mask.
struct ShoreBar { float k; float rip; };
ShoreBar ShoreBarAt( float along )
{
	float w = sin( along * 0.021 + 1.9 ) * 1.4 + sin( along * 0.009 + 0.3 ) * 0.9;
	float r = sin( along * 0.059 + w );
	float width = 0.8 + sin( along * 0.017 + 4.1 ) * 0.08; // channels of different width
	float rip = smoothstep( width, 0.985, r );
	// the bar itself is uneven: broad peaks and lower shoulders
	float bar = 0.9 + ( sin( along * 0.031 + 7.3 ) * 0.6 + sin( along * 0.083 + 1.1 ) * 0.4 ) * 0.14;
	ShoreBar o; o.k = bar * ( 1.0 - rip * 0.58 ); o.rip = rip;
	return o;
}

// along-shore phase wobble (in periods): crests bend over the uneven bottom (and run ahead in the deeper rip channels,
// where the waves travel faster)
float ShoreWobble( float along )
{
	return sin( along * 0.029 + 0.7 ) * 0.07 + sin( along * 0.083 + 2.1 ) * 0.035
		+ sin( along * 0.19 + 0.4 ) * 0.022 + sin( along * 0.37 + 2.6 ) * 0.011
		+ ShoreBarAt( along ).rip * 0.045;
}

// ------------------------------------------------------------ per-wave height

float ShoreWaveAmp( float m, float along )
{
	// sets: groups of ~7 waves with larger ones in the middle, plus per-wave randomness
	float waveSet = abs( sin( m * 0.4487989505128276 ) ) * 0.6 + 0.55;   // PI / 7
	float rnd = ( ShoreHash1( m ) - 0.5 ) * 2.0;
	// along-shore variation so waves peel instead of closing out
	// peaks ~40-50 m wide with lower shoulders between them, different for every wave (the warp keeps them from repeating
	// along the beach); each peak breaks first and peels outward from it
	float warp = sin( along * 0.016 + m * 0.9 ) * 1.6;
	float a1 = sin( along * 0.062 + m * 1.7 + warp );
	float a2 = sin( along * 0.13 + m * 4.1 + 1.3 - warp * 0.7 );
	// (plus a shorter ~20 m variation: bores that rise and sag along the crest, more peel sections)
	float a3 = sin( along * 0.29 + m * 2.3 + warp * 0.5 ) * 0.6 + sin( along * 0.47 + m * 5.9 + 0.8 ) * 0.4;
	float alongV = a1 * 0.6 + a2 * 0.4 + a3 * 0.28;
	return max( shoreP_amplitude * waveSet * ( 1.0 + rnd * shoreP_variation * 0.5 + alongV * shoreP_variation * 0.7 ) * ShoreBarAt( along ).k, 0.02 );
}

// ------------------------------------------------------------ cross-section shape

ShoreBreak ShoreBreakParams( float A, float d )
{
	ShoreBreak P;
	// break depth for this wave (Green's law shoaling, H = gamma d)
	P.db = pow( A * 3.556 / shoreP_gamma, 0.8 );
	P.b = ( P.db - d ) / ( P.db * shoreP_breakSpan ); // <0 shoaling, 0..1 plunging, >1 bore
	float shoal = pow( 10.0 / clamp( d, 0.35, 10.0 ), 0.25 );
	P.Ash = A * shoal;
	P.p = lerp( 1.0, 3.0, smoothstep( -2.5, 0.0, P.b ) );
	P.meanP = 1.0 / sqrt( ( P.p + 0.25 ) * PI );
	P.crestPeak = 1.0 + smoothstep( -1.0, 0.3, P.b ) * 0.22;
	P.yc = P.Ash * 2.0 * ( 1.0 - P.meanP ) * P.crestPeak; // crest height
	P.yt = P.Ash * -2.0 * P.meanP * P.crestPeak; // trough level
	P.H = P.yc - P.yt;
	P.wBore = smoothstep( 0.85, 1.35, P.b );
	P.Xi = P.H * 0.8; // lip throw at impact
	// bore height, limited by the depth and fading out in the last few decimetres
	P.Hb = min( P.H * 0.6, max( d, 0.0 ) * 0.75 ) * ( smoothstep( 0.0, 0.3, d ) * 0.7 + 0.3 );
	P.ycB = lerp( P.yc, P.yt * 0.6 + P.Hb, P.wBore ); // crest / roller top
	P.ytB = lerp( P.yt, P.yt * 0.6, P.wBore ); // trough
	// the collapsing crest moves to where the lip landed (no shift once the bore has run out of height)
	P.Xc = lerp( 0.0, P.Xi - P.Hb * 0.55, P.wBore ) * smoothstep( 0.03, 0.3, P.Hb );
	// horizontal extent of the face: concave tube face while plunging, short convex roller front on the bore
	P.Wt = lerp( P.H * lerp( 0.25, 0.55, smoothstep( 0.1, 0.9, P.b ) ), P.Hb * 0.6 + 0.08, P.wBore );
	return P;
}

// Whitewater made by the breaking wave at xi (m): the horizontal position relative to the crest's rest position, positive
// shoreward (a parcel's own position, or a fixed world point: the shore simulation deposits it where the water actually
// is, not where the parcel rests).
//  * nothing at all while the lip is in the air: the face and the lip are clear, glassy water
//  * the lip lands in the trough at the plunge point (b ~ 0.9, xi = Xi): whitewater appears there and spreads out from it
//    while the tube collapses behind it
//  * the collapsed tube becomes the roller: whitewater over the front and top of the bore, shedding foam behind it
//    (carried on by ShoreSim)
// whitewater over the face after the plunge: s = 0 at the crest .. 1 at the foot of the face
float ShoreFaceFill( float b, float s )
{
	// the foot turns white first (where the lip lands), the top of the face last: along a peeling crest the edge of the
	// broken section slants down and forward instead of standing vertical
	float k = ( 1.0 - s ) * 0.45;
	return smoothstep( k + 0.9, k + 1.08, b );
}

float ShoreWhitewater( float xi, float lam, ShoreBreak P )
{
	float landed = smoothstep( 0.86, 0.99, P.b );
	float spread = saturate( ( P.b - 0.9 ) / 0.45 );
	float reach = P.Xi * 0.25 + spread * ( P.Xi * 0.9 + 1.0 ); // radius around the plunge point
	float impact = landed * smoothstep( reach, reach * 0.6, abs( xi - P.Xi ) ) * ( 1.0 - smoothstep( 1.4, 1.9, P.b ) );
	float toe = P.Xc + P.Wt;
	// the collapsing tube turns white from its foot (next to the plunge point) up to the crest
	float faceFill = ShoreFaceFill( P.b, saturate( ( xi - P.Xc ) / max( P.Wt, 0.05 ) ) );
	float ahead = exp( ( xi - toe ) * -3.0 ) * P.wBore;
	float behind = exp( ( P.Xc - xi ) / lam * -16.0 ) * P.wBore;
	float roller = xi > toe ? ahead : ( xi < P.Xc ? behind : max( faceFill, P.wBore ) );
	return saturate( max( impact, roller ) );
}

// the cross-section for break parameters P (see ShoreBreakParams)
// u: local phase in [-0.5, 0.5], crest at 0, u < 0 in front (shoreward) of the crest
// Returns x: shoreward displacement, y: height above mean, b: breaking progress (+ foam, face, roller withFoam)
ShoreProfile ShoreProfileAt( float u, float lam, ShoreBreak P, bool withFoam )
{
	// --- shoaling: peaked (cnoidal-like) crest, the front compressed by a phase skew
	float skew = smoothstep( -3.0, 0.0, P.b ) * 0.55;
	float phi = u - skew * ( 1.0 - cos( u * SHORE_TAU ) ) / SHORE_TAU;
	float c = max( ( cos( phi * SHORE_TAU ) + 1.0 ) * 0.5, 0.0 ); // pow() of a rounding-negative base is NaN
	float yPre = P.Ash * 2.0 * ( pow( c, P.p ) - P.meanP ) * P.crestPeak;
	float Q = smoothstep( -3.0, 0.0, P.b ) * 0.25 + 0.1;
	float xPre = sin( u * SHORE_TAU ) * P.Ash * Q;

	// --- plunging / bore profile. Front: the upper uf of the phase is an elliptic arc from the crest down to the trough
	// (concave tube face -> convex roller front), the rest of the front is trough, stretched to meet the next wave. Back:
	// the shoaling back, decaying exponentially behind the bore.
	float uf = 0.09;
	bool inFace = u > -uf;
	float th = clamp( -u / uf, 0.0, 1.0 ) * 1.5707963267948966;
	float fx = lerp( 1.0 - cos( th ), sin( th ), P.wBore );
	float fy = lerp( 1.0 - sin( th ), cos( th ), P.wBore );
	float sTr = clamp( ( -u - uf ) / ( 0.5 - uf ), 0.0, 1.0 );
	float ul = u * lam;
	float xFront = ( inFace ? P.Xc + P.Wt * fx : lerp( P.Xc + P.Wt, lam * 0.5, sTr ) ) + ul;
	float yFront = inFace ? P.ytB + ( P.ycB - P.ytB ) * fy : P.ytB;
	float cb = pow( max( ( cos( u * ( SHORE_TAU * 0.85 ) ) + 1.0 ) * 0.5, 0.0 ), P.p );
	float yBack = P.ytB + ( P.ycB - P.ytB ) * lerp( cb, exp( u * -7.0 ), P.wBore );
	float xBack = P.Xc * exp( u * -6.0 ) * smoothstep( 0.5, 0.35, u ) + xPre * ( 1.0 - P.wBore );
	bool front = u < 0.0;
	float wC = smoothstep( -0.35, 0.25, P.b ) * shoreP_curl;
	ShoreProfile r;
	r.x = lerp( xPre, front ? xFront : xBack, wC );
	r.y = lerp( yPre, front ? yFront : yBack, wC );
	r.b = P.b;
	r.foam = 0.0; r.face = 0.0; r.roller = 0.0;
	if ( withFoam )
	{
		// --- whitewater (a function of where the parcel is now, see ShoreWhitewater) and, per parcel, the clear face of the
		// plunging wave and the relief of the roller
		r.foam = ShoreWhitewater( r.x - u * lam, lam, P );
		bool onFace = front && inFace;
		// the clear, concave face of a plunging wave (WaterSurface keeps the foam carried by the water off it)
		float faceFill = ShoreFaceFill( P.b, fx );
		r.face = ( onFace ? 1.0 : 0.0 ) * smoothstep( -0.4, 0.0, P.b ) * ( 1.0 - faceFill );
		// turbulent relief of the whitewater roller (m): its front and the top it tumbles over
		r.roller = ( front ? ( inFace ? 1.0 : 0.0 ) : exp( u * -25.0 ) ) * P.wBore * P.Hb * wC;
	}
	return r;
}

// ( x, y, foam, b )
float4 ShoreShape( float u, float A, float d, float lam )
{
	ShoreProfile s = ShoreProfileAt( u, lam, ShoreBreakParams( A, d ), true );
	return float4( s.x, s.y, s.foam, s.b );
}

// the cross-section at u and at u + du (for the surface normal) in one call:
// ( x0, y0, foam, b ), ( x1, y1, face, roller )
ShorePair ShoreShapePair( float u, float du, float A, float d, float lam )
{
	ShoreBreak P = ShoreBreakParams( A, d );
	ShoreProfile s0 = ShoreProfileAt( u, lam, P, true );
	ShoreProfile s1 = ShoreProfileAt( u + du, lam, P, false );
	ShorePair o;
	o.s0 = float4( s0.x, s0.y, s0.foam, s0.b );
	o.s1 = float4( s1.x, s1.y, s0.face, s0.roller );
	return o;
}

// the surface at a fixed WORLD point (for the Eulerian shore simulation): whitewater there and the water height there
// (the parcel shown at this point rests ~x up-wave: first-order inverse of the horizontal displacement, which is large on
// a breaking wave)
// ( foam, height, displacement of the parcel resting here, b )
float4 ShoreWorld( float u, float A, float d, float lam, float env )
{
	ShoreBreak P = ShoreBreakParams( A, d );
	ShoreProfile s0 = ShoreProfileAt( u, lam, P, false );
	ShoreProfile s1 = ShoreProfileAt( u + s0.x * env / lam, lam, P, false );
	return float4( ShoreWhitewater( -u * lam, lam, P ), s1.y, s0.x, P.b );
}

// the bore that follows the plunge: roller height Hb, horizontal extent of its front Wt, forward shift of the crest Xc (to
// where the lip landed), bore weight (0 while plunging .. 1)
float4 ShoreBore( float A, float d )
{
	ShoreBreak P = ShoreBreakParams( A, d );
	return float4( P.Hb, P.Wt, P.Xc, P.wBore );
}

// breaking progress b, wave height H, trough level (relative to mean) and how far the lip is thrown
float4 ShoreCrest( float A, float d )
{
	ShoreBreak P = ShoreBreakParams( A, d );
	return float4( P.b, P.yc - P.yt, P.ytB, P.Xi );
}

// Depth that sets the breaking state of the wave a parcel belongs to: the depth under that wave's crest, not under the
// parcel. Breaking is a property of the wave: a parcel ahead of the crest is in shallower water and would otherwise
// "break" first (whitewater creeping up the foot of a still glassy face). Near the troughs it hands over to the local
// depth, where the neighbouring wave takes over (the profile stays continuous at u = +-0.5). u: local phase, lam: local
// wavelength.
float ShoreBreakDepth( float2 xz, float2 dir, float u, float lam, float d )
{
	float2 pc = xz + dir * ( u * lam );
	float dc = _TWShoreE.y - TWHeightAt( pc );
	return lerp( dc, d, smoothstep( 0.3, 0.5, abs( u ) ) );
}

// ------------------------------------------------------------ cheap wave direction lookup

// vec3( dir.x, dir.z, exposure ) from the direction texture (bilinear from 4 loads)
float3 ShoreDirAt( float2 xz )
{
	uint rw, rh;
	_TWShoreDirTex.GetDimensions( rw, rh );
	float res = ( float ) rw;
	float2 fp = ( xz - shoreP_dirMin ) / shoreP_dirSize * res - 0.5;
	float2 fc = clamp( fp, 0.0, res - 1.001 );
	int2 i = ( int2 ) floor( fc );
	float2 t = frac( fc );
	float4 a = _TWShoreDirTex.Load( int3( i, 0 ) );
	float4 b = _TWShoreDirTex.Load( int3( i + int2( 1, 0 ), 0 ) );
	float4 c = _TWShoreDirTex.Load( int3( i + int2( 0, 1 ), 0 ) );
	float4 d = _TWShoreDirTex.Load( int3( i + int2( 1, 1 ), 0 ) );
	return lerp( lerp( a, b, t.x ), lerp( c, d, t.x ), t.y ).xyz;
}

// ------------------------------------------------------------ surf zone water

// Optical properties of the water stirred up by breaking waves: suspended sand and fine bubbles scatter light (milky,
// luminous), fine sediment and dissolved matter absorb blue. Returns the extra { scatter, absorb } coefficients (1/m) that
// make the surf zone turquoise, not ocean-clear.
ShoreMedium ShoreSurfMedium( float2 xz, float depth )
{
	float k = 0.0;
	if ( depth < 4.5 && depth > -0.2 )
	{
		float expo;
		if ( shoreP_dirOn > 0.5 ) { expo = ShoreDirAt( xz ).z; } else { expo = saturate( length( TWShoreSample( xz ).yz ) * 1.4 ); }
		k = smoothstep( 4.5, 1.2, depth ) * smoothstep( -0.2, 0.15, depth ) * expo * shoreP_turbidity * shoreP_enabled;
	}
	ShoreMedium o; o.scatter = float3( 0.9, 1.0, 0.85 ) * k; o.absorb = float3( 0.1, 0.2, 0.62 ) * k;
	return o;
}

// ------------------------------------------------------------ evaluation at a point

// Local wave phase data at a (Lagrangian) point: shared by evaluate() and the crest finder.
ShorePhase ShorePhaseAt( float2 xz )
{
	float4 sh = TWShoreSample( xz );
	float T = sh.x;
	float2 dirE = float2( sh.y, sh.z );
	float exposure = length( dirE );
	float2 dir = dirE / max( exposure, 1e-4 );
	float along = dot( xz, float2( -dir.y, dir.x ) );
	// shoreP.phase is the accumulated phase (see the uniform): the travel time T delays each point by T / period, exactly as
	// before, but the clock itself is never divided by the period
	float s = shoreP_phase - T / shoreP_period + ShoreWobble( along );
	ShorePhase o; o.sh = sh; o.T = T; o.dir = dir; o.exposure = exposure; o.along = along; o.s = s;
	return o;
}

// ------------------------------------------------------------ light through thin crests

// Water path (m) along the refracted view ray Tv (unit, inside the water) from the surface point with rest position lagXZ
// until the ray leaves through the other side of the wave, or 1e4 if it doesn't within a few metres. The upper part of a
// steep wave is only a few metres thick horizontally: the view ray crosses it and exits into the sky behind, which is what
// makes breaking faces and crests glow turquoise. Marches the analytic cross-section (up to 3 steps).
float ShoreCrestPath( float2 p, float d, float3 T )
{
	float outp = 1e4;
	if ( d >= 6.0 || shoreP_enabled <= 0.0 ) { return outp; } // (before the phase lookup)
	ShorePhase ph = ShorePhaseAt( p );
	float tXi = dot( T.xz, ph.dir ); // shoreward component of the ray
	float env = smoothstep( 26.0, 13.0, d ) * saturate( ph.exposure * 1.4 ) * shoreP_enabled;
	// rays heading out through the back of the wave (a view from the beach side), near breakers
	if ( env > 0.05 && tXi < -0.05 && d < 6.0 )
	{
		float lam = sqrt( clamp( d, 0.3, 25.0 ) * SHORE_GRAVITY ) * shoreP_period;
		float m = floor( ph.s + 0.5 );
		float u = ph.s - m;
		float A = ShoreWaveAmp( m, ph.along );
		ShoreBreak P = ShoreBreakParams( A, ShoreBreakDepth( p, ph.dir, u, lam, d ) );
		ShoreProfile s0 = ShoreProfileAt( u, lam, P, false );
		float y0 = s0.y * env;
		// only the upper part of steep (nearly breaking) waves is thin enough to see through
		if ( y0 > A * 0.2 && P.b > -1.5 )
		{
			float xi0 = -u * lam + s0.x * env;
			float slope = T.y / -tXi; // ray rise per metre of horizontal travel
			float prevGap = 0.0;
			float prevDist = 0.0;
			float off = 1.1;
			[loop]
			for ( int k = 0; k < 3; k ++ )
			{
				float uk = min( u + off / lam, 0.5 );
				ShoreProfile sk = ShoreProfileAt( uk, lam, P, false );
				float dist = abs( xi0 - ( -uk * lam + sk.x * env ) );
				// ray height above the surface there (> 0: the ray has left the water)
				float gap = y0 + slope * dist - sk.y * env;
				if ( gap > 0.0 )
				{
					float fr = -prevGap / max( gap - prevGap, 1e-4 );
					outp = lerp( prevDist, dist, saturate( fr ) ) / -tXi;
					break;
				}
				prevGap = gap;
				prevDist = dist;
				off *= 2.6;
			}
		}
	}
	return outp;
}

// ------------------------------------------------------------ swash

// Run-up of the most recent wave at a point on the beach (distances in metres up the beach face). The run-up is compared
// with the height of the sand (converted with the nominal beach slope), so the front is exact at the waterline and follows
// the contours of the sand. sh: TWShoreSample( xz ).
ShoreRunup ShoreSwashRunup( float4 sh, float along, float groundH )
{
	float Tp = shoreP_period;
	float exposure = length( float2( sh.y, sh.z ) );
	float Ts = sh.w;
	float inland = max( groundH - _TWShoreE.y, 0.0 ) / SHORE_BEACH_SLOPE;
	// the same accumulated phase as ShorePhaseAt: the swash runs on the wave clock, so it has to be the same clock
	// (frame.time / period would step the run-up whenever the period changed)
	float ss = shoreP_phase - Ts / Tp + ShoreWobble( along );
	float ms = floor( ss );
	float tau = ss - ms; // 0..1 time since that wave's bore reached the shoreline
	float Am = ShoreWaveAmp( ms, along );
	// vertical run-up ~ H on this gentle beach, converted to a horizontal excursion
	float RhMax = Am * 2.1 * shoreP_runup * saturate( exposure * 1.4 ) / SHORE_BEACH_SLOPE;
	// decelerating uprush, then a backwash that starts slowly and accelerates as the sheet drains
	float su = saturate( tau / SHORE_SWASH_UP );
	float sb = saturate( ( tau - SHORE_SWASH_UP ) / SHORE_SWASH_DOWN );
	bool isUp = tau < SHORE_SWASH_UP;
	float Rh = ( isUp ? 1.0 - pow( 1.0 - su, 1.5 ) : 1.0 - pow( sb, 1.6 ) ) * RhMax - 0.3;
	// the front is lobed, not a straight line: each wave runs up a little differently along the beach
	float lobes = sin( along * 0.61 + ms * 2.3 ) * 0.5 + sin( along * 1.73 + ms * 5.1 ) * 0.3 + sin( along * 4.3 + ms * 1.7 ) * 0.2;
	// the backwash never quite exposes the lower beach face: a film of water always covers the first decimetres past the
	// shoreline, so the sea never meets the sand along mesh triangles
	float Rt = max( Rh + lobes * ( max( Rh, 0.0 ) * 0.07 + 0.35 ), 0.35 ) * shoreP_enabled;
	ShoreRunup o; o.tau = tau; o.Rt = Rt; o.inland = inland; o.RhMax = RhMax; o.su = su; o.sb = sb; o.isUp = isUp;
	return o;
}

// Water film thickness clipped at the leading edge of the swash sheet, for the water shader's edge fade: min( thickness,
// distance to the front (m) * 0.08 ). Only evaluated where the film is thin, so the sheet ends on the analytic front
// instead of the mesh triangles at ~no cost.
// returns ( clipped thickness, distance to the swash front (m, > 0 on the water side; 1e3 away from the swash), tau, run-up Rt )
float4 ShoreSwashEdge( float2 p, float t )
{
	float4 outp = float4( t, 1e3, 0.0, 0.0 );
	if ( t < 0.3 )
	{
		float g = TWHeightAt( p );
		if ( g > _TWShoreE.y - 0.8 )
		{
			ShorePhase ph = ShorePhaseAt( p );
			ShoreRunup r = ShoreSwashRunup( ph.sh, ph.along, g );
			float front = r.Rt - r.inland;
			// The lapping region reaches down the beach face past where the sea's edge sits in the trough of the backwash (it
			// stopped at sea level: a strip in between with a straight edge across the draining water), and fades in from
			// there and from 0.3 m of film instead of switching on. The front distance is divided by the weight so the
			// effects at the front recede with it.
			float w = smoothstep( _TWShoreE.y - 0.8, _TWShoreE.y - 0.4, g ) * smoothstep( 0.3, 0.15, t );
			if ( w > 0.0 )
			{
				outp = float4( lerp( t, min( t, front * 0.08 ), w ), front / max( w, 1e-3 ), r.tau, r.Rt * w );
			}
		}
	}
	return outp;
}

float ShoreSwashClip( float2 p, float t ) { return ShoreSwashEdge( p, t ).x; }

// ------------------------------------------------------------ evaluate()
// Returns displacement relative to (xz, seaLevel), foam, breaking indicator and the swash surface level (absolute height)
// for this point. ShoreEvaluate (withNormal, the water mesh) also gives face: 1 on the clear concave face of a plunging
// wave, roller: relief of the whitewater (m). ShoreEvaluateWorld: the shore simulation's view (a fixed world point instead
// of a water parcel): foam and height are those of the water shown at xz, the displacement is not meaningful.
// mode: 0 = the water mesh (with the normal), 1 = plain (queries), 2 = world (ShoreSim)
ShoreSample ShoreEvaluateImpl( float2 xz, float depth, float groundH, int mode )
{
	ShorePhase ph = ShorePhaseAt( xz );
	float4 sh = ph.sh;
	float exposure = ph.exposure;
	float along = ph.along;
	float2 dir = ph.dir;
	float Tp = shoreP_period;

	float d = depth;
	float c = sqrt( clamp( d, 0.3, 25.0 ) * SHORE_GRAVITY );
	float lam = c * Tp;

	float s = ph.s;
	float m = floor( s + 0.5 );
	float u = s - m;

	// wave height with smooth hand-over between consecutive waves at the trough
	float A0 = ShoreWaveAmp( m, along );
	float An = ShoreWaveAmp( m + sign( u ), along );
	float A = lerp( A0, An, smoothstep( 0.32, 0.5, abs( u ) ) * 0.5 );
	// the breaking state of the whole wave comes from the depth under its crest
	float dB = ShoreBreakDepth( xz, dir, u, lam, d );

	// offshore fade-in (FFT covers deep water) and fade on land (the swash sheet takes over there)
	float env = smoothstep( 26.0, 13.0, d ) * smoothstep( -0.25, 0.05, d ) * saturate( exposure * 1.4 ) * shoreP_enabled;

	// finite difference along the propagation direction for the normal
	float e = 0.15;
	float face = 0.0;
	float roller = 0.0;
	float4 s0, s1 = 0.0;
	if ( mode == 2 )
	{
		float4 r = ShoreWorld( u, A, dB, lam, env );
		s0 = float4( r.z, r.y, r.x, r.w );
	}
	else if ( mode == 0 )
	{
		ShorePair pair = ShoreShapePair( u, -e / lam, A, dB, lam );
		s0 = pair.s0;
		s1 = pair.s1;
		face = s1.z * env;
		roller = s1.w * env;
	}
	else
	{
		s0 = ShoreShape( u, A, dB, lam );
	}

	float3 disp = float3( dir.x * s0.x, s0.y, dir.y * s0.x ) * env;

	float3 nShore = float3( 0.0, 1.0, 0.0 );
	if ( mode == 0 )
	{
		float dX = e + ( s1.x - s0.x ) * env;
		float dY = ( s1.y - s0.y ) * env;
		float3 tAlong = float3( dir.x * dX, dY, dir.y * dX );
		float3 tAcross = float3( -dir.y, 0.0, dir.x );
		// epsilon: never a zero vector; never facing down (the mesh doesn't overhang, the lip sheet does)
		float3 n = normalize( cross( tAcross, tAlong ) + float3( 0.0, 1e-4, 0.0 ) );

		// the whitewater roller is not a smooth tube: lumps of foam tumble along its front and over its top (relief of a few
		// decimetres, with its slope in the normal). Noise, not a sum of sines: regular bumps along the crest read as a row
		// of identical puffs. Bigger, fewer lumps in some stretches, a lower, smoother churn in others (different for every
		// wave).
		float sx = u * lam; // rest position along the wave direction (m, seaward)
		float t = _TWShoreE.x;
		float mW = floor( ph.s + 0.5 );
		// (the lumps only exist on the roller: amp is 0 elsewhere)
		if ( roller != 0.0 )
		{
			float lumpy = saturate( TWPerlin2( float2( along * 0.045, mW * 3.7 ) ) * 1.2 + 0.55 );
			float amp = roller * lerp( 0.25, 0.7, lumpy );
			float2 q1 = float2( along * 0.28, sx * 0.7 - t * 0.8 );
			float2 q2 = float2( along * 0.8 + 11.3, sx * 1.6 - t * 1.5 );
			float eL = 0.25;
			float L0 = TWPerlin2( q1 ) * 0.7 + TWPerlin2( q2 ) * 0.3;
			float La = TWPerlin2( q1 + float2( eL * 0.28, 0.0 ) ) * 0.7 + TWPerlin2( q2 + float2( eL * 0.8, 0.0 ) ) * 0.3;
			float Ls = TWPerlin2( q1 + float2( 0.0, eL * 0.7 ) ) * 0.7 + TWPerlin2( q2 + float2( 0.0, eL * 1.6 ) ) * 0.3;
			// lumps stand up from the roller (rounded caps, flatter troughs between them)
			float lump = max( L0 * 1.5 + 0.2, -0.3 );
			disp.y += lump * amp;
			float dAlong = ( L0 * 1.5 + 0.2 > -0.3 ) ? ( La - L0 ) / eL * 1.5 : 0.0;
			float dSx = ( L0 * 1.5 + 0.2 > -0.3 ) ? ( Ls - L0 ) / eL * 1.5 : 0.0;
			float dShore = -dSx; // d/d(shoreward) = - d/dsx
			float2 g = ( float2( -dir.y, dir.x ) * dAlong + dir * dShore ) * amp * n.y;
			nShore = normalize( float3( n.x - g.x, max( n.y, 0.04 ), n.z - g.y ) );
		}
		else
		{
			nShore = normalize( float3( n.x, max( n.y, 0.04 ), n.z ) );
		}
	}

	// ---- swash: run-up of the most recent wave on the sand
	ShoreRunup swr = ShoreSwashRunup( sh, along, groundH );
	float front = swr.Rt - swr.inland; // signed distance to the leading edge (m), > 0 under the sheet
	bool covered = front > 0.0;
	// leading edge velocity along the slope (m/s), positive = uphill
	float dRdt = ( swr.isUp ? pow( 1.0 - swr.su, 0.5 ) * ( 1.5 / SHORE_SWASH_UP ) : pow( max( swr.sb, 1e-3 ), 0.6 ) * ( -1.6 / SHORE_SWASH_DOWN ) ) * swr.RhMax / Tp;
	// thin sheet (a few cm, thickening behind the leading edge). The mesh sheet overshoots the leading edge a little; the
	// water shader cuts it exactly on the front (swashClip), so the edge doesn't follow the mesh triangles.
	float fm = front + SHORE_SWASH_OVERSHOOT;
	float thick = clamp( min( fm * 0.3, max( fm - 0.1, 0.0 ) * ( SHORE_BEACH_SLOPE * 0.22 ) + 0.03 ), -0.1, 0.12 );
	float swashLevel = groundH + thick;
	// bubbly foam line riding the leading edge all the way up (left behind as the swash mark)
	float uprush = smoothstep( 0.46, 0.32, swr.tau );
	float edge = smoothstep( 0.8, 0.0, front ) * smoothstep( -0.05, 0.05, front ) * uprush * smoothstep( 0.0, 1.0, swr.Rt );
	float swashFoam = edge * 0.9;

	// ---- depth-averaged water velocity (along dir), for foam advection
	// waves / bores: shallow-water particle velocity c * eta / h; swash sheet: the tip moves at dR/dt, slower toward the
	// shoreline during uprush, faster there while it drains
	float eta = disp.y;
	float uWave = clamp( c * eta / ( max( d, 0.0 ) + max( eta, d * -0.8 ) + 0.15 ), -2.5, 4.0 );
	float rel = saturate( swr.inland / max( swr.Rt, 0.5 ) );
	float uSwash = dRdt * ( swr.isUp ? clamp( ( swr.inland + 3.0 ) / ( swr.Rt + 3.0 ), 0.15, 1.0 ) : ( 1.0 - rel ) * 0.6 + 0.9 );
	float wSwash = smoothstep( 0.3, 0.05, d );
	float uFlow = lerp( uWave, uSwash, wSwash ) * ( ( covered || d > 0.02 ) ? 1.0 : 0.0 );

	ShoreSample o;
	// the churn of a bore is uneven along the crest: dense in some stretches, torn into patches and lace in others (different
	// for every wave, drifting slowly along it)
	float wwPatch = 0.0;
	if ( s0.z * env != 0.0 ) { wwPatch = smoothstep( -0.5, 0.45, TWPerlin2( float2( along * 0.06 + _TWShoreE.x * 0.05, m * 2.9 + 0.4 ) ) ); }
	o.disp = disp; o.nShore = nShore; o.env = env; o.foam = s0.z * env * lerp( 0.3, 1.0, wwPatch ); o.breaking = s0.w; o.u = u; o.dir = dir;
	o.exposure = exposure; o.swashLevel = swashLevel; o.swashCovered = covered ? 1.0 : 0.0; o.thick = thick;
	o.swashFoam = swashFoam; o.runup = swr.Rt; o.inland = swr.inland; o.dRdt = dRdt; o.tau = swr.tau;
	o.flow = dir * uFlow; o.flowSpeed = uFlow; o.face = face; o.roller = roller;
	return o;
}

ShoreSample ShoreEvaluate( float2 xz, float depth, float groundH ) { return ShoreEvaluateImpl( xz, depth, groundH, 0 ); }
ShoreSample ShoreEvaluateNoNormal( float2 xz, float depth, float groundH ) { return ShoreEvaluateImpl( xz, depth, groundH, 1 ); }
ShoreSample ShoreEvaluateWorld( float2 xz, float depth, float groundH ) { return ShoreEvaluateImpl( xz, depth, groundH, 2 ); }

#endif
