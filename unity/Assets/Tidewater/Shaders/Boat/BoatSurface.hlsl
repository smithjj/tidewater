// The procedural surfaces of the lobster boat: src/world/boat/BoatMaterials.js (WGSL snippets) as HLSL functions. Each takes the
// model-space position the JS calls positionLocal ( p, the boat frame: +Z forward, +X port ), the vertex colour (linear albedo), aux =
// ( rough, metal, pattern, anim ) and uv, and returns the surface parameters.
//   kind: 0 hull, 1 gelcoat, 2 wood, 3 fittings, 4 glass, 5 glow, 6 trap (7, the glTF factors, is set in BoatFragment.hlsl), 8 game props (the rod)
#ifndef TW_BOAT_SURFACE_INCLUDED
#define TW_BOAT_SURFACE_INCLUDED

#include "../Common/MaterialXNoise.hlsl"

#define BOAT_TWO_PI 6.283185307179586

struct BoatIn
{
	float4 aux;
	float3 color;      // vertex colour, linear
	float2 uv;
	float3 p;          // positionLocal (boat frame, JS handedness)
	float3 rest;       // kind 8: the rest position of the part (JS handedness), before the vertex bend: the patterns stay on parts that move
	float3 P;          // world position (derivatives only)
	float3 N;          // world normal, facing the viewer
	float time;
	float night;
	float3 skyIrradiance;   // E / pi, exposed
	float3 sunColor;        // exposed
	float sunDirY;
	float navOn;
};

struct BoatOut
{
	float3 albedo;
	float roughness;
	float metalness;
	float3 normal;
	float ao;
	float3 emissive;
	float alpha;
	float clearcoat;
	float coatRoughness;
};

float BoatIsPattern( float auxZ, float id ) { return step( abs( auxZ - id ), 0.5 ); }
float BoatHash21( float2 p ) { return frac( sin( dot( p, float2( 127.1, 311.7 ) ) ) * 43758.5453 ); }
float BoatLstep( float a, float b, float x ) { return smoothstep( a, b, x ); }
float BoatInvstep( float a, float b, float x ) { return 1.0 - smoothstep( a, b, x ); }

// hex colour -> linear (TSL color( 0x.. ) is an sRGB hex converted to linear)
float3 BoatCol( uint hex )
{
	float3 c = float3( ( hex >> 16 ) & 255u, ( hex >> 8 ) & 255u, hex & 255u ) / 255.0;
	return c <= 0.04045 ? c / 12.92 : pow( ( c + 0.055 ) / 1.055, 2.4 );
}

// Mikkelsen surface-gradient bump mapping from a procedural height in meters. P / N are world space and N already faces the viewer.
float3 BoatBumpNormal( float3 P, float3 N, float height )
{
	float3 dPdx = ddx( P );
	float3 dPdy = ddy( P );
	float3 n = N;
	float3 r1 = cross( dPdy, n );
	float3 r2 = cross( n, dPdx );
	float det = dot( dPdx, r1 );
	float3 grad = sign( det ) * ( ddx( height ) * r1 + ddy( height ) * r2 );
	return normalize( abs( det ) * n - grad + n * 1e-12 );
}

// Molded non-skid: jittered pebbles on a ~1.8 cm lattice (uv in meters), faded when sub-pixel.
float BoatNonSkidHeight( float2 uv )
{
	float2 q = uv * 55.0;
	float2 c = floor( q );
	float2 f = frac( q ) - 0.5;
	float2 j = ( float2( BoatHash21( c ), BoatHash21( c + 17.31 ) ) - 0.5 ) * 0.3;
	float d = length( f - j );
	float pebble = BoatInvstep( 0.16, 0.36, d );
	float fade = BoatInvstep( 0.3, 0.8, fwidth( q.x ) );
	return pebble * fade;
}

BoatOut BoatDefaults( BoatIn i, float rough )
{
	BoatOut s;
	s.albedo = i.color; s.roughness = rough; s.metalness = 0.0; s.normal = i.N; s.ao = 1.0; s.emissive = 0.0; s.alpha = 1.0;
	s.clearcoat = 0.0; s.coatRoughness = 0.0;
	return s;
}

// ------------------------------------------------------------------ hull

