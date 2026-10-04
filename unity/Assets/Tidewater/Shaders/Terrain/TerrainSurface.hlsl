// Port of the TERRAIN_SURFACE / TERRAIN_MATERIAL_WGSL material code of src/world/Terrain.js: coral sand (dry /
// damp / wet, wind ripples, shell grit, wrack line, swash marks), the seabed (sand with ripple fields,
// seagrass meadows, rubble heads), tropical lawn, jungle floor and canopy, landslide scars, worn dirt paths and
// weathered volcanic rock (triplanar, faceted blocks, bedding, lichen, moss, splash-zone zonation, rain
// streaks). Detail comes from one small tileable height texture sampled at several scales / rotations;
// normals use surface-gradient bump mapping; the baked horizon AO feeds the surface ao. Rock is only evaluated
// where it can appear.
//
// Everything here works in SIM space (x east, y up, z south). The caller passes the sim position and camera and
// gets a sim-space normal back.
//
// Differences from the JS, all pending other systems:
//  - the heightfield sun shadow modulation (materialSunModulation) is not applied: HDRP's cascaded shadows stand
//    in for it for now. terMeadowW is still computed for when it is.
//  - REFRACTION_CLIP is 0 (the refraction pass does not exist yet).

// GrassField FADE_T0 (vegetation/GrassField.js): tier 0 blades drop out at random distances in this range.
// Kept as a literal so the terrain doesn't pull in the grass field's shaders.
#define TW_GRASS_FADE_0 62.0
#define TW_GRASS_FADE_1 87.0
#define TW_WET_DARKEN 0.58

// the swell direction (WORLD.swellDir) and the reef (WORLD.reef)
static const float2 TW_SWELL_DIR = float2( -0.11914599, -0.99288991 ); // normalize( -0.12, -1 )
static const float2 TW_REEF_CENTER = float2( -78.0, 58.0 );
#define TW_REEF_RADIUS 58.0

struct TWSurfaceOut
{
	float3 albedo;
	float roughness;
	float3 normal; // sim space
	float ao;
	float meadowW; // meadow weight (the JS terMeadowW)
};

float4 TWDetail( float2 uv )
{
	return SAMPLE_TEXTURE2D( _TWDetailTex, sampler_TWDetailTex, uv );
}

// Travelling gust field in [0, 1] (VegNodes gustAt): noise of (worldXZ - windDir * t * speed),
// where the time-integrated offset comes from _TWGust. Two fetches of the detail texture's
// fbm channel (~35 m and ~15 m gust cells).
float TWGustAt( float2 xz, float2 offset )
{
	float2 p = xz - offset;
	float n = SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, p / 140.0, 0.0 ).w * 0.62
		+ SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, p / 61.0 + 0.37, 0.0 ).w * 0.38;
	return smoothstep( 0.46, 0.6, n );
}

// ---- wetness (swash zone) from the shore system (ShoreSim): x = wetness, y = foam residue stranded on the sand. Outside the
// simulated region (or while there is no sim) a static damp band: the JS terrainWetness of App.js.
float2 TWWetFoam( float2 xz, float h )
{
	float4 s = ShoreSimSample( xz );
	float inside = ShoreSimInside( ShoreSimUvOf( xz ) );
	float band = smoothstep( 0.45, 0.0, h );
	// foam left on the sand: the lace the water carried, stranded and popping (ShoreSim.sandFoam)
	return float2( max( s.y, band * ( 1.0 - inside ) ), ShoreSimSandFoam( xz, s, h ) );
}

float4 TWSplat( float2 xz )
{
	float2 uv = ( xz - _TWTerrainParams.x ) / _TWTerrainParams.y;
	return SAMPLE_TEXTURE2D( _TWSplatTex, sampler_TWSplatTex, uv );
}

