// The village surfaces (src/world/village/VillageMaterials.js): HLSL of the WGSL surface snippets, line for line.
//
// Surface detail comes from the tileable texture sets baked on the GPU at startup (VillageBake.compute, VillageTextures.cs): albedo / mask
// maps plus RG normal, B roughness, A ambient occlusion. UVs are in metres (GeoBuilder), so texel density is constant (256-1024 px/m).
// Per-vertex attributes keep every piece unique:  tint (float3) base colour (paint colour, wood tone, rope colour ...), vdata (float4)
// material specific parameters (documented per material below). Normal layers are blended in slope (derivative) space.
// Materials (one per kind, a compile time constant): wood (+ glass, pattern 9), hard (+ rope), roofMetal, thatch, stone, fabric, net.
//
// Port notes: the three.js normalMap() of a packed slope normal is perturbNormalByMap() with the mesh uv derivative frame
// (VillageNormalFromSlope; the frame is multiplied by the sign of its determinant so it follows +u / +v in the mirrored Unity world).
// World positions used for weathering (in.P in the JS) are the sim positions i.p (x east, y up, z south); the camera-relative i.P only
// feeds the derivative frame. Textures are the globals vlg<Name>, sampled repeat / trilinear / 8x anisotropic (set on the textures).

#ifndef VILLAGE_SURFACE_INCLUDED
#define VILLAGE_SURFACE_INCLUDED

#define VLM_TAU 6.283185307179586

struct VillageIn
{
	float4 vdata;
	float3 tint;
	float2 uv;
	float3 p;            // sim world position (x east, y up, z south)
	float3 P;            // camera-relative world position
	float3 N;            // world normal (facing the viewer for double-sided kinds)
	float2 pixel;        // screen pixel (dither)
	bool front;
	float4 wind;         // sim wind direction (x, z), speed
	float time;
	float night;
	float3 skyIrradiance;
	float3 sunColor;
	float sunDirY;
};

struct VillageOut
{
	float3 albedo;
	float roughness;
	float metalness;
	float ao;
	float3 normal;
	float3 emissive;
	float alpha;
};

TEXTURE2D( vlgWoodA );   SAMPLER( sampler_vlgWoodA );
TEXTURE2D( vlgWoodN );   SAMPLER( sampler_vlgWoodN );
TEXTURE2D( vlgPaintN );  SAMPLER( sampler_vlgPaintN );
TEXTURE2D( vlgRoofA );   SAMPLER( sampler_vlgRoofA );
TEXTURE2D( vlgRoofN );   SAMPLER( sampler_vlgRoofN );
TEXTURE2D( vlgThatchA ); SAMPLER( sampler_vlgThatchA );
TEXTURE2D( vlgThatchN ); SAMPLER( sampler_vlgThatchN );
TEXTURE2D( vlgStoneA );  SAMPLER( sampler_vlgStoneA );
TEXTURE2D( vlgStoneN );  SAMPLER( sampler_vlgStoneN );
TEXTURE2D( vlgHardA );   SAMPLER( sampler_vlgHardA );
TEXTURE2D( vlgHardN );   SAMPLER( sampler_vlgHardN );
TEXTURE2D( vlgGrime );   SAMPLER( sampler_vlgGrime );
TEXTURE2D( vlgRope );    SAMPLER( sampler_vlgRope );
TEXTURE2D( vlgNet );     SAMPLER( sampler_vlgNet );

#define VLG_TEX( name, uv ) SAMPLE_TEXTURE2D( name, sampler_##name, uv )
#define VLG_TEX_LOD( name, uv, lod ) SAMPLE_TEXTURE2D_LOD( name, sampler_##name, uv, lod )

float vlmHash21( float x, float y ) { return frac( sin( x * 12.9898 + y * 78.233 ) * 43758.5453 ); }
float vlmN01( float n ) { return n * 0.5 + 0.5; }

// baked normal (RG = xy of a unit normal) -> surface slope ( -dh/du, -dh/dv )
float2 vlmSlopeOf( float4 t )
{
	float2 xy = t.xy * 2.0 - 1.0;
	return xy / sqrt( max( 1.0 - dot( xy, xy ), 0.04 ) );
}

float vlmBand01( float p, float k ) { return step( k - 0.5, p ) * step( p, k + 0.5 ); }

// three normalMap( packN( vec3( s, 1 ) ) ): tangent frame from the mesh uv derivatives
float3 VillageNormalFromSlope( float3 P, float3 N, float2 uv, float2 s )
{
	float3 dp1 = ddx( P ); float3 dp2 = ddy( P );
	float2 duv1 = ddx( uv ); float2 duv2 = ddy( uv );
	float3 dp2perp = cross( dp2, N ); float3 dp1perp = cross( N, dp1 );
	float3 T = dp2perp * duv1.x + dp1perp * duv2.x;
	float3 B = dp2perp * duv1.y + dp1perp * duv2.y;
	// the cotangent frame loses its sign with the handedness of the space and of the screen derivatives
	float det = dot( dp1, dp2perp );
	float sg = det < 0.0 ? -1.0 : 1.0;
	float invmax = rsqrt( max( max( dot( T, T ), dot( B, B ) ), 1e-20 ) ) * sg;
	float3 m = normalize( float3( s, 1.0 ) );
	return normalize( T * invmax * m.x + B * invmax * m.y + N * m.z );
}

// ---------------------------------------------------------------------------
// tidal zone (materials that sit in the splash zone)

struct VlmTidal { float3 col; float rough; float wet; };