// Hull exterior: antifouling / boot stripe / white topsides painted by height in the boat frame.
BoatOut BoatHull( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.25 );
	float3 p = i.p;
	float tS = saturate( ( p.z - ( -3.9 ) ) / 8.2 );
	float sheer = pow( tS, 2.2 ) * 0.62 + 0.98 + pow( max( 1.0 - tS / 0.2, 0.0 ), 2.0 ) * 0.04;

	// boot top sweeps up slightly toward the bow
	float boot = p.y - BoatLstep( 0.8, 4.3, p.z ) * 0.06;
	float aaB = fwidth( boot ) + 1e-4;
	float aboveBottom = BoatLstep( -aaB, aaB, boot - 0.05 );
	float aboveStripe = BoatLstep( -aaB, aaB, boot - 0.15 );

	float below = sheer - p.y;
	float aaS = fwidth( below ) + 1e-4;
	float cove = BoatLstep( -aaS, aaS, below - 0.1 ) * BoatInvstep( -aaS, aaS, below - 0.122 );

	float n1 = mx_fractal_noise_float3( p * float3( 0.9, 2.4, 0.9 ), 3, 2.0, 0.5 );
	float streakN = mx_noise_float3( float3( p.z * 7.0, p.y * 0.45, p.x * 7.0 ) );
	float scuffN = mx_noise_float3( float3( p.z * 2.2, p.y * 34.0, p.x * 2.2 ) );

	float3 c = lerp( BoatCol( 0x7a1d15 ), BoatCol( 0x0f1a30 ), aboveBottom );
	c = lerp( c, BoatCol( 0xf2efe6 ), aboveStripe );
	c = lerp( c, BoatCol( 0x0f1a30 ), cove );

	// waterline scum on the white, algae on the antifouling just below the boot top
	float scum = BoatInvstep( 0.16, n1 * 0.09 + 0.42, boot ) * aboveStripe * saturate( n1 * 0.6 + 0.7 );
	c = lerp( c, BoatCol( 0x8b7b57 ), scum * 0.32 );
	float algae = BoatLstep( -0.35, 0.03, boot ) * ( 1.0 - aboveBottom ) * saturate( n1 + 0.45 );
	c = lerp( c, BoatCol( 0x2c3a1f ), algae * 0.45 );

	// faint vertical weathering streaks under the gunwale
	float streak = BoatLstep( 0.3, 0.75, streakN ) * BoatLstep( 0.05, 0.2, below ) * BoatInvstep( 0.4, 0.95, below ) * aboveStripe;
	c = lerp( c, BoatCol( 0xa29579 ), streak * 0.2 );

	// scuffs where traps come over the rail (starboard, aft of the wheelhouse)
	float side = BoatLstep( -0.2, 0.2, -p.x ) * BoatInvstep( -0.6, 0.2, p.z ) * BoatLstep( -3.7, -2.8, p.z );
	float scuff = BoatLstep( 0.45, 0.8, scuffN ) * BoatLstep( 0.13, 0.18, below ) * BoatInvstep( 0.45, 0.7, below ) * ( side * 0.8 + 0.2 );
	c = lerp( c, BoatCol( 0x5f5e59 ), scuff * 0.55 );

	s.albedo = c;
	s.roughness = lerp( lerp( 0.75, 0.35, aboveBottom ), 0.2, aboveStripe ) + scum * 0.25 + scuff * 0.3;
	s.clearcoat = aboveBottom * ( 1.0 - scum * 0.5 ) * ( 1.0 - scuff * 0.6 );
	s.coatRoughness = 0.08 + scum * 0.3;
	return s;
}

// ------------------------------------------------------------------ gelcoat

