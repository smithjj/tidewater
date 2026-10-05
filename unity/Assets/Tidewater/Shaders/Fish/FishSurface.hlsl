// The fish surface (src/world/fish/FishMaterial.js: fishModule, surface( prop = true )): HLSL of the WGSL, line for line. Procedural skin: counter-
// shading from the dark back to the pale belly, overlapping scales in rows (colour and relief, faded out when smaller than a pixel), the
// lateral line, the edge of the gill cover, silvery guanine reflection with an iridescent sheen, the species' markings, fin rays, eyes, the cut
// faces of the cut fish, the salted fillet, the crushed ice, the banana leaf and the spiny lobster's shell. Props only: dull drying skin, wet
// sheen (clear coat), salted skin. (The swimming wave of the schools, fishSwimOffset, and the ray / turtle skins come with Fish.js.)
//
// Per vertex (mesh): uv0 = aData.xy, uv1 = aData.zw, uv2 = rest position ( x, y ), uv3 = ( rest z, 0 ), in the fish's model frame (the same
// numbers as the JS: the Unity mesh is mirrored in z, the vertex stage and the surface mirror back). Per draw: _FishA = ( pattern + seed * 0.9,
// curl, sag, jaw ), _FishB = ( cloudy eye, wet, dried, blood ), _FishL = length (m), _FishFade = ( share, outgoing ) of the level-of-detail
// cross-fade.

#ifndef FISH_SURFACE_INCLUDED
#define FISH_SURFACE_INCLUDED

// species order = pattern ids (FishSpecies.cs PATTERN)
#define PT_SILVERSIDE 0.0
#define PT_CHROMIS 1.0
#define PT_GRUNT 2.0
#define PT_YELLOWTAIL 3.0
#define PT_TANG 4.0
#define PT_SERGEANT 5.0
#define PT_WRASSE 6.0
#define PT_PARROT 7.0
#define PT_ANGEL 8.0
#define PT_BARRACUDA 9.0
#define PT_REDSNAPPER 10.0
#define PT_GROUPER 11.0
#define PT_TUNA 12.0
#define PT_MAHI 13.0
#define PT_MULLET 14.0
#define PT_NEEDLEFISH 15.0
#define PT_JACK 16.0
#define PT_TARPON 17.0
#define PT_STINGRAY 18.0
#define PT_EAGLERAY 19.0
#define PT_TURTLE 20.0
#define FISH_PATTERNS 21
#define FISH_ROWS 8

// FishGeometry PART
#define PA_BODY 0.0
#define PA_DORSAL1 1.0
#define PA_DORSAL2 2.0
#define PA_ANAL 3.0
#define PA_CAUDAL 4.0
#define PA_PECTORAL 5.0
#define PA_PELVIC 6.0
#define PA_FINLET 7.0
#define PA_EYE 8.0
#define PA_MOUTH 9.0
#define PA_FLESH 10.0
#define PA_ICE 11.0
#define PA_LEAF 12.0
#define PA_SHELL 13.0
#define PA_FILLET 14.0
#define PA_DISC 15.0
#define PA_WHIP 16.0
#define PA_CARAPACE 17.0
#define PA_SKIN 18.0
#define PA_FLIPPER 19.0

// the skin table: 8 rows per species (FishMaterial.js buildTable), set by FishPropsView
float4 _FishSkin[ FISH_PATTERNS * FISH_ROWS ];

float4 fishRow( float pattern, int k ) { return _FishSkin[ clamp( ( int ) pattern, 0, FISH_PATTERNS - 1 ) * FISH_ROWS + k ]; }
float fishPartOf( float4 d ) { return floor( d.y + 0.01 ); }
float fishJawOf( float4 d ) { return max( frac( d.y + 0.01 ) - 0.01, 0.0 ) / 0.9; }

// smoothstep with the edges in either order (WGSL / GLSL: t = clamp( ( x - a ) / ( b - a ) ), then the cubic)
float sm( float a, float b, float x ) { float t = saturate( ( x - a ) / ( b - a ) ); return t * t * ( 3.0 - 2.0 * t ); }

float fishHash( float2 p ) { return frac( sin( dot( p, float2( 127.1, 311.7 ) ) ) * 43758.5453 ); }

// value noise 2D (cheap, for blotches and skin variation)
float fishVnoise( float2 p )
{
	float2 i = floor( p ); float2 f = frac( p );
	float2 w = f * f * ( 3.0 - f * 2.0 );
	float a = fishHash( i ); float b = fishHash( i + float2( 1.0, 0.0 ) ); float c = fishHash( i + float2( 0.0, 1.0 ) ); float d = fishHash( i + float2( 1.0, 1.0 ) );
	return lerp( lerp( a, b, w.x ), lerp( c, d, w.x ), w.y );
}

// posterior edge of the gill cover (local z) at height fraction h: convex backward, sweeping forward under the throat
float fishOpercleEdge( float zOp, float h ) { return zOp - 0.028 * ( 1.0 - h * h ) + sm( -0.35, -1.0, h ) * 0.07; }
float fishOpercleMask( float h ) { return sm( -0.98, -0.9, h ) * ( 1.0 - sm( 0.45, 0.62, h ) ); }

float fishBand( float x, float center, float width, float soft ) { return 1.0 - sm( width, width + soft, abs( x - center ) ); }