// Tidal zone: algae / barnacles / wet darkening driven by world height above sea level.
VlmTidal vlmTidal( float3 pw, float3 col, float rough )
{
	float2 q = float2( pw.x + pw.z * 0.7, pw.y );
	float4 Gt = VLG_TEX( vlgGrime, q * float2( 0.6, 1.2 ) );
	float y = pw.y + ( Gt.a - 0.5 ) * 0.3;
	float wet = 1.0 - smoothstep( 0.3, 0.75, y );
	float damp = ( 1.0 - smoothstep( 0.7, 1.5, y ) ) * 0.4;
	float3 algae = lerp( float3( 0.028, 0.042, 0.02 ), float3( 0.11, 0.105, 0.05 ), Gt.a * 0.6 + Gt.r * 0.4 );
	float4 Gb = VLG_TEX( vlgGrime, q * 3.1 );
	float barn = Gb.b * smoothstep( - 1.6, - 0.35, y ) * ( 1.0 - smoothstep( 0.25, 0.6, y ) );
	float3 c = col * ( 1.0 - damp );
	c = lerp( c, algae, wet );
	c = lerp( c, float3( 0.52, 0.5, 0.44 ), barn * 0.9 );
	float r = lerp( rough, 0.3, wet * 0.9 );
	r = lerp( r, 0.55, damp );
	r = lerp( r, 0.9, barn );
	VlmTidal t;
	t.col = c; t.rough = r; t.wet = wet;
	return t;
}

// ---------------------------------------------------------------------------
// GLASS (evaluated inside the wood material): window panes and lantern glass, emissive at night.
// uv: normalized 0..1 across a pane. kind 0 window / 1 lantern, lit 0/1, tint: curtain / glass colour

struct VlmGlass { float3 color; float rough; float3 emissive; };

VlmGlass vlmGlass( VillageIn i, float2 uvm, float3 aTint, float seed, float kind, float lit )
{
	// grime map channels: R streaks, G salt, B spots, A macro
	float4 Gl = VLG_TEX( vlgGrime, uvm * 0.45 + float2( seed * 3.7, seed * 1.3 ) );
	float edgeD = min( min( uvm.x, 1.0 - uvm.x ), min( uvm.y, 1.0 - uvm.y ) );
	float frameDirt = 1.0 - smoothstep( 0.0, 0.07, edgeD );
	float curtain = ( 1.0 - smoothstep( 0.18, 0.3, uvm.x ) + smoothstep( 0.7, 0.82, uvm.x ) ) * step( 0.35, seed );
	float folds = vlmN01( sin( uvm.x * 70.0 + seed * 20.0 ) );
	float3 interior = float3( 0.012, 0.014, 0.017 ) * ( Gl.a * 0.8 + 0.6 );
	float3 winCol = lerp( interior, aTint * 0.16 * ( folds * 0.4 + 0.6 ), curtain );
	winCol = winCol + float3( 0.05, 0.05, 0.045 ) * Gl.g + float3( 0.03, 0.026, 0.02 ) * ( Gl.b + frameDirt );
	float3 lanternCol = aTint * 0.55 * ( 1.0 - Gl.b * 0.25 );
	float isLantern = step( 0.5, kind );
	VlmGlass g;
	g.color = lerp( winCol, lanternCol, isLantern );
	g.rough = lerp( 0.035 + Gl.r * 0.22 + Gl.g * 0.18 + Gl.b * 0.1 + frameDirt * 0.25, 0.32 + Gl.r * 0.15, isLantern );

	float nightOn = smoothstep( 0.15, 0.75, i.night );
	float3 warm = float3( 1.0, 0.56, 0.24 );
	float center = 1.0 - smoothstep( 0.1, 0.75, abs( uvm.x - 0.5 ) * 1.4 + abs( uvm.y - 0.62 ) );
	float3 winGlow = lerp( warm * ( center * 0.6 + 0.5 ), ( aTint * 0.5 + warm * 0.5 ) * ( folds * 0.3 + 0.45 ), curtain )
		* ( vlmHash21( seed, 3.1 ) * 0.5 + 0.75 ) * 3.2 * ( 1.0 - Gl.r * 0.25 );
	float flicker = sin( i.time * 9.0 + seed * 40.0 ) * sin( i.time * 5.3 + seed * 13.0 ) * 0.12 + 0.9;
	float3 lanternGlow = float3( 1.0, 0.64, 0.3 ) * 6.0 * flicker;
	g.emissive = lerp( winGlow, lanternGlow, isLantern ) * nightOn * max( lit, isLantern );
	return g;
}

// ---------------------------------------------------------------------------
// WOOD: raw and painted timber.
// vdata: x seed, y paint (0 raw, 0.3 heavily worn .. 1 fresh; < 0: raw deck board with a worn walking
//        path along it, centred at u = -paint - 1), z pattern, w weathering (0 fresh .. 1 silver)
// World-space weathering on every piece: rain / rust streaks down vertical faces, bird droppings on
// upward faces above head height, the tidal belt near the water (vlmTidal).
// patterns: 0 plain, 1 lap siding, 2 board & batten, 3 vertical tongue & groove,
//           4 louvers, 5 planks along u (staves, clinker), 6 horizontal planks (flush),
//           7 nailed deck plank (nail heads + rust stains every 0.8 m),
//           9 GLASS (window panes / lantern glass): vdata = seed, kind (0 window, 1 lantern), 9, lit
// tint: paint colour when painted, otherwise a wood tone multiplier.
// uv: metres, u along the grain; end-grain faces are flagged with u + 1000, post tops with u + 2000.

