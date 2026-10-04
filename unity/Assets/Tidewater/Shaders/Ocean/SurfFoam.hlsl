// Port of the WGSL module of src/ocean/SurfFoam.js (prefix surfFoam): the foam look used by the water shader (hooks called from
// WaterSurface.fragment and WaterMaterial.shade).
//
// Whitewater is a volume of bubbles: a dense mat that is bright from every side, with a lumpy, bubbly surface (darker in its
// own dips), tearing into patches and then into lace (thin bubble strands around clear holes) as it decays. Thin foam is
// translucent over the water colour.
//
// The pattern lives in world space, never in the coordinates of the displaced surface (those are squeezed and stretched by the
// big horizontal motion of the shore waves and would draw the foam into streaks). It is carried by the water with a dual-phase
// flow map: two copies of the pattern, half a cycle apart, each advected by the local flow (from ShoreSim) for one cycle and
// then re-placed at random, cross-faded so neither is ever stretched for long and the resets never show. Steep whitewater faces
// (bore / roller fronts) use a vertical projection that rolls down the face.
//
//   SurfFoamInfo SurfFoamShading( SurfFoamArgs a ), float3 SurfFoamLight( info, N, L, V, sun, P, skyIrradiance ),
//   float4 SurfFoamFlowLace( q, flow, salt )
// Needs ShoreSim.hlsl (ShoreSimInside / ShoreSimUvOf) and ShoreWaves.hlsl (ShoreDirAt, time). _TWSurfLace is the filtered lace
// pattern (repeat, aniso, mips).

#ifndef TW_SURF_FOAM_INCLUDED
#define TW_SURF_FOAM_INCLUDED

#define SURF_FOAM_BUMP 0.7        // relief of the whitewater and of thick foam (normal perturbation strength)
#define SURF_FOAM_PERIOD 1.2      // s, flow map cycle
#define SURF_FOAM_MAXFLOW 2.5     // m/s, the pattern lags behind faster flow (bounds the distortion per cycle)

TEXTURE2D(_TWSurfLace); SAMPLER(sampler_TWSurfLace);

struct SurfFoamArgs
{
	float coverage;
	float foam;
	float footprint;
	float depth;
	float bubbles;
	float2 lagXZ;
	float3 normal;
	float3 baseNormal;
	float fresh;
	float sim;
	float4 simState;
	float roller;
	float3 P;
	float seaLevel;
	float3 sunDir;
};

struct SurfFoamInfo
{
	float foam;
	float density;
	float height;
	float surf;
	float ww;
	float relief;
	float selfShadow;
	float cavity;
	// relief split into world-space patterns and their weights: the weights come from values interpolated across the water
	// mesh (fresh whitewater, depth, footprint), so the screen-space gradient of a weight is constant per triangle;
	// differentiating the product drew every mesh triangle as a flat facet into the foam. Only the patterns are differentiated.
	float reliefPat; // whitewater lumps (m), relief = reliefPat * reliefK
	float reliefK;
	float thinPat; // thin foam: height = thinPat * thinK + bubPat * bubK
	float thinK;
	float bubPat;
	float bubK;
};

// Henyey-Greenstein phase (1/sr)
float SurfFoamPhaseHG( float cosT, float g )
{
	float g2 = g * g;
	return ( ( 1.0 - g2 ) / ( 4.0 * PI ) ) / pow( max( 1.0 + g2 - cosT * 2.0 * g, 1e-4 ), 1.5 );
}

float SurfFoamHash11( float x ) { return frac( sin( x * 91.7 + 17.3 ) * 43758.5453 ); }

// Lace distance (0 on a bubble strand .. 1 in a hole) at pattern coordinates q (m), carried by flow (m/s in the same
// coordinates) with the dual-phase flow map. salt decorrelates layers.
float4 SurfFoamFlowLace( float2 q, float2 flow, float salt )
{
	float t = _TWShoreE.x / SURF_FOAM_PERIOD;
	float2 v = clamp( flow, -SURF_FOAM_MAXFLOW, SURF_FOAM_MAXFLOW ) * SURF_FOAM_PERIOD;
	float4 outp = 0.0;
	[unroll]
	for ( int k = 0; k < 2; k ++ )
	{
		float tk = t + ( ( float ) k * 0.5 + salt * 0.37 );
		float ph = frac( tk );
		float cycle = floor( tk ) + ( ( float ) k * 13.1 + salt * 5.7 );
		float2 jitter = float2( SurfFoamHash11( cycle ), SurfFoamHash11( cycle + 0.5 ) ) * 7.0;
		float2 p = ( q - v * ( ph - 0.5 ) ) / SHORE_SIM_LACE_TILE + jitter;
		float w = 1.0 - abs( ph * 2.0 - 1.0 );
		outp += SAMPLE_TEXTURE2D( _TWSurfLace, sampler_TWSurfLace, p ) * w;
	}
	return outp; // the two weights always add up to 1
}