// White fiberglass (deck, lining, house, console); pattern 1 = molded non-skid, 2 = wheelhouse interior (painted panels: seams, screws,
// grime, water stains), 3 = headliner (perforated vinyl between battens).
BoatOut BoatGelcoat( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.35 );
	float4 aux = i.aux;
	float3 vColor = i.color;
	float grip = BoatIsPattern( aux.z, 1.0 );
	float h = BoatNonSkidHeight( i.uv ) * grip;
	float3 p = i.p;
	float dirtN = mx_noise_float3( p * 1.7 );
	// worked deck: trodden grime, blotchy oil / bait / water stains
	float blot = BoatLstep( 0.2, 0.55, mx_fractal_noise_float3( p * float3( 2.2, 2.2, 2.2 ) + float3( 7.7, 0.0, 3.1 ), 3, 2.0, 0.5 ) );
	float dirt = ( saturate( dirtN * 0.5 + 0.35 ) * 0.22 + blot * 0.2 ) * grip;
	// a little grime where walls meet the sole
	float corner = BoatInvstep( 0.35, 0.5, p.y ) * 0.08 * ( 1.0 - grip );

	// old gelcoat everywhere (not the non-skid): chalky yellowed patches, grime settling low down and faint rust / dirt streaks running
	// down from fittings
	float plainK = 1.0 - grip;
	float wear = mx_fractal_noise_float3( p * float3( 1.1, 1.6, 1.1 ) + float3( 5.3, 1.1, 2.9 ), 3, 2.0, 0.5 );
	float streakN = mx_noise_float3( float3( p.x * 14.0, p.y * 1.2, p.z * 14.0 ) );
	float streaks = BoatLstep( 0.3, 0.7, streakN ) * 0.7 * plainK;
	// (deck / sole at 0.35 m)
	float lowDirt = BoatInvstep( 0.38, 1.15, p.y ) * saturate( wear + 0.7 ) * 0.34 * plainK;
	float3 base = vColor * ( 1.0 - h * 0.07 ) * ( 1.0 - dirt - corner * 1.8 - lowDirt );
	base = lerp( base, base * float3( 0.94, 0.89, 0.78 ), BoatLstep( -0.15, 0.35, wear ) * 0.45 * plainK );
	base = lerp( base, base * float3( 0.78, 0.7, 0.6 ), streaks * 0.25 );
	float rough = lerp( aux.x, 0.72, grip ) + h * 0.1 + ( saturate( wear + 0.3 ) * 0.18 + lowDirt * 0.5 ) * plainK;
	float metal = aux.y;
	float groove = 0.0;
	float interiorAO = 1.0;
	float3 fill = 0.0;
	// wheelhouse interior (2) and headliner (3) only: the rest of the gelcoat skips the noise
	if ( aux.z > 1.5 )
	{
		float mottle = mx_fractal_noise_float3( p * float3( 2.3, 3.1, 2.3 ), 3, 2.0, 0.5 );

		// ---- painted panels with screwed seams, grime and water stains
		float panel = BoatIsPattern( aux.z, 2.0 );
		// seams: vertical every 0.61 m along the boat, horizontal at the rail height and below the windows
		float sz = ( p.z + 0.13 ) / 0.61;
		float seamDz = 0.305 - abs( frac( sz ) - 0.5 ) * 0.61; // distance to the nearest vertical seam (m)
		float dyA = abs( p.y - 1.17 );
		float dyB = abs( p.y - 1.52 );
		float seamD = min( seamDz, min( dyA, dyB ) );
		float aa = fwidth( seamD ) + 1e-4;
		float seam = BoatInvstep( 0.0015, 0.0015 + aa * 1.5, seamD ) * panel;
		// pan-head screws every 0.15 m along the seams, 12 mm off the joint
		float screwV = length( float2( ( frac( p.y / 0.15 ) - 0.5 ) * 0.15, seamDz - 0.012 ) );
		float screwH = length( float2( ( frac( p.z / 0.15 ) - 0.5 ) * 0.15, min( dyA, dyB ) - 0.012 ) );
		float screwD = min( screwV, screwH );
		float screw = BoatInvstep( 0.0035, 0.0035 + fwidth( screwD ) + 1e-4, screwD ) * panel;
		// grime: darker toward the sole and along the seams (hands, boots, salt), mottled
		// (the panels start at the rail, 1.17 m: grime collects on their lower part and the bottom seam)
		float low = BoatInvstep( 1.15, 1.6, p.y );
		float grime = saturate( low * ( 0.35 + mottle * 0.3 ) + BoatInvstep( 0.0, 0.035, seamD ) * 0.18 + mottle * 0.08 ) * panel;
		// years of sun and diesel: blotchy yellowing over whole panels
		float age = mx_fractal_noise_float3( p * float3( 0.9, 1.4, 0.9 ) + float3( 3.1, 0.0, 1.7 ), 3, 2.0, 0.5 );
		// hand smudges at grab height, boot scuffs (streaks along the boat) just above the sole
		float smudge = BoatLstep( 0.25, 0.6, mx_noise_float3( p * float3( 7.0, 5.0, 7.0 ) + 9.0 ) ) * BoatLstep( 1.05, 1.25, p.y ) * BoatInvstep( 1.5, 1.75, p.y );
		float scuffN = mx_noise_float3( float3( p.z * 3.0, p.y * 60.0, p.x * 3.0 ) );
		float scuff = BoatLstep( 0.45, 0.7, scuffN ) * BoatInvstep( 0.9, 1.15, p.y ) * BoatLstep( 0.2, 0.35, mottle + 0.5 );
		// water stains running down from the window sills, brown at their ends
		float run = mx_noise_float3( float3( p.z * 9.0, p.y * 0.8, p.x * 9.0 ) );
		float stain = BoatLstep( 0.25, 0.7, run ) * BoatLstep( 0.9, 1.35, p.y ) * BoatInvstep( 1.3, 1.56, p.y ) * panel;
		// warm, yellowed off-white paint
		float3 pc = vColor * float3( 0.97, 0.94, 0.87 );
		pc = lerp( pc, pc * float3( 0.9, 0.82, 0.64 ), BoatLstep( -0.1, 0.35, age ) * 0.7 );
		pc = pc * ( 1.0 - grime * 0.6 ) * ( 1.0 - smudge * 0.12 * panel ) * ( 1.0 - scuff * 0.25 * panel );
		pc = lerp( pc, pc * float3( 0.66, 0.54, 0.4 ), stain * 0.6 );
		pc = lerp( pc, pc * 0.3, seam );
		// pan-head screws, some rusted with a short streak below
		float rustK = step( 0.55, BoatHash21( floor( float2( p.y / 0.15, p.z / 0.15 ) + 0.5 ) ) );
		float3 screwCol = lerp( float3( 0.55, 0.55, 0.53 ), float3( 0.32, 0.17, 0.08 ), rustK );
		pc = lerp( pc, screwCol, screw );

		// ---- headliner: off-white perforated vinyl, quilted between the battens
		float head = BoatIsPattern( aux.z, 3.0 );
		float2 hq = p.xz / 0.006;
		float perf = BoatInvstep( 0.12, 0.3, length( frac( hq ) - 0.5 ) ) * BoatInvstep( 0.3, 0.7, fwidth( hq.x ) );
		float quilt = sin( ( p.z + 0.1 ) / 0.5 * 3.14159 ) * 0.5 + 0.5;
		float3 hc = float3( 0.72, 0.69, 0.63 ) * ( 1.0 - perf * 0.18 ) * ( quilt * 0.12 + 0.88 ) * ( mottle * 0.08 + 0.96 );

		base = lerp( lerp( base, pc, panel ), hc, head );
		rough = lerp( lerp( rough, 0.5 + grime * 0.3 + age * 0.08 - smudge * 0.12 - screw * 0.2, panel ), 0.8, head );
		metal = metal + screw * 0.8 * ( 1.0 - rustK );
		// inside an enclosed wheelhouse most of the sky and sea is hidden: dim the ambient (the sun through the windows is direct light
		// and unaffected). Darker into the corners and overhead.
		interiorAO = lerp( lerp( 1.0, 0.55 - low * 0.12 - BoatLstep( 1.9, 2.3, p.y ) * 0.1, panel ), 0.12, head );
		// the headliner faces the sole, not the sea: its light is the sun and sky bounced off the deck, the sole and the walls (warm,
		// neutral) - a small fill in place of the hidden IBL
		float skyL = dot( i.skyIrradiance, float3( 0.3, 0.5, 0.2 ) );
		float sunL = dot( i.sunColor, float3( 0.3, 0.5, 0.2 ) ) * saturate( i.sunDirY );
		fill = hc * float3( 1.0, 0.95, 0.86 ) * ( skyL * 0.5 + sunL * 0.004 ) * head;
		groove = seam * -0.0006 + screw * 0.0004 + ( perf * -0.0002 + quilt * 0.002 ) * head;
	}

	s.albedo = base;
	s.roughness = rough;
	s.metalness = metal;
	s.ao = interiorAO;
	s.emissive = fill;
	s.normal = BoatBumpNormal( i.P, i.N, h * 0.0008 + groove );
	return s;
}