TWSurfaceOut TWTerrainSurface( float3 p, float3 camPos )
{
	float2 xz = p.xz;
	float h = p.y;
	float outRough = 0.9;
	float3 outN = float3( 0.0, 1.0, 0.0 );
	float outAO = 1.0;
	float3 albedoOut = 0.0;
	float terMeadowW = 0.0;

	// ---- data maps
	float4 nr = TWNormalRock( xz );
	float3 N0 = normalize( float3( nr.x, sqrt( max( 1.0 - nr.x * nr.x - nr.y * nr.y, 0.0025 ) ), nr.y ) );
	float4 sp = TWSplat( xz );
	float slope = 1.0 - N0.y;
	float camDist = length( camPos - p );
	float3 dpx = ddx( p ); float3 dpy = ddy( p );
	float fwY = fwidth( h );

	// ---- deep seabed (seen through the water: no rock, land cover, swash or wind ripples),
	// the same colour / relief / AO as the full path below, from 6 detail samples instead of ~17
	// detail samples shared by both paths (sampled in uniform control flow: no derivative seams
	// where the paths meet)
	float macroA = TWDetail( TWRot2( xz, 0.7 ) / 173.0 ).w;
	float macroB = TWDetail( TWRot2( xz, 2.1 ) / 47.0 ).w;
	float mcr = macroA * 0.6 + macroB * 0.4;
	float4 dN = TWDetail( xz / 1.9 );
	float4 dM = TWDetail( TWRot2( xz, 1.3 ) / 6.7 + 0.21 );
	float4 dF = TWDetail( TWRot2( xz, 2.4 ) / 0.63 + 0.53 );
	float grain = min( dN.z, 0.62 );
	float grainF = min( dF.z, 0.62 );
	bool seabedPath = h < -0.8 && nr.z < 0.05 && slope < 0.18;
	float2 swDir = TW_SWELL_DIR;
	float2 rc = TW_REEF_CENTER;
	// ripple phases of both paths (identical there), differentiated in uniform control flow
	float ph2 = dot( xz, swDir ) * ( 6.2832 / 0.75 ) + dM.w * 16.0 + macroB * 24.0;
	float fade2 = 1.0 - smoothstep( 0.5, 2.0, fwidth( ph2 ) );
	float rip2 = pow( sin( ph2 ) * 0.5 + 0.5, 1.4 );
	float ph3 = dot( xz, swDir ) * ( 6.2832 / 0.16 ) + dM.w * 26.0 + dN.w * 5.0;
	float fade3 = 1.0 - smoothstep( 0.6, 2.2, fwidth( ph3 ) );
	float rip3 = pow( sin( ph3 ) * 0.5 + 0.5, 1.5 );
	// bump height of either path: the surface-gradient bump runs after the branch (its screen-space
	// derivatives would be undefined in quads that straddle the two paths)
	float hdOut = 0.0;
	if ( seabedPath )
	{
		float underW = 1.0;
		float sandW = smoothstep( 0.3, 0.72, sp.x + ( dM.z - 0.5 ) * 0.5 + ( mcr - 0.5 ) * 0.35 );
		// ---- seabed: sand with ripple fields, seagrass meadows, rubble heads
		float depth = -h;
		float reefD = length( xz - rc );
		float reefW = 1.0 - smoothstep( TW_REEF_RADIUS * 0.5, TW_REEF_RADIUS * 1.15, reefD + ( mcr - 0.5 ) * 30.0 );
		float3 under = lerp( S( 0.84, 0.78, 0.64 ), S( 0.72, 0.7, 0.58 ), smoothstep( 1.0, 9.0, depth ) );
		under = under * ( ( dM.w - 0.5 ) * 0.14 + 1.0 ) * ( ( grain - 0.45 ) * 0.25 + 1.0 );
		// megaripple fields (~0.75 m) across the swell, troughs collect darker shell hash; not in
		// the swash zone or the first metre of depth
		float fieldW = smoothstep( 0.42, 0.62, macroB + ( dM.w - 0.5 ) * 0.35 ) * smoothstep( 0.9, 2.0, depth ) * ( 1.0 - reefW );
		under = under * ( ( rip2 - 0.55 ) * 0.22 * fieldW * fade2 + 1.0 );
		// small wave ripples (~0.16 m) everywhere below the swash
		// seagrass meadows: ragged edges, blade streaks leaning with the wave surge, epiphyte tips
		// (the fringe breaks up into clumps: noise at three scales thresholds the soft splat edge)
		float clumps = ( dM.w - 0.5 ) * 0.5 + ( dN.y - 0.45 ) * 0.4 + ( macroB - 0.5 ) * 0.3;
		float seagrassW = smoothstep( 0.3, 0.55, sp.z + clumps ) * underW * smoothstep( 0.3, 0.9, depth );
		float2 swPerp = float2( -swDir.y, swDir.x );
		float blades = TWDetail( float2( dot( xz, swDir ) / 2.6, dot( xz, swPerp ) / 0.35 ) ).y;
		float3 meadow = lerp( S( 0.12, 0.16, 0.07 ), S( 0.27, 0.29, 0.15 ), smoothstep( 0.35, 0.75, blades ) );
		meadow = lerp( meadow, S( 0.24, 0.2, 0.11 ), smoothstep( 0.55, 0.8, dM.y + ( macroB - 0.5 ) * 0.4 ) * 0.5 );
		// sparse at the fringe: sand shows between the blades; thinner, paler patches inside
		meadow = lerp( under, meadow, smoothstep( 0.3, 0.85, sp.z + clumps * 0.5 ) * 0.35 + 0.65 );
		meadow = lerp( meadow, lerp( meadow, under, 0.45 ), smoothstep( 0.58, 0.8, macroB + ( dM.w - 0.5 ) * 0.4 ) );
		under = lerp( under, meadow, seagrassW );
		// rubble heads: coral rubble and rock turfed with algae, pink coralline crusts
		float rubbleW = smoothstep( 0.3, 0.6, sp.w + ( dN.x - 0.5 ) * 0.4 + ( dM.w - 0.5 ) * 0.3 ) * underW;
		float3 rubble = lerp( S( 0.2, 0.19, 0.15 ), S( 0.36, 0.33, 0.26 ), smoothstep( 0.3, 0.7, dN.x ) );
		rubble = lerp( rubble, S( 0.2, 0.24, 0.1 ), smoothstep( 0.5, 0.7, dF.y ) * 0.6 );
		rubble = lerp( rubble, S( 0.58, 0.38, 0.44 ), smoothstep( 0.62, 0.74, dM.x ) * 0.6 );
		under = lerp( under, rubble, rubbleW );
		// reef flat: coral rubble and pink crusts toward the reef
		under = lerp( under, lerp( S( 0.56, 0.50, 0.44 ), S( 0.60, 0.43, 0.46 ), smoothstep( 0.45, 0.7, dM.x ) ), reefW * 0.7 * smoothstep( 0.4, 0.6, dN.x ) );

		// damp sand below the berm (wet = 0 under water)
		float dampMottle = smoothstep( 0.3, 0.7, dM.w + ( dN.y - 0.45 ) * 0.6 + ( macroB - 0.5 ) * 0.4 );
		float wetK = smoothstep( 1.7, 0.5, h + dM.w * 0.3 ) * sandW * lerp( 0.22, 0.5, dampMottle );
		float3 wetAlbedo = TWSaturation( under * TW_WET_DARKEN, 1.15 ) * float3( 0.97, 0.98, 1.0 );
		albedoOut = lerp( under, wetAlbedo, wetK );
		outRough = 0.75;
		float waveR = rip3 * 0.012 * fade3 * smoothstep( -0.3, -0.9, h ) * ( 1.0 - seagrassW );
		float megaR = rip2 * 0.035 * fade2 * fieldW * ( 1.0 - seagrassW );
		float sandH = grain * 0.004 + grainF * 0.003 + waveR + megaR;
		float seabedH = seagrassW * ( blades * 0.05 + 0.08 ) + rubbleW * ( dN.x * 0.07 + dF.x * 0.015 );
		hdOut = sandH + seabedH;
		outAO = nr.w * ( 1.0 - seagrassW * 0.3 ) * ( 1.0 - rubbleW * smoothstep( 0.55, 0.2, dN.x ) * 0.35 );
		terMeadowW = 0.0;
	}
	else
	{
		// ---- detail samples (tileable heights: x rock, y soil, z sand, w fbm)
		// sand grain without the pebble peaks (pebbles are drawn separately where they belong)

		// ---- downslope streaks (rock flutes, hanging vegetation, landslide scars): the detail
		// texture stretched vertically on the two vertical projection planes
		float2 sw4 = pow( abs( N0.xz ), float2( 4.0, 4.0 ) );
		float2 swN = sw4 / ( sw4.x + sw4.y + 1e-5 );
		// (everything they shape is weighted by the slope and vanishes at 0.28 or less: explicit
		// gradients in the branch, the same as the implicit ones of the full-screen quad)
		float streak = 0.5; float scar = 0.5;
		if ( slope > 0.28 )
		{
			float4 stA = TWDetailGrad( float2( p.z / 9.3, h / 37.0 ), float2( dpx.z / 9.3, dpx.y / 37.0 ), float2( dpy.z / 9.3, dpy.y / 37.0 ) );
			float4 stB = TWDetailGrad( float2( p.x / 9.3 + 0.5, h / 37.0 + 0.3 ), float2( dpx.x / 9.3, dpx.y / 37.0 ), float2( dpy.x / 9.3, dpy.y / 37.0 ) );
			streak = stA.w * swN.x + stB.w * swN.y;
			scar = stA.y * swN.x + stB.y * swN.y;
		}

		// ---- rock: only evaluated where the rock mask or the slope allow it. Exposure follows the
		// form: steep faces, convex spurs and ridges (high AO) go bare, gully floors (low AO,
		// drainage lines) keep soil and plants; noise and fall-line streaks break the outline up.
		// Around the bare rock a band of scree / dark soil and moss; plants creep over it.
		float gully = sp.z * smoothstep( -0.5, 0.5, h );
		float3 rockAlbedo = 0.2; float rockRough = 0.8; float rockHd = 0.0;
		float rockW = 0.0; float screeW = 0.0;
		float cliffK = smoothstep( 0.28, 0.55, slope );
		float convex = smoothstep( 0.5, 0.85, nr.w );
		// rv below, without the rock's own relief term, which adds at most 0.1925 (R.height <= 1): where
		// even that can't reach the scree band (0.28), rock and scree weigh 0 and the rock isn't shaded
		float rvBound = nr.z * 0.7 + smoothstep( 0.3, 0.62, slope ) * 0.5 + convex * 0.14 - gully * 0.4
			+ ( dM.w - 0.5 ) * 0.34 + ( dN.w - 0.5 ) * 0.22 + ( streak - 0.5 ) * 1.0 * cliffK + 0.2;
		if ( ( nr.z > 0.06 || slope > 0.3 ) && rvBound > 0.28 )
		{
			TWRockGrad g;
			g.dpdx = dpx; g.dpdy = dpy; g.fwY = fwY; g.useGrad = true;
			TWRockSurfaceOut R = TWRockSurface( p, N0, h, mcr, 0.5, 1.0, g );
			float rv = nr.z * 0.7 + smoothstep( 0.3, 0.62, slope ) * 0.5 + convex * 0.14 - gully * 0.4
				+ ( R.height - 0.45 ) * 0.35 + ( dM.w - 0.5 ) * 0.34 + ( dN.w - 0.5 ) * 0.22
				+ ( streak - 0.5 ) * 1.0 * cliffK;
			// fades to 0 at the branch boundary: no step along the slope / mask iso-lines
			float branchK = max( smoothstep( 0.06, 0.18, nr.z ), smoothstep( 0.3, 0.42, slope ) );
			rockW = smoothstep( 0.5, 0.68, rv ) * branchK;
			screeW = smoothstep( 0.28, 0.52, rv ) * branchK * ( 1.0 - rockW );
			// weathered basalt: darker and browner than the sea-cliff palette, streaked; moss and
			// ferns on the ledges and on the less steep parts of the faces
			// dark wet stains down the fall line, paler dry ribs between them
			float stain = smoothstep( 0.5, 0.7, streak ) * cliffK;
			// dark, weathered basalt (the island's inland rock is darker and browner than the pale
			// sea-cliff palette), stained down the fall line
			float3 basalt = R.albedo * float3( 0.36, 0.34, 0.31 ) * ( 1.0 - stain * 0.45 ) * ( smoothstep( 0.42, 0.25, streak ) * cliffK * 0.15 + 1.0 );
			// soil and humus caught in the joints and hollows of the rock (low relief), so the face
			// reads as fractured stone instead of a smooth plate
			float joints = smoothstep( 0.42, 0.22, R.height + ( dN.w - 0.5 ) * 0.25 );
			basalt = lerp( basalt, lerp( S( 0.13, 0.1, 0.07 ), S( 0.2, 0.2, 0.1 ), dF.y ), joints * 0.7 );
			// moss / small plants on every ledge and on the less steep parts, more near the edges
			float ledgeMoss = smoothstep( 0.4, 0.75, N0.y + ( dN.y - 0.45 ) * 0.6 ) * smoothstep( 0.25, 0.55, macroB + dM.y * 0.4 );
			float fringe = smoothstep( 0.5, 0.58, rv ) * smoothstep( 0.8, 0.6, rv ) * smoothstep( 0.3, 0.6, dM.w + dN.y * 0.4 );
			float mossK = saturate( max( ledgeMoss * 0.75, fringe * 0.8 ) + R.moss * 0.3 + joints * 0.25 );
			rockAlbedo = lerp( basalt, lerp( S( 0.12, 0.17, 0.05 ), S( 0.22, 0.26, 0.09 ), dF.y ), mossK );
			rockRough = R.rough;
			// craggier than the boulders: the big blocks and plates stand out from afar
			rockHd = R.hd * 2.2 + R.height * 0.6;
		}

		// ---- weights
		float notRock = 1.0 - rockW;
		float underW = smoothstep( 0.12, -0.6, h );
		float landW = 1.0 - underW;
		float sandW = smoothstep( 0.3, 0.72, sp.x + ( dM.z - 0.5 ) * 0.5 + ( mcr - 0.5 ) * 0.35 ) * notRock;
		float pathW = smoothstep( 0.28, 0.62, sp.y + ( dN.y - 0.45 ) * 0.4 + ( dM.w - 0.5 ) * 0.25 ) * notRock * landW
			* ( 1.0 - smoothstep( 50.0, 220.0, camDist ) * 0.85 );
		// forest on the higher / steeper ground and in the gullies, tall-grass meadow on the valley
		// floor and around the village (same classification as the vegetation's land cover)
		float jungleW = saturate( smoothstep( 9.0, 24.0, h + ( mcr - 0.5 ) * 18.0 ) + smoothstep( 0.18, 0.36, slope ) + gully * 0.6 );
		// landslide scars: raw red-brown laterite in streaks down steep slopes, rare
		float lateriteW = smoothstep( 0.62, 0.74, scar + ( macroB - 0.5 ) * 0.3 ) * smoothstep( 0.3, 0.42, slope )
			* smoothstep( 0.52, 0.66, mcr ) * notRock * 0.85;

		// ---- beach sand: pale coral sand, drifts of warmer / coarser sand, grain
		float dryK = smoothstep( 0.8, 3.0, h );
		float3 sand = lerp( S( 0.83, 0.75, 0.6 ), S( 0.9, 0.84, 0.72 ), smoothstep( 0.3, 0.72, mcr + dryK * 0.2 ) );
		sand = lerp( sand, S( 0.84, 0.72, 0.55 ), smoothstep( 0.55, 0.8, dM.w + ( macroB - 0.5 ) * 0.6 ) * 0.45 );
		sand = sand * ( ( dM.w - 0.5 ) * 0.16 + 1.0 ) * ( ( dN.w - 0.5 ) * 0.1 + 1.0 );
		sand = sand * ( ( grain - 0.45 ) * 0.3 + 0.97 ) * ( ( grainF - 0.45 ) * 0.2 + 1.0 );
		// disturbed / trodden patches: slightly darker, coarser sand (footfall, crabs, wind scour)
		float trod = smoothstep( 0.52, 0.7, dN.y + ( dM.y - 0.5 ) * 0.6 ) * dryK;
		sand = sand * ( 1.0 - trod * 0.07 );
		// the high-water band collects shell grit, coral bits and dried seaweed
		float hw = h + ( dM.w - 0.5 ) * 0.5;
		float wrackBand = smoothstep( 1.15, 1.4, hw ) * smoothstep( 2.1, 1.7, hw );
		float pebDensity = smoothstep( 0.62, 0.85, macroB + dM.w * 0.3 ) * 0.2 + wrackBand * smoothstep( 0.3, 0.6, dM.y );
		float pebW = smoothstep( 0.66, 0.8, dN.z ) * saturate( pebDensity ) * smoothstep( 0.6, 0.9, sp.x ) * landW
			* ( 1.0 - smoothstep( 12.0, 35.0, camDist ) );
		float3 pebCol = lerp( lerp( S( 0.86, 0.82, 0.74 ), S( 0.78, 0.64, 0.6 ), smoothstep( 0.45, 0.75, dM.x ) ), S( 0.36, 0.33, 0.3 ), smoothstep( 0.74, 0.82, dM.y ) );
		sand = lerp( sand, pebCol, pebW * 0.75 );
		float wrack = wrackBand * smoothstep( 0.58, 0.72, dN.y ) * smoothstep( 0.4, 0.6, macroB );
		sand = lerp( sand, S( 0.24, 0.18, 0.11 ), wrack * 0.8 );
		// trampled sand along the paths
		sand = sand * ( 1.0 - pathW * 0.07 );

		// wind ripples on the dry sand: crests across the wind (bent by the fbm), wavelength
		// ~10.5 cm, fading where trodden; visible in the albedo too (finer sand on the crests).
		// (A wavelength varying in space must not divide the absolute coordinate: the phase then
		// swings by x * dλ / λ², which at 100 m from the origin turned the ripples into patches
		// of random direction and spacing with seams and moiré, and made fwidth switch them off
		// in blotches. The spacing varies through the smooth warp instead.)
		float2 wd = _TWWind.xy;
		float ph1 = dot( xz, wd ) * ( 6.2832 / 0.105 ) + dM.w * 24.0 + dN.w * 7.0 + macroB * 30.0;
		float fade1 = 1.0 - smoothstep( 0.6, 2.2, fwidth( ph1 ) );
		float rip1 = pow( sin( ph1 ) * 0.5 + 0.5, 1.6 );
		float windK = fade1 * smoothstep( 1.5, 2.2, h ) * ( 1.0 - pathW ) * ( 1.0 - trod * 0.7 )
			* smoothstep( 0.2, 0.5, macroB + dM.w * 0.3 );
		sand = sand * ( ( rip1 - 0.5 ) * 0.12 * windK + 1.0 );

		// ---- seabed: sand with ripple fields, seagrass meadows, rubble heads (only below the berm:
		// everything here is weighted by underW, 0 on land)
		float depth = -h;
		float fieldW = 0.0; float seagrassW = 0.0; float blades = 0.0; float rubbleW = 0.0;
		float3 under = sand;
		if ( underW > 0.0 )
		{
			float reefD = length( xz - rc );
			float reefW = 1.0 - smoothstep( TW_REEF_RADIUS * 0.5, TW_REEF_RADIUS * 1.15, reefD + ( mcr - 0.5 ) * 30.0 );
			under = lerp( S( 0.84, 0.78, 0.64 ), S( 0.72, 0.7, 0.58 ), smoothstep( 1.0, 9.0, depth ) );
			under = under * ( ( dM.w - 0.5 ) * 0.14 + 1.0 ) * ( ( grain - 0.45 ) * 0.25 + 1.0 );
			// megaripple fields (~0.75 m) across the swell, troughs collect darker shell hash; not in
			// the swash zone or the first metre of depth
			fieldW = smoothstep( 0.42, 0.62, macroB + ( dM.w - 0.5 ) * 0.35 ) * smoothstep( 0.9, 2.0, depth ) * ( 1.0 - reefW );
			under = under * ( ( rip2 - 0.55 ) * 0.22 * fieldW * fade2 + 1.0 );
			// small wave ripples (~0.16 m) everywhere below the swash
			// seagrass meadows: ragged edges, blade streaks leaning with the wave surge, epiphyte tips
			// (the fringe breaks up into clumps: noise at three scales thresholds the soft splat edge)
			float clumps = ( dM.w - 0.5 ) * 0.5 + ( dN.y - 0.45 ) * 0.4 + ( macroB - 0.5 ) * 0.3;
			seagrassW = smoothstep( 0.3, 0.55, sp.z + clumps ) * underW * smoothstep( 0.3, 0.9, depth );
			float2 swPerp = float2( -swDir.y, swDir.x );
			blades = TWDetail( float2( dot( xz, swDir ) / 2.6, dot( xz, swPerp ) / 0.35 ) ).y;
			float3 meadow = lerp( S( 0.12, 0.16, 0.07 ), S( 0.27, 0.29, 0.15 ), smoothstep( 0.35, 0.75, blades ) );
			meadow = lerp( meadow, S( 0.24, 0.2, 0.11 ), smoothstep( 0.55, 0.8, dM.y + ( macroB - 0.5 ) * 0.4 ) * 0.5 );
			// sparse at the fringe: sand shows between the blades; thinner, paler patches inside
			meadow = lerp( under, meadow, smoothstep( 0.3, 0.85, sp.z + clumps * 0.5 ) * 0.35 + 0.65 );
			meadow = lerp( meadow, lerp( meadow, under, 0.45 ), smoothstep( 0.58, 0.8, macroB + ( dM.w - 0.5 ) * 0.4 ) );
			under = lerp( under, meadow, seagrassW );
			// rubble heads: coral rubble and rock turfed with algae, pink coralline crusts
			rubbleW = smoothstep( 0.3, 0.6, sp.w + ( dN.x - 0.5 ) * 0.4 + ( dM.w - 0.5 ) * 0.3 ) * underW;
			float3 rubble = lerp( S( 0.2, 0.19, 0.15 ), S( 0.36, 0.33, 0.26 ), smoothstep( 0.3, 0.7, dN.x ) );
			rubble = lerp( rubble, S( 0.2, 0.24, 0.1 ), smoothstep( 0.5, 0.7, dF.y ) * 0.6 );
			rubble = lerp( rubble, S( 0.58, 0.38, 0.44 ), smoothstep( 0.62, 0.74, dM.x ) * 0.6 );
			under = lerp( under, rubble, rubbleW );
			// reef flat: coral rubble and pink crusts toward the reef
			under = lerp( under, lerp( S( 0.56, 0.50, 0.44 ), S( 0.60, 0.43, 0.46 ), smoothstep( 0.45, 0.7, dM.x ) ), reefW * 0.7 * smoothstep( 0.4, 0.6, dN.x ) );
		}
		sand = lerp( sand, under, underW );

		// ---- ground: tall-grass meadow (tone shared with the grass field), forest floor and, from
		// afar, the forest canopy; laterite scars
		float3 V = normalize( camPos - p );
		float NdV = saturate( dot( N0, V ) );
		TWMeadowToneOut mt = TWMeadowTone( macroA, macroB, slope, N0.z, dM.w * 0.65 + dN.w * 0.35, true );
		// clumps (1-3 m) and tussocks, blade-scale grain
		float clump = dM.w * 0.6 + dN.y * 0.4;
		// seen from afar the tussocks and their shadowed gaps are what makes tall grass read as
		// grass (not lawn): the clump contrast grows with distance as the blades fade out
		float clumpK = lerp( 0.34, 0.95, smoothstep( 40.0, 140.0, camDist ) );
		float3 lawn = mt.tone * ( ( clump - 0.5 ) * clumpK + 1.0 ) * ( ( dF.y - 0.4 ) * 0.22 + 1.0 );
		lawn = lawn * lerp( 1.0, smoothstep( 0.25, 0.55, dN.y * 0.5 + dM.y * 0.5 ) * 0.35 + 0.72, smoothstep( 50.0, 160.0, camDist ) );
		// grass combed along the wind: long streaks (anisotropic sample of the fbm channel)
		// (comb and gust only where the lawn shows: not under full forest, sand or water)
		bool lawnShows = jungleW < 1.0 && landW > 0.0 && sandW < 1.0;
		float comb = 0.5;
		if ( lawnShows )
		{
			float2 combUV = float2( dot( xz, wd ) / 7.5, dot( xz, float2( -wd.y, wd.x ) ) / 0.9 );
			comb = TWDetail( combUV + float2( 0.31, 0.77 ) ).w;
			lawn = lawn * ( ( comb - 0.5 ) * 0.3 + 1.0 );
		}
		// seen from above the dark soil shows between the clumps; at grazing angles blade sides
		// cover everything (lighter, more saturated)
		float gapK = smoothstep( 0.3, 0.95, NdV ) * smoothstep( 0.62, 0.3, clump );
		lawn = lerp( lawn, MEADOW_soil, gapK * 0.45 );
		lawn = lerp( lawn, TWSaturation( lawn * 1.12, 1.15 ), smoothstep( 0.45, 0.1, NdV ) * 0.6 );
		// travelling gusts flatten the grass: the paler blade backs show as waves (same gust
		// field as the grass blades)
		float windStrength = max( _TWWind.z * 0.1, 0.03 );
		if ( lawnShows )
		{
			float gust = TWGustAt( xz, _TWGust.xy ) * saturate( windStrength * 0.5 );
			lawn = lerp( lawn, lawn * float3( 1.25, 1.22, 1.06 ) + 0.01, gust * 0.6 );
		}
		// bare trodden soil in places, sandy soil toward the beach
		lawn = lerp( lawn, S( 0.4, 0.33, 0.23 ), smoothstep( 0.72, 0.84, dN.y + ( dM.y - 0.5 ) * 0.5 ) * 0.25 );
		lawn = lerp( lawn, S( 0.60, 0.52, 0.38 ), saturate( sp.x * 1.6 ) * smoothstep( 0.45, 0.62, dN.z + dM.y * 0.3 ) * 0.7 );
		// inside the geometric grass field (GrassField) the ground is only seen between the blades:
		// the shaded base of the sward, dark and brownish with dead leaves; it hands over to the
		// sward's own look (above) where the blades thin out
		float grassHere = smoothstep( 2.5, 4.5, h ) * ( 1.0 - smoothstep( 0.45, 0.85, jungleW ) ) * ( 1.0 - saturate( sp.x * 1.6 ) );
		float fieldK = ( 1.0 - smoothstep( TW_GRASS_FADE_0, TW_GRASS_FADE_1, length( p.xz - camPos.xz ) ) ) * grassHere;
		float3 swardBase = lerp( mt.tone * 0.4, MEADOW_soil, 0.4 ) * ( ( dN.y - 0.45 ) * 0.6 + 1.0 ) * ( ( dF.y - 0.4 ) * 0.3 + 1.0 );
		lawn = lerp( lawn, swardBase, fieldK * 0.85 );
		float3 litter = lerp( S( 0.2, 0.15, 0.09 ), S( 0.34, 0.25, 0.13 ), dF.y );
		float3 jungle = lerp( S( 0.1, 0.16, 0.05 ), litter, smoothstep( 0.52, 0.7, dN.y ) );
		jungle = lerp( jungle, S( 0.12, 0.1, 0.06 ), gully * 0.3 );
		float farK = smoothstep( 40.0, 160.0, camDist );
		float3 cover = lerp( S( 0.08, 0.13, 0.04 ), S( 0.17, 0.24, 0.07 ), smoothstep( 0.3, 0.7, mcr ) );
		cover = lerp( cover, S( 0.27, 0.29, 0.12 ), smoothstep( 0.66, 0.84, macroB + dM.w * 0.2 ) * 0.45 );
		jungle = lerp( jungle, cover, farK * 0.8 );
		jungle = jungle * ( ( mcr - 0.5 ) * 0.3 + 1.0 );
		// canopy: seen from a distance (or on slopes too steep for the trees) the forest reads as
		// a carpet of lumpy crowns with dark gaps
		float canopyW = jungleW * max( smoothstep( 0.2, 0.4, slope ), smoothstep( 90.0, 260.0, camDist ) ) * smoothstep( 25.0, 70.0, camDist ) * notRock * ( 1.0 - screeW * 0.7 );
		float canopyH = 0.0;
		if ( canopyW > 0.0 )
		{
			float crowns = TWDetail( TWRot2( xz, 0.9 ) / 61.0 ).w;
			float crownsB = TWDetail( TWRot2( xz, 2.3 ) / 13.0 + 0.37 ).w;
			canopyH = smoothstep( 0.32, 0.7, crowns * 0.45 + crownsB * 0.4 + dM.w * 0.15 );
			float3 canopy = lerp( S( 0.05, 0.08, 0.025 ), lerp( S( 0.14, 0.21, 0.06 ), S( 0.22, 0.27, 0.09 ), macroB ), canopyH );
			// steep faces: the canopy hangs in streaks down the fall line
			canopy = canopy * ( ( streak - 0.5 ) * 0.5 * smoothstep( 0.3, 0.5, slope ) + 1.0 );
			jungle = lerp( jungle, canopy, canopyW );
		}
		float3 ground = lerp( lawn, jungle, jungleW );
		// around the bare rock: dark humus, stones and moss, with the surrounding plants creeping
		// in (a soft, noisy band; no speckle)
		float creepIn = smoothstep( 0.4, 0.75, dM.w + ( dN.y - 0.45 ) * 0.5 + ( macroB - 0.5 ) * 0.3 );
		float3 scree = lerp( S( 0.16, 0.13, 0.1 ), S( 0.27, 0.24, 0.2 ), smoothstep( 0.45, 0.75, dM.x + ( dN.x - 0.5 ) * 0.3 ) );
		scree = lerp( scree, lerp( S( 0.13, 0.18, 0.06 ), S( 0.22, 0.26, 0.09 ), dF.y ), smoothstep( 0.45, 0.7, dM.y + ( dN.y - 0.45 ) * 0.5 ) * 0.7 );
		ground = lerp( ground, scree, screeW * ( 1.0 - creepIn * 0.7 ) );
		float3 laterite = lerp( S( 0.42, 0.25, 0.16 ), S( 0.52, 0.36, 0.24 ), dM.w ) * ( ( dN.y - 0.4 ) * 0.3 + 1.0 );
		ground = lerp( ground, laterite, lateriteW );

		// ---- worn dirt paths / trampled ground (darker, redder soil in the forest)
		float3 dirt = lerp( S( 0.38, 0.31, 0.23 ), S( 0.5, 0.43, 0.32 ), dM.w ) * ( ( dN.y - 0.4 ) * 0.35 + 0.95 );
		dirt = lerp( dirt, S( 0.3, 0.22, 0.15 ), jungleW * 0.7 );
		dirt = lerp( dirt, S( 0.56, 0.53, 0.48 ), smoothstep( 0.72, 0.82, dN.z ) * 0.5 );
		// grass creeping onto the trail, a grassy strip between the two worn ruts
		float creep = smoothstep( 0.45, 0.7, dN.y + ( dF.y - 0.45 ) * 0.5 ) * smoothstep( 0.9, 0.5, sp.y );
		dirt = lerp( dirt, lawn, creep * 0.8 );

		// ---- combine
		float meadowW = ( 1.0 - jungleW ) * ( 1.0 - pathW ) * notRock * ( 1.0 - sandW ) * landW * ( 1.0 - screeW );
		terMeadowW = meadowW;
		float3 albedo = lerp( ground, dirt, pathW );
		albedo = lerp( albedo, sand, max( sandW, underW * notRock ) );
		albedo = lerp( albedo, rockAlbedo, rockW );

		// ---- eroded beach scarp (splat alpha on land): storm-cut face of the foredune. Layered
		// sandy soil (laminae of paler and darker sand, darker humus-stained patches), live roots
		// and pale dead-root tangles hanging out of the face, damp darker sand at the toe.
		float scarpW = saturate( sp.w * 1.6 ) * landW * notRock;
		float scarpH = 0.0;
		if ( scarpW > 0.003 )
		{
			// laminae: thin storm layers, warped and wedging out, with a set of cross-beds at a low angle
			float2 tanF = normalize( float2( -N0.z, N0.x ) + float2( 1e-4, 0.0 ) );
			float alongF = dot( xz, tanF );
			float hwS = h + ( dM.w - 0.5 ) * 0.5 + ( dN.w - 0.5 ) * 0.12 + ( macroB - 0.5 ) * 0.35;
			float lam = sin( hwS * ( 6.2832 / 0.11 ) + dN.x * 2.0 ) * 0.5 + 0.5;
			float crossBed = sin( ( hwS + alongF * 0.09 ) * ( 6.2832 / 0.075 ) ) * 0.5 + 0.5;
			float lamB = smoothstep( 0.3, 0.7, sin( ( hwS + dM.y * 0.4 ) * ( 6.2832 / 0.43 ) ) * 0.5 + 0.5 );
			float layerMix = lerp( lam, crossBed, smoothstep( 0.4, 0.6, dM.x ) );
			float3 soil = lerp( S( 0.5, 0.42, 0.31 ), S( 0.68, 0.6, 0.46 ), smoothstep( 0.3, 0.8, layerMix ) * 0.45 + lamB * 0.2 + ( grain - 0.45 ) * 0.5 + ( dM.w - 0.5 ) * 0.3 );
			// humus-stained, rootier soil in patches (the old dune surface caught in the cut)
			soil = lerp( soil, S( 0.26, 0.2, 0.14 ), smoothstep( 0.55, 0.8, dM.y + ( macroB - 0.5 ) * 0.5 ) * 0.55 );
			// roots: horizontal coordinate along the face, stretched down the fall line
			float4 ru = TWDetail( float2( dot( xz, tanF ) / 0.8, h / 0.5 ) + float2( 0.37, 0.11 ) );
			float4 rvv = TWDetail( float2( dot( xz, tanF ) / 1.7 + h * 0.6, h / 0.9 ) + float2( 0.71, 0.29 ) );
			float rootLive = smoothstep( 0.035, 0.0, abs( ru.x - 0.5 ) ) * smoothstep( 0.45, 0.62, ru.w );
			float rootDead = smoothstep( 0.03, 0.0, abs( rvv.z - 0.52 ) ) * smoothstep( 0.5, 0.66, rvv.y );
			soil = lerp( soil, S( 0.17, 0.12, 0.08 ), rootLive * 0.85 );
			soil = lerp( soil, S( 0.8, 0.75, 0.66 ), rootDead * 0.75 );
			// damp, darker sand toward the toe and in seepage streaks
			float seep = smoothstep( 0.6, 0.8, TWDetail( float2( dot( xz, tanF ) / 2.3, h / 6.0 ) ).w ) * 0.35;
			soil = soil * ( 1.0 - seep - smoothstep( 0.5, 1.0, sp.w ) * 0.08 );
			// under the lip: the dark, rooty topsoil the grass grows in, overhanging the cut
			float lip = smoothstep( 0.86, 0.97, sp.w + ( dN.y - 0.45 ) * 0.1 );
			soil = lerp( soil, S( 0.2, 0.15, 0.1 ) * ( dN.x * 0.4 + 0.8 ), lip * 0.85 );
			// foot of the face: undercut by the swash of storm waves, in its own shadow
			soil *= 1.0 - ( 1.0 - smoothstep( 0.6, 0.78, sp.w ) ) * smoothstep( 0.35, 0.55, sp.w ) * 0.25;
			// slumped sand and fallen chunks at the toe: damp beach sand rather than soil
			soil = lerp( S( 0.63, 0.56, 0.44 ) * ( grain * 0.3 + 0.85 ), soil, smoothstep( 0.35, 0.75, sp.w ) );
			albedo = lerp( albedo, soil, scarpW );
			scarpH = ( lip * 0.03 + layerMix * 0.012 + lamB * 0.018 + rootLive * 0.012 + rootDead * 0.009 + dN.x * 0.025 );
		}

		// ---- wetness (swash zone) from the shore system, else a static damp band
		float2 wetFoam = TWWetFoam( xz, h );
		float wet = saturate( wetFoam.x ) * ( 1.0 - jungleW * 0.8 ) * landW;
		// damp sand below the berm: darker in mottled, drying patches even when the swash has not
		// reached it lately
		float dampMottle = smoothstep( 0.3, 0.7, dM.w + ( dN.y - 0.45 ) * 0.6 + ( macroB - 0.5 ) * 0.4 );
		float damp = smoothstep( 1.7, 0.5, h + dM.w * 0.3 ) * sandW * lerp( 0.22, 0.5, dampMottle );
		// sand dries in mottled patches; backwash leaves faint rills down the slope
		float mottle = smoothstep( 0.25, 0.75, dM.w + ( dN.y - 0.45 ) * 0.5 );
		float dryEdge = smoothstep( 0.0, 0.6, wet ) * smoothstep( 1.0, 0.6, wet );
		float wetK = max( wet, damp ) * ( 1.0 - dryEdge * mottle * 0.6 ) * notRock;
		float2 slopeDir = normalize( N0.xz + float2( 1e-4, 0.0 ) );
		float rillK = smoothstep( 0.2, 0.9, wet ) * smoothstep( 0.05, 0.6, h ) * sandW;
		float rill = 0.5;
		if ( rillK > 0.0 ) { rill = TWDetail( float2( dot( xz, slopeDir ) / 3.2, dot( xz, float2( -slopeDir.y, slopeDir.x ) ) / 0.3 ) ).w; }
		float3 wetAlbedo = TWSaturation( albedo * TW_WET_DARKEN, 1.15 ) * float3( 0.97, 0.98, 1.0 ) * ( ( rill - 0.5 ) * 0.25 * rillK + 1.0 );
		albedo = lerp( albedo, wetAlbedo, wetK );
		// swash marks: thin wavy lines of grit left at the limits of earlier uprushes
		float sl = ( h + dM.w * 0.18 + dN.w * 0.05 ) / 0.13;
		float slD = abs( frac( sl ) - 0.5 );
		float slW = fwidth( sl ) + 1e-4;
		float swashLine = smoothstep( slW * 1.5 + 0.04, 0.0, slD ) * smoothstep( 0.2, 0.4, h ) * smoothstep( 1.6, 1.2, h )
			* smoothstep( 0.45, 0.65, dM.y + ( macroB - 0.5 ) * 0.4 ) * sandW * ( 1.0 - smoothstep( 0.8, 2.0, slW * 10.0 ) );
		albedo = lerp( albedo * ( 1.0 - swashLine * 0.3 ), S( 0.9, 0.88, 0.84 ), swashLine * smoothstep( 0.6, 0.75, dF.z ) * 0.5 );
		// foam residue: lacy patterns stranded on the sand
		float residue = saturate( wetFoam.y ) * landW * notRock;
		if ( residue > 0.0 ) { residue *= smoothstep( 0.42, 0.18, TWDetail( TWRot2( xz, 0.4 ) / 0.9 ).x ) * 0.7 + 0.3; }
		albedo = lerp( albedo, S( 0.88, 0.9, 0.9 ), residue );

		// ---- roughness
		float rough = lerp( 0.88, 0.93, sandW );
		rough = lerp( rough, 0.9, pathW );
		rough = lerp( rough, rockRough, rockW );
		// wet sand has a film of water: glossy while fresh, satin as it drains
		rough = lerp( rough, lerp( 0.42, 0.16, wet ), wetK );
		rough = lerp( rough, 0.7, residue );
		rough = lerp( rough, 0.75, underW );
		rough = lerp( rough, 0.94, scarpW );
		outRough = rough;

		// ---- micro relief for the normal
		float windR = rip1 * 0.005 * windK;
		float waveR = rip3 * 0.012 * fade3 * smoothstep( -0.3, -0.9, h ) * ( 1.0 - seagrassW );
		float megaR = rip2 * 0.035 * fade2 * fieldW * ( 1.0 - seagrassW );
		float sandH = ( grain * 0.004 + grainF * 0.003 + pebW * 0.004 + windR + waveR + megaR ) * ( 1.0 - wet * 0.6 )
			+ rill * 0.006 * rillK;
		// seagrass canopy stands proud of the sand with a ragged scarp; rubble is knobbly
		float seabedH = seagrassW * ( blades * 0.05 + 0.08 ) + rubbleW * ( dN.x * 0.07 + dF.x * 0.015 );
		float groundH = dN.y * lerp( 0.05, 0.035, jungleW ) + dF.y * 0.012 + dM.y * 0.045 + canopyH * canopyW * 2.5
			+ clump * 0.12 * ( 1.0 - jungleW ) + comb * 0.03 * ( 1.0 - jungleW );
		float screeH = dN.z * 0.04 + dM.x * 0.06;
		float dirtH = dN.z * 0.012 + dN.y * 0.01;
		float hd = lerp( lerp( groundH, screeH, screeW ), dirtH, pathW );
		hd = lerp( hd, sandH + seabedH, max( sandW, underW * notRock ) );
		hd = lerp( hd, rockHd, rockW );
		hd = lerp( hd, scarpH, scarpW );
		hdOut = hd;

		// ---- ambient occlusion: baked horizon + cavity, plus litter / crevices / seagrass canopy
		float aoDetail = lerp( 1.0, dN.y * 0.5 + 0.7, jungleW * ( 1.0 - sandW ) * notRock * landW )
			* lerp( 1.0, smoothstep( 0.2, 0.7, clump ) * 0.35 + 0.65, meadowW );
		outAO = nr.w * aoDetail * ( 1.0 - seagrassW * 0.3 ) * ( 1.0 - rubbleW * smoothstep( 0.55, 0.2, dN.x ) * 0.35 )
			* lerp( 1.0, smoothstep( 0.15, 0.6, canopyH ) * 0.6 + 0.4, canopyW );

		albedoOut = albedo;
	}

	outN = TWPerturbNormal( p, N0, hdOut, 1.0 );

	TWSurfaceOut o;
	o.albedo = albedoOut;
	o.roughness = outRough;
	o.normal = outN;
	o.ao = saturate( outAO );
	o.meadowW = terMeadowW;
	return o;
}
