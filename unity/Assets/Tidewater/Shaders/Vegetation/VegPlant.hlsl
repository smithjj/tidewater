// Palms (trunk, coconuts, fronds), young palms, banana plants, ferns, monstera, elephant ear, heliconia and bird of paradise (src/world/vegetation/VegMaterials.js:
// PALM_BARK, BROAD, plantModule). HLSL of the WGSL, line for line. Sim frame throughout (see VegCommon.hlsl).
//
// aMat = ( part, age / stem colour, leaflet length ( m ), frond seed ); uv = ( s along, t across )
// parts: 0 stem, 1 coconut frond, 2 fern frond, 3 banana leaf, 5 coconut, 6 monstera, 7 elephant ear, 8 heliconia leaf, 9 heliconia bract, 10 strelitzia leaf, 11 strelitzia flower

#ifndef VEG_PLANT_INCLUDED
#define VEG_PLANT_INCLUDED

struct VegBark { float3 bark; float hd; };

// Coconut palm bark: irregular leaf-scar rings, fine vertical fissures, grey-brown to silver weathering, lichen, dark stains and the root mass at the base, the fibrous
// old frond bases under the crown. Returns the albedo and a relief height (m) for the bump. y: height along the stem (m), a: 0..1 around, H: stem height.
VegBark vegPalmBark( float y, float a, float H, float seed, float iv )
{
	float A = a * 6.2832;
	float ca = cos( A ); float sa = sin( A );
	float yn = y / H;
	// rings: phase grows faster toward the crown, jittered per ring band and around the trunk
	float wob = ( vegNoise( float2( ca * 1.3 + y * 0.35, sa * 1.3 + seed * 9.0 ) ) - 0.5 ) * 0.7
		+ sin( A * 2.0 + y * 1.1 + seed * 5.0 ) * 0.1;
	float phase = y * lerp( 8.5, 11.0, iv ) + pow( yn, 2.2 ) * H * 5.5 + vegNoise( float2( y * 0.8, seed * 7.3 ) ) * 3.2
		+ vegNoise( float2( y * 3.1, seed * 2.9 ) ) * 0.9 + wob;
	float k0 = floor( phase );
	// ragged ring edges: the scar line wanders a little around the trunk
	float phaseR = phase + ( vegNoise( float2( a * 38.0, k0 * 2.3 + seed * 5.0 ) ) - 0.5 ) * 0.22;
	float k = floor( phaseR );
	float fr = phaseR - k;
	// each ring scar has its own width and depth
	float rk = vegHash12( float2( k, seed * 13.7 ) );
	float gw = lerp( 0.05, 0.14, rk );
	float groove = ( vsm( gw, 0.0, fr ) + vsm( 1.0 - gw * 0.6, 1.0, fr ) ) * lerp( 0.45, 1.0, vegHash12( float2( k * 1.3, seed * 3.1 ) ) );
	float ridge = vsm( 0.07, 0.15, fr ) * vsm( 0.45, 0.16, fr );
	// partial rings: each ring fades out around part of the circumference
	float amp = vsm( 0.28, 0.6, vegNoise( float2( ca * 1.8 + k * 3.17, sa * 1.8 + k * 1.71 + seed * 11.0 ) ) ) * 0.75 + 0.25;
	// fine vertical fissures (level set of a vertically stretched noise) and bark plates
	float nf = vegNoise( float2( a * 54.0, y * 1.6 + seed * 41.0 ) );
	float nf2 = vegNoise( float2( a * 110.0, y * 3.4 + seed * 29.0 ) );
	// fissures: short, broken vertical splits (not continuous grain lines)
	float segA = vsm( 0.45, 0.62, vegNoise( float2( a * 30.0, y * 4.5 + seed * 9.0 ) ) );
	float segB = vsm( 0.5, 0.66, vegNoise( float2( a * 60.0 + 3.7, y * 9.0 + seed * 21.0 ) ) );
	float crack = max( vsm( 0.09, 0.0, abs( nf - 0.5 ) ) * segA, vsm( 0.06, 0.0, abs( nf2 - 0.5 ) ) * segB * 0.7 );
	float plate = vegNoise( float2( a * 24.0, y * 3.5 + seed * 17.0 ) );
	float blotch = vegNoise( float2( a * 6.0, y * 0.4 + seed * 13.0 ) );
	// grey-brown bark weathering to silver-grey, per palm
	float3 bark = lerp( vegC( 0x5e554au ), vegC( 0xa49a88u ), vsat( blotch * 0.55 + plate * 0.25 + ( iv - 0.5 ) * 0.5 + yn * 0.15 ) );
	// warmer tan on some trees and in patches, dark weathered (rain-soaked) blotches
	bark = lerp( bark, bark * float3( 1.12, 1.0, 0.82 ), vsat( ( vegNoise( float2( a * 4.0, y * 0.25 + seed * 31.0 ) ) - 0.4 ) * 2.0 ) * iv );
	bark = bark * lerp( 1.0, 0.72, vsm( 0.6, 0.8, vegNoise( float2( a * 5.0, y * 0.7 + seed * 43.0 ) ) ) );
	// fine mottling (rough, fibrous surface) and pale sun-bleached patches
	float mott = vegNoise( float2( a * 90.0, y * 80.0 + seed * 7.0 ) ) * 0.6 + vegNoise( float2( a * 35.0, y * 30.0 + seed * 3.0 ) ) * 0.4;
	bark = bark * ( mott * 0.45 + 0.78 ) * ( plate * 0.25 + 0.88 );
	bark = lerp( bark, vegC( 0xb3ad9fu ), vsm( 0.55, 0.75, vegNoise( float2( a * 8.0, y * 1.3 + seed * 61.0 ) ) ) * 0.35 * ( 1.0 - yn * 0.5 ) );
	bark = bark * lerp( 1.0, 0.7, groove * amp ) * ( ridge * amp * 0.1 + 1.0 ) * lerp( 1.0, 0.5, crack );
	// lichen: pale grey-green crusts and white spots, a little orange; darker rain streaks
	float lic = vsm( 0.6, 0.72, vegNoise( float2( a * 9.0, y * 2.1 + seed * 23.0 ) ) + ( vegNoise( float2( a * 31.0, y * 7.0 ) ) - 0.5 ) * 0.3 ) * vsm( 0.9, 0.2, yn );
	float3 licC = lerp( vegC( 0x8e917fu ), vegC( 0xa9a799u ), plate );
	bark = lerp( bark, licC, lic * 0.5 );
	float streak = vsm( 0.62, 0.8, vegNoise( float2( a * 16.0, y * 0.12 + seed * 5.0 ) ) ) * vsm( 1.0, 0.6, yn );
	bark = bark * ( 1.0 - streak * 0.28 );
	// damp, dark base (splash of sand and soil) and the mass of exposed roots at the ground
	float baseK = vsm( 1.4, 0.2, y + ( blotch - 0.5 ) * 0.8 );
	bark = lerp( bark, bark * float3( 0.62, 0.56, 0.48 ), baseK );
	float rootN = vegNoise( float2( a * 26.0, y * 2.2 + seed * 19.0 ) );
	float roots = vsm( 0.42, 0.05, y ) * vsm( 0.35, 0.6, rootN );
	bark = lerp( bark, lerp( vegC( 0x3a2e22u ), vegC( 0x5e4a36u ), rootN ), vsm( 0.5, 0.0, y ) * 0.85 );
	// fibrous old frond bases (boot) under the crown: criss-cross fibre mat, brown
	float boot = vsm( H - 1.0, H - 0.35, y + ( blotch - 0.5 ) * 0.3 );
	float fib = sin( A * 34.0 + y * 30.0 ) * sin( A * 34.0 - y * 30.0 ) * 0.5 + 0.5;
	bark = lerp( bark, lerp( vegC( 0x4d3722u ), vegC( 0x8a744cu ), fib * 0.6 + plate * 0.4 ), boot );
	VegBark o;
	o.bark = bark;
	o.hd = ( ridge * amp * 0.004 - groove * amp * 0.008 - crack * 0.006 + plate * 0.004 + mott * 0.004 + roots * 0.01 ) * ( 1.0 - boot ) + boot * fib * 0.004;
	return o;
}

