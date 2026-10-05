// Trees and shrubs: the near leaf-card crowns and bark, and the octahedral impostors of the far ones (src/world/vegetation/VegMaterials.js canopyDeform / canopyModule /
// CANOPY_MASK, Impostors.js). Sim frame (see VegCommon.hlsl).
//
// aVeg = ( height fraction, branch flex, flutter weight, phase ); iDat = ( yaw, vertical scale ( negative: shrub ), plant height ( m ), seed );
// aMat = ( part, canopy exposure ( ao ), colour random, card random ); parts: 0 bark, 1 tree card, 4 shrub card, 5 shrub stem.

#ifndef VEG_CANOPY_INCLUDED
#define VEG_CANOPY_INCLUDED

TEXTURE2D( _VegLeafAtlas ); SAMPLER( sampler_VegLeafAtlas );

// leaf cluster tile ( 0..3 ) at the card's uv st -> ( coverage, bright / 1.4, cell, 1 ). The atlas has v up like the card (LeafAtlas bake)
float4 vegLeafSample( float2 st, float tile )
{
	float2 tuv = ( float2( frac( tile * 0.5 ) * 2.0, floor( tile * 0.5 ) ) + clamp( st, 0.004, 0.996 ) ) * 0.5;
	return SAMPLE_TEXTURE2D( _VegLeafAtlas, sampler_VegLeafAtlas, tuv );
}

#if defined(VEG_CANOPY)

struct VegCanopyOut { float3 pos; float3 normal; };

VegCanopyOut vegCanopyDeform( float3 p, float3 n, float4 iPos, float4 iDat, float4 ext, float4 veg, float4 aMat, float4 aLobe )
{
	float3 base = iPos.xyz;
	float sc = iPos.w;
	float Hh = iDat.z;
	float seed = iDat.w;
	float hf = veg.x; float flex = veg.y; float flut = veg.z; float ph = veg.w;
	float3 P = vegInstanceP( iPos, ext, p );
	bool isShrubI = iDat.y < 0.0;
	// merged tree + shrub geometry: keep the parts of this instance's plant type
	bool isShrubPart = aMat.x > 3.5;
	float keep = isShrubPart == isShrubI ? 1.0 : 0.0;
	// leaf cards move towards their lobe centre by the variant's lobe scale (0: dropped lobe)
	float lk = aLobe.w >= 0.0 ? 1.0 - vegLobeScale( seed, aLobe.w, isShrubI ) : 0.0;
	float3 ld = aLobe.xyz * lk;
	float yaw = iDat.x; float cy = cos( yaw ); float sy = sin( yaw );
	float sv = abs( iDat.y );
	P += float3( ld.x * cy + ld.z * sy, ld.y * sv, ld.z * cy - ld.x * sy ) * sc;
	float3 N = vegInstanceN( iPos, ext, n );
	float w = vegWindStrength();
	float g = vegGustAt( base.xz );
	float t = _TWFrame.x;
	float ph0 = seed * 6.2832;
	float h2 = hf * hf;
	float sway = ( w * w * 0.009 * ( g * 0.8 + 0.3 ) + sin( t * 0.9 + ph0 ) * w * 0.0045 * ( g + 0.4 ) ) * Hh * h2;
	float swayP = sin( t * 0.67 + ph0 * 1.3 ) * w * 0.002 * Hh * h2;
	float branch = sin( t * ( ph + 1.7 ) + ph * 20.0 + ph0 ) * flex * w * sc * 0.07 * ( g + 0.5 )
		- flex * w * w * sc * 0.04 * ( g + 0.3 ); // branches sag / stream in strong gusts
	float flutter = sin( t * 9.5 + ph * 50.0 + P.x * 1.9 + P.z * 2.3 ) * flut * sc * ( w * 0.035 + 0.005 );
	float3 pos = P + vegWindDir3() * sway + vegWindPerp3() * swayP + VEG_UP * branch + N * flutter;
	// near LOD: trees and shrubs hand over to the impostors at their own distance
	float dCam = length( _VegCam.xyz - base );
	float nearK = dCam < ( isShrubI ? _VegNear.y : _VegNear.x ) * ( 1.0 + VEG_LOD_BAND / 2.0 ) ? 1.0 : 0.0;
	VegCanopyOut o;
	o.pos = base + ( pos - base ) * ( keep * nearK );
	o.normal = N;
	return o;
}