float SurfFoamBigAt( float2 qq, float2 pflow )
{
	return sqrt( SurfFoamFlowLace( qq / 6.0, pflow / 6.0, 2.0 ).x );
}

// coverage: total foam amount (0..1, all sources); foam: the default (offshore whitecap) foam; footprint: pixel size on the
// surface (m); depth: sea depth; bubbles: fine bubble detail; normal: surface normal; fresh: whitewater made by the breaking
// wave right here (roller, plunge point, swash front); sim: foam carried by the water (ShoreSim, already kept off the clear
// face of plunging waves); simState: ShoreSimSample() at this pixel; roller: relief of the whitewater roller (m)
SurfFoamInfo SurfFoamShading( SurfFoamArgs a )
{
	// world position of the fragment (the pattern is in world space); a caller that left P unset (zero) gets the rest position
	// instead: close, but the lace then rides the horizontal wave motion
	float3 P = all( a.P == 0.0 ) ? float3( a.lagXZ.x, a.seaLevel, a.lagXZ.y ) : a.P;
	float2 xz = P.xz;
	float coverage = a.coverage;
	float opacity = a.foam;
	float density = saturate( coverage );
	float height = 0.0;
	float wwOut = 0.0; // how much of it is whitewater (deeper crevices)
	float wwRelief = 0.0; // relief of the whitewater (m)
	float wwShadow = 1.0; // sun visibility inside the churn
	float wwCav = 1.0; // sky visibility in its crevices
	float reliefPat = 0.0; float reliefK = 0.0;
	float thinPat = 0.0; float thinK = 0.0;
	float bubPat = 0.0; float bubK = 0.0;
	// surf look near the beach, the default whitecap look offshore
	float surf = smoothstep( 7.0, 3.0, a.depth ) * ShoreSimInside( ShoreSimUvOf( xz ) );
	if ( surf > 0.0 && coverage > 0.04 )
	{
		// flow of the water here, from the shore simulation (along the local wave direction)
		float2 dir = ShoreDirAt( xz ).xy;
		float speed = a.simState.w;
		float2 flow = dir * speed;

		// --- pattern: world-space lace carried by the flow; on steep faces a vertical projection (along the crest x height)
		// rolling down the face with the roller (from the normal of the wave itself: steep ripple facets must not switch the
		// projection, that drew combs of vertical streaks into the foam on flat water and on the swash)
		float steep = smoothstep( 0.82, 0.5, a.baseNormal.y );
		float ww = saturate( a.fresh * 1.4 );
		// far pixels (a lace cell under a few pixels) only use the pattern's average (see "far" below): skip the lace and
		// whitewater lump lookups there
		bool farOnly = a.footprint >= 0.12;
		float4 flatL = 0.5;
		if ( ! farOnly ) { flatL = SurfFoamFlowLace( xz, flow, 0.0 ); }
		float4 lace = flatL;
		// lumps of tumbling whitewater (~0.6 m), only where there is whitewater
		float lumps = 0.5;
		float2 tangent = float2( -dir.y, dir.x );
		// (compressed vertically: a front only a metre or two high must not show single lumps as columns)
		// (the along-crest coordinate warped by low-frequency noise: the 3.5 m lace tile must not repeat as a row of identical
		// lumps and spikes along the break)
		float al = dot( xz, tangent );
		float2 qv = float2( al + sin( al * 0.19 + 0.8 ) * 2.1 + sin( al * 0.47 + 2.9 ) * 0.6 + sin( P.y * 1.7 + al * 0.11 ) * 0.5, P.y * 1.1 );
		if ( steep > 0.01 && ! farOnly )
		{
			float4 vert = SurfFoamFlowLace( qv, float2( 0.0, -0.9 ), 1.0 );
			lace = lerp( flatL, vert, steep );
		}
		// Churning whitewater is a pile of foam lumps at several scales (tumbling masses ~1.3 m, clumps ~0.5 m, bubble clusters
		// ~0.2 m): a relief (m) for the normals, sunlit caps and self-shadowed crevices (a short march toward the sun through
		// the lump field)
		if ( ww > 0.02 && ! farOnly )
		{
			float3 L = a.sunDir;
			float2 pq = lerp( xz, qv, steep );
			float2 pflow = lerp( flow, float2( 0.0, -0.9 ), steep );
			float bigL = SurfFoamBigAt( pq, pflow );
			float midL = sqrt( SurfFoamFlowLace( pq / 2.2, pflow / 2.2, 3.0 ).x );
			lumps = bigL * 0.6 + midL * 0.4;
			float A = 0.22; // relief of the lumps (m)
			reliefPat = bigL * A + midL * ( A * 0.45 ) + ( 1.0 - lace.x ) * ( A * 0.12 );
			wwRelief = reliefPat * ww;
			// the sun direction in the pattern's coordinates, and its elevation above the local surface
			float2 Lp = lerp( L.xz, float2( dot( L.xz, tangent ), L.y * 1.1 ), steep );
			float2 Ld = Lp / max( length( Lp ), 1e-3 );
			float NdL = dot( a.normal, L );
			float tanE = NdL / max( length( L - a.normal * NdL ), 0.05 );
			float occ = 0.0;
			const float stepsD[ 3 ] = { 0.14, 0.34, 0.7 };
			[unroll]
			for ( int i = 0; i < 3; i ++ )
			{
				float d = stepsD[ i ];
				float hk = SurfFoamBigAt( pq + Ld * d, pflow );
				occ = max( occ, smoothstep( 0.0, 0.05, ( hk - bigL ) * A - tanE * d ) );
			}
			wwShadow = 1.0 - occ * lerp( 0.85, 0.7, steep );
			// crevices between the lumps: occluded from the sky too
			wwCav = lerp( 0.5, 1.0, smoothstep( 0.05, 0.75, lumps ) );
		}

		// --- whitewater: the aerated mass of a roller / plunge / swash front. Dense and opaque, its surface boiling: lumps with
		// shaded crevices between them, bubble clusters on each; it only tears (and shows water through) at its edges.
		float boil = lace.x * 0.7 + lace.z * 0.3;
		// (the edge of the churn is torn by its lumps: ragged fingers, not a clean boundary)
		float wwEdge = smoothstep( 0.25, 0.75, ww * 1.35 - boil * 0.3 - ( 1.0 - lumps ) * 0.5 );
		float whitewater = ( ww * 0.25 + wwEdge * 0.75 ) * ( ( 1.0 - boil ) * 0.12 + 0.88 );

		// --- foam carried by the water: a lacy web of bubble strands and clusters around holes. With more foam the strands
		// widen into a mat; as it spreads and thins it tears into filaments.
		// (w: strand half-width; the pattern covers 6% of the area at w = 0.1, 34% at 0.3, 82% at 0.6)
		float c = saturate( ( a.sim + max( coverage - a.sim - a.fresh, 0.0 ) * 0.5 - 0.05 ) / 0.95 );
		// hole edges are ragged (bubble clusters), hole sizes vary between patches
		float w = max( pow( c, 1.4 ) * 0.9 * ( lace.z * 0.5 + 0.75 ) + ( lace.y - 0.5 ) * 0.06, 0.0 );
		float soft = 0.05 + a.footprint * 4.5;
		float matK = ( 1.0 - smoothstep( w - soft, w + soft, lace.x ) ) * smoothstep( 0.0, 0.05, c );
		// scattered bubbles in the holes next to the strands
		float bub = lace.y * smoothstep( w + 0.25, w, lace.x ) * smoothstep( 0.02, 0.2, c ) * 0.5;
		// thin foam is translucent and uneven, thick foam is opaque
		float inner = saturate( ( w - lace.x ) / 0.25 );
		float laceFoam = max( matK * saturate( inner * 0.35 + 0.45 + lace.z * 0.3 ), bub );

		// once a lace cell (~0.2 m) covers a few pixels, use the average of the pattern
		float farK = smoothstep( 0.03, 0.12, a.footprint );
		float average = max( saturate( pow( w, 1.45 ) * 1.9 ) * 0.8, ww * 0.95 );
		float nearK = max( laceFoam, whitewater );
		opacity = lerp( a.foam, lerp( nearK, average, farK ), surf );

		// optical thickness (thin foam is translucent, thick foam scatters like snow) and a relief height for the lighting:
		// lumpy boiling whitewater, thick foam higher than its thin edges
		density = max( saturate( c * 1.3 ) * ( inner * 0.5 + 0.5 ), ww );
		float reliefThin = inner * saturate( c * 1.5 );
		float relief = reliefThin * ( 1.0 - ww );
		wwOut = ww * ( 1.0 - farK * 0.6 );
		// far away the lumps average out: a mean shadowing of the churn instead
		wwShadow = lerp( wwShadow, 0.82, farK );
		wwCav = lerp( wwCav, 0.8, farK );
		wwRelief *= 1.0 - farK;
		height = ( relief + a.bubbles * 0.15 ) * surf * ( 1.0 - farK );
		reliefK = ww * ( 1.0 - farK );
		thinPat = reliefThin;
		thinK = ( 1.0 - ww ) * surf * ( 1.0 - farK );
		bubPat = a.bubbles * 0.15;
		bubK = surf * ( 1.0 - farK );
	}

	SurfFoamInfo o;
	o.foam = opacity; o.density = density; o.height = height; o.surf = surf; o.ww = wwOut; o.relief = wwRelief; o.selfShadow = wwShadow; o.cavity = wwCav;
	o.reliefPat = reliefPat; o.reliefK = reliefK; o.thinPat = thinPat; o.thinK = thinK; o.bubPat = bubPat; o.bubK = bubK;
	return o;
}

