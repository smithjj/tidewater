// The shared blocks of the vegetation shaders (src/world/vegetation/VegNodes.js): hashing, value noise, wind and gusts, the plant deformation, the level-of-detail window
// and fade, the leaf translucency. HLSL of the WGSL, line for line.
//
// Space: everything here is the browser's frame (sim: x east, y up, z south). The vertex stage works in it and mirrors the world position and normal to Unity's
// (z -> -z) when it writes them; the surface mirrors the view vector and the normal back before it computes. MIRROR3 does both.
//
// Per draw (set by VegetationView): _VegInst = 3 float4 per instance ( iPos = base xyz + scale, iDat = lean azimuth / yaw, lean / vertical scale (negative: shrub),
// stem height H, seed; ext = yaw, vertical stretch ), _VegList = the instances of the draw, _VegLod = ( visible from, fade-out start, fade-out end ), _VegCam =
// the main camera (sim), _VegGust = the integrated gust offset (xz), _VegNear = ( tree, shrub ) hand-over distance to the impostors.

#ifndef VEG_COMMON_INCLUDED
#define VEG_COMMON_INCLUDED

StructuredBuffer<float4> _VegInst;
StructuredBuffer<uint> _VegList;
float4 _VegLod;
float4 _VegCam;
float4 _VegGust;
float4 _VegNear;
float4 _VegLobe[ 10 ]; // the lobe table (5 variants x 8 lobes), 4 per float4

TEXTURE2D( _TWDetailTex ); SAMPLER( sampler_TWDetailTex );

#define VEG_UP float3( 0.0, 1.0, 0.0 )
#define VEG_LOD_BAND 0.12
#define VEG_FERN_FADE_0 42.0
#define VEG_FERN_FADE_1 58.0
#define VEG_TREE_VARIANTS 3.0
#define VEG_SHRUB_VARIANTS 2.0

float3 vegM( float3 v ) { return float3( v.x, v.y, - v.z ); }

// WGSL smoothstep (reversed edges allowed)
float vsm( float a, float b, float x ) { float t = saturate( ( x - a ) / ( b - a ) ); return t * t * ( 3.0 - 2.0 * t ); }
float vsat( float x ) { return saturate( x ); }
float vlum( float3 c ) { return dot( c, float3( 0.2126, 0.7152, 0.0722 ) ); }

// the linear colour of an sRGB hex (three's new Color( hex ))
float3 vegC( uint hex )
{
	float3 c = float3( ( hex >> 16 ) & 255u, ( hex >> 8 ) & 255u, hex & 255u ) / 255.0;
	return lerp( pow( c * 0.9478672986 + 0.0521327014, 2.4 ), c * 0.0773993808, step( c, 0.04045 ) );
}

// Dave Hoskins' sine-free hash, [0, 1)
float vegHash12( float2 p )
{
	float3 p3 = frac( float3( p.x, p.y, p.x ) * 0.1031 );
	p3 += dot( p3, p3.yzx + 33.33 );
	return frac( ( p3.x + p3.y ) * p3.z );
}

float vegNoise( float2 p )
{
	float2 i = floor( p );
	float2 fr = frac( p );
	float2 u = fr * fr * ( fr * - 2.0 + 3.0 );
	float a = vegHash12( i );
	float b = vegHash12( i + float2( 1.0, 0.0 ) );
	float c = vegHash12( i + float2( 0.0, 1.0 ) );
	float d = vegHash12( i + float2( 1.0, 1.0 ) );
	return lerp( lerp( a, b, u.x ), lerp( c, d, u.x ), u.y );
}

// Bayer 4 x 4 threshold in (0, 1), shifted by a different offset every frame (LODFade.js bayer4)
float vegBayer4( float2 pixel )
{
	uint f = ( uint ) _FrameCount;
	uint2 p = uint2( pixel ) + uint2( f * 3u, ( f >> 2u ) * 1u );
	uint x0 = p.x & 1u; uint x1 = ( p.x >> 1u ) & 1u;
	uint y0 = p.y & 1u; uint y1 = ( p.y >> 1u ) & 1u;
	uint v = ( ( x0 ^ y0 ) << 3u ) | ( y0 << 2u ) | ( ( x1 ^ y1 ) << 1u ) | y1;
	return ( float( v ) + 0.5 ) / 16.0;
}

// Wind ---------------------------------------------------------------------------------------

// 0 (calm) .. 2.5 (storm). Tiny floor so nothing is ever perfectly frozen.
float vegWindStrength() { return max( _TWFrame.y * 0.1, 0.03 ); }
float3 vegWindDir3() { return float3( _TWWind.x, 0.0, _TWWind.y ); }
float3 vegWindPerp3() { return float3( - _TWWind.y, 0.0, _TWWind.x ); }

// travelling gust field in [0, 1] (two fetches of the detail texture's fbm channel)
float vegGustAt( float2 xz )
{
	float2 p = xz - _VegGust.xy;
	float n = SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, p / 140.0, 0.0 ).w * 0.62
		+ SAMPLE_TEXTURE2D_LOD( _TWDetailTex, sampler_TWDetailTex, p / 61.0 + 0.37, 0.0 ).w * 0.38;
	return vsm( 0.46, 0.6, n );
}