#endif // VEG_CANOPY

// Species per instance ( seed ): trees 0 dark glossy ( bronze new flush ), 1 mid green, 2 yellow-green, 3 blue-green; shrubs 0 sea grape ( round leaves, red veins ),
// 1 croton ( variegated ), 2 hibiscus ( flowering ). The far impostors use the same palette ( vegCanopyLeafColor ) and brightness structure.
float3 vegPick4( float s4, float3 a, float3 b, float3 c, float3 d ) { return s4 < 0.5 ? a : ( s4 < 1.5 ? b : ( s4 < 2.5 ? c : d ) ); }
float3 vegPick3( float s3, float3 a, float3 b, float3 c ) { return s3 < 0.5 ? a : ( s3 < 1.5 ? b : c ); }
float vegTreeSpecies( float seed ) { return floor( frac( seed * 5.31 ) * 4.0 ); }
// shrubs: 0 sea grape 45 %, 1 croton 15 %, 2 hibiscus 40 %
float vegShrubSpecies( float seed )
{
	float h = frac( seed * 3.17 );
	return h < 0.45 ? 0.0 : ( h < 0.6 ? 1.0 : 2.0 );
}

// base leaf colour of an instance ( species, per-card random cr, per-instance tint )
float3 vegCanopyLeafColor( float seed, float cr, bool isShrub )
{
	float spT = vegTreeSpecies( seed ); float spS = vegShrubSpecies( seed );
	// tree species: dark glossy ( bronze flush ), fresh mid green, yellow-green, blue-green ( Caribbean hillside forest: deep, olive and yellow-greens, muted, with a
	// few dry / bronze and flowering crowns )
	float3 t0 = lerp( lerp( vegC( 0x283a1bu ), vegC( 0x364a23u ), cr ), vegC( 0x5e4a2eu ), vsm( 0.96, 0.995, cr ) * 0.5 );
	float3 t1 = lerp( vegC( 0x34491fu ), vegC( 0x485c27u ), cr );
	float3 t2 = lerp( vegC( 0x4f5a27u ), vegC( 0x646a31u ), cr );
	float3 t3 = lerp( vegC( 0x2a3b2au ), vegC( 0x3a4a36u ), cr );
	float3 s0 = lerp( vegC( 0x3f5522u ), vegC( 0x52662au ), cr );
	float3 s1 = lerp( vegC( 0x2c421eu ), vegC( 0x44561fu ), cr );
	float3 s2 = lerp( vegC( 0x34521cu ), vegC( 0x466624u ), cr );
	float iv = vegHash12( float2( seed * 17.3, 4.1 ) );
	float iv2 = vegHash12( float2( seed * 5.9, 8.3 ) );
	float3 c = ( isShrub ? vegPick3( spS, s0, s1, s2 ) : vegPick4( spT, t0, t1, t2, t3 ) ) * ( iv * 0.36 + 0.74 );
	// a few trees dry / dropping leaves ( brown-olive ), or flowering ( flamboyant, orange-red )
	float treeK = isShrub ? 0.0 : 1.0;
	float dry = step( iv2, 0.035 ) * treeK;
	c = lerp( c, lerp( vegC( 0x5c5234u ), vegC( 0x6e5a3au ), cr ), dry * 0.5 );
	float flower = step( 0.988, iv2 ) * treeK * step( 0.5, cr );
	c = lerp( c, vegC( 0x8a4a2cu ), flower * 0.5 );
	return lerp( float3( vlum( c ), vlum( c ), vlum( c ) ), c, 0.85 );
}

float3 vegBarkColor( float n, float n2 )
{
	return lerp( lerp( vegC( 0x302a22u ), vegC( 0x5c5549u ), n * 0.6 + n2 * 0.4 ), vegC( 0x7b7b6au ), vsm( 0.64, 0.8, n2 ) * 0.3 );
}