void VillageWood( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float seed = aData.x;
	float paint = aData.y;
	float pattern = aData.z;
	float weather = aData.w;
	float2 uv0 = i.uv;
	float isCap = step( 1500.0, uv0.x );
	float isEnd = step( 500.0, uv0.x ) * ( 1.0 - isCap );
	float endAny = max( isCap, isEnd );
	float2 uvS = uv0 - float2( isCap * 2000.0 + isEnd * 1000.0, 0.0 );
	float2 fw = fwidth( uvS );
	float px = max( fw.x, fw.y );
	float nearK = 1.0 - smoothstep( 0.0012, 0.005, px );

	float mLap = vlmBand01( pattern, 1.0 );
	float mBB = vlmBand01( pattern, 2.0 );
	float mTG = vlmBand01( pattern, 3.0 );
	float mLv = vlmBand01( pattern, 4.0 );
	float mPk = vlmBand01( pattern, 5.0 );
	float mHz = vlmBand01( pattern, 6.0 );
	float mNail = step( 6.5, pattern );
	float isVert = mBB + mTG;
	float2 g = lerp( uvS, uvS.yx, isVert ); // g.x along the grain

	// every board of siding / planking gets its own piece of the wood texture
	float boardIdx = floor( uvS.y / 0.2 ) * mLap + floor( uvS.y / 0.16 ) * mHz
		+ floor( uvS.x / 0.42 ) * mBB + floor( uvS.x / 0.14 ) * mTG + floor( uvS.y / 0.105 ) * mPk;
	float pieceSeed = seed * 131.0 + boardIdx * 7.13;
	float2 off = float2( vlmHash21( pieceSeed, 1.7 ), vlmHash21( pieceSeed, 9.2 ) );
	float2 tuv = g * float2( 0.5, 1.0 ) + off;
	float4 A = VLG_TEX( vlgWoodA, tuv );
	float4 N = VLG_TEX( vlgWoodN, tuv );
	float4 D = VLG_TEX( vlgWoodN, tuv * float2( 3.0, 4.0 ) + float2( 0.37, 0.61 ) );

	// grime / salt / macro variation in mesh space
	float4 Gm = VLG_TEX( vlgGrime, uvS * 0.5 + off.yx * 0.5 );
	float macroV = VLG_TEX( vlgGrime, uvS * 0.07 + float2( seed * 0.37, seed * 0.71 ) ).a;
	float hasPaint = step( 0.001, paint );
	// raw timber weathers along the grain: the same grime map stretched ~15x along the board, so
	// dirt, salt and tone vary in long streaks and change gradually from one end to the other
	float4 Gs = VLG_TEX( vlgGrime, g * float2( 0.035, 0.55 ) + off * 0.5 );
	float rawK = 1.0 - hasPaint;

	// end grain: polar remap of the side-grain texture -> growth rings become arcs (plank ends) or circles (post tops)
	float2 pith = lerp(
		float2( vlmHash21( seed, 5.3 ) * 0.4 - 0.6, vlmHash21( seed, 6.1 ) * 0.3 + 0.25 ),
		float2( ( vlmHash21( seed, 3.1 ) - 0.5 ) * 0.02, ( vlmHash21( seed, 4.7 ) - 0.5 ) * 0.02 ),
		isCap
	);
	float2 ev = uvS - pith;
	float er = length( ev );
	float2 eUV = float2( atan2( ev.y, ev.x ) / VLM_TAU * 3.0 + off.x, er + off.y );
	float eLod = log2( max( fwidth( er ) * 1024.0, 1.0 ) );
	float4 E = VLG_TEX_LOD( vlgWoodA, eUV, eLod );

	// raw timber colour: the baked map is fully weathered silver; less weathered pieces shift to warm brown
	float wW = clamp( weather + ( macroV - 0.5 ) * 0.35, 0.0, 1.0 );
	// wood exposed under chipped paint was protected until recently: lighter and warmer
	float wWx = wW * lerp( 1.0, 0.6, hasPaint );
	float3 sideCol = lerp( A.rgb * float3( 1.26, 1.02, 0.78 ) * 1.04, A.rgb, smoothstep( 0.12, 0.6, wWx ) );
	float3 endCol = lerp( E.rgb * float3( 1.3, 1.0, 0.7 ), E.rgb, wW ) * 0.66;
	float3 woodTone = lerp( aTint, float3( 1.2, 1.2, 1.2 ), hasPaint );
	float boardTone = ( vlmHash21( pieceSeed, 3.3 ) - 0.5 ) * 0.16 + ( Gs.a - 0.5 ) * 0.14;
	float3 wood = lerp( sideCol, endCol, endAny ) * woodTone * ( macroV * 0.2 + 0.9 ) * ( 1.0 + boardTone * rawK );

	// paint film: chips follow the baked chip field; the film has thickness at the chip edges
	// chip field quantiles: 5% 0.26, 10% 0.31, 20% 0.35, 30% 0.39 -> paint 0.7 leaves ~8% bare wood,
	// 0.6 ~14%, 0.5 ~22%; lap drip edges and the splash zone near wall bottoms wear faster
	float chip = A.a;
	float lapEdge = ( 1.0 - smoothstep( 0.0, 0.18, frac( uvS.y / 0.2 ) ) ) * mLap;
	float lowWall = ( 1.0 - smoothstep( 0.0, 0.7, uvS.y ) ) * ( mLap + mBB + mTG );
	float thr = clamp( 0.395 - ( paint - 0.4 ) * 0.32 + lapEdge * 0.05 + lowWall * 0.05, 0.15, 0.55 );
	float painted = smoothstep( thr - 0.012, thr + 0.012, chip ) * hasPaint * ( 1.0 - endAny );
	float e = 1.0 / 1024.0;
	float cX = VLG_TEX( vlgWoodA, tuv + float2( e, 0.0 ) ).a - VLG_TEX( vlgWoodA, tuv - float2( e, 0.0 ) ).a;
	float cY = VLG_TEX( vlgWoodA, tuv + float2( 0.0, e ) ).a - VLG_TEX( vlgWoodA, tuv - float2( 0.0, e ) ).a;
	float edge = ( 1.0 - smoothstep( 0.0, 0.025, abs( chip - thr ) ) ) * hasPaint * nearK;
	float2 edgeSlope = float2( cX, cY ) * edge * - 9.0;
	float4 Pn = VLG_TEX( vlgPaintN, g * float2( 1.0, 2.0 ) + off * 3.7 );

	float fade = clamp( Gm.a * 0.55 + wW * 0.25 + macroV * 0.2, 0.0, 1.0 );
	float3 chalk = aTint * 0.64 + float3( 0.27, 0.265, 0.25 );
	// bright paints (white trim, cream siding) never stay white out here: dusty, yellowed, grey
	float3 aged = lerp( aTint, aTint * float3( 0.86, 0.84, 0.77 ), smoothstep( 0.45, 0.8, dot( aTint, float3( 0.2126, 0.7152, 0.0722 ) ) ) * ( 0.6 + Gm.a * 0.4 ) );
	float3 paintCol = lerp( aged, chalk, fade * 0.5 ) * ( ( Pn.b - 0.5 ) * 0.16 + 1.0 );

	// siding / plank patterns (shading and slope in mesh space)
	float fvLap = frac( uvS.y / 0.2 );
	float lapFade = 1.0 - smoothstep( 0.02, 0.07, fw.y );
	float lapShade = lerp( 0.92, lerp( 0.45, 1.03, smoothstep( 0.0, 0.09, fvLap ) ) - fvLap * 0.07, lapFade );
	float lapSy = lerp( 0.0, fvLap < 0.07 ? - 2.4 : - 0.14, lapFade );

	float fuBB = frac( uvS.x / 0.42 );
	float bbFade = 1.0 - smoothstep( 0.03, 0.09, fw.x );
	float inBatten = step( fuBB, 0.12 );
	float bbShade = lerp( 0.95, lerp( 1.0, 0.7, ( 1.0 - smoothstep( 0.12, 0.2, fuBB ) ) * ( 1.0 - inBatten ) ) * ( inBatten * 0.06 + 1.0 ), bbFade );
	float bbSx = ( fuBB < 0.015 ? - 2.0 : ( abs( fuBB - 0.112 ) < 0.012 ? 2.0 : 0.0 ) ) * bbFade;

	float fuTG = frac( uvS.x / 0.14 );
	float tgFade = 1.0 - smoothstep( 0.015, 0.05, fw.x );
	float tgShade = lerp( 0.96, lerp( 0.6, 1.0, smoothstep( 0.0, 0.06, fuTG ) ), tgFade );
	float tgSx = ( fuTG < 0.03 ? - 1.0 : ( fuTG < 0.06 ? 1.0 : 0.0 ) ) * tgFade;

	float fvLv = frac( uvS.y / 0.07 );
	float lvFade = 1.0 - smoothstep( 0.008, 0.03, fw.y );
	float lvShade = lerp( 0.8, lerp( 0.42, 1.05, smoothstep( 0.0, 0.8, fvLv ) ), lvFade );
	float lvSy = lerp( 0.0, fvLv > 0.85 ? 1.2 : - 1.0, lvFade );

	float fvPk = frac( uvS.y / 0.105 );
	float pkFade = 1.0 - smoothstep( 0.012, 0.04, fw.y );
	float pkShade = lerp( 0.95, lerp( 0.58, 1.0, smoothstep( 0.0, 0.07, fvPk ) ), pkFade );
	float pkSy = ( fvPk < 0.035 ? - 1.0 : ( fvPk < 0.07 ? 1.0 : 0.0 ) ) * pkFade;

	float fvHz = frac( uvS.y / 0.16 );
	float hzFade = 1.0 - smoothstep( 0.015, 0.05, fw.y );
	float hzShade = lerp( 0.95, lerp( 0.5, 1.0, smoothstep( 0.0, 0.05, fvHz ) ), hzFade );

	float shade = 1.0
		+ mLap * ( lapShade - 1.0 ) + mBB * ( bbShade - 1.0 ) + mTG * ( tgShade - 1.0 )
		+ mLv * ( lvShade - 1.0 ) + mPk * ( pkShade - 1.0 ) + mHz * ( hzShade - 1.0 );

	// nail heads with rust bleed on deck planks
	float nu = ( frac( ( uvS.x - 0.1 ) / 0.8 + 0.5 ) - 0.5 ) * 0.8;
	float nv = min( abs( uvS.y - 0.045 ), abs( uvS.y - 0.155 ) );
	float nearN = 1.0 - smoothstep( 0.0015, 0.004, px );
	float nail = ( 1.0 - smoothstep( 0.0045, 0.0075, length( float2( nu, nv ) ) ) ) * mNail * nearN;
	float stain = ( 1.0 - smoothstep( 0.0, 0.05, length( float2( nu * 0.3, nv * 1.4 ) ) ) ) * mNail;

	// ---- colour
	float3 col = lerp( wood * ( stain * - 0.4 + 1.0 ), paintCol, painted );
	col = lerp( col, float3( 0.04, 0.032, 0.028 ), nail );
	float dirt = lerp( 0.22, 0.5, wW );
	float4 Gd = lerp( Gm, Gs, rawK );
	col = col * ( 1.0 - Gd.r * dirt * lerp( 0.75, 0.35, rawK ) );
	col = lerp( col, float3( 0.62, 0.61, 0.57 ), Gd.g * lerp( 0.28, 0.16, rawK ) * ( painted * 0.5 + 0.5 ) );
	col = col * ( 1.0 - Gm.b * 0.3 * hasPaint );
	float isWall = mLap + mBB + mTG;
	// splash-back along the bottom of walls: rain throws sand and soil up the boards, and green
	// mildew grows where they stay damp; a ragged upper edge
	float splashH = 0.55 + ( Gm.a - 0.5 ) * 0.5 + ( macroV - 0.5 ) * 0.3;
	float splash = ( 1.0 - smoothstep( 0.0, splashH, uvS.y ) ) * isWall;
	col = col * ( 1.0 - splash * 0.5 );
	col = lerp( col, col * float3( 0.78, 0.9, 0.62 ), splash * smoothstep( 0.4, 0.7, Gm.a + Gm.b * 0.5 ) * 0.7 );
	float ao = lerp( N.a, lerp( 1.0, N.a, 0.35 ) * Pn.a, painted );
	col = col * shade * lerp( 1.0, ao, 0.5 );

	// ---- world-space weathering
	float3 Ng = i.N;
	float upF = saturate( Ng.y );
	float sideF = 1.0 - abs( Ng.y );
	// worn walking path down the middle of deck boards: fibres worn off (paler, warmer, smoother),
	// grey dirt ground into the grain and dark scuffs
	float walkC = - paint - 1.0;
	float wd = ( uvS.x - walkC + ( macroV - 0.5 ) * 0.5 ) / 0.6;
	float walkK = step( paint, - 0.5 ) * smoothstep( 0.6, 0.9, upF ) * exp( - wd * wd ) * ( 1.0 - endAny );
	// the path is worn brown-grey: the silver skin scuffed off, grime trodden into the grain
	float3 trodden = col * float3( 0.8, 0.76, 0.72 );
	col = lerp( col, trodden, walkK * 0.55 );
	// grit and dirt packed along the edges of deck boards (the gaps collect it)
	float bev = min( uvS.y, 0.18 - uvS.y );
	float edgeDirt = mNail * smoothstep( 0.6, 0.9, upF ) * ( 1.0 - smoothstep( 0.004, 0.03, bev ) ) * ( 1.0 - endAny );
	col = col * ( 1.0 - edgeDirt * ( 0.18 + Gs.r * 0.15 ) );
	// water that pools along the board edges keeps them damp and darker in broad, gradual bands
	// (no blotches: it follows the boards); sun and salt bleach the open middle of the deck
	float damp = smoothstep( 0.6, 0.9, upF ) * rawK * ( 1.0 - endAny ) * smoothstep( 0.45, 0.75, macroV );
	col = col * ( 1.0 - damp * ( 1.0 - smoothstep( 0.0, 0.06, bev ) ) * 0.18 );
	// rain and rust streaks running down vertical faces (from nails, bolts, sills and roof edges)
	float4 sqA = VLG_TEX( vlgGrime, float2( ( i.p.x + i.p.z ) * 2.3 + seed * 5.0, i.p.y * 0.12 ) );
	float4 sqB = VLG_TEX( vlgGrime, float2( ( i.p.x - i.p.z ) * 5.1, i.p.y * 0.3 + 0.37 ) );
	float streak = sideF * sideF * clamp( smoothstep( 0.45, 0.85, sqB.r ) + smoothstep( 0.6, 0.7, sqA.a ) * 0.35, 0.0, 1.0 ) * ( 0.45 + wW * 0.55 ) * ( 1.0 - endAny );
	float rustS = streak * smoothstep( 0.5, 0.6, sqA.a );
	col = col * ( 1.0 - streak * 0.24 );
	col = lerp( col, col * float3( 1.04, 0.78, 0.6 ), rustS * 0.6 );
	// bird droppings: sparse splats on upward faces above head height (rails, posts, cap beams, sills)
	float2 bq = i.p.xz / 0.3;
	float2 bc = floor( bq );
	float bh = vlmHash21( bc.x + seed, bc.y );
	float2 bo = frac( bq ) - 0.5 - ( float2( vlmHash21( bc.x + 3.1, bc.y ), vlmHash21( bc.x, bc.y + 7.7 ) ) - 0.5 ) * 0.45;
	float bd = length( bo * float2( 1.0, 1.35 ) ) + ( Gm.a - 0.5 ) * 0.08;
	float dropK = step( 0.93, bh ) * smoothstep( 0.75, 0.95, upF ) * step( 2.6, i.p.y ) * ( 1.0 - smoothstep( 0.1, 0.15, bd ) );
	float dropCore = dropK * ( 1.0 - smoothstep( 0.02, 0.07, bd ) );
	col = lerp( col, lerp( float3( 0.62, 0.61, 0.56 ), float3( 0.8, 0.79, 0.74 ), dropCore ), dropK * 0.6 );

	// ---- normal (grain space -> mesh space) + analytic pattern relief
	float2 sWood = vlmSlopeOf( N ) + vlmSlopeOf( D ) * nearK * 0.5;
	float2 sPaint = vlmSlopeOf( N ) * 0.3 + vlmSlopeOf( Pn );
	float2 sG = ( lerp( sWood, sPaint, painted ) + edgeSlope ) * ( 1.0 - endAny * 0.6 ) * ( 1.0 - walkK * 0.45 ) * ( 1.0 - dropK * 0.7 );
	float2 sM = lerp( sG, sG.yx, isVert );
	float2 sPat = float2(
		mBB * bbSx + mTG * tgSx,
		mLap * lapSy + mLv * lvSy + mPk * pkSy
	);
	// ---- roughness: satin-ish paint vs dry raw timber; grime and salt dull it
	float roughRaw = N.b + wW * 0.03;
	float roughPaint = lerp( 0.4, 0.7, Pn.b ) + fade * 0.12;
	float rough = lerp( roughRaw, roughPaint, painted ) + Gm.r * 0.06 + Gm.g * 0.08 - nail * 0.3 - walkK * 0.1 + streak * 0.05 - dropCore * 0.2;
	VlmTidal res = vlmTidal( i.p, col, rough );

	// ---- glass mode (windows and lanterns share this material to save a draw call)
	float isGlass = step( 8.5, pattern );
	VlmGlass glass = vlmGlass( i, i.uv, aTint, seed, paint, weather );
	s.albedo = lerp( res.col, glass.color, isGlass );
	s.roughness = clamp( lerp( res.rough, glass.rough, isGlass ), 0.03, 1.0 );
	s.ao = lerp( ao, 1.0, isGlass );
	s.emissive = glass.emissive * isGlass;
	s.normal = VillageNormalFromSlope( i.P, i.N, i.uv, ( sM + sPat ) * ( 1.0 - isGlass ) );
}