// Broadleaf plants (parts 6 monstera, 7 elephant ear, 8 heliconia leaf, 9 heliconia bract). Blade coordinates: y along the midrib (0 back of the basal lobes .. 1 tip),
// x across in units of the half-width W (m). Returns the outline half-width (x units) at y and whether a hole / slit / tear is cut at (y, x).
struct VegBroad { float w; bool cut; };

VegBroad vegBroadShape( float part, float y, float x, float age, float W, float fseed, float seed )
{
	float ax = abs( x );
	float w = 0.0;
	bool cut = false;
	// ragged, slightly irregular margin on every leaf (more on old ones)
	float rag = 1.0 - ( vegNoise( float2( y * 38.0 + ( x > 0.0 ? 17.0 : 0.0 ), fseed * 31.0 ) ) * 0.05 + age * age * 0.12 * vegNoise( float2( y * 11.0, fseed * 7.0 + x ) ) );
	if ( part < 6.5 )
	{
		// monstera: cordate blade, basal lobes behind the petiole, V sinus; mature leaves are split from the margin between the primary veins, with a row of holes
		// (fenestrations) inside
		float yb = 0.16;
		float g = ( y - yb ) / ( 1.0 - yb );
		if ( y >= yb )
		{
			w = pow( max( sin( 3.14159 * min( 1.0, 0.4 + 0.6 * g ) ), 0.0 ), 0.8 ) * ( 1.0 - 0.15 * vsm( 0.8, 1.0, g ) );
		}
		else
		{
			float q = ( yb - y ) / yb;
			w = sqrt( max( 1.0 - q * q * q, 0.0 ) ) * 0.96;
			cut = ax < 0.3 * ( 1.0 - y / yb );
		}
		w *= rag;
		float matK = vsm( 0.28, 0.45, age );
		if ( matK > 0.0 && y > yb * 0.4 && y < 0.93 )
		{
			float L = W / 0.47;
			float X = ax * W;
			float Y = ( y - yb ) * L;
			float p = ( Y - 0.55 * X ) / ( L * 0.8 / 5.5 ) + fseed * 0.37;
			float k = floor( p + 0.5 );
			float e = abs( p - k ); // 0 on the gap between two primary veins
			float hk = vegHash12( float2( k + ( x > 0.0 ? 50.0 : 0.0 ), fseed * 19.3 ) );
			float r = ax / max( w, 0.05 );
			float depth = lerp( 0.42, 0.75, hk ) + ( 1.0 - matK ) * 0.4;
			// slits widen toward the margin, their inner end rounded
			bool slit = e < 0.035 + 0.1 * vsm( depth, 1.0, r ) && r > depth - 0.03;
			// fenestrations: one or two elongated holes along the gap line, inside the slit
			float r0 = 0.14; float r1 = depth - 0.1;
			float qh = ( r - r0 ) / max( r1 - r0, 0.05 );
			float nh = hk > 0.45 ? 2.0 : 1.0;
			float qq = frac( qh * nh );
			bool hole = qh > 0.0 && qh < 1.0 && e < 0.16 * pow( sin( 3.14159 * qq ), 0.6 ) * matK && hk > 0.12;
			cut = cut || slit || hole;
		}
	}
	else if ( part < 7.5 )
	{
		// elephant ear: peltate blade (petiole joins inside it), rounded basal lobes, acute tip
		float yb = 0.3;
		float g = ( y - yb ) / ( 1.0 - yb );
		if ( y >= yb )
		{
			w = pow( max( sin( 3.14159 * min( 1.0, 0.5 + 0.5 * g ) ), 0.0 ), 0.85 );
		}
		else
		{
			float q = ( yb - y ) / yb;
			w = sqrt( max( 1.0 - pow( q, 1.6 ), 0.0 ) ) * 0.98;
			cut = y < 0.1 && ax < 0.2 * ( 1.0 - y / 0.1 );
		}
		w *= rag;
	}
	else if ( part < 8.5 || ( part > 9.5 && part < 10.5 ) )
	{
		// heliconia / bird of paradise: paddle blade, torn along the lateral veins (more on old leaves)
		w = pow( max( sin( 3.14159 * min( y * 1.02, 1.0 ) ), 0.0 ), 0.5 ) * ( 1.0 - 0.3 * vsm( 0.75, 1.0, y ) ) * rag;
		float xl = y * 22.0 + ax * 1.2 + sin( y * 31.0 + fseed * 10.0 ) * 0.3;
		float k = floor( xl );
		float r1 = vegHash12( float2( k + ( x > 0.0 ? 40.0 : 0.0 ), fseed * 7.7 ) );
		float r2 = vegHash12( float2( k + 0.5, fseed * 3.3 + seed ) );
		float fx = abs( frac( xl ) - 0.5 );
		cut = r1 < ( part > 9.5 ? 0.2 : 0.12 ) + age * 0.45 && ax / max( w, 0.05 ) > lerp( 0.25, 0.8, r2 ) && fx > 0.44;
	}
	else if ( part < 9.5 )
	{
		// bract: boat-shaped, pointed
		w = sin( 3.14159 * min( 1.0, 0.15 + y * 0.9 ) ) * ( 1.0 - 0.4 * vsm( 0.7, 1.0, y ) );
	}
	else
	{
		// bird of paradise flower pieces: pointed
		w = sin( 3.14159 * min( 1.0, 0.1 + y * 0.9 ) ) * ( 1.0 - 0.5 * vsm( 0.6, 1.0, y ) );
	}
	if ( part < 7.5 )
	{
		float dmg = vegNoise( float2( y * 26.0 + fseed * 17.0, x * 11.0 + seed * 9.0 ) );
		cut = cut || dmg > 0.95 - age * 0.08;
	}
	VegBroad o;
	o.w = w;
	o.cut = cut;
	return o;
}