// ------------------------------------------------------------------ wood

// Varnished teak/mahogany; grain follows uv.x. Pattern 1 = plain (brass, paint), 2 = teak-and-holly sole (planks along uv.x, 0.1 m wide,
// pale holly strips, scuffed varnish).
BoatOut BoatWood( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.35 );
	float4 aux = i.aux;
	float2 w = i.uv;
	float g1 = mx_noise_float3( float3( w.x * 0.8, w.y * 30.0, 0.37 ) );
	float g2 = mx_noise_float3( float3( w.x * 10.0, w.y * 170.0, 5.1 ) );
	float rings = sin( w.y * 150.0 + g1 * 5.0 + w.x * 0.6 ) * 0.5 + 0.5;
	float grain = saturate( rings * 0.45 + g2 * 0.22 + g1 * 0.22 + 0.28 );
	float plain = BoatIsPattern( aux.z, 1.0 );
	float3 wood = lerp( BoatCol( 0x4a230f ), BoatCol( 0x9c5b2b ), grain );
	float rough = aux.x + g2 * 0.04 * ( 1.0 - plain );

	// teak and holly: per-plank tone, holly strip between planks, butt joints, worn traffic lane
	float sole = BoatIsPattern( aux.z, 2.0 );
	float pv = w.y / 0.1;
	float plankI = floor( pv );
	float pf = abs( frac( pv ) - 0.5 );
	float aaP = fwidth( pv ) + 1e-4;
	float holly = BoatLstep( 0.5 - 0.06 - aaP, 0.5 - 0.06, pf );
	float caulk = BoatLstep( 0.5 - 0.012 - aaP, 0.5 - 0.012, pf );
	float butt = BoatInvstep( 0.0, 0.004 + fwidth( w.x ), abs( frac( w.x / 1.8 + BoatHash21( float2( plankI, 3.0 ) ) ) - 0.5 ) * 1.8 );
	float tone = BoatHash21( float2( plankI, 7.0 ) ) * 0.25 + 0.85;
	float lane = mx_noise_float3( float3( w.x * 1.3, w.y * 1.3, 2.0 ) ) * 0.5 + 0.5;
	float3 teak = lerp( BoatCol( 0x6b3f1d ), BoatCol( 0xa87445 ), grain ) * tone;
	teak = lerp( teak, teak * float3( 1.12, 1.08, 1.02 ), lane * 0.5 ); // worn, sun-bleached
	teak = lerp( teak, BoatCol( 0xd9c9a6 ), holly * ( 1.0 - caulk ) );
	teak = lerp( teak, BoatCol( 0x1c140e ), max( caulk, butt ) );
	wood = lerp( wood, teak, sole );
	rough = lerp( rough, 0.45 + lane * 0.25, sole );

	s.albedo = lerp( wood, 1.0, plain ) * i.color;
	s.roughness = rough;
	s.metalness = aux.y;
	s.normal = BoatBumpNormal( i.P, i.N, ( g2 * 0.0002 - max( caulk, butt ) * 0.0012 ) * sole );
	// the sole is inside the wheelhouse: most of the sky is hidden (ambient only)
	s.ao = lerp( 1.0, 0.6, sole );
	return s;
}

// ------------------------------------------------------------------ fittings