// ---------------------------------------------------------------------------
// CORRUGATED METAL ROOFING
// uv: u = distance up-slope from the eave (m), v = along the eave (m)
// vdata: x seed, y rust amount, z galvanized (1) / painted (0)

void VillageRoofMetal( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float seed = aData.x;
	float rustAmt = aData.y;
	float galv = aData.z;
	float2 uvm = i.uv;
	float2 fw = fwidth( uvm );
	float nearK = 1.0 - smoothstep( 0.0015, 0.006, max( fw.x, fw.y ) );
	float sheetF = uvm.y / 0.84 + floor( seed * 13.0 );
	float sheetId = floor( sheetF );
	float sheetR = vlmHash21( sheetId, seed * 91.0 );
	float2 tuv = float2( sheetF, uvm.x / 1.68 + sheetR * 5.37 );
	float4 M = VLG_TEX( vlgRoofA, tuv );
	float4 N = VLG_TEX( vlgRoofN, tuv );
	float4 macroV = VLG_TEX( vlgRoofA, tuv * float2( 0.13, 0.09 ) + float2( seed, seed * 1.7 ) );

	float eave = 1.0 - smoothstep( 0.0, 0.9, uvm.x );
	float replaced = step( 0.86, sheetR );
	float rustIn = clamp( rustAmt + 0.15 + replaced * 0.2 + ( macroV.r - 0.4 ) * 0.5, 0.0, 1.0 );
	// rust channel quantiles: 50% 0.25, 70% 0.31, 90% 0.42 -> rust 0.4 ~10%, 0.6 ~25%, 0.8 ~45%
	float thr = lerp( 0.62, 0.23, rustIn ) - eave * 0.2;
	float rust = smoothstep( thr - 0.04, thr + 0.04, M.r );
	float fade = clamp( M.b * 0.7 + macroV.b * 0.3, 0.0, 1.0 );

	float3 paintCol = lerp( aTint, aTint * 0.7 + float3( 0.12, 0.1, 0.085 ), fade * 0.55 ) * ( sheetR * 0.16 + 0.9 );
	float3 patchCol = paintCol * float3( 0.72, 0.68, 0.66 ) + float3( 0.03, 0.025, 0.02 );
	float3 galvCol = float3( 0.34, 0.35, 0.35 ) * ( fade * 0.35 + 0.78 );
	float3 baseCol = lerp( lerp( paintCol, patchCol, replaced ), galvCol, galv );
	float3 rustCol = lerp( float3( 0.12, 0.045, 0.02 ), float3( 0.4, 0.17, 0.065 ), M.g );
	float3 col = lerp( baseCol, rustCol, rust );
	col = col * ( 1.0 - M.a * 0.45 );
	col = col * lerp( 1.0, N.a, 0.55 );

	// texture X runs along the eave (mesh v), texture Y up the slope (mesh u)
	float2 sT = vlmSlopeOf( N );
	float4 Hd = VLG_TEX( vlgHardN, uvm * 1.7 );
	float2 sl = float2( sT.y, sT.x ) + vlmSlopeOf( Hd ) * ( rust * 0.8 + 0.2 ) * nearK;
	s.normal = VillageNormalFromSlope( i.P, i.N, i.uv, sl );

	s.albedo = col;
	s.metalness = lerp( lerp( 0.04, 0.5, galv ), 0.0, rust );
	s.roughness = clamp( lerp( lerp( 0.55, 0.42, galv ) + ( N.b - 0.5 ) * 0.4 + fade * 0.15, 0.9, rust ) + M.a * 0.08, 0.05, 1.0 );
	s.ao = N.a;
}