// eye colour: pupil, iris with radial streaks, dark rim
float3 fishEyeCol( float r, float ang, float3 irisC, float cloudy )
{
	float streak = sin( ang * 26.0 ) * 0.5 + 0.5;
	float irisL = dot( irisC, float3( 0.3, 0.59, 0.11 ) );
	float3 iris = irisC * min( 1.0, 0.36 / max( irisL, 1e-3 ) ) * lerp( 0.6, 1.05, streak ) * ( sm( 0.55, 0.72, r ) * 0.45 + 0.5 );
	float ring = sm( 0.8, 0.97, r );
	float3 e = lerp( iris, float3( 0.025, 0.025, 0.028 ), ring );
	e = lerp( e, float3( 0.004, 0.005, 0.007 ), 1.0 - sm( 0.5, 0.56, r ) );
	// cloudy eyes of fish out of the water for a while
	e = lerp( e, float3( 0.42, 0.44, 0.46 ), cloudy * 0.55 * ( 1.0 - sm( 0.7, 1.0, r ) ) );
	return e;
}

// Level-of-detail cross-fade (materials/LODFade.js): Bayer 4x4 threshold in (0, 1), shifted every frame. The incoming level keeps the cells
// below the fade, the outgoing one the others.
float fishBayer4( float2 pixel )
{
	uint f = ( uint ) _FrameCount;
	uint2 p = uint2( pixel ) + uint2( f * 3u, ( f >> 2u ) * 1u );
	uint x0 = p.x & 1u; uint x1 = ( p.x >> 1u ) & 1u;
	uint y0 = p.y & 1u; uint y1 = ( p.y >> 1u ) & 1u;
	uint v = ( ( x0 ^ y0 ) << 3u ) | ( y0 << 2u ) | ( ( x1 ^ y1 ) << 1u ) | y1;
	return ( float( v ) + 0.5 ) / 16.0;
}

bool fishLodVisible( float2 pixel, float fade, bool outgoing )
{
	float t = fishBayer4( pixel );
	return outgoing ? ( t >= fade ) : ( t < fade );
}

float fishIGN( float2 px ) { return frac( 52.9829189 * frac( dot( px, float2( 0.06711056, 0.00583715 ) ) ) ); }

// props: thin fin membranes let some light through: stochastic transparency, resolved by the temporal anti-aliasing (the rays stay opaque).
// Same in the depth, shadow and colour passes (one dither per frame).
bool fishPropKeep( float4 Dm, float2 pixel, float time )
{
	float partM = fishPartOf( Dm );
	bool finM = partM > 0.5 && partM < 6.5;
	float rayM = 1.0 - sm( 0.07, 0.14, abs( frac( Dm.w + 0.5 ) - 0.5 ) );
	float alphaM = finM ? max( lerp( 0.9, 0.55, sm( 0.25, 1.0, Dm.z ) ), rayM ) : 1.0;
	float dither = fishIGN( pixel + frac( time * 7.3 ) * 97.0 );
	return dither < alphaM;
}

// perturbNormalByHeight (common.js, Mikkelsen): dhdx / dhdy = derivatives of the height (m) at this pixel
float3 fishPerturbNormalByHeight( float3 P, float3 N, float dhdx, float dhdy, float strength )
{
	float3 dPdx = ddx( P );
	float3 dPdy = ddy( P );
	float3 r1 = cross( dPdy, N );
	float3 r2 = cross( N, dPdx );
	float det = dot( dPdx, r1 );
	float3 grad = sign( det ) * ( dhdx * r1 + dhdy * r2 ) * strength;
	return normalize( abs( det ) * N - grad );
}

struct FishIn
{
	float4 D;        // aData
	float3 Lp;       // rest position (model units)
	float4 I;        // pattern, seed, length (m), 0
	float4 flags;    // cloudy eye, wet, dried, blood
	float3 P;        // camera-relative world position
	float3 N;        // world normal
	float3 V;        // towards the viewer
	float3 Pview;    // view-space position (pixel footprint)
};

struct FishOut
{
	float3 albedo;
	float roughness;
	float metalness;
	float coat;
	float3 normal;
	float spec;
	float transl;
};