float3 vegBroadAlbedo( float part, float y, float x, float age, float W, float fseed, float seed, float iv, float hGround )
{
	float ax = abs( x );
	float fr = vegHash12( float2( fseed * 51.3, seed * 17.9 ) );
	float n1 = vegNoise( float2( y * 9.0 + fseed * 13.0, x * 4.0 ) );
	float n2 = vegNoise( float2( y * 37.0 + fseed * 3.0, x * 17.0 + seed * 5.0 ) );
	float3 c = float3( 0.0, 0.0, 0.0 );
	if ( part < 6.5 )
	{
		// monstera: deep glossy green, juvenile leaves lighter yellow-green, old ones yellowing
		float3 g = lerp( vegC( 0x223b15u ), vegC( 0x324f1du ), iv * 0.6 + fr * 0.4 );
		g = lerp( vegC( 0x4f6f28u ), g, vsm( 0.1, 0.35, age ) );
		g = lerp( g, vegC( 0x9a913eu ), vsm( 0.85, 0.97, age ) * ( 0.6 + 0.4 * n1 ) );
		c = g * ( n1 * 0.16 + 0.92 );
		// primary veins a touch paler, midrib pale
		float L = W / 0.47;
		float p = ( ( y - 0.16 ) * L - 0.55 * ax * W ) / ( L * 0.8 / 5.5 ) + fseed * 0.37;
		float rib = vsm( 0.1, 0.0, abs( frac( p ) - 0.5 ) ) * step( 0.16, y );
		c = c * ( 1.0 + rib * 0.12 );
		c = lerp( c, vegC( 0x80994au ), vsm( 0.03, 0.0, ax * W ) * 0.7 );
	}
	else if ( part < 7.5 )
	{
		// elephant ear: mid green, pale veins radiating from where the petiole joins
		float3 g = lerp( vegC( 0x33581eu ), vegC( 0x4a7128u ), iv * 0.6 + fr * 0.4 );
		g = lerp( g, vegC( 0x98913fu ), vsm( 0.85, 0.97, age ) * ( 0.6 + 0.4 * n1 ) );
		float ang = atan2( ax * W, ( y - 0.3 ) * W / 0.4 );
		float vein = vsm( 0.1, 0.0, abs( frac( ang * 2.6 ) - 0.5 ) - 0.38 ) * 0.7;
		c = g * ( n1 * 0.14 + 0.93 );
		c = lerp( c, vegC( 0x7f9658u ), vein * 0.35 + vsm( 0.025, 0.0, ax * W ) * step( 0.3, y ) * 0.5 );
	}
	else if ( part > 10.5 )
	{
		// bird of paradise flower: beak green-grey with a purple-red keel, orange sepals, blue tongue
		float3 beak = lerp( lerp( vegC( 0x5b6b4au ), vegC( 0x6b2f3au ), vsm( 0.3, 0.9, ax ) * 0.7 ), vegC( 0xc0703au ), vsm( 0.8, 1.0, y ) * 0.4 );
		float3 sepal = lerp( vegC( 0xe0741cu ), vegC( 0xf09a2au ), n1 );
		float3 tongue = lerp( vegC( 0x2c3f9au ), vegC( 0x4058b8u ), n1 );
		return age > 0.75 ? tongue : ( age > 0.25 ? sepal : beak );
	}
	else if ( part > 9.5 )
	{
		// bird of paradise leaf: glaucous grey-green, pale midrib
		float3 g = lerp( vegC( 0x3e5a36u ), vegC( 0x4f6a43u ), iv * 0.6 + fr * 0.4 );
		g = lerp( g, vegC( 0x8f8a4au ), vsm( 0.8, 0.95, age ) );
		c = g * ( n1 * 0.1 + 0.95 );
		c = lerp( c, vegC( 0xa3a878u ), vsm( 0.015, 0.0, ax * W ) * 0.6 );
	}
	else if ( part < 8.5 )
	{
		float3 g = lerp( vegC( 0x365d20u ), vegC( 0x4b7229u ), iv * 0.6 + fr * 0.4 );
		g = lerp( g, vegC( 0x8f8a3au ), vsm( 0.8, 0.95, age ) );
		c = g * ( ( sin( y * 180.0 + ax * 30.0 ) * 0.5 + 0.5 ) * 0.06 + 0.95 ) * ( n1 * 0.12 + 0.94 );
		c = lerp( c, vegC( 0xaab86eu ), vsm( 0.02, 0.0, ax * W ) * 0.7 );
	}
	else
	{
		// heliconia bract: scarlet, yellow lip, green tip on the youngest (top) bracts
		c = lerp( vegC( 0x8e1a12u ), vegC( 0xbd2a1au ), n1 * 0.6 + fr * 0.4 );
		c = lerp( c, vegC( 0xe3bd38u ), vsm( 0.62, 0.9, ax ) );
		c = lerp( c, vegC( 0x6c8a2eu ), vsm( 0.8, 1.0, y ) * vsm( 0.6, 1.0, age ) * 0.7 );
		return c;
	}
	// weathering: browned dry margins and tips, fungal spots, splashed soil on low leaves
	float edge = vsm( 0.8, 1.0, ax + n2 * 0.25 - 0.1 ) * ( 0.25 + age * 0.9 );
	c = lerp( c, lerp( vegC( 0x7b6639u ), vegC( 0x5a4528u ), n2 ), vsat( edge ) * 0.85 );
	float spot = vegNoise( float2( y * 14.0 + fseed * 9.0, x * 6.0 + seed * 3.0 ) );
	c = lerp( c, vegC( 0x5e5433u ), vsm( 0.86, 0.93, spot ) * 0.5 * ( 0.4 + age ) );
	c = lerp( c, vegC( 0x6c5c45u ), vsm( 0.55, 0.0, hGround ) * vsm( 0.5, 0.75, n2 ) * 0.5 );
	// dead leaves: brown and papery
	c = lerp( c, lerp( vegC( 0x6e5634u ), vegC( 0x8f7a4eu ), n1 ), vsm( 0.93, 0.99, age ) );
	return c;
}