// Everything else opaque: stainless, bronze, painted metal, plastics, rope, vinyl, flag. Pattern 1 = laid rope, 2 = flag (animated),
// 3 = whip antenna (animated sway), 4 = wrinkle-finish paint / textured plastic with scuffs, 5 = printed page (tide table, rows of type;
// uv 0..1), 6 = folded paper chart, 7 = photo print, 8 = label tape (white, black type), 9 = heavy fabric (oilskin, lifejacket; folds +
// stitching).
BoatOut BoatFittings( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.5 );
	float4 aux = i.aux;
	float3 vColor = i.color;
	float2 u = i.uv;

	float strand = sin( ( u.x / 0.07 + u.y ) * ( BOAT_TWO_PI * 3.0 ) );
	float ropeShade = BoatLstep( -0.7, 0.7, strand ) * 0.4 + 0.6;

	float stripeIdx = floor( saturate( u.y ) * 12.999 );
	float red = 1.0 - ( stripeIdx - 2.0 * floor( stripeIdx / 2.0 ) );
	float canton = step( u.x, 0.4 ) * step( 6.0 / 13.0, u.y );
	// the 50 stars: nine rows of six and five (alternating) on the canton's grid -- rows a tenth of its height apart, columns a sixth of its
	// width, the five-star rows half a column in -- each a five-pointed star 0.0616 across, all in hoist units (the canton is 0.76 x 7/13)
	float cu = u.x / 0.4;
	float cv = ( 1.0 - u.y ) / ( 7.0 / 13.0 );
	float srow = clamp( round( cv * 10.0 ), 1.0, 9.0 );
	float oddRow = srow - 2.0 * floor( srow / 2.0 );
	float scol = lerp( ( clamp( round( cu * 6.0 ), 1.0, 5.0 ) ) / 6.0, ( clamp( round( cu * 6.0 - 0.5 ), 0.0, 5.0 ) + 0.5 ) / 6.0, oddRow );
	float2 sp = float2( ( cu - scol ) * 0.76, ( srow / 10.0 - cv ) * 0.5385 );
	float sa = atan2( sp.x, sp.y ) + 0.6283185;
	float sth = abs( sa - floor( sa / 1.2566371 ) * 1.2566371 - 0.6283185 );
	float2 sq = length( sp ) * float2( cos( sth ), sin( sth ) );
	float2 tip = float2( 0.0308, 0.0 );
	float2 notch = float2( 0.01177 * cos( 0.6283185 ), 0.01177 * sin( 0.6283185 ) );
	float star = smoothstep( -0.002, 0.002, ( ( notch.x - tip.x ) * ( sq.y - tip.y ) - ( notch.y - tip.y ) * ( sq.x - tip.x ) ) / length( notch - tip ) );
	float3 stripes = lerp( BoatCol( 0xf4f1ea ), BoatCol( 0xb3172a ), red );
	float3 flag = lerp( stripes, lerp( BoatCol( 0x1c2a5c ), BoatCol( 0xf4f1ea ), star ), canton );

	float3 c = lerp( vColor, vColor * ropeShade, BoatIsPattern( aux.z, 1.0 ) );
	c = lerp( c, flag, BoatIsPattern( aux.z, 2.0 ) );
	float rough = aux.x;
	float bump = 0.0;
	// interior props (patterns 4..9) only: ordinary fittings skip the noise
	if ( aux.z > 3.5 )
	{
		float3 p = i.p;

		// wrinkle finish: fine crinkle bump, pale scuffs on the edges people touch
		float wrinkle = BoatIsPattern( aux.z, 4.0 );
		float wn = mx_noise_float3( p * 380.0 );
		float scuffN = mx_fractal_noise_float3( p * float3( 6.0, 14.0, 6.0 ), 3, 2.0, 0.5 );
		float scuff = BoatLstep( 0.35, 0.6, scuffN ) * wrinkle;
		c = lerp( c, lerp( c * ( 1.0 + wn * 0.08 ), c + 0.05, scuff * 0.6 ), wrinkle );
		rough = lerp( rough, rough + wn * 0.08 - scuff * 0.15, wrinkle );
		bump += wn * 0.00015 * wrinkle;

		// printed page: margins, header block, rows of type, a table grid, coffee ring
		float page = BoatIsPattern( aux.z, 5.0 );
		float row = frac( u.y * 34.0 );
		float word = BoatHash21( floor( float2( u.x * 16.0, u.y * 34.0 ) ) );
		float inText = step( 0.08, u.x ) * step( u.x, 0.92 ) * step( 0.06, u.y ) * step( u.y, 0.82 );
		float typeK = BoatLstep( 0.35, 0.45, row ) * BoatInvstep( 0.7, 0.8, row ) * step( 0.18, word ) * inText;
		float header = step( 0.86, u.y ) * step( u.y, 0.93 ) * step( 0.08, u.x ) * step( u.x, 0.6 );
		float grid = BoatLstep( 0.478, 0.49, abs( frac( u.x * 4.0 ) - 0.5 ) ) * inText;
		float ring = BoatInvstep( 0.0, 0.02, abs( length( u - float2( 0.7, 0.3 ) ) - 0.16 ) ) * 0.5;
		float3 paper = lerp( float3( 0.86, 0.84, 0.78 ), float3( 0.1, 0.1, 0.12 ), max( max( typeK * 0.7, header * 0.85 ), grid * 0.5 ) );
		c = lerp( c, lerp( paper, float3( 0.45, 0.3, 0.18 ), ring ), page );
		rough = lerp( rough, 0.9, page );

		// folded paper chart: sea and land tints, depth contours, fold creases, pencilled course
		float chartK = BoatIsPattern( aux.z, 6.0 );
		float cn = mx_fractal_noise_float3( float3( u * 3.0 + float2( 1.3, 4.2 ), 0.7 ), 4, 2.0, 0.5 );
		float shore = cn + ( u.x - 0.55 ) * 1.4;
		float landK = BoatLstep( 0.0, 0.02, shore );
		float contour = BoatInvstep( 0.03, 0.06, abs( frac( shore * 7.0 ) - 0.5 ) ) * ( 1.0 - landK );
		float crease = BoatInvstep( 0.0, 0.006, abs( u.x - 0.5 ) ) + BoatInvstep( 0.0, 0.006, abs( u.y - 0.5 ) );
		float course = BoatInvstep( 0.001, 0.004, abs( u.y - 0.2 - u.x * 0.45 ) ) * step( 0.1, u.x ) * step( u.x, 0.7 );
		float3 chart = lerp( float3( 0.72, 0.8, 0.84 ), float3( 0.86, 0.78, 0.58 ), landK );
		chart = chart * ( 1.0 - contour * 0.25 ) * ( 1.0 - crease * 0.2 );
		chart = lerp( chart, float3( 0.2, 0.2, 0.22 ), course * 0.8 );
		c = lerp( c, chart, chartK );
		rough = lerp( rough, 0.85, chartK );

		// photo print: white border, a sunlit boat / sea / sky picture
		float photo = BoatIsPattern( aux.z, 7.0 );
		float inPic = step( 0.07, u.x ) * step( u.x, 0.93 ) * step( 0.07, u.y ) * step( u.y, 0.8 );
		float horizon = 0.45 + sin( u.x * 3.0 + aux.w * 6.0 ) * 0.03;
		float3 pic = lerp( float3( 0.08, 0.22, 0.35 ), float3( 0.45, 0.65, 0.85 ), step( horizon, u.y ) );
		pic = lerp( pic, float3( 0.85, 0.25, 0.12 ), BoatInvstep( 0.06, 0.08, length( ( u - float2( 0.4 + aux.w * 0.2, horizon ) ) * float2( 1.0, 3.0 ) ) ) );
		pic = lerp( float3( 0.92, 0.9, 0.86 ), pic * ( mx_noise_float3( float3( u * 20.0, aux.w * 9.0 ) ) * 0.15 + 0.9 ), inPic );
		c = lerp( c, pic, photo );
		rough = lerp( rough, 0.3, photo );

		// label tape: white with blocks of black type
		float label = BoatIsPattern( aux.z, 8.0 );
		float lt = step( 0.3, frac( u.x * 7.0 ) ) * step( 0.3, BoatHash21( floor( float2( u.x * 28.0, 3.0 ) ) ) ) * step( 0.25, u.y ) * step( u.y, 0.75 );
		c = lerp( c, lerp( float3( 0.85, 0.85, 0.82 ), 0.05, lt ), label );

		// heavy fabric: soft folds, stitched seams, grime
		float fabric = BoatIsPattern( aux.z, 9.0 );
		float fold = mx_fractal_noise_float3( p * float3( 9.0, 4.0, 9.0 ), 2, 2.0, 0.5 );
		// stitched seams every 0.31 m (dashed)
		float stitch = BoatLstep( 0.486, 0.494, abs( frac( p.y / 0.31 ) - 0.5 ) ) * step( 0.5, frac( p.x * 160.0 + p.z * 160.0 ) );
		c = lerp( c, c * ( fold * 0.35 + 0.8 ) * ( 1.0 - stitch * 0.3 ), fabric );
		rough = lerp( rough, 0.65 + fold * 0.1, fabric );
		bump += fold * 0.004 * fabric;
	}

	s.albedo = c;
	s.roughness = rough;
	s.metalness = aux.y;
	s.normal = BoatBumpNormal( i.P, i.N, bump );
	// patterns 4..9 are wheelhouse interior pieces: most of the sky is hidden (ambient only)
	s.ao = aux.z > 3.5 ? 0.6 : 1.0;
	return s;
}

