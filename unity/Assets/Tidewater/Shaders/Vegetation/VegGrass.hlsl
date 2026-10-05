// The grass field's vertex stage (GrassField.js createGrassMaterial): the blade of a patch slot of a cell, grown from the terrain heights and the density mask, bent by the
// wind. Everything is in the SIM frame; the caller mirrors z. The varyings the surface needs ride in the vertex channels: color = ( hf, kind * 2 + centre, dune fraction, rnd ),
// uv0 = the detail fbm at the clump ( mA, mB: the meadow tone is recomputed in the fragment ), uv1 = ( across, gust ), uv2 = ( dead blade, 0 ).

#ifndef VEG_GRASS_INCLUDED
#define VEG_GRASS_INCLUDED

StructuredBuffer<float2> _GrassCells; // the cells ( x0, z0 ) of the draw
TEXTURE2D( _GrassMask ); SAMPLER( sampler_GrassMask ); // R dune grass, G meadow grass, B sea oats, A creeper (linear, clamp)
TEXTURE2D( _TWHeightTex ); // the terrain's heights, R32F, loaded and filtered by hand
float4 _GrassP; // ( origin, size, texel, res )

#define GR_FADE_T2 float2( 11.0, 17.0 )
#define GR_FADE_T1 float2( 36.0, 45.0 )
#define GR_FADE_T0 float2( 62.0, 87.0 )

// sRGB triple -> linear (TerrainShading.srgb)
float3 vegSrgb( float3 c ) { return lerp( pow( c * 0.9478672986 + 0.0521327014, 2.4 ), c * 0.0773993808, step( c, 0.04045 ) ); }

// the tropical meadow's tone, shared with the terrain's shading (TerrainShading.js MEADOW / meadowTone): mA, mB the detail fbm at the 173 m / 47 m scales
struct VegMeadow { float3 tone; float dry; float lush; };

VegMeadow vegMeadowTone( float mA, float mB, float slope, float south )
{
	float3 cLush = vegSrgb( float3( 0.13, 0.2, 0.05 ) ), cGreen = vegSrgb( float3( 0.25, 0.32, 0.1 ) ), cOlive = vegSrgb( float3( 0.36, 0.37, 0.14 ) );
	float3 cYellow = vegSrgb( float3( 0.5, 0.46, 0.2 ) ), cStraw = vegSrgb( float3( 0.62, 0.54, 0.33 ) );
	float m = mA * 0.55 + mB * 0.45 + slope * 0.25 + south * 0.04;
	float olive = vsm( 0.52, 0.6, m );
	float yellow = vsm( 0.6, 0.67, m );
	float straw = vsm( 0.66, 0.73, m + ( mB - 0.5 ) * 0.2 );
	float lush = vsm( 0.46, 0.37, mB * 0.7 + mA * 0.3 + slope * 0.2 );
	float3 c = lerp( cGreen, cOlive, olive );
	c = lerp( c, cYellow, yellow * 0.8 );
	c = lerp( c, cStraw, straw * 0.55 );
	c = lerp( c, cLush, lush * 0.75 );
	VegMeadow r;
	r.tone = c; r.dry = olive * 0.4 + yellow * 0.6; r.lush = lush;
	return r;
}

// the rotation of an xz vector by a constant angle (TerrainShading.rot2)
float2 vegRotXZ( float2 v, float a ) { float c = cos( a ), s = sin( a ); return float2( v.x * c - v.y * s, v.x * s + v.y * c ); }

// per-tier visibility at camera distance d ( 1 = full width, 0 = gone ); tier 0 thins out per blade
float vegGrassTierFade( float tier, float d, float cut )
{
	float f2 = 1.0 - vsm( GR_FADE_T2.x, GR_FADE_T2.y, d );
	float f1 = 1.0 - vsm( GR_FADE_T1.x, GR_FADE_T1.y, d );
	float f0 = 1.0 - vsm( cut - 5.0, cut, d );
	return tier > 1.5 ? f2 : ( tier > 0.5 ? f1 : f0 );
}