// ---------------------------------------------------------------------------
// PALM THATCH
// uv: u = distance up-slope from the eave (m), v = along the eave (m)
// vdata: x seed, y age (0 golden .. 1 grey)

void VillageThatch( VillageIn i, inout VillageOut s )
{
	float4 aData = i.vdata;
	float seed = aData.x;
	float age = aData.y;
	float2 uvm = i.uv;
	float2 tuv = float2( uvm.y + vlmHash21( seed, 1.3 ) * 7.0, uvm.x );
	float4 A = VLG_TEX( vlgThatchA, tuv );
	float4 N = VLG_TEX( vlgThatchN, tuv );
	// macro variation: patches of newer / older thatch and rain streaks down the slope
	float4 Gm = VLG_TEX( vlgGrime, float2( tuv.x * 0.23, tuv.y * 0.19 ) + seed );
	float4 Gs = VLG_TEX( vlgGrime, float2( tuv.x * 0.5, tuv.y * 0.25 ) + seed * 1.7 );

	float lum = dot( A.rgb, float3( 0.3, 0.59, 0.11 ) );
	float3 greyed = float3( lum, lum, lum ) * float3( 0.95, 0.9, 0.8 );
	float ageL = clamp( age + ( Gm.a - 0.5 ) * 0.9, 0.0, 1.0 );
	float3 col = lerp( A.rgb, greyed, ageL * 0.8 ) * ( Gm.a * 0.35 + 0.82 ) * ( 1.0 - ageL * 0.2 );
	col = col * ( 1.0 - Gs.r * 0.25 ) * ( 1.0 - Gs.b * 0.35 );
	col = col * lerp( 1.0, N.a, 0.55 );
	float2 sT = vlmSlopeOf( N );
	s.normal = VillageNormalFromSlope( i.P, i.N, i.uv, float2( sT.y, sT.x ) );
	s.albedo = col;
	s.roughness = N.b;
	s.ao = N.a;
}