// Foam radiance: a dense scatterer, wrapped diffuse sun (light diffuses through the bubbles), sky ambient, darker in the dips of
// the bubbly relief, glowing at thin edges when backlit.
float3 SurfFoamLight( SurfFoamInfo info, float3 N, float3 L, float3 V, float3 sun, float3 P, float3 skyIrradiance )
{
	float ww = info.ww;
	float cavity = info.cavity;
	// relief normal from the screen-space gradient of the height (Mikkelsen surface gradient): the thin-foam relief (in units of
	// ~3 cm) plus the whitewater lumps (m)
	// (gradients of the world-space patterns only, scaled by their weights: see SurfFoamInfo)
	float kb = SURF_FOAM_BUMP * 0.04;
	float3 dpx = ddx( P );
	float3 dpy = ddy( P );
	float dhdx = ddx( info.reliefPat ) * info.reliefK + ( ddx( info.thinPat ) * info.thinK + ddx( info.bubPat ) * info.bubK ) * kb;
	float dhdy = ddy( info.reliefPat ) * info.reliefK + ( ddy( info.thinPat ) * info.thinK + ddy( info.bubPat ) * info.bubK ) * kb;
	float3 r1 = cross( dpy, N );
	float3 r2 = cross( N, dpx );
	float det = dot( dpx, r1 );
	float3 grad = ( r1 * dhdx + r2 * dhdy ) * sign( det );
	float3 Nf = normalize( N * abs( det ) - grad + N * 1e-6 );
	float NdL = dot( Nf, L );
	// foam lets light diffuse into it (wrapped lighting); churning whitewater much less so: its sides facing away from the sun
	// are shaded grey-blue by the sky, its caps sunlit, its crevices in the shadow of the lumps around them
	float wrap = lerp( 0.45, 0.12, ww );
	float diff = saturate( NdL * ( 1.0 - wrap ) + wrap ) * lerp( 1.0, info.selfShadow, ww );
	// dips between the lumps are shaded by the lumps around them (sky occlusion)
	float ao = lerp( lerp( 1.0, info.height * 0.3 + 0.76, info.surf ), cavity, ww );
	// light through thin aerated water (torn edges, thin foam, spray-soaked lips): green-white, strongly forward scattered
	float thin = ( 1.0 - info.density ) * 0.8 + ww * ( 1.0 - cavity ) * 0.3;
	float trans = SurfFoamPhaseHG( dot( -V, L ), 0.55 ) * thin * 1.3;
	float3 transCol = lerp( 1.0, float3( 0.6, 0.92, 0.82 ), ww * 0.7 + 0.3 );
	// light bounced around inside the churn (from its sunlit lumps) keeps the shaded foam from going as dark and as blue as the
	// open sky alone would make it
	float3 sky = skyIrradiance;
	float3 skyGrey = dot( sky, float3( 0.2126, 0.7152, 0.0722 ) );
	float3 amb = lerp( sky, skyGrey, ww * 0.35 ) * ao + sun * ( ww * 0.05 ) * ao;
	return ( sun * ( diff * lerp( 1.0, sqrt( cavity ), ww ) / PI + transCol * trans ) + amb ) * 0.86;
}

#endif