// ------------------------------------------------------------------ glass

BoatOut BoatGlass( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.05 );
	float2 u = i.uv;
	float spots = BoatLstep( 0.45, 0.85, mx_noise_float3( float3( u * 38.0, 3.3 ) ) );
	float haze = saturate( mx_noise_float3( float3( u * 4.0, 9.1 ) ) * 0.5 + 0.5 );
	float edge = BoatInvstep( 0.0, 0.18, u.y );
	float salt = saturate( ( spots * 0.6 + haze * 0.25 ) * ( edge * 0.8 + 0.35 ) );
	s.albedo = BoatCol( 0xa9bec4 ); // no vertex colours on the glass (three: vertexColors off)
	s.alpha = 0.16 + salt * 0.22;
	s.roughness = 0.03 + salt * 0.35;
	return s;
}

// ------------------------------------------------------------------ glow

// Emissive parts. aux.z selects: 0 nav light, 1 radar display, 2 chart plotter, 3 gauge dial, 4 flood/spot light, 5 cabin dome light, 6 LCD.
BoatOut BoatGlow( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.3 );
	float4 aux = i.aux;
	float3 vColor = i.color;
	float mode = aux.z;
	float t = i.time;
	float night = i.night;
	float2 u = i.uv;

	// radar: head-up PPI with a 24 rpm sweep
	float2 q = ( u - 0.5 ) * 2.0;
	float r = length( q );
	float ang = atan2( q.x, q.y );
	float da = frac( ( t * 2.513 - ang ) / BOAT_TWO_PI );
	float trail = exp( da * -5.0 );
	float beam = BoatInvstep( 0.0, 0.01, da );
	float ringD = abs( frac( r * 3.0 + 0.5 ) - 0.5 );
	float ring = BoatInvstep( 0.012, 0.03, ringD );
	float landN = mx_fractal_noise_float3( float3( q * 2.3 + float2( 1.7, 0.4 ), 0.5 ), 3, 2.0, 0.5 );
	float land = BoatLstep( 0.18, 0.4, landN + q.x * 0.35 ) * BoatLstep( 0.3, 0.45, r );
	float2 cell = floor( q * 7.0 );
	float blip = step( 0.93, BoatHash21( cell ) ) * BoatInvstep( 0.1, 0.3, length( frac( q * 7.0 ) - 0.5 ) ) * BoatLstep( 0.2, 0.3, r );
	float heading = BoatInvstep( 0.004, 0.012, abs( q.x ) ) * step( 0.0, q.y );
	float inside = BoatInvstep( 0.96, 0.99, r );
	float echoes = saturate( land + blip ) * ( trail * 0.75 + 0.25 );
	float3 radar = ( ( float3( 0.0, 0.012, 0.04 )
		+ float3( 0.05, 0.22, 0.28 ) * ( ring * 0.5 + heading * 0.6 )
		+ float3( 1.0, 0.72, 0.12 ) * echoes
		+ float3( 0.15, 0.9, 0.35 ) * ( beam * 0.8 + trail * 0.08 ) )
		* inside + float3( 0.01, 0.015, 0.02 ) ) * 1.3;

	// chart plotter
	float2 cp = u * float2( 1.35, 1.0 );
	float cn = mx_fractal_noise_float3( float3( cp * 2.6 + float2( 3.1, 1.2 ), 2.2 ), 4, 2.0, 0.5 );
	float shore = cn + ( u.x - 0.62 ) * 1.1;
	float isLand = BoatLstep( 0.0, 0.015, shore );
	float shallow = BoatLstep( -0.25, 0.0, shore );
	float contour = BoatInvstep( 0.02, 0.05, abs( frac( shore * 9.0 ) - 0.5 ) ) * ( 1.0 - isLand );
	float3 water = lerp( float3( 0.2, 0.42, 0.78 ), float3( 0.62, 0.82, 0.97 ), shallow );
	float track = BoatInvstep( 0.003, 0.007, abs( u.x - 0.5 - ( u.y - 0.5 ) * 0.25 ) ) * step( 0.5, u.y );
	float boatIcon = BoatInvstep( 0.012, 0.022, length( u - float2( 0.5, 0.5 ) ) );
	float3 chart = ( lerp( water, float3( 0.88, 0.8, 0.55 ), isLand ) * ( 1.0 - contour * 0.35 )
		+ float3( 0.9, 0.1, 0.8 ) * track
		+ float3( 1.0, 0.35, 0.05 ) * boatIcon ) * 0.85;

	// gauge dial (backlit ticks + needle)
	float2 gq = ( u - 0.5 ) * 2.0;
	float gr = length( gq );
	float ga = atan2( gq.x, gq.y );
	float tickF = abs( frac( ga / BOAT_TWO_PI * 24.0 ) - 0.5 );
	float ticks = BoatInvstep( 0.08, 0.15, tickF ) * BoatLstep( 0.72, 0.76, gr ) * BoatInvstep( 0.86, 0.9, gr ) * BoatInvstep( 2.3, 2.4, abs( ga ) );
	float lx = i.p.x; // positionLocal.x
	float needleA = sin( t * 0.7 + lx * 13.0 ) * 0.06 + lx * 3.7 + 0.3;
	float2 nd = float2( sin( needleA ), cos( needleA ) );
	float along = dot( gq, nd );
	float perp = length( gq - nd * along );
	float needle = BoatInvstep( 0.025, 0.045, perp ) * step( -0.1, along ) * BoatInvstep( 0.68, 0.72, along );
	float backlight = night * 1.4 + 0.25;
	float3 gauge = ( float3( 0.9, 0.95, 1.0 ) * ticks + float3( 1.0, 0.45, 0.08 ) * needle ) * backlight + 0.004;

	float3 nav = vColor * i.navOn * ( night * 7.0 + 1.5 );
	float3 flood = vColor * ( night * 9.0 + 0.02 );
	float3 dome = vColor * ( night * 2.2 + 0.02 );
	float3 lcd = vColor * ( night * 0.5 + 0.45 );

	s.albedo = vColor * 0.06;
	s.emissive = nav * BoatIsPattern( mode, 0.0 )
		+ radar * BoatIsPattern( mode, 1.0 )
		+ chart * BoatIsPattern( mode, 2.0 )
		+ gauge * BoatIsPattern( mode, 3.0 )
		+ flood * BoatIsPattern( mode, 4.0 )
		+ dome * BoatIsPattern( mode, 5.0 )
		+ lcd * BoatIsPattern( mode, 6.0 );
	s.roughness = aux.x;
	return s;
}