// ---------------------------------------------------------------------------
// HARD SURFACES: iron, steel, galvanized, rubber, plastic, painted metal - and rope.
// vdata: x seed, y rust (0..1), z metalness, w roughness
// rope: vdata.w = 2 + rope radius (uv: u along the rope, v around, metres)

void VillageHard( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float seed = aData.x;
	float rustAmt = aData.y;
	float isRope = step( 1.5, aData.w );
	float2 uv0 = i.uv;
	float2 off = float2( vlmHash21( seed, 2.3 ), vlmHash21( seed, 8.9 ) );
	float2 tuv = uv0 + off;
	float4 HA = VLG_TEX( vlgHardA, tuv );
	float4 HN = VLG_TEX( vlgHardN, tuv );
	float hasRust = step( 0.001, rustAmt );
	float thr = lerp( 0.95, 0.3, rustAmt );
	float rust = smoothstep( thr - 0.06, thr + 0.06, HA.r ) * hasRust;
	float3 baseCol = aTint * ( 1.0 - HA.b * 0.2 ) * ( ( HA.r - 0.5 ) * 0.1 + 1.0 );
	baseCol = lerp( baseCol, float3( 0.3, 0.3, 0.29 ), HA.g * 0.55 * hasRust * ( 1.0 - rust ) );
	float3 rustCol = lerp( float3( 0.12, 0.045, 0.02 ), float3( 0.4, 0.17, 0.06 ), smoothstep( 0.3, 0.9, HA.r ) );
	float3 hardCol = lerp( baseCol, rustCol, rust ) * lerp( 1.0, HN.a, 0.5 );
	float hardRough = lerp( clamp( aData.w + ( HN.b - 0.5 ) * 0.35 - HA.g * 0.15 * hasRust, 0.04, 1.0 ), 0.88, rust );
	float2 hardS = vlmSlopeOf( HN ) * ( rust * 0.8 + 0.25 );

	// rope: uvs normalised by the circumference so the three strands wrap seamlessly
	float ropeR = max( aData.w - 2.0, 0.004 );
	float4 R = VLG_TEX( vlgRope, uv0 / ( ropeR * VLM_TAU ) + float2( aData.x * 3.1, 0.0 ) );
	float3 ropeCol = aTint * lerp( 0.45, 1.25, R.r ) * lerp( 1.0, R.g, 0.6 );
	float2 rn = R.ba * 2.0 - 1.0;
	float2 ropeS = rn / sqrt( max( 1.0 - dot( rn, rn ), 0.04 ) );

	VlmTidal res = vlmTidal( i.p, lerp( hardCol, ropeCol, isRope ), lerp( hardRough, 0.93, isRope ) );
	s.normal = VillageNormalFromSlope( i.P, i.N, i.uv, lerp( hardS, ropeS, isRope ) );
	s.albedo = res.col;
	s.roughness = res.rough;
	s.metalness = aData.z * ( 1.0 - max( rust, res.wet ) ) * ( 1.0 - isRope );
	s.ao = lerp( HN.a, R.g, isRope );
}