struct VegGrassOut { float3 pos; float3 normal; float4 col; float2 uv0; float2 uv1; float2 uv2; };

VegGrassOut vegGrassDeform( float3 P, float3 N, float4 side4, float4 slot, float4 blade, float2 cell )
{
	float3 side = side4.xyz;
	float2 xz = cell + slot.xy;
	float hf = blade.x;
	float kind = blade.y;
	float bRnd = blade.z;

	float4 m = SAMPLE_TEXTURE2D_LOD( _GrassMask, sampler_GrassMask, ( xz - _GrassP.x ) / _GrassP.y, 0.0 );
	bool isGrass = kind < 0.5;
	bool isOat = kind > 0.5 && kind < 2.5;
	float lush = m.g;
	float dune = m.r;
	// the backshore grass is dense in its clumps (the mask carries the clumping and the edge)
	float duneF = dune / ( dune + lush + 1e-3 );
	float grassP = lerp( lush, min( dune * 1.1, 1.0 ), duneF );
	float density = isGrass ? grassP : ( isOat ? m.b : m.a );
	float r = vegHash12( xz * 1.37 + 0.51 );
	float r2 = vegHash12( xz * 2.11 + 7.3 );
	float flowerOk = kind > 3.5 ? ( r2 < 0.35 ? 1.0 : 0.0 ) : 1.0;
	float present = ( r < density ? 1.0 : 0.0 ) * flowerOk;

	// the meadow tone at the clump: the same function and inputs as the terrain's meadow shading
	float mA = SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, vegRotXZ( xz, 0.7 ) / 173.0, 0.0 ).w;
	float mB = SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, vegRotXZ( xz, 2.1 ) / 47.0, 0.0 ).w;
	VegMeadow mt = vegMeadowTone( mA, mB, 0.0, 0.0 );
	// size: tall meadow grass, knee to waist high: swathes of taller grass in the lush patches, lower where it is dry; wiry dune tufts
	float patchN = vegNoise( xz * ( 1.0 / 6.5 ) + 17.3 ) * 0.7 + vegNoise( xz * ( 1.0 / 2.3 ) ) * 0.3;
	float lushH = lerp( 0.7, 1.3, patchN ) * ( mt.lush * 0.25 + 1.0 ) * ( 1.0 - mt.dry * 0.25 );
	// dune tufts vary a lot in size (young shoots to big old clumps)
	float grassH = lerp( lushH, 0.62, duneF ) * lerp( r2 * 0.35 + 0.83, r2 * r2 * 0.9 + 0.5, duneF ) * ( density * 0.35 + 0.65 );
	float oatH = r2 * 0.55 + 1.0;
	float vineS = r2 * 0.4 + 0.8;
	float hScale = isGrass ? grassH : ( isOat ? oatH : vineS );
	// meadow clumps fan out wider than the wiry dune tufts
	float spread = isGrass ? lerp( 1.35, 1.3, duneF ) : 1.0;

	// distance LOD: tiers fade out (narrow to nothing), the remaining blades widen so the coverage (blade density x width) stays constant; tier 0 thins out per blade near R_FAR
	float dist = length( xz - _VegCam.xz );
	// tier: grass blades and oats carry their own (aBlade.w), otherwise the slot's
	float tier = max( kind < 2.5 ? blade.w : 0.0, slot.w );
	float cutK = bRnd * 7.13 + r2;
	float cut = lerp( GR_FADE_T0.x + 5.0, GR_FADE_T0.y, cutK - floor( cutK ) );
	float own = vegGrassTierFade( tier, dist, cut );
	float f2 = 1.0 - vsm( GR_FADE_T2.x, GR_FADE_T2.y, dist );
	float f1 = 1.0 - vsm( GR_FADE_T1.x, GR_FADE_T1.y, dist );
	float comp = 7.0 / ( f2 * 3.0 + f1 * 2.0 + 2.0 );
	// oats and creeper simply shrink out
	float widthK = isGrass ? comp * own : 1.0;
	float sizeK = isGrass ? 1.0 : own;
	float scale = hScale * present * sizeK;

	// per-slot random yaw
	float yaw = vegHash12( xz * 0.73 + 3.3 ) * 6.2832;
	float cy = cos( yaw ), sy = sin( yaw );

	// the terrain height (float texels loaded and bilinearly filtered by hand)
	float2 hfp = ( xz - _GrassP.x ) / _GrassP.z - 0.5;
	float2 hfi = floor( hfp );
	float2 hfr = hfp - hfi;
	int2 ij = int2( clamp( hfi, 0.0, _GrassP.w - 2.0 ) );
	float ha = _TWHeightTex.Load( int3( ij, 0 ) ).x;
	float hb = _TWHeightTex.Load( int3( ij + int2( 1, 0 ), 0 ) ).x;
	float hc = _TWHeightTex.Load( int3( ij + int2( 0, 1 ), 0 ) ).x;
	float hd = _TWHeightTex.Load( int3( ij + int2( 1, 1 ), 0 ) ).x;
	float ground = lerp( lerp( ha, hb, hfr.x ), lerp( hc, hd, hfr.x ), hfr.y );
	float3 base = float3( xz.x, ground - 0.03, xz.y );
	float3 pl = float3( P.x * spread, P.y, P.z * spread );
	float3 o0 = float3( pl.x * cy - pl.z * sy, pl.y, pl.x * sy + pl.z * cy ) * scale;

	// wind: travelling gusts bend blades downwind (length preserving), plus flutter
	float w = vegWindStrength();
	float g = vegGustAt( xz );
	float tm = _TWFrame.x;
	float ph = r * 6.2832;
	float bendAmt = w * ( g * 0.55 + 0.22 ) + sin( tm * 1.9 + ph + xz.x * 0.2 ) * w * 0.1;
	float flut = sin( tm * 7.3 + ph * 3.0 + bRnd * 20.0 ) * ( w * 0.06 + 0.015 );
	float stiff = isGrass ? 1.0 : ( isOat ? 0.8 : 0.08 );
	float hf2 = hf * hf;
	float oL = length( o0 );
	float3 disp = ( vegWindDir3() * ( bendAmt * hf2 * stiff ) + vegWindPerp3() * ( flut * hf2 * stiff ) ) * ( oL + 1e-4 );
	float3 ob = normalize( o0 + disp + float3( 0.0, 1e-5, 0.0 ) ) * oL;

	float wScale = isGrass ? lerp( 1.5, 1.55, duneF ) : 1.0;
	float wide = wScale * widthK * min( scale * 2.0, max( scale, 0.5 ) );
	float3 sideR = float3( side.x * cy - side.z * sy, side.y, side.x * sy + side.z * cy );
	float3 pos = base + ob + sideR * wide;

	// the lighting normal: the blade normal bent towards up (soft, grass-like shading), rounded across the blade (a folded leaf is lit differently on its two halves)
	float across = side4.w;
	float3 sideDir = normalize( sideR + float3( 1e-5, 0.0, 0.0 ) );
	float3 nR = float3( N.x * cy - N.z * sy, N.y, N.x * sy + N.z * cy );

	// a share of the blades is dead / straw coloured (more in the dry patches)
	float dk = bRnd * 13.7 + r * 3.1;
	float dryBlade = ( dk - floor( dk ) < mt.dry * 0.3 + 0.07 ) ? 1.0 : 0.0;

	VegGrassOut o;
	o.pos = pos;
	o.normal = normalize( nR * 0.5 + VEG_UP + sideDir * ( across * 0.35 ) );
	float centre = kind > 2.5 ? blade.w : 0.0;
	o.col = float4( hf, kind * 2.0 + centre, duneF, bRnd );
	o.uv0 = float2( mA, mB );
	o.uv1 = float2( across, saturate( g * w * 0.6 ) * stiff );
	o.uv2 = float2( dryBlade, 0.0 );
	return o;
}

#endif // VEG_GRASS_INCLUDED