float3 vegBroadStem( float part, float a, float f, float seed, float age )
{
	float n = vegNoise( float2( a * 6.0, f * 30.0 + seed * 11.0 ) );
	if ( part < 6.5 ) { return lerp( vegC( 0x4a6a2au ), vegC( 0x5c7c33u ), n ) * lerp( 1.0, 0.8, vsm( 0.7, 1.0, age ) ); }
	if ( part < 7.5 ) { return lerp( vegC( 0x566a34u ), vegC( 0x5d4a3cu ), n * 0.5 ); }
	if ( part < 8.5 ) { return lerp( lerp( vegC( 0x4a6b2eu ), vegC( 0x61773au ), n ), vegC( 0x5a4a30u ), vsm( 0.6, 0.8, vegNoise( float2( a * 3.0, f * 8.0 + seed * 5.0 ) ) ) * 0.6 ); }
	if ( part > 9.5 ) { return lerp( vegC( 0x55664au ), vegC( 0x6d7658u ), n ); }
	return vegC( 0x8e1a12u );
}

// the fragment's inputs of the plant surface: st = uv, aMat, the instance's seed (iDat.w), stem height H, the base of the instance and the world height above it
struct VegPlantIn
{
	float2 st;
	float4 aMat;
	float seed;
	float H;
	float3 base;
	float trunkY;
	float hGround;
	float2 pixel;
	float fwS;     // fwidth( s ), evaluated in uniform control flow
};