// ------------------------------------------------------------------ trap

// Vinyl-coated wire traps: alpha-tested mesh. uv in meters, aux.xy = face size (for the solid frame border), pattern 1 = diamond twine
// netting.
BoatOut BoatTrap( BoatIn i )
{
	BoatOut s = BoatDefaults( i, 0.55 );
	float4 aux = i.aux;
	float2 u = i.uv;
	float net = BoatIsPattern( aux.z, 1.0 );
	float2 diag = float2( u.x + u.y, u.x - u.y ) * 0.7071;
	float2 q = lerp( u / 0.038, diag / 0.05, net );
	float2 fr = abs( frac( q ) - 0.5 );
	float2 fw = fwidth( q );
	float hf = lerp( 0.5 - 0.0045 / 0.038, 0.5 - 0.002 / 0.05, net );
	float lineX = BoatLstep( hf - fw.x, hf, fr.x );
	float lineY = BoatLstep( hf - fw.y, hf, fr.y );
	float b = 0.014;
	float inner = step( b, u.x ) * step( b, u.y ) * step( u.x, aux.x - b ) * step( u.y, aux.y - b );
	float border = ( 1.0 - inner ) * ( 1.0 - net );
	s.alpha = max( max( lineX, lineY ), border );
	s.albedo = i.color * lerp( 1.0, 0.8, border );
	return s;
}

// ------------------------------------------------------------------ game props (src/game/GameMaterials.js createPropMaterial)
// aux = ( roughness, metalness, pattern, anim ). Only the fishing tackle patterns are ported so far (the rod and reel): 8 carbon blank under
// clear coat, 9 epoxy-coated thread wraps, 10 EVA foam grip, 11 braided line on the spool, 12 machined / anodised metal, 13 knurled metal,
// 14 rubber. The pattern coordinates are the rest position, in the part's local frame (+Y along the rod / reel axis).
float BoatGpHash( float3 p ) { return frac( sin( dot( p, float3( 127.1, 311.7, 74.7 ) ) ) * 43758.5453 ); }
float BoatGpNoise( float3 p )
{
	float3 ip = floor( p ); float3 f = frac( p ); float3 u = f * f * ( 3.0 - 2.0 * f );
	float a = lerp( lerp( BoatGpHash( ip ), BoatGpHash( ip + float3( 1, 0, 0 ) ), u.x ), lerp( BoatGpHash( ip + float3( 0, 1, 0 ) ), BoatGpHash( ip + float3( 1, 1, 0 ) ), u.x ), u.y );
	float b = lerp( lerp( BoatGpHash( ip + float3( 0, 0, 1 ) ), BoatGpHash( ip + float3( 1, 0, 1 ) ), u.x ), lerp( BoatGpHash( ip + float3( 0, 1, 1 ) ), BoatGpHash( ip + float3( 1, 1, 1 ) ), u.x ), u.y );
	return lerp( a, b, u.z );
}
float BoatGpFbm( float3 p ) { return BoatGpNoise( p ) * 0.55 + BoatGpNoise( p * 2.13 + 7.1 ) * 0.3 + BoatGpNoise( p * 4.7 + 3.3 ) * 0.15; }