FishOut FishSurface( FishIn I_ )
{
	float4 D = I_.D; float3 Lp = I_.Lp; float4 I = I_.I;
	float pattern = floor( I.x + 0.5 );
	float part = fishPartOf( D );
	float scaleSize = fishRow( pattern, 4 ).w; float scaleVis = fishRow( pattern, 5 ).w;
	// scale rows: posterior margins are arcs, rows offset by half a scale
	float ss = max( scaleSize, 0.004 );
	// gentle waviness of the scale rows (in scale units: big scales stay in orderly rows)
	float warp = ( sin( Lp.z * 23.0 + D.z * 31.0 ) * 0.35 + sin( Lp.z * 41.0 - D.z * 17.0 ) * 0.25 ) * lerp( 1.0, 0.3, sm( 0.015, 0.045, scaleSize ) );
	float sa = ( 0.5 - Lp.z ) / ss + warp; float sb = D.z / ( ss * 0.8 ) + warp * 0.6;
	float rowI = floor( sb );
	float fb = frac( sb ) * 2.0 - 1.0;
	float sf = frac( sa + rowI * 0.5 + fb * fb * 0.32 );
	// pixel footprint (m) against the scale size (m): fade out sub-pixel detail
	float px = length( fwidth( I_.Pview ) );
	float sfade = ( 1.0 - sm( 0.25, 0.7, px / ( ss * I.z ) ) ) * ( scaleSize > 0.001 ? 1.0 : 0.0 );

	// ---- relief
	float bumpH = 0.0;
	{
		bool isBody0 = part == PA_BODY;
		float L0 = I.z;
		// scales: each rises toward its free posterior margin
		float sc = sm( 0.0, 0.9, sf ) * ( 1.0 - sm( 0.9, 1.0, sf ) ) * sfade * scaleVis;
		// gill cover: raised in front of its edge
		float4 eyeOp = fishRow( pattern, 6 );
		float h0 = D.w;
		float zE0 = fishOpercleEdge( eyeOp.w, h0 );
		float onOp0 = fishOpercleMask( h0 );
		float op = sm( -0.004, 0.004, Lp.z - zE0 ) * onOp0;
		float grainFade = 1.0 - sm( 0.3, 0.8, px / ( 0.004 * I.z ) );
		float grain = ( fishVnoise( float2( Lp.z, D.z ) * 420.0 ) - 0.5 ) * 0.00022 * grainFade;
		float bodyH = sc * 0.0016 + op * 0.0025 + grain;
		// fin rays: ridges
		bool isFin0 = part > 0.5 && part < 7.5;
		float rd0 = abs( frac( D.w + 0.5 ) - 0.5 );
		float ray0 = ( 1.0 - sm( 0.0, 0.25, rd0 ) ) * 0.0004;
		bumpH = ( isBody0 ? bodyH : ( isFin0 ? ray0 : 0.0 ) ) * L0;
	}
	float dhdx = ddx( bumpH ); float dhdy = ddy( bumpH );

	float rough = 0.4;
	float metal = 0.0;
	float coat = 0.0;
	float spec = 0.5;   // the JS specularIntensity: used by the portrait studio (FishStudio.shader); the world's HDRP lit keeps its fixed 4 %
	float transl = 0.0; // thin-surface translucency: only the fins' share is ported, only the portrait studio uses it
	float seed = I.y; float L = I.z;
	float pat = pattern;
	float P = part;
	float u = D.x; float h = D.w; float sd = D.z;
	float z = Lp.z; float y = Lp.y;
	float t = D.z; float w = D.w; // fins: along / across the rays
	bool isBody = P == PA_BODY;
	bool isFin = P > 0.5 && P < 7.5;
	float bodyK = isBody ? 1.0 : 0.0;
	float4 r0 = fishRow( pat, 0 ); float4 r1 = fishRow( pat, 1 ); float4 r2 = fishRow( pat, 2 ); float4 r3 = fishRow( pat, 3 );
	float4 r4 = fishRow( pat, 4 ); float4 r5 = fishRow( pat, 5 ); float4 r6 = fishRow( pat, 6 ); float4 r7 = fishRow( pat, 7 );
	float3 back = r0.xyz; float3 flank = r1.xyz; float3 belly = r2.xyz;
	float3 finC = r3.xyz; float3 edgeC = r4.xyz; float3 irisC = r5.xyz;
	float4 eye = r6; float4 lat = r7;
	float4 flags = I_.flags;
	float n1 = fishVnoise( float2( z, y ) * 38.0 + seed * 17.0 );
	float n2 = fishVnoise( float2( z, sd ) * 11.0 + seed * 5.0 );
	float fwW = fwidth( w ); float fwH = fwidth( h );

	// ---- counter-shading
	float tBack = sm( 0.2, 0.75, h );
	float tBelly = 1.0 - sm( -0.7, -0.1, h );
	float3 c = lerp( lerp( flank, back, tBack ), belly, tBelly );
	float silver = 1.0 - tBack * 0.75; // guanine reflection weight
	metal = r0.w * silver * bodyK;
	rough = r2.w;
	// scales: a thin shadow line under each free margin, the exposed field slightly brighter toward the margin
	float scaleShade = sm( 0.3, 0.9, sf ) * sfade * scaleVis;
	float pocket = sm( 0.88, 0.97, sf ) * ( 1.0 - sm( 0.97, 1.0, sf ) ) * sfade * scaleVis;
	float cellK = ( fishHash( float2( floor( sa + floor( sb ) * 0.5 ), floor( sb ) ) ) - 0.5 ) * 0.1 * sfade * scaleVis;
	// (on silvery skin the pocket is a thin line: the mirror-like scale reflects its own light)
	c *= lerp( 1.0, 0.96 + scaleShade * 0.07 - pocket * lerp( 0.14, 0.06, r0.w ) + cellK, bodyK );
	c *= lerp( 1.0, n1 * 0.14 + 0.93, bodyK );
	c *= lerp( 1.0, n2 * 0.2 + 0.9, bodyK );
	rough = lerp( rough, rough * lerp( 0.8, 1.25, n2 ), bodyK );

	// ---- fins: ray-striped membranes, darker and thinner toward the edge
	if ( isFin && P != PA_FINLET )
	{
		float3 fin = lerp( finC, edgeC, sm( 0.4, 1.0, t ) );
		float rd = abs( frac( w + 0.5 ) - 0.5 );
		float rayW = P == PA_DORSAL1 ? 0.1 : 0.06;
		float ray = ( 1.0 - sm( rayW, fwW * 1.2 + rayW + 0.04, rd ) ) * ( 1.0 - sm( 0.2, 0.6, fwW ) );
		fin *= lerp( 0.9, 1.06, ray );
		// thicker and darker where the fin joins the body, thinnest at the edge
		fin *= sm( 0.0, 0.15, t ) * 0.2 + 0.8;
		c = fin;
		transl = lerp( 0.8, 0.55, ray ) * ( sm( 0.0, 0.3, t ) * 0.4 + 0.6 );
		// paired fins: the fin colour, a little lighter toward the edge (thin membrane)
		bool paired = P == PA_PECTORAL || P == PA_PELVIC;
		c = paired ? c * lerp( 0.85, 1.1, sm( 0.2, 1.0, t ) ) : c;
		transl *= paired ? 0.45 : 1.0;
		rough = 0.4;
	}

	// ---- species markings (body; some on fins)
	if ( pat == PT_SILVERSIDE )
	{
		// silver lateral band with a dark upper edge; translucent green back
		float bandK = fishBand( h, 0.02, 0.1, fwH + 0.05 ) * bodyK;
		c = lerp( c, float3( 0.78, 0.82, 0.84 ), bandK * 0.8 );
		c = lerp( c, float3( 0.12, 0.2, 0.2 ), fishBand( h, 0.14, 0.015, fwH + 0.02 ) * bodyK * 0.6 );
		metal += bandK * 0.25;
	}
	else if ( pat == PT_CHROMIS )
	{
		// dark margins on the tail lobes, azure line from the snout through the eye
		float lobe = P == PA_CAUDAL ? sm( 5.5, 7.5, abs( w - 8.0 ) ) : 0.0;
		c = lerp( c, float3( 0.01, 0.015, 0.03 ), lobe );
		float lineK = fishBand( y - ( z - eye.x ) * 0.35, eye.y + 0.015, 0.004, 0.003 ) * sm( eye.x - 0.02, eye.x + 0.05, z ) * bodyK;
		c = lerp( c, float3( 0.3, 0.6, 0.95 ), lineK * 0.7 );
	}
	else if ( pat == PT_GRUNT )
	{
		// French grunt: yellow with oblique blue-silver stripes (straight above the lateral line); bluestriped grunt: straight blue
		// stripes. Red mouth.
		bool blue = frac( seed * 3.7 ) < 0.4;
		float above = sm( 0.35, 0.45, h );
		float slope = blue ? 0.0 : lerp( 0.45, 0.0, above );
		float sv = sin( ( y - z * slope ) * ( blue ? 190.0 : 150.0 ) );
		float stripe = sm( 0.45, 0.8, sv ) * bodyK * ( 1.0 - tBelly * 0.7 );
		float3 lineC = blue ? float3( 0.12, 0.26, 0.55 ) : float3( 0.52, 0.6, 0.7 );
		c = lerp( c, lineC, stripe * ( blue ? 0.9 : 0.7 ) );
	}
	else if ( pat == PT_YELLOWTAIL )
	{
		// yellow stripe from the snout widening into the yellow tail; yellow spots on the back
		float wS = lerp( 0.006, 0.035, sm( 0.1, -0.25, z ) );
		float stripe = fishBand( y - 0.004, 0.0, wS, 0.004 ) * bodyK;
		float2 qq = float2( z, y ) * 70.0;
		float2 cell = floor( qq );
		float2 j = ( float2( fishHash( cell + 3.1 ), fishHash( cell + 7.7 ) ) - 0.5 ) * 0.5;
		float rr = fishHash( cell + 1.3 ) * 0.14 + 0.1;
		float spots = ( 1.0 - sm( rr, rr + 0.12, length( frac( qq ) - 0.5 - j ) ) ) * step( 0.4, fishHash( cell + seed ) ) * tBack * bodyK;
		c = lerp( c, float3( 0.85, 0.62, 0.05 ), max( stripe, spots * 0.8 ) );
		c = lerp( c, float3( 0.86, 0.66, 0.06 ), P == PA_CAUDAL ? 1.0 : 0.0 );
	}
	else if ( pat == PT_TANG )
	{
		// fine dark wavy lines, pale scalpel at the tail base
		float lines = sm( 0.75, 0.95, sin( y * 170.0 + z * 30.0 + n1 * 3.0 ) ) * 0.3 * bodyK;
		c *= 1.0 - lines;
		float spine = ( 1.0 - sm( 0.01, 0.02, length( float2( z + 0.27, y * 1.5 ) ) ) ) * bodyK;
		c = lerp( c, float3( 0.85, 0.8, 0.55 ), spine );
		c = lerp( c, edgeC, isFin ? sm( 0.8, 1.0, t ) : 0.0 );
	}
	else if ( pat == PT_SERGEANT )
	{
		// five black bars from behind the head to the tail stalk (a faint sixth on the peduncle), a dark spot at the base of the pectoral fin
		float barsP = sm( 0.45, 0.7, sin( ( z - 0.215 ) * 52.0 + 1.57 ) ) * sm( -0.29, -0.24, z ) * ( 1.0 - sm( 0.225, 0.26, z ) );
		float sixth = fishBand( z, -0.33, 0.012, 0.01 ) * 0.4;
		float bars = max( barsP, sixth ) * ( 1.0 - tBelly * 0.8 );
		c = lerp( c, float3( 0.02, 0.02, 0.03 ), bars * ( isBody ? 0.92 : ( isFin ? 0.4 : 0.0 ) ) );
		float pecSpot = ( 1.0 - sm( 0.012, 0.02, length( float2( z - eye.w + 0.03, y + 0.005 ) ) ) ) * bodyK;
		c = lerp( c, float3( 0.03, 0.035, 0.05 ), pecSpot * 0.8 );
	}
	else if ( pat == PT_WRASSE )
	{
		// bluehead wrasse: yellow initial phase with a dark midlateral stripe; blue-headed males
		bool male = frac( seed * 7.1 ) < 0.15;
		float stripe = fishBand( h, 0.05, 0.1, fwH + 0.04 ) * bodyK * sm( 0.25, 0.1, z );
		float3 female = lerp( c, float3( 0.04, 0.04, 0.03 ), stripe * 0.9 );
		float head = sm( 0.12, 0.17, z );
		float collar = fishBand( z, 0.13, 0.012, 0.006 );
		float3 maleC = lerp( lerp( float3( 0.1, 0.42, 0.28 ), float3( 0.05, 0.14, 0.62 ), head ), float3( 0.02, 0.02, 0.02 ), collar * bodyK );
		c = male ? maleC : female;
	}
	else if ( pat == PT_PARROT )
	{
		// stoplight (terminal phase: green, pink / orange marks, yellow spot on the gill cover) or queen parrotfish (blue-green, orange-pink
		// marks around the mouth)
		bool queen = frac( seed * 4.3 ) < 0.4;
		float3 base = queen ? float3( 0.06, 0.34, 0.42 ) : float3( 0.1, 0.42, 0.26 );
		c = lerp( c, base * lerp( 0.8, 1.1, scaleShade ), bodyK * 0.75 );
		float mark = fishBand( y - ( z - 0.3 ) * 0.4, -0.03, 0.008, 0.008 ) * sm( 0.18, 0.35, z ) * bodyK;
		c = lerp( c, queen ? float3( 0.75, 0.42, 0.28 ) : float3( 0.85, 0.45, 0.32 ), mark );
		float spot = ( 1.0 - sm( 0.01, 0.02, length( float2( z - eye.w - 0.02, y - 0.05 ) ) ) ) * bodyK;
		c = lerp( c, float3( 0.88, 0.72, 0.12 ), spot * ( queen ? 0.0 : 1.0 ) );
	}
	else if ( pat == PT_ANGEL )
	{
		// French angelfish: black, yellow rims on the scales, yellow face and eye ring
		float rims = sm( 0.72, 0.95, sf ) * max( sfade, 0.35 ) * bodyK;
		c = lerp( c, float3( 0.62, 0.48, 0.06 ), rims * 0.6 );
		float face = sm( 0.4, 0.43, z ) * bodyK;
		c = lerp( c, float3( 0.55, 0.45, 0.2 ), face * 0.6 );
		float er0 = length( float2( z - eye.x, y - eye.y ) ) / eye.z;
		float ringA = sm( 1.05, 1.2, er0 ) * ( 1.0 - sm( 1.45, 1.65, er0 ) ) * bodyK;
		c = lerp( c, float3( 0.7, 0.52, 0.06 ), ringA * 0.85 );
	}
	else if ( pat == PT_BARRACUDA )
	{
		// dark oblique bars on the upper flank, black blotches on the lower rear flank
		float bars = sm( 0.35, 0.8, sin( z * 58.0 + h * 1.5 + n1 ) ) * sm( 0.2, 0.55, h ) * bodyK;
		c *= 1.0 - bars * 0.45;
		float bl = sm( 0.6, 0.78, n2 ) * sm( 0.1, -0.25, z ) * ( 1.0 - sm( -0.3, 0.2, h ) ) * bodyK;
		c = lerp( c, float3( 0.03, 0.03, 0.035 ), bl * 0.9 );
		c = lerp( c, float3( 0.75, 0.78, 0.8 ), P == PA_CAUDAL ? sm( 0.85, 1.0, t ) * sm( 5.0, 7.0, abs( w - 8.0 ) ) : 0.0 );
	}
	else if ( pat == PT_REDSNAPPER )
	{
		// rose red back fading to a silvery pink belly; rows of scales show as fine oblique lines
		float rows = sm( 0.6, 0.95, sin( y * 210.0 + z * 120.0 ) ) * max( sfade, 0.3 ) * bodyK * 0.15;
		c *= 1.0 - rows;
	}
	else if ( pat == PT_GROUPER )
	{
		// Nassau grouper: dark brown bars, a band from the snout through the eye, a black saddle on the tail stalk, dark spots around the eye
		float zz = 0.5 - z;
		float wob = ( n2 - 0.5 ) * 0.03;
		float bars = sm( 0.2, 0.6, sin( ( zz + wob ) * 34.0 - 1.2 ) ) * sm( 0.3, 0.38, zz ) * sm( 0.86, 0.78, zz ) * ( 1.0 - tBelly * 0.85 );
		float stripe = fishBand( y - eye.y - ( z - eye.x ) * 0.25, 0.0, 0.008, 0.006 ) * sm( eye.x - 0.08, eye.x, z ) * sm( 0.5, 0.45, z );
		float saddle = sm( 0.4, 0.7, h ) * fishBand( zz, 0.8, 0.025, 0.01 );
		float spots = sm( 0.72, 0.85, fishVnoise( float2( z, y ) * 160.0 + seed * 3.0 ) ) * sm( eye.x - 0.12, eye.x, z );
		float dark = max( max( bars * 0.85, stripe * 0.85 ), max( saddle, spots * 0.7 ) ) * bodyK;
		c = lerp( c, float3( 0.13, 0.085, 0.05 ), dark );
		float pale = sm( 0.86, 0.93, fishVnoise( float2( z, y ) * 150.0 + 9.0 ) ) * bodyK * 0.2;
		c = lerp( c, float3( 0.85, 0.8, 0.72 ), pale );
	}
	else if ( pat == PT_TUNA )
	{
		// blackfin tuna: sharp dark back, bronze band, pale bars on the belly, dusky yellow finlets
		float bronze = fishBand( h, 0.28, 0.05, fwH + 0.06 ) * sm( 0.3, 0.2, z ) * bodyK;
		c = lerp( c, float3( 0.42, 0.34, 0.14 ), bronze * 0.6 );
		c = lerp( c, back, sm( 0.28, 0.4, h ) * bodyK );
		float bars = sm( 0.6, 0.9, sin( z * 95.0 ) ) * sm( 0.0, -0.3, h ) * sm( 0.2, 0.1, z ) * bodyK;
		c = lerp( c, float3( 0.85, 0.88, 0.9 ), bars * 0.35 );
		c = lerp( c, float3( 0.55, 0.48, 0.16 ), P == PA_FINLET ? 0.85 : 0.0 );
	}
	else if ( pat == PT_MAHI )
	{
		// mahi-mahi: blue-green back, golden flanks with scattered blue spots
		float2 cell = floor( float2( z, y ) * 55.0 );
		float2 jit = float2( fishHash( cell + 3.1 ), fishHash( cell + 7.7 ) ) - 0.5;
		float2 fc = frac( float2( z, y ) * 55.0 ) - 0.5 - jit * 0.55;
		float rs = lerp( 0.1, 0.24, fishHash( cell + 1.3 ) );
		float spots = ( 1.0 - sm( rs, rs + 0.1, length( fc * float2( 1.0, 1.25 ) ) ) ) * step( 0.45, fishHash( cell + seed * 7.0 ) ) * bodyK * ( 1.0 - tBelly );
		c = lerp( c, float3( 0.08, 0.22, 0.5 ), spots * 0.75 );
		c = lerp( c, float3( 0.2, 0.5, 0.3 ), sm( 0.0, 0.5, h ) * bodyK * 0.35 );
	}
	else if ( pat == PT_MULLET )
	{
		// faint dark stripes along the scale rows of the upper flank
		float lines = sm( 0.7, 0.95, sin( sd * 280.0 ) ) * sm( -0.1, 0.3, h ) * bodyK * 0.25;
		c *= 1.0 - lines;
	}
	else if ( pat == PT_NEEDLEFISH )
	{
		// dark blue lateral stripe, dark beak
		float stripe = fishBand( h, 0.0, 0.06, fwH + 0.05 ) * bodyK;
		c = lerp( c, float3( 0.12, 0.25, 0.45 ), stripe * 0.6 );
		c = lerp( c, float3( 0.12, 0.16, 0.16 ), sm( 0.32, 0.36, z ) * bodyK * 0.7 );
	}
	else if ( pat == PT_JACK )
	{
		// bar jack: black stripe along the base of the dorsal fin into the lower tail lobe, electric blue below it
		float top = fishBand( h, 0.82, 0.06, fwH + 0.05 ) * sm( 0.2, 0.05, z ) * bodyK;
		float blue = fishBand( h, 0.68, 0.05, fwH + 0.05 ) * sm( 0.2, 0.05, z ) * bodyK;
		c = lerp( c, float3( 0.15, 0.45, 0.9 ), blue * 0.5 );
		c = lerp( c, float3( 0.02, 0.03, 0.05 ), top * 0.85 );
		float lobe = P == PA_CAUDAL ? sm( 7.5, 5.5, w ) * sm( 0.1, 0.3, t ) : 0.0;
		c = lerp( c, float3( 0.02, 0.03, 0.05 ), lobe * 0.8 );
	}
	else if ( pat == PT_TARPON )
	{
		// huge scales with dark edges
		float rims = sm( 0.8, 0.97, sf ) * sfade * bodyK;
		c *= 1.0 - rims * 0.35;
	}

	// ---- lateral line (a row of pores along a dark line)
	float hl = lat.x + lat.y * ( 1.0 - sm( 0.12, 0.55, u ) );
	float lineK2 = fishBand( h, hl, fwH * 0.5 + 0.012, fwH + 0.008 ) * bodyK * sm( 0.18, 0.25, u ) * sm( 0.9, 0.8, u );
	c *= 1.0 - lineK2 * 0.3;

	// ---- gill cover edge and the preopercle (dark creases); gills show red on dead fish
	float zE = fishOpercleEdge( eye.w, h );
	float dOp = z - zE;
	float onOp = fishOpercleMask( h ) * bodyK;
	float crease = ( 1.0 - sm( 0.0015, 0.004, abs( dOp ) ) ) * onOp;
	c *= 1.0 - crease * 0.45;
	float pre = ( 1.0 - sm( 0.001, 0.003, abs( dOp - 0.04 ) ) ) * onOp * sm( 0.6, 0.2, h );
	c *= 1.0 - pre * 0.2;
	float gill = sm( 0.0, -0.0015, dOp ) * sm( -0.007, -0.003, dOp ) * onOp * sm( 0.0, -0.5, h ) * flags.w;
	c = lerp( c, float3( 0.3, 0.03, 0.035 ), gill * 0.8 );

	// ---- lips (the mouth line from the snout to the corner), the edge of the upper jaw bone and the nostrils
	float hz = lat.z; float hy = lat.w; float tipY = r3.w;
	float mt = clamp( ( z - hz ) / ( 0.5 - hz ), 0.0, 1.0 );
	float yLip = lerp( hy, tipY, mt );
	float lips = ( 1.0 - sm( 0.0015, 0.0035, abs( y - yLip ) ) ) * step( hz - 0.004, z ) * bodyK;
	c *= 1.0 - lips * 0.55;
	float maxZ = hz + 0.006 - ( y - hy ) * 0.35;
	float maxilla = ( 1.0 - sm( 0.001, 0.0025, abs( z - maxZ ) ) ) * sm( hy - 0.002, hy + 0.002, y ) * sm( hy + 0.04, hy + 0.025, y ) * bodyK;
	c *= 1.0 - maxilla * 0.3;
	float nostril = ( 1.0 - sm( 0.1, 0.2, length( float2( z - eye.x - eye.z * 1.7, y - eye.y - eye.z * 0.25 ) ) / eye.z ) ) * bodyK;
	c *= 1.0 - nostril * 0.6;

	// ---- painted eye (under the dome where there is one)
	float er = length( float2( z - eye.x, y - eye.y ) ) / eye.z;
	float painted = ( 1.0 - sm( 0.95, 1.1, er ) ) * bodyK;
	c = lerp( c, fishEyeCol( er, atan2( y - eye.y, z - eye.x ), irisC, flags.x ), painted );
	metal *= 1.0 - painted;

	// ---- eye dome: pupil, iris, glossy cornea
	if ( P == PA_EYE )
	{
		float r = length( float2( t, w ) );
		c = fishEyeCol( r, atan2( w, t ), irisC, flags.x );
		c = lerp( c, flank * 0.6, sm( 0.93, 1.0, r ) );
		rough = lerp( 0.04, 0.3, flags.x );
		spec = 1.0;
		metal = 0.0;
	}
	else if ( P == PA_MOUTH )
	{
		// inside of the mouth: pale pink lips to a dark throat (grunts are red inside)
		float3 lip = pat == PT_GRUNT ? float3( 0.6, 0.08, 0.06 ) : float3( 0.5, 0.3, 0.3 );
		c = lerp( lip, float3( 0.03, 0.012, 0.012 ), sm( 0.05, 0.85, t ) );
		metal = 0.0;
		rough = 0.35;
	}
	else if ( P == PA_FLESH )
	{
		// cut face: muscle rings around the backbone, bone and blood at the centre
		float r = length( float2( t, w * 1.2 ) );
		float dark = pat == PT_TUNA ? 1.0 : 0.0;
		float3 meat = lerp( float3( 0.62, 0.36, 0.32 ), float3( 0.3, 0.035, 0.035 ), dark );
		float rings = sm( 0.6, 0.95, sin( r * 520.0 + atan2( w, abs( t ) ) * 2.0 ) ) * 0.12;
		float3 m = meat * ( 1.0 - rings );
		float bone = 1.0 - sm( 0.006, 0.009, length( float2( t, w - 0.004 ) ) );
		m = lerp( m, float3( 0.75, 0.68, 0.58 ), bone );
		m = lerp( m, float3( 0.3, 0.02, 0.02 ), ( 1.0 - sm( 0.01, 0.03, r ) ) * 0.5 * ( 1.0 - bone ) );
		// skin rim
		c = m;
		metal = 0.0;
		rough = 0.3;
	}
	else if ( P == PA_FILLET )
	{
		// salted, sun dried flesh: pale and translucent at the thin edges, muscle chevrons, salt crystals
		float ax = abs( t );
		float chev = sm( 0.55, 0.9, sin( ( w + ax * 0.35 ) * 160.0 ) ) * 0.1;
		float3 m = lerp( float3( 0.36, 0.26, 0.13 ), float3( 0.52, 0.42, 0.26 ), sm( 0.35, 1.0, ax ) ) * ( 1.0 - chev );
		float salt = step( 0.94, fishHash( floor( float2( t, w ) * 900.0 ) ) );
		m = lerp( m, float3( 0.8, 0.8, 0.78 ), salt * 0.6 );
		m *= lerp( 0.9, 1.05, n1 );
		c = m;
		metal = 0.0;
		rough = 0.6;
	}
	else if ( P == PA_ICE )
	{
		// glassy crushed ice: dim albedo (light passes into it), sharp glints
		c = float3( 0.3, 0.4, 0.46 ) * lerp( 0.8, 1.15, frac( t * 7.3 ) );
		rough = lerp( 0.04, 0.2, frac( w * 5.1 ) );
		metal = 0.0;
		transl = 0.9;
		spec = 1.0;
	}
	else if ( P == PA_LEAF )
	{
		// banana leaf: glossy green, pale midrib, fine parallel veins
		float ax = abs( t );
		float veins = sm( 0.6, 0.95, sin( w * 420.0 + ax * 60.0 ) ) * 0.12;
		float3 lc = lerp( float3( 0.025, 0.08, 0.015 ), float3( 0.05, 0.13, 0.025 ), n2 ) * ( 1.0 - veins );
		lc = lerp( lc, float3( 0.25, 0.3, 0.1 ), 1.0 - sm( 0.015, 0.03, ax ) );
		lc = lerp( lc, float3( 0.25, 0.22, 0.08 ), sm( 0.9, 1.0, ax ) * 0.6 );
		c = lc;
		metal = 0.0;
		rough = 0.28;
	}
	else if ( P == PA_DISC || P == PA_WHIP )
	{
		// rays: sandy, finely mottled back (stingray) or black with white rings (eagle ray); white belly
		bool top = w > 0.0;
		float eagleK = pat == PT_EAGLERAY ? 1.0 : 0.0;
		float2 qq = float2( Lp.x, Lp.z ) * 20.0;
		float2 cell = floor( qq );
		float2 jit = ( float2( fishHash( cell + 1.7 ), fishHash( cell + 5.3 ) ) - 0.5 ) * 0.4;
		float rad = fishHash( cell + 9.1 ) * 0.14 + 0.12;
		float ring = abs( length( frac( qq ) - 0.5 - jit ) - rad );
		float spots = ( 1.0 - sm( 0.035, 0.075, ring ) ) * step( 0.45, fishHash( cell ) );
		float mottle = fishVnoise( float2( Lp.x, Lp.z ) * 60.0 ) * 0.25 + n2 * 0.2 + 0.7;
		float3 dorsal = lerp( back * mottle, lerp( back, float3( 0.75, 0.78, 0.8 ), spots * 0.85 ), eagleK );
		dorsal = lerp( dorsal, edgeC, sm( 0.8, 1.0, t ) * 0.4 * ( 1.0 - eagleK ) );
		c = top ? dorsal : belly;
		c = P == PA_WHIP ? finC : c;
		metal = 0.0;
		rough = r2.w;
	}
	else if ( P == PA_CARAPACE )
	{
		// green turtle shell: scutes (vertebral row, costals, marginals) with dark seams and radiating olive / brown / amber streaks
		float X = t; float Y = w;
		float ax = abs( X );
		float rr = length( float2( X, Y ) );
		bool vert = ax < 0.3;
		float4 ySeams = vert ? float4( -0.58, -0.22, 0.14, 0.5 ) : float4( -0.42, -0.02, 0.36, 2.0 );
		float yc = Y + ax * ax * 0.25;
		float dY = min( min( abs( yc - ySeams.x ), abs( yc - ySeams.y ) ), min( abs( yc - ySeams.z ), abs( yc - ySeams.w ) ) );
		float dX = abs( ax - 0.3 );
		bool marg = rr > 0.84;
		float angle = atan2( Y, X );
		float dM = min( abs( rr - 0.84 ), abs( frac( angle * 3.819718634205488 ) - 0.5 ) * 0.25 );
		float seam = min( marg ? dM : min( dY, dX ), abs( rr - 0.84 ) );
		float streak = sin( atan2( yc - ( floor( yc * 2.8 ) + 0.5 ) / 2.8, X - sign( X ) * 0.55 ) * 11.0 + n2 * 6.0 ) * 0.5 + 0.5;
		float blotch = sm( 0.45, 0.8, fishVnoise( float2( X, Y ) * 9.0 ) );
		float3 shell = lerp( back, flank, streak * 0.55 + blotch * 0.45 );
		shell = lerp( shell, float3( 0.2, 0.14, 0.06 ), sm( 0.6, 0.9, n1 ) * 0.4 );
		shell *= lerp( 0.45, 1.0, sm( 0.003, 0.012, seam ) );
		c = shell;
		metal = 0.0;
		rough = 0.35;
	}
	else if ( P == PA_SKIN || P == PA_FLIPPER )
	{
		// scaly grey-brown skin with pale scale margins; pale yellow plastron
		float2 qq = float2( Lp.x + Lp.y, Lp.z ) * 55.0;
		float rowS = floor( qq.y );
		float2 q2 = qq + float2( rowS * 0.5, 0.0 );
		float2 ff = frac( q2 ) - 0.5;
		float scale = sm( 0.32, 0.47, max( abs( ff.x ), abs( ff.y ) ) );
		float tone = fishHash( floor( q2 ) ) * 0.35 + 0.8;
		float3 skin = lerp( finC * tone, edgeC, scale * 0.45 );
		c = ( w > 1.5 && P == PA_SKIN ) ? belly * lerp( 0.85, 1.05, n1 ) : skin;
		metal = 0.0;
		rough = 0.5;
	}
	else if ( P == PA_SHELL )
	{
		// spiny lobster: red-brown carapace with cream spots, banded legs
		float2 sp = float2( t, w ) * 12.0;
		float spots = ( 1.0 - sm( 0.18, 0.3, length( frac( sp ) - 0.5 ) ) ) * step( 0.6, fishHash( floor( sp ) ) );
		float3 sh = lerp( float3( 0.16, 0.045, 0.03 ), float3( 0.32, 0.1, 0.04 ), n1 );
		sh = lerp( sh, float3( 0.7, 0.55, 0.22 ), spots * 0.85 );
		c = sh;
		metal = 0.0;
		rough = 0.35;
	}

	// ---- props: dull, drying skin; wet sheen; salted skin
	c = lerp( c, ( float3 ) dot( c, float3( 0.2126, 0.7152, 0.0722 ) ), flags.z * 0.55 * bodyK );
	c *= lerp( 1.0, 0.85, flags.z * bodyK );
	metal *= 1.0 - flags.z;
	rough = lerp( rough, rough * 0.55, flags.y );
	coat = flags.y * ( isBody ? 0.6 : ( isFin ? 0.25 : 0.0 ) );

	// iridescent sheen on silvery skin at grazing angles
	float cosV = abs( dot( I_.N, I_.V ) );
	float irid = r1.w * bodyK * silver * ( 1.0 - cosV );
	float3 hueA = float3( 0.55, 0.95, 0.8 ); float3 hueB = float3( 0.95, 0.6, 1.0 );
	c = lerp( c, c * lerp( hueA, hueB, cosV ) * 1.25, irid * 0.6 );

	FishOut o;
	o.albedo = c;
	o.roughness = rough;
	o.metalness = metal;
	o.coat = coat;
	o.spec = spec;
	o.transl = transl;
	o.normal = fishPerturbNormalByHeight( I_.P, I_.N, dhdx, dhdy, 1.0 );
	return o;
}

#endif // FISH_SURFACE_INCLUDED