// alpha mask (fronds / pinnae / torn banana blades) + the LOD cross-fade of the instance
bool vegPlantMask( VegPlantIn f )
{
	float s = f.st.x;
	float t = f.st.y;
	float fwS = f.fwS;
	float part = f.aMat.x; float age = f.aMat.y; float Ll = f.aMat.z; float fseed = f.aMat.w;
	float seed = f.seed;
	float m = 1.0;
	if ( part > 0.5 && part < 1.5 )
	{
		// coconut frond: ~95 narrow leaflets per side (100-125 on a real frond), separated by gaps that show the sky; leaflets bunch and spread irregularly, some are
		// short, split or torn away (runs of missing leaflets, more on old fronds)
		float N = 95.0;
		float x = s * N + ( vegNoise( float2( s * 7.0, fseed * 23.0 + seed * 3.0 ) ) - 0.5 ) * 2.2;
		float k = floor( x );
		float r1 = vegHash12( float2( k, fseed * 91.7 ) );
		float r2 = vegHash12( float2( k * 1.37 + 3.1, fseed * 17.3 + seed * 5.0 ) );
		float fx = frac( x ) - 0.5 - ( r1 - 0.5 ) * 0.35;
		float tEnd = lerp( 0.72, 1.0, r2 ) * ( r1 < 0.06 ? 0.45 : 1.0 );
		float tt = t / tEnd;
		float hw = pow( max( 1.0 - tt, 0.0 ), 0.6 ) * 0.22 * ( vsm( 0.0, 0.1, tt ) * 0.4 + 0.6 );
		// split leaflets: a slit from the tip back along the midvein
		bool split = r2 > 0.9 && tt > lerp( 0.35, 0.7, r1 ) && abs( fx ) < hw * 0.3;
		// torn-out runs of leaflets
		bool torn = vegNoise( float2( k * 0.21 + seed * 17.0, fseed * 37.0 ) ) > 0.83 - age * 0.2 && s > 0.3;
		// sub-pixel leaflets widen instead of aliasing (fronds turn solid in the distance)
		float hwE = max( hw, min( fwS * ( N * 0.6 ), 0.5 ) * ( tt < 1.0 ? 1.0 : 0.0 ) );
		bool leaf = abs( fx ) < hwE && tt < 1.0 && s > 0.06 && ! split && ! torn;
		bool rachis = t * Ll < 0.026;
		m = ( leaf || rachis ) ? 1.0 : 0.0;
	}
	else if ( part > 1.5 && part < 2.5 )
	{
		// fern: rounded pinnae
		float N = 26.0;
		float x = s * N;
		float k = floor( x );
		float r2 = vegHash12( float2( k, fseed * 31.1 ) );
		float fx = frac( x ) - 0.5;
		float tt = t / lerp( 0.8, 1.0, r2 );
		float hw = sqrt( max( 1.0 - tt * tt, 0.0 ) ) * 0.34;
		float hwE = max( hw, min( fwS * ( N * 0.6 ), 0.5 ) * ( tt < 1.0 ? 1.0 : 0.0 ) );
		bool leaf = abs( fx ) < hwE && tt < 1.0 && s > 0.04;
		bool rachis = t * Ll < 0.006;
		m = ( leaf || rachis ) ? 1.0 : 0.0;
	}
	else if ( part > 2.5 && part < 3.5 && s >= 0.0 )
	{
		// banana: full blade, torn along lateral veins, ragged edge
		float x = s * 13.0 + sin( s * 31.0 + fseed * 10.0 ) * 0.35;
		float k = floor( x );
		float r1 = vegHash12( float2( k, fseed * 7.7 ) );
		float r2 = vegHash12( float2( k + 0.5, fseed * 3.3 + seed ) );
		float fx = abs( frac( x ) - 0.5 );
		// dead leaves are shredded
		float dead = step( 0.8, age );
		// wind-torn strips: most tears start at the margin and run in along the veins, more on older leaves; dead leaves are shredded
		bool tear = r1 < lerp( 0.45 + age * 0.6, 0.95, dead ) && t > lerp( 0.12, 0.75, r2 ) * lerp( 1.0, 0.4, dead ) && fx > lerp( 0.45, 0.37, r2 ) - dead * 0.12;
		bool edge = t < 0.985 - vegNoise( float2( s * 60.0, fseed * 9.0 ) ) * 0.07;
		m = ( ! tear && edge ) ? 1.0 : 0.0;
	}
	else if ( part > 5.5 && s >= 0.0 )
	{
		// broadleaf blades (petioles, s < 0, are kept whole)
		VegBroad sh = vegBroadShape( part, s, t, age, Ll, fseed, seed );
		m = ( abs( t ) < sh.w && ! sh.cut ) ? 1.0 : 0.0;
	}
	return m > 0.5 && vegLodDither( f.base, _VegLod.xyz, f.pixel );
}

