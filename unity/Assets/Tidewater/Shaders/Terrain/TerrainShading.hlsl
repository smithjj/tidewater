// Port of src/world/terrain/TerrainShading.js: shared shading building blocks for the terrain and the
// scattered rocks (the WGSL `terrainShadingModule`). All positions / normals here are in SIM space
// (x east, y up, z south; see Util/Sim.cs): the terrain surface mirrors the Unity world position before calling
// in, and mirrors the resulting normal back.
//
//   TWsrgb( c )                    linear value of an sRGB triplet (JS: srgb( r, g, b ), here the S( r, g, b ) macro)
//   TWRot2( v, a )                 rotates a float2 by an angle (JS: rot2)
//   TWPerturbNormal( P, N, hd, s ) surface-gradient bump
//   TWTriWeights( N ), TWTriplanar( p, w, tile, g )
//   TWRockSurface( p, N, h, mcr, seed, mossAmount, g )
//   TWSaturation( c, s ), TWMeadowTone( ... )
//   PAL_<name> (rock palette), MEADOW_<name> (meadow palette)
// The detail texture is the global _TWDetailTex (repeat, trilinear, anisotropy 4: JS smpAniso4Repeat).
// Input samplers are declared by the includer (TerrainFragment.hlsl).

float3 TWsrgb( float3 c )
{
	float3 lo = c / 12.92;
	float3 hi = pow( ( c + 0.055 ) / 1.055, 2.4 );
	return lerp( hi, lo, ( float3 ) ( c <= 0.04045 ) );
}

#define S( r, g, b ) TWsrgb( float3( r, g, b ) )

float2 TWRot2( float2 v, float a )
{
	float c = cos( a ), s = sin( a );
	return float2( c * v.x - s * v.y, s * v.x + c * v.y );
}