// leaf-cluster tile of a card: trees broad / narrow leaves by species, shrubs round / narrow
float vegLeafTile( float seed, bool isShrub )
{
	float spT = vegTreeSpecies( seed ); float spS = vegShrubSpecies( seed );
	return isShrub ? ( spS == 1.0 ? 3.0 : 2.0 ) : ( frac( spT * 0.5 ) > 0.25 ? 1.0 : 0.0 );
}

// alpha-test threshold compensating the coverage loss of the minified ( mipmapped ) leaf texture
float vegCoverageThreshold( float2 st )
{
	float lod = log2( max( max( fwidth( st.x ), fwidth( st.y ) ) * 256.0, 1e-4 ) );
	return lerp( 0.5, 0.3, vsat( lod / 4.0 ) );
}

// Runtime colour of an impostor fragment ( same palette as the near canopy )
float3 vegImpostorColor( float seed, float cr, float leaf, float bright, bool isGroup1 )
{
	float3 leafC = vegCanopyLeafColor( seed, cr, isGroup1 ) * ( bright * 1.4 );
	// limbs and twigs seen through the crown gaps are in the crown's shade: dark and a little green-brown, and the leaves dominate the blend
	float3 barkC = lerp( vegC( 0x1c1a13u ), vegC( 0x2e2b20u ), ( bright - 0.4 ) / 0.5 );
	float3 c = lerp( barkC, leafC, vsm( 0.05, 0.55, leaf ) );
	// far crowns keep their green through the haze ( a little more saturated than the near canopy )
	return max( lerp( float3( vlum( c ), vlum( c ), vlum( c ) ), c, 1.25 ), 0.0 );
}

#if defined(VEG_IMPOSTOR)

#define VEG_OCT_N 6.0
#define VEG_IMP_BLEND_DIST 100.0
#define VEG_IMP_THIN_0 140.0
#define VEG_IMP_THIN_1 320.0
#define VEG_IMP_THIN_FRACTION 0.5
#define VEG_IMP_SHRUB_MAX 180.0

TEXTURE2D( _VegImpA ); SAMPLER( sampler_VegImpA );
TEXTURE2D( _VegImpB ); SAMPLER( sampler_VegImpB );
// the groups' frames: ( centre y, radius, rh, hv ) of trees and shrubs, and where their variants start in the atlas
float4 _VegGroup0; float4 _VegGroup1; float4 _VegImpInfo; // ( variant base of group 1, number of cells, 0, 0 )

float3 vegOctDecode( float u, float v )
{
	float x = ( u - v ) * 0.5; float z = ( u + v ) * 0.5;
	return normalize( float3( x, 1.0 - abs( x ) - abs( z ), z ) );
}
float2 vegOctEncode( float3 d )
{
	float3 p = d / ( abs( d.x ) + abs( d.y ) + abs( d.z ) );
	return float2( p.x + p.z, p.z - p.x );
}

// frame sampler: the frame's direction, the view ray re-projected on its plane
void vegImpSample( float2 ij, float3 O, float3 D, float variant, float Rf, out float4 sA, out float4 sB )
{
	float3 d = vegOctDecode( ij.x / ( VEG_OCT_N - 1.0 ) * 2.0 - 1.0, ij.y / ( VEG_OCT_N - 1.0 ) * 2.0 - 1.0 );
	float3 right = normalize( cross( VEG_UP, d ) );
	float3 up = cross( d, right );
	float t = - dot( O, d ) / dot( D, d );
	float3 P = O + D * t;
	float a = dot( P, right ) / Rf; float b = dot( P, up ) / Rf;
	float cu = variant * VEG_OCT_N + ij.x + ( a * 0.5 + 0.5 );
	float cv = ij.y + ( b * 0.5 + 0.5 );
	bool inCell = abs( a ) < 1.0 && abs( b ) < 1.0;
	// the atlas has v up like the bake's view
	float2 st = float2( cu / _VegImpInfo.y, cv / VEG_OCT_N );
	float k = inCell ? 1.0 : 0.0;
	sA = SAMPLE_TEXTURE2D( _VegImpA, sampler_VegImpA, st ) * k;
	sB = SAMPLE_TEXTURE2D( _VegImpB, sampler_VegImpB, st ) * k;
}

#endif // VEG_IMPOSTOR

#endif // VEG_CANOPY_INCLUDED