float3 vegPlantAlbedo( VegPlantIn f, VegBark stem )
{
	float s = f.st.x;
	float t = f.st.y;
	float4 aMat = f.aMat;
	float part = aMat.x; float age = aMat.y; float Ll = aMat.z; float fseed = aMat.w;
	float seed = f.seed;
	float3 col = float3( 0.0, 0.0, 0.0 );
	float iv = vegHash12( float2( seed * 37.1, 1.7 ) );
	if ( part < 0.5 )
	{
		// stems: age 0 -> weathered, ringed palm trunk; 1 -> green banana pseudostem
		float y = f.trunkY;
		float a = f.st.x;
		float3 bark = stem.bark;
		// (palm trunks have age 0: the pseudostem colour only where it is mixed in)
		float3 green = float3( 0.0, 0.0, 0.0 );
		if ( age > 0.0 )
		{
			float fiss = vegNoise( float2( a * 46.0, y * 1.1 + seed * 50.0 ) );
			float blotch = vegNoise( float2( a * 7.0, y * 0.45 + seed * 13.0 ) );
			// banana pseudostem: overlapping sheaths (vertical streaks), dark blotches, dry brown sheath strips peeling off low down (no leaf-scar rings)
			float streakS = vegNoise( float2( a * 24.0, y * 0.35 + seed * 7.0 ) );
			green = lerp( vegC( 0x4e6a2au ), vegC( 0x6b8438u ), streakS * 0.7 + fiss * 0.3 );
			green = lerp( green, vegC( 0x3b3322u ), vsm( 0.62, 0.82, blotch ) * 0.55 );
			green = lerp( green, lerp( vegC( 0x6e5534u ), vegC( 0x8f7a52u ), fiss ), vsm( 0.55, 0.75, streakS ) * vsm( 1.1, 0.3, y ) );
		}
		col = lerp( bark, green, age );
	}
	else if ( part < 1.5 )
	{
		// age (aMat.y): 0 young upper fronds (lighter yellow-green) .. 0.55 old lower fronds (olive, yellowing); 1 = the two hanging fronds, whose state is picked per
		// tree and frond (one crown mesh is shared): still olive, yellowing or dead brown
		float fr = vegHash12( float2( fseed * 51.3, seed * 17.9 ) );
		float a = vsat( age / 0.55 );
		float3 g = lerp( vegC( 0x728c33u ), vegC( 0x445f27u ), vsm( 0.0, 0.35, a ) );
		g = lerp( g, vegC( 0x69702fu ), vsm( 0.55, 1.0, a ) );
		// per tree: yellower or bluer greens; per frond: value
		g = lerp( g, g * float3( 1.12, 1.02, 0.78 ), iv * 0.8 );
		g = lerp( g, g * float3( 0.86, 0.98, 1.08 ), ( 1.0 - iv ) * 0.5 );
		g = g * lerp( 0.82, 1.1, fr );
		// leaflets: darker toward their tips, paler at the base, each a little different
		float kL = floor( s * 95.0 );
		float perLeaf = vegHash12( float2( kL, fseed * 13.1 ) );
		float3 c = g * lerp( 1.08, 0.86, vsm( 0.2, 1.0, t ) ) * ( perLeaf * 0.22 + 0.9 );
		// browned, dried tips on some leaflets (more on old fronds)
		float tipK = vsm( 0.72, 0.97, t ) * step( 0.55 - a * 0.35, vegHash12( float2( kL * 1.7, fseed * 5.3 + seed ) ) );
		c = lerp( c, lerp( vegC( 0x8c7a4au ), vegC( 0x6e5b39u ), perLeaf ), tipK * 0.85 );
		// hanging fronds
		float hangK = vsm( 0.8, 0.95, age );
		float state = vegHash12( float2( fseed * 7.1, seed * 29.3 ) );
		float3 yellowing = lerp( vegC( 0x8f8a3cu ), vegC( 0xa08a45u ), perLeaf );
		float3 deadC = lerp( vegC( 0x7a6440u ), vegC( 0x5c4a30u ), perLeaf );
		float3 hangC = state > 0.62 ? deadC : ( state > 0.35 ? yellowing : c * 0.9 );
		c = lerp( c, hangC, hangK );
		// midrib: pale yellow on young fronds, straw on old, brown when dead
		bool rachis = t * Ll < 0.026;
		float3 rib = lerp( lerp( vegC( 0xb3a660u ), vegC( 0x98894eu ), a ), vegC( 0x6f5a3au ), hangK * step( 0.62, state ) );
		col = rachis ? rib : c;
	}
	else if ( part < 2.5 )
	{
		float3 c = lerp( vegC( 0x345c20u ), vegC( 0x55802cu ), vsm( 0.1, 1.0, s ) * 0.6 + iv * 0.4 );
		col = c * lerp( 0.85, 1.1, vegHash12( float2( floor( s * 26.0 ), fseed ) ) );
	}
	else if ( part < 3.5 )
	{
		if ( s < 0.0 )
		{
			// banana pseudostem: overlapping sheaths (vertical streaks), brown / purple-black blotches, dry brown sheath fibre peeling low down
			float fq = ( s + 1.0 ) * 2.0;
			float a = t;
			float streakS = vegNoise( float2( a * 26.0, fq * 3.0 + fseed * 7.0 ) );
			float blotch = vegNoise( float2( a * 9.0, fq * 6.0 + fseed * 13.0 ) );
			float3 gS = lerp( vegC( 0x55672eu ), vegC( 0x6e7d3au ), streakS * 0.7 + iv * 0.3 );
			gS = lerp( gS, vegC( 0x3a2a28u ), vsm( 0.6, 0.8, blotch ) * 0.6 );
			float dry = vsm( 0.45, 0.7, vegNoise( float2( a * 14.0 + 3.0, fq * 2.5 + fseed * 19.0 ) ) ) * vsm( 0.8, 0.2, fq );
			col = lerp( gS, lerp( vegC( 0x6a5233u ), vegC( 0x8e7a52u ), streakS ), dry * 0.9 );
		}
		else
		{
			// blade: muted green (per plant and leaf), paler midrib, faint lateral veins; dried, browned margins and torn strip edges; old leaves yellowing; dead ones
			// brown and papery
			float lf = vegHash12( float2( fseed * 23.1, seed * 3.7 ) );
			float3 baseC = lerp( vegC( 0x3f5f24u ), vegC( 0x55742du ), iv * 0.55 + lf * 0.45 );
			baseC = lerp( baseC, vegC( 0x7c7d35u ), vsm( 0.35, 0.6, age ) * 0.55 );
			float vein = ( sin( s * 260.0 ) * 0.5 + 0.5 ) * 0.07;
			float mott = vegNoise( float2( s * 11.0 + fseed * 3.0, t * 4.0 ) );
			float3 c = baseC * ( vein + 0.94 ) * ( mott * 0.14 + 0.93 );
			c = lerp( c, vegC( 0xa9b06eu ), vsm( 0.05, 0.0, t ) * 0.75 ); // midrib
			float xs = s * 13.0 + sin( s * 31.0 + fseed * 10.0 ) * 0.35;
			float stripEdge = vsm( 0.36, 0.47, abs( frac( xs ) - 0.5 ) ) * vsm( 0.4, 0.8, t ) * ( 0.3 + age );
			float dryEdge = vsm( 0.72, 1.0, t + ( vegNoise( float2( s * 25.0, fseed * 4.0 ) ) - 0.5 ) * 0.3 ) * ( 0.45 + age * 0.8 );
			float3 brownC = lerp( vegC( 0x6b5531u ), vegC( 0x8f7a48u ), mott );
			c = lerp( c, brownC, vsat( max( dryEdge, stripEdge * 0.6 ) ) * 0.85 );
			float3 deadC = lerp( vegC( 0x5e4a2cu ), vegC( 0x86704au ), vegNoise( float2( s * 18.0, t * 3.0 + fseed * 5.0 ) ) );
			c = lerp( c, deadC, vsm( 0.85, 0.95, age ) );
			col = c;
		}
	}
	else if ( part > 5.5 )
	{
		if ( s < 0.0 )
			col = vegBroadStem( part, t, ( s + 1.0 ) * 2.0, fseed, age );
		else
			col = vegBroadAlbedo( part, s, t, age, Ll, fseed, seed, iv, f.hGround );
	}
	else
	{
		// coconuts: green -> yellow -> brown
		col = lerp( lerp( vegC( 0x68762au ), vegC( 0x9c8a34u ), vsm( 0.3, 0.7, age ) ), vegC( 0x5c4122u ), vsm( 0.82, 0.95, age ) );
	}
	return col;
}

#endif // VEG_PLANT_INCLUDED