BoatOut BoatProps( BoatIn i )
{
	BoatOut s = BoatDefaults( i, i.aux.x );
	float pat = i.aux.z;
	float3 lp = i.rest;
	float3 col = i.color;
	float rough = i.aux.x;
	float h = 0.0;
	if ( pat > 7.5 )
	{
		float ang = atan2( lp.x, lp.z );
		if ( pat < 8.5 )
		{
			// carbon blank: fine woven / wrapped scrim under a glossy clear coat, faded out where the weave is below a pixel
			float u = ang * 18.0; float w = lp.y * 700.0;
			float aa = 1.0 - smoothstep( 0.3, 1.2, fwidth( w ) );
			float weave = sin( u + w ) * sin( u - w );
			col = col * ( 1.0 + weave * 0.09 * aa ) * lerp( 0.94, 1.04, BoatGpNoise( float3( 0.0, lp.y * 3.0, 0.0 ) ) );
			h = weave * 0.00004 * aa;
			s.clearcoat = 1.0; s.coatRoughness = 0.05;
		}
		else if ( pat < 9.5 )
		{
			// nylon thread wraps sealed in epoxy: tight turns around the blank, a little uneven
			float w = lp.y * 3200.0;
			float aa = 1.0 - smoothstep( 0.3, 1.2, fwidth( w ) );
			float turns = sin( w + ang * 0.16 ) * aa;
			col = col * ( 1.0 + turns * 0.12 ) * lerp( 0.92, 1.06, BoatGpNoise( lp * 300.0 ) );
			h = turns * 0.00003;
			s.clearcoat = 1.0; s.coatRoughness = 0.04;
		}
		else if ( pat < 10.5 )
		{
			// EVA foam: closed-cell pores, darker and smoother where the hand holds it, grime
			float pore = smoothstep( 0.72, 0.9, BoatGpNoise( lp * 2600.0 ) );
			float grime = BoatGpFbm( lp * 60.0 );
			col = col * ( 1.0 - pore * 0.35 ) * lerp( 0.85, 1.08, grime );
			rough = clamp( rough - grime * 0.12, 0.55, 1.0 );
			h = -pore * 0.0002;
		}
		else if ( pat < 11.5 )
		{
			// braided line wound on the spool: fine crossing turns
			float w = lp.y * 1500.0; float u = ang * 60.0;
			float aa = 1.0 - smoothstep( 0.3, 1.2, fwidth( w ) );
			float b = sin( u + w ) * 0.5 + 0.5;
			col = col * lerp( 0.8, 1.08, b * aa + 0.5 * ( 1.0 - aa ) );
			h = b * 0.00006 * aa;
		}
		else if ( pat < 12.5 )
		{
			// machined / anodised metal: circumferential lathe marks, slightly uneven sheen
			float w = length( lp.xz ) * 5000.0 + lp.y * 800.0;
			float aa = 1.0 - smoothstep( 0.3, 1.2, fwidth( w ) );
			float m = sin( w ) * aa;
			col = col * ( 1.0 + m * 0.03 );
			rough = clamp( rough + m * 0.05 + ( BoatGpNoise( lp * 150.0 ) - 0.5 ) * 0.06, 0.12, 0.9 );
		}
		else if ( pat < 13.5 )
		{
			// knurled metal (lock nut): a diamond grip pattern in the relief
			float u = ang * 24.0; float w = lp.y * 900.0;
			float aa = 1.0 - smoothstep( 0.3, 1.2, fwidth( w ) );
			float k = abs( sin( u + w ) ) * abs( sin( u - w ) );
			col = col * ( 0.85 + k * 0.25 * aa );
			h = k * 0.00012 * aa;
		}
		else
		{
			// rubber: matte, a few scuffs
			float sc = BoatGpFbm( lp * 250.0 );
			col = col * lerp( 0.9, 1.15, sc );
			rough = clamp( rough + ( sc - 0.5 ) * 0.2, 0.6, 1.0 );
		}
	}
	s.albedo = col;
	s.roughness = rough;
	s.metalness = i.aux.y;
	if ( h != 0.0 ) s.normal = BoatBumpNormal( i.P, s.normal, h );
	return s;
}

BoatOut BoatSurface( int kind, BoatIn i )
{
	if ( kind == 0 ) return BoatHull( i );
	if ( kind == 1 ) return BoatGelcoat( i );
	if ( kind == 2 ) return BoatWood( i );
	if ( kind == 3 ) return BoatFittings( i );
	if ( kind == 4 ) return BoatGlass( i );
	if ( kind == 5 ) return BoatGlow( i );
	if ( kind == 8 ) return BoatProps( i );
	return BoatTrap( i );
}

#endif