// Rotation taking +Y to the unit vector T, applied to v.
float3 vegRotUpTo( float3 v, float3 T )
{
	float3 k = float3( T.z, 0.0, - T.x );
	float3 c1 = cross( k, v );
	return v + c1 + cross( k, c1 ) / ( T.y + 1.0 );
}

// the instance matrix T * Ry( yaw ) * S( s, s * stretch, s ) of an instance (InstanceLOD.js VegInstances), applied to a point and, as three's instance node does it, to
// a normal (not normalised: its length scales the leaf flutter the same way)
float3 vegInstanceP( float4 iPos, float4 ext, float3 p )
{
	float c = cos( ext.x ), s = sin( ext.x );
	float3 q = p * float3( iPos.w, iPos.w * ext.y, iPos.w );
	return iPos.xyz + float3( q.x * c + q.z * s, q.y, - q.x * s + q.z * c );
}

float3 vegInstanceN( float4 iPos, float4 ext, float3 n )
{
	float c = cos( ext.x ), s = sin( ext.x );
	float3 q = n / float3( iPos.w, iPos.w * ext.y, iPos.w );
	return float3( q.x * c + q.z * s, q.y, - q.x * s + q.z * c );
}

// Crown variants: the per-instance variant picks a row of the lobe table (per-lobe size, 0 = dropped)
float vegVariantOf( float seed, bool isShrub )
{
	return floor( frac( seed * 7.77 ) * ( isShrub ? VEG_SHRUB_VARIANTS : VEG_TREE_VARIANTS ) );
}

float vegLobeScale( float seed, float li, bool isShrub )
{
	float row = vegVariantOf( seed, isShrub ) + ( isShrub ? VEG_TREE_VARIANTS : 0.0 );
	int i = ( int )( row * 8.0 + max( li, 0.0 ) );
	return _VegLobe[ i / 4 ][ i % 4 ];
}

// LOD window factor for an instance at base (0 = hidden, 1 = full size); lodRange = ( visible from, fade-out start, fade-out end )
float vegLodScale( float3 base, float3 lodRange )
{
	float d = length( _VegCam.xyz - base );
	bool inside = d >= lodRange.x * ( 1.0 - VEG_LOD_BAND / 2.0 );
	bool hardEnd = lodRange.z - lodRange.y < 0.05;
	float endK = hardEnd ? ( d < lodRange.y * ( 1.0 + VEG_LOD_BAND / 2.0 ) ? 1.0 : 0.0 ) : 1.0 - vsm( lodRange.y, lodRange.z, d );
	return inside ? endK : 0.0;
}

// fragment-stage keep test of the LOD cross-fade for an instance at base (true: keep the pixel)
bool vegLodDither( float3 base, float3 lodRange, float2 pixel )
{
	float d = length( _VegCam.xyz - base );
	float t = vegBayer4( pixel );
	float x = lodRange.x; float y = lodRange.y;
	float fadeIn = x > 0.0 ? vsm( x * ( 1.0 - VEG_LOD_BAND / 2.0 ), x * ( 1.0 + VEG_LOD_BAND / 2.0 ), d ) : 1.0;
	float fadeOut = lodRange.z - y < 0.05 ? vsm( y * ( 1.0 - VEG_LOD_BAND / 2.0 ), y * ( 1.0 + VEG_LOD_BAND / 2.0 ), d ) : 0.0;
	return t < fadeIn && t >= fadeOut;
}

// Leaf translucency: sunlight transmitted through the leaf when it is lit from behind (relative to the viewer). P, N in the sim frame.
float3 vegTranslucency( float3 albedo, float3 N, float strength, float3 P )
{
	float3 V = normalize( _VegCam.xyz - P );
	float3 L = _TWSunDir.xyz;
	float back = vsat( dot( - N, L ) );
	float fwd = pow( vsat( dot( - V, L ) ), 3.0 ) * 0.7 + 0.3;
	float3 tint = albedo * float3( 1.25, 1.45, 0.55 ) + float3( 0.012, 0.018, 0.0 );
	return tint * ( back * fwd * strength ) * ( 1.0 - _TWFrame.z );
}

// what a body hands to the surface function
struct VegOut { float3 albedo; float3 normal; float roughness; float ao; };

struct VegPlant
{
	float3 pos;
	float3 normal;
	float trunkY;  // height along the stem (m)
	float3 trunkT; // stem axis (world)
};