// ---------------------------------------------------------------------------
// STONE: rubble masonry, lime plaster over stone, sand dusted near the ground
// uv in metres (u horizontal, v vertical from the bottom of the piece).
// vdata: x seed, y style (0 coursed rubble, 1 plaster over stone, 2 small rubble)
// tint: plaster colour

void VillageStone( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float seed = aData.x;
	float style = aData.y;
	float2 uv0 = i.uv;
	float isCap = step( 1500.0, uv0.x );
	float isEnd = step( 500.0, uv0.x ) * ( 1.0 - isCap );
	float2 uvS = uv0 - float2( isCap * 2000.0 + isEnd * 1000.0, 0.0 );
	float isRubble = step( 1.5, style );
	float isPlaster = vlmBand01( style, 1.0 );
	float2 off = float2( vlmHash21( seed, 4.1 ), vlmHash21( seed, 7.7 ) );
	float2 tuv = uvS * lerp( 0.5, 0.8, isRubble ) + off;
	float4 A = VLG_TEX( vlgStoneA, tuv );
	float4 N = VLG_TEX( vlgStoneN, tuv );
	// lime plaster: trowel marks from the paint film set, stains / dust from the grime map
	float2 puv = uvS + off * 3.0;
	float4 PN = VLG_TEX( vlgPaintN, puv * float2( 0.7, 1.4 ) );
	float4 PA = VLG_TEX( vlgGrime, puv * 0.5 );

	// plaster survives where the baked plaster field is high; the plaster edge has thickness
	float pf = A.a + ( PA.a - 0.5 ) * 0.12;
	float pm = smoothstep( 0.3, 0.33, pf ) * isPlaster;
	float e = 1.0 / 1024.0;
	float gX = VLG_TEX( vlgStoneA, tuv + float2( e, 0.0 ) ).a - VLG_TEX( vlgStoneA, tuv - float2( e, 0.0 ) ).a;
	float gY = VLG_TEX( vlgStoneA, tuv + float2( 0.0, e ) ).a - VLG_TEX( vlgStoneA, tuv - float2( 0.0, e ) ).a;
	float pEdge = ( 1.0 - smoothstep( 0.0, 0.03, abs( pf - 0.315 ) ) ) * isPlaster;

	float3 stoneCol = A.rgb * lerp( float3( 1.0, 1.0, 1.0 ), aTint * 1.1, 0.15 );
	float3 plasterCol = aTint * ( 1.0 - PA.r * 0.3 ) * ( 1.0 - PA.b * 0.25 ) * ( ( PA.a - 0.5 ) * 0.16 + 1.0 );
	plasterCol = plasterCol * lerp( 1.0, PN.a, 0.6 ) * ( ( PN.b - 0.5 ) * 0.12 + 1.0 );
	float3 col = lerp( stoneCol * lerp( 1.0, N.a, 0.5 ), plasterCol, pm );
	// sand dust and splash dirt near the ground
	float low = 1.0 - smoothstep( 0.05, 0.75, uvS.y );
	float dust = low * smoothstep( 0.25, 0.7, PA.a ) * 0.8;
	col = lerp( col, float3( 0.46, 0.4, 0.3 ), dust );
	col = col * ( 1.0 - low * 0.18 );

	float2 sl = lerp( vlmSlopeOf( N ), vlmSlopeOf( PN ) * 2.5, pm ) + float2( gX, gY ) * pEdge * - 6.0;
	s.normal = VillageNormalFromSlope( i.P, i.N, i.uv, sl );
	s.albedo = col;
	s.roughness = clamp( lerp( N.b, PN.b * 0.3 + 0.66, pm ) + dust * 0.05, 0.05, 1.0 );
	s.ao = lerp( N.a, PN.a, pm );
}