float TWLuminance( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

// ---- palette (sRGB picked from photo references, stored linear)
static const float3 PAL_rockDark = S( 0.15, 0.145, 0.135 );
static const float3 PAL_rockMid = S( 0.3, 0.285, 0.265 );
static const float3 PAL_rockLight = S( 0.48, 0.455, 0.42 );
static const float3 PAL_rockWarm = S( 0.44, 0.37, 0.30 );
static const float3 PAL_lichenPale = S( 0.70, 0.70, 0.64 );
static const float3 PAL_lichenOrange = S( 0.78, 0.50, 0.20 );
static const float3 PAL_blackZone = S( 0.075, 0.075, 0.07 );
static const float3 PAL_barnacle = S( 0.78, 0.76, 0.70 );
static const float3 PAL_algae = S( 0.20, 0.27, 0.10 );
static const float3 PAL_coralline = S( 0.62, 0.44, 0.46 );
static const float3 PAL_moss = S( 0.19, 0.29, 0.08 );
static const float3 PAL_mossDry = S( 0.3, 0.34, 0.14 );

// ---- tropical meadow (tall guinea / elephant grass)
// The tone is shared by the terrain and the grass field (blade base colour): both evaluate it from
// the same inputs, so the geometric grass fades into the ground without a visible boundary.
static const float3 MEADOW_lush = S( 0.13, 0.2, 0.05 );
static const float3 MEADOW_green = S( 0.25, 0.32, 0.1 );
static const float3 MEADOW_olive = S( 0.36, 0.37, 0.14 );
static const float3 MEADOW_yellow = S( 0.5, 0.46, 0.2 );
static const float3 MEADOW_straw = S( 0.62, 0.54, 0.33 );
static const float3 MEADOW_soil = S( 0.17, 0.13, 0.08 );

struct TWRockGrad
{
	float3 dpdx;
	float3 dpdy;
	float fwY;
	bool useGrad;
};

// implicit-derivative RockGrad for a world position (call in uniform control flow)
TWRockGrad TWImplicitGrad( float3 p )
{
	TWRockGrad g;
	g.dpdx = ddx( p ); g.dpdy = ddy( p ); g.fwY = 0.0; g.useGrad = false;
	return g;
}

// Mikkelsen surface-gradient bump: perturb world normal N by the scalar height field hd
// (screen-space derivatives, so any mix of projections / scales works). Robustness for terrain:
//  - the tilt is limited to ~55 degrees (at grazing angles |det| collapses and the unclamped
//    gradient would swing the normal into the tangent plane: 'chrome' patches on steep faces)
//  - the bump fades out where the screen-space frame is degenerate (the thin sliver triangles
//    of CDLOD geomorphing, which otherwise light up as bright lines along the grid) or where the
//    rendered facet disagrees with N (sub-texel crags)
float3 TWPerturbNormal( float3 p, float3 N, float hd, float scale )
{
	float3 dpx = ddx( p ); float3 dpy = ddy( p );
	float dhdx = ddx( hd ) * scale; float dhdy = ddy( hd ) * scale;
	float3 r1 = cross( dpy, N );
	float3 r2 = cross( N, dpx );
	float det = dot( dpx, r1 );
	float ad = abs( det );
	float3 grad = ( r1 * dhdx + r2 * dhdy ) * sign( det );
	float area = length( cross( dpx, dpy ) );
	float fr = area / max( length( dpx ) * length( dpy ), 1e-20 ); // sin of the footprint angle
	float facet = ad / max( area, 1e-20 ); // cos between the facet and N
	float k = smoothstep( 0.12, 0.35, fr ) * smoothstep( 0.3, 0.6, facet );
	float3 g = grad * min( 1.0, ad * 1.4 / max( length( grad ), 1e-20 ) ) * k;
	return normalize( N * max( ad, 1e-20 ) - g );
}

// triplanar blend weights (sharp)
float3 TWTriWeights( float3 N )
{
	float3 a = abs( N );
	float3 w = a * a * ( a * a );
	return w / ( w.x + w.y + w.z );
}

float4 TWDetailGrad( float2 uv, float2 gx, float2 gy )
{
	return SAMPLE_TEXTURE2D_GRAD( _TWDetailTex, sampler_TWDetailTex, uv, gx, gy );
}

// one channel-set of the detail texture, triplanar. The samples use explicit gradients (the
// derivatives of the world position in g), so they may run in non-uniform control flow.
float4 TWTriplanar( float3 p, float3 w, float tile, TWRockGrad g )
{
	float s = 1.0 / tile;
	float4 x = TWDetailGrad( p.zy * s, g.dpdx.zy * s, g.dpdy.zy * s );
	float4 y = TWDetailGrad( p.xz * s + 0.37, g.dpdx.xz * s, g.dpdy.xz * s );
	float4 z = TWDetailGrad( p.xy * s + 0.71, g.dpdx.xy * s, g.dpdy.xy * s );
	return x * w.x + y * w.y + z * w.z;
}

struct TWRockSurfaceOut
{
	float3 albedo;
	float rough;
	float hd;     // bump height (m)
	float moss;   // 0..1
	float wet;
	float height;
};

// Weathered volcanic rock seen on the headlands, sea stacks and boulders.
//   p world position, N world normal (geometric / mcr), h height above sea level
//   mcr: 0..1 large scale variation, seed: per object variation (0..1)
// With g.useGrad every sample uses the gradients in g (branch safe).
TWRockSurfaceOut TWRockSurface( float3 p, float3 N, float h, float mcr, float seed, float mossAmount, TWRockGrad g )
{
	float3 w = TWTriWeights( N );
	// big blocks (4 m cells), plates (0.9 m) and grain / chips
	float4 big = TWTriplanar( p, w, 27.0, g );
	float4 mid = TWTriplanar( p, w, 6.1, g );
	float4 fine = TWTriplanar( p, w, 1.3, g );
	// pixel footprint (m): features smaller than a few pixels fade out instead of sparkling
	float px = g.useGrad ? max( length( g.dpdx ), length( g.dpdy ) ) : length( abs( g.dpdx ) + abs( g.dpdy ) );
	float fineK = 1.0 - smoothstep( 0.006, 0.02, px );
	float midK = 1.0 - smoothstep( 0.03, 0.1, px );
	float hr = big.x * 0.45 + mid.x * 0.35 + ( ( fine.x - 0.5 ) * fineK + 0.5 ) * 0.2;

	// layered lava flows / bedding on steep faces: irregular bands (1D lookup of the fbm channel
	// along the height, warped), faded out once a band gets thinner than a few pixels
	float steep = 1.0 - smoothstep( 0.55, 0.85, N.y );
	float bandY = p.y + mid.w * 2.5 + mcr * 6.0;
	float fwY = g.fwY;
	if ( ! g.useGrad ) { fwY = fwidth( bandY ); }
	float2 bandUV = float2( bandY / 14.0, seed * 0.37 + 0.13 );
	float strata;
	if ( g.useGrad )
	{
		strata = TWDetailGrad( bandUV, float2( fwY / 14.0, 0.0 ), 0.0 ).w;
	}
	else
	{
		strata = SAMPLE_TEXTURE2D( _TWDetailTex, sampler_TWDetailTex, bandUV ).w;
	}
	float strataAA = 1.0 - smoothstep( 0.15, 0.6, fwY );
	float tone = hr * 0.9 + ( mcr - 0.5 ) * 0.7 + ( strata - 0.5 ) * 0.8 * steep * strataAA + ( seed - 0.5 ) * 0.3;
	float3 col = lerp( PAL_rockDark, PAL_rockMid, smoothstep( 0.1, 0.5, tone ) );
	col = lerp( col, PAL_rockLight, smoothstep( 0.5, 0.85, tone ) );
	// iron staining / warm weathering in patches
	col = lerp( col, PAL_rockWarm, smoothstep( 0.62, 0.8, mid.w + mcr * 0.3 ) * 0.18 );
	// joints between the big blocks, fainter between plates
	col = col * ( smoothstep( 0.05, 0.25, big.x ) * 0.35 + 0.65 ) * ( smoothstep( 0.05, 0.25, mid.x ) * 0.15 + 0.85 );
	// rain streaks: dark stains running down steep faces, paler bands between (the fbm channel
	// stretched vertically on the two vertical projection planes)
	float2 sUVa = float2( p.z / 3.1, p.y / 41.0 );
	float2 sUVb = float2( p.x / 3.1 + 0.5, p.y / 41.0 + 0.3 );
	float4 sa = TWDetailGrad( sUVa, float2( g.dpdx.z / 3.1, g.dpdx.y / 41.0 ), float2( g.dpdy.z / 3.1, g.dpdy.y / 41.0 ) );
	float4 sb = TWDetailGrad( sUVb, float2( g.dpdx.x / 3.1, g.dpdx.y / 41.0 ), float2( g.dpdy.x / 3.1, g.dpdy.y / 41.0 ) );
	float2 sw4 = pow( abs( N.xz ), float2( 4.0, 4.0 ) );
	float stainS = ( sa.w * sw4.x + sb.w * sw4.y ) / ( sw4.x + sw4.y + 1e-5 );
	float stain = smoothstep( 0.52, 0.72, stainS ) * steep;
	col = col * ( 1.0 - stain * 0.4 ) * ( smoothstep( 0.35, 0.2, stainS ) * steep * 0.12 + 1.0 );

	// lichens on the dry upper faces
	float dry = smoothstep( 2.6, 4.0, h );
	float lichen = smoothstep( 0.6, 0.78, mid.y ) * smoothstep( 0.2, 0.7, N.y ) * dry * smoothstep( 0.45, 0.65, mcr );
	col = lerp( col, PAL_lichenPale, lichen * 0.45 );
	col = lerp( col, PAL_lichenOrange, smoothstep( 0.8, 0.88, mid.y ) * dry * smoothstep( 0.5, 0.8, N.y ) * 0.3 );

	// moss / grass on ledges and tops, ferns hanging along the bedding planes of steep faces
	float ledge = smoothstep( 0.55, 0.7, strata ) * steep * strataAA * smoothstep( 0.4, 0.6, mid.w + ( mcr - 0.5 ) * 0.4 );
	float moss = max( smoothstep( 0.62, 0.9, N.y + ( big.x - 0.5 ) * 0.5 + ( mcr - 0.5 ) * 0.3 ), ledge * 0.8 )
		* smoothstep( 2.5, 5.0, h ) * mossAmount;
	col = lerp( col, lerp( PAL_moss, PAL_mossDry, mid.w ), moss * 0.9 );

	// shoreline zonation: black lichen band (splash zone), barnacles and algae in the intertidal
	float splash = smoothstep( 0.5, 1.0, h ) * smoothstep( 2.8, 1.8, h + mid.w * 1.2 ) * smoothstep( 0.35, 0.6, mcr + mid.w * 0.3 );
	col = lerp( col, PAL_blackZone, splash * 0.55 );
	// sun-bleached, weathered upper faces
	col = lerp( col, PAL_rockLight, smoothstep( 0.35, 0.95, N.y ) * smoothstep( 1.5, 3.0, h ) * 0.3 );
	float inter = smoothstep( -0.7, -0.2, h ) * smoothstep( 0.7, 0.2, h );
	float barn = smoothstep( 0.62, 0.72, fine.z ) * inter * fineK;
	col = lerp( col, PAL_algae, inter * smoothstep( 0.4, 0.6, mid.y ) * 0.6 );
	col = lerp( col, PAL_barnacle, barn * 0.8 );
	// below the water: algae films and pink coralline crusts
	float sub = smoothstep( -0.3, -1.2, h );
	col = lerp( col, lerp( PAL_algae, PAL_coralline, smoothstep( 0.45, 0.7, mid.w ) ), sub * 0.55 );

	// wet below the swash line (dark, glossy)
	float wet = smoothstep( 1.0, 0.25, h + mid.w * 0.3 );
	col = col * lerp( 1.0, 0.55, wet );

	TWRockSurfaceOut r;
	r.albedo = col;
	r.rough = lerp( lerp( 0.88, 0.8, steep ), 0.45, wet ) + moss * 0.06;
	// relief (m): tilted blocks and plates with bevelled joints, then grain
	r.hd = big.x * 0.25 + mid.x * 0.07 * ( midK * 0.6 + 0.4 ) + fine.x * 0.012 * fineK * ( 1.0 - wet * 0.6 ) + barn * 0.005;
	r.moss = moss;
	r.wet = wet;
	r.height = hr;
	return r;
}

// saturation helper
float3 TWSaturation( float3 c, float s )
{
	return lerp( ( float3 ) TWLuminance( c ), c, s );
}

struct TWMeadowToneOut
{
	float3 tone;
	float dry;
	float lush;
};

//   mA, mB: detail fbm channel at the 173 m / 47 m scales (~0.5 +- 0.1): the samples at
//   rot2( xz, 0.7 ) / 173 and rot2( xz, 2.1 ) / 47 that the terrain takes anyway; slope: 1 - N.y;
//   south: N.z (the sun side); detail: optional finer fbm (~0.5 +- 0.1) that breaks up the patches
//   (hasDetail = false: none)
// Returns { tone, dry, lush }: mostly fresh green grass with olive, sun-bleached yellow and a few
// straw-dry patches (more on exposed slopes), darker lush grass in the damp patches (the hollows
// are darkened further by the AO).
TWMeadowToneOut TWMeadowTone( float mA, float mB, float slope, float south, float detail, bool hasDetail )
{
	float m = mA * 0.55 + mB * 0.45 + slope * 0.25 + south * 0.04;
	float dd = hasDetail ? detail - 0.5 : 0.0;
	m += dd * 0.28;
	float olive = smoothstep( 0.52, 0.6, m );
	float yellow = smoothstep( 0.6, 0.67, m );
	float straw = smoothstep( 0.66, 0.73, m + ( mB - 0.5 ) * 0.2 );
	float lush = smoothstep( 0.46, 0.37, mB * 0.7 + mA * 0.3 + slope * 0.2 + dd * 0.2 );
	float3 c = lerp( MEADOW_green, MEADOW_olive, olive );
	c = lerp( c, MEADOW_yellow, yellow * 0.8 );
	c = lerp( c, MEADOW_straw, straw * 0.55 );
	c = lerp( c, MEADOW_lush, lush * 0.75 );
	TWMeadowToneOut o;
	o.tone = c;
	o.dry = olive * 0.4 + yellow * 0.6;
	o.lush = lush;
	return o;
}