// Plant deformation shared by palms / young palms / bananas / ferns / broadleaf plants.
//
// Geometry conventions (local space, applied after the instance matrix = T * Ry * S):
//   aMat.x == 0  -> stem vertex: aVeg.x = height fraction u, x/z = radial offset.
//   aMat.x >= 1  -> crown vertex: position is relative to the crown centre.
//   aVeg = ( u, s along frond, flutter weight, phase )
// Per instance: iPos = ( base.xyz, scale ), iDat = ( lean azimuth, lean ( fraction of H ), stem height H ( m ), seed )
// P / N0: the vertex through the instance matrix
VegPlant vegPlantDeform( float3 P, float3 N0, float4 iPos, float4 iDat, float4 veg, float4 aMat, float4 aLobe, float3 lodRange )
{
	float part = aMat.x;
	float3 base = iPos.xyz;
	float sc = iPos.w;
	float3 leanDir = float3( cos( iDat.x ), 0.0, sin( iDat.x ) );
	float lean = iDat.y;
	float H = iDat.z;
	// merged geometries (understory): the instance's plant kind is the integer part of the seed;
	// vertices of the other plants collapse onto the base
	float seed = frac( iDat.w );
	float kindI = floor( iDat.w );
	float kindV = - aLobe.w - 1.0;
	bool keepKind = kindV < 0.5 || abs( kindV - kindI ) < 0.5;

	float u = veg.x;
	float s = veg.y;
	float flut = veg.z;
	float ph = veg.w;

	float t = _TWFrame.x;
	float w = vegWindStrength();
	float g = vegGustAt( base.xz );
	float ph0 = seed * 6.2832;
	float3 windDir3 = vegWindDir3();
	float3 windPerp3 = vegWindPerp3();

	// stem sway: horizontal offset of the top as a fraction of H
	float sway = w * w * 0.014 * ( g * 0.9 + 0.3 ) + sin( t * 0.83 + ph0 ) * w * 0.0065 * ( g + 0.45 );
	float swayP = sin( t * 0.61 + ph0 * 1.7 ) * w * 0.0028;
	float3 windOff = windDir3 * sway + windPerp3 * swayP;

	// stem curve: mix of a straight tilt and a "banana" curve (vertical at the top)
	float c = frac( seed * 7.31 );
	float fc = lerp( u, u * ( ( 1.0 - u ) + 1.0 ), c );
	float df = lerp( 1.0, ( 1.0 - u ) * 2.0, c );
	float3 off = leanDir * ( lean * fc ) + windOff * ( u * u );
	float3 T = normalize( VEG_UP + leanDir * ( lean * df ) + windOff * ( u * 2.0 ) );

	float3 radial = float3( P.x - base.x, 0.0, P.z - base.z );
	float3 axisPt = base + float3( 0.0, u * H, 0.0 ) + off * H;
	float3 stemPos = axisPt + vegRotUpTo( radial, T );
	float3 stemN = vegRotUpTo( N0, T );

	// crown: follows the stem top, tilted half as much as the stem tip
	float3 Ttop = normalize( VEG_UP + leanDir * ( lean * ( 1.0 - c ) ) + windOff * 2.0 );
	float3 Ttilt = normalize( VEG_UP + Ttop );
	float3 C = base + float3( 0.0, H, 0.0 ) + ( leanDir * lean + windOff ) * H;
	float3 o1 = vegRotUpTo( P - base, Ttilt );
	float3 N1 = vegRotUpTo( N0, Ttilt );

	// frond / leaf motion: gust bending + slow bounce + leaflet flutter, applied as a length preserving rotation about the crown centre so fronds stream downwind
	float s2 = s * s;
	float bend = s2 * sc * 4.5 * ( w * w * 0.16 * ( g * 0.8 + 0.35 )
		+ sin( t * ( ph * 0.5 + 1.3 ) + ph * 23.0 + ph0 ) * w * 0.075 * ( g + 0.3 ) );
	float bounce = sin( t * ( ph + 2.1 ) + ph * 41.0 ) * s2 * w * sc * 0.1;
	float flutter = flut * sc * ( sin( t * ( ph * 5.0 + 11.0 ) + ph * 60.0 + s * 9.0 ) * ( w * 0.045 + 0.008 )
		+ sin( t * 23.0 + ph * 13.0 + s * 17.0 ) * ( max( w - 1.0, 0.0 ) * 0.05 ) );
	float3 disp = windDir3 * bend + VEG_UP * ( bounce - bend * 0.3 ) + N1 * flutter;
	float L = length( o1 );
	float3 o2 = normalize( o1 + disp + float3( 0.0, 1e-5, 0.0 ) ) * L;
	float3 crownPos = C + o2;

	// the dead (hanging) frond is only kept on some palms
	bool hideDead = part > 0.5 && part < 1.5 && aMat.y > 0.95 && frac( seed * 13.7 ) > 0.4;
	float3 crownPos2 = hideDead ? C : crownPos;

	bool isStem = part < 0.5;
	float3 pos = isStem ? stemPos : crownPos2;

	VegPlant o;
	o.normal = isStem ? stemN : N1;
	o.trunkY = u * H;
	o.trunkT = T;

	// LOD window / distance fade: shrink around the base (ferns fade out earlier)
	float dCam = length( _VegCam.xyz - base );
	float fernFade = ( kindI > 2.5 && kindI < 3.5 ) ? 1.0 - vsm( VEG_FERN_FADE_0, VEG_FERN_FADE_1, dCam ) : 1.0;
	float k = vegLodScale( base, lodRange ) * fernFade * ( keepKind ? 1.0 : 0.0 );
	o.pos = base + ( pos - base ) * k;
	return o;
}

#endif // VEG_COMMON_INCLUDED