// ---------------------------------------------------------------------------
// FABRIC: laundry / tarps, knotted fishing nets and pennant flags in one double sided material (one draw call).
// vdata: x seed, y sway weight (flag: distance from the pole), z mode:
//   z = 0          cloth
//   0 < z < 5000   net, z = mesh size (m)
//   z > 5000       flag, z = pole x + 10000, w = pole z (the cloth swings downwind on the GPU)

void VillageFabric( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float isFlag = step( 5000.0, aData.z );
	float isNet = step( 0.001, aData.z ) * ( 1.0 - isFlag );
	float2 uvm = i.uv;

	// net
	float tile = max( aData.z, 0.02 ) * 4.0;
	float2 nuv = uvm / tile;
	float4 Nt = VLG_TEX( vlgNet, nuv );
	float2 fwn = fwidth( nuv );
	float farK = smoothstep( 0.05, 0.1, max( fwn.x, fwn.y ) );
	float2 sc = i.pixel;
	float ign = frac( frac( sc.x * 0.06711056 + sc.y * 0.00583715 ) * 52.9829189 );
	float netAlpha = lerp( Nt.r, step( ign, 0.55 ), farK );
	float3 netCol = aTint * lerp( 0.5, 1.05, Nt.g ) * lerp( 1.0, 0.8, farK );
	float2 nn = Nt.ba * 2.0 - 1.0;
	float2 netS = nn / sqrt( max( 1.0 - dot( nn, nn ), 0.04 ) ) * ( 1.0 - farK );

	// cloth: woven canvas, sun faded
	float weave = vlmN01( sin( uvm.x * 900.0 ) ) * vlmN01( sin( uvm.y * 900.0 ) );
	float wf = 1.0 - smoothstep( 0.0005, 0.002, fwidth( uvm.x ) );
	float fadeN = VLG_TEX( vlgGrime, uvm * 0.8 + aData.x ).a;
	float3 clothCol = lerp( aTint, aTint * 0.6 + float3( 0.3, 0.3, 0.3 ), fadeN * 0.5 ) * ( weave * 0.12 * wf + 0.94 );

	// flag normal (the cloth swings downwind around its pole, see the vertex animation); sim space -> Unity world (z mirrored)
	float a = aData.y;
	float3 dir = normalize( float3( i.wind.x, 0.0, i.wind.y ) );
	float3 perp = float3( - dir.z, 0.0, dir.x );
	float strength = smoothstep( 0.5, 9.0, i.wind.z );
	float phase = i.time * 7.5 - a * 5.0 + aData.x * 30.0;
	float slopeW = cos( phase ) * 0.45 * ( a + 0.2 ) * ( strength * 0.7 + 0.3 );
	float3 flagN = normalize( perp - dir * slopeW ) * ( i.front ? 1.0 : - 1.0 );
	flagN.z = - flagN.z;

	s.normal = normalize( lerp( VillageNormalFromSlope( i.P, i.N, i.uv, netS * isNet ), flagN, isFlag ) );
	s.alpha = lerp( 1.0, netAlpha, isNet );
	s.albedo = lerp( lerp( clothCol, netCol, isNet ), aTint * ( fadeN * 0.15 + 0.88 ), isFlag );
}

// ---------------------------------------------------------------------------
// NETS: blended, no depth write. Knotted mesh up close (soft-edged strands from the net texture);
// further away the strands average into a see-through veil of the right coverage. Alpha testing
// (or dithering) the sub-pixel strands wrote a noisy foreground depth over whatever is behind the net,
// and the TAA reprojected the background with it: grain and ghosted houses behind every net.
// vdata as for FABRIC nets: x seed, y sway weight, z mesh size (m).

void VillageNet( VillageIn i, inout VillageOut s )
{
	float3 aTint = i.tint;
	float4 aData = i.vdata;
	float2 uvm = i.uv;
	float tile = max( aData.z, 0.02 ) * 4.0;
	float2 nuv = uvm / tile;
	float4 Nt = VLG_TEX( vlgNet, nuv );
	float2 fwn = fwidth( nuv );
	float farK = smoothstep( 0.05, 0.1, max( fwn.x, fwn.y ) );
	float2 nn = Nt.ba * 2.0 - 1.0;
	float2 netS = nn / sqrt( max( 1.0 - dot( nn, nn ), 0.04 ) ) * ( 1.0 - farK );

	s.normal = normalize( VillageNormalFromSlope( i.P, i.N, i.uv, netS ) );
	// strands up close; average coverage of the knotted mesh (~0.4) once they are sub-pixel
	s.alpha = lerp( Nt.r, 0.4, farK ) * 0.95;
	s.albedo = aTint * lerp( 0.5, 1.05, Nt.g ) * lerp( 1.0, 0.85, farK );
}

// kind: 0 wood, 1 hard, 2 roofMetal, 3 thatch, 4 stone, 5 fabric, 6 net (a compile time constant, see VillageFragment.hlsl)
VillageOut VillageSurface( int kind, VillageIn i )
{
	VillageOut s;
	s.albedo = i.tint;
	s.roughness = 0.85;
	s.metalness = 0.0;
	s.ao = 1.0;
	s.normal = i.N;
	s.emissive = float3( 0, 0, 0 );
	s.alpha = 1.0;

	if ( kind == 0 ) { s.roughness = 0.85; VillageWood( i, s ); }
	else if ( kind == 1 ) { s.roughness = 0.5; VillageHard( i, s ); }
	else if ( kind == 2 ) { s.roughness = 0.5; VillageRoofMetal( i, s ); }
	else if ( kind == 3 ) { s.roughness = 0.95; VillageThatch( i, s ); }
	else if ( kind == 4 ) { s.roughness = 0.92; VillageStone( i, s ); }
	else if ( kind == 5 ) { s.roughness = 0.88; VillageFabric( i, s ); }
	else { s.roughness = 0.9; VillageNet( i, s ); }

	return s;
}

#endif
