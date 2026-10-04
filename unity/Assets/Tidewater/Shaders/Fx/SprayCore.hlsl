// Vertex and fragment of Spray.shader (src/fx/Spray.js, the sprite material).

StructuredBuffer<float4> _SprayPosR;
StructuredBuffer<float4> _SprayVelR;
StructuredBuffer<float4> _SprayInfoR;

TEXTURE2D(_SprayPuff); SAMPLER(sampler_SprayPuff);     // tileable value noise, repeat, mipmapped
TEXTURE2D(_SprayDots); SAMPLER(sampler_SprayDots);     // scattered drops (r: coverage), repeat, mipmapped

float4 _SprayMat;            // intensity, max distance
float4 _TWSunDir;            // toward the sun, sim space
float4 _TWSunColor;          // sun illuminance (lux) x colour

// optional hooks (clouds, the shadow of the waves that made the particles: Breakers)
StructuredBuffer<float4> _BrkCrest;
float4 _BrkParams;           // x: number of crest slots (0: none)

#define SPRAY_DROPLET 0.0
#define SPRAY_MIST 1.0
#define SPRAY_LIGAMENT 2.0
#define SPRAY_SPRAY 3.0
#define SPRAY_SHEET 4.0

float SprayByKind( float kind, float a, float b, float c, float d, float e )
{
	return kind < 0.5 ? a : ( kind < 1.5 ? b : ( kind < 2.5 ? c : ( kind < 3.5 ? d : e ) ) );
}
#define KIND_STRETCH( k ) SprayByKind( k, 1.0 / 40.0, 0.0, 1.0 / 40.0, 1.0 / 30.0, 1.0 / 60.0 )   // motion blur (s of travel)
#define KIND_ALPHA( k ) SprayByKind( k, 0.55, 0.06, 0.8, 0.66, 0.22 )     // (dense spray: see-through, streaked by its motion, never cotton wool)
#define KIND_FADEIN( k ) SprayByKind( k, 0.02, 0.2, 0.02, 0.12, 0.02 )    // (dense spray blooms out of the splash instead of popping in)
#define KIND_FADEOUT( k ) SprayByKind( k, 0.8, 0.45, 0.8, 0.7, 0.5 )      // fraction of life when it starts to fade

// Henyey-Greenstein phase (1/sr)
float SprayPhaseHG( float cosT, float g )
{
	float g2 = g * g;
	return ( ( 1.0 - g2 ) / ( 4.0 * PI ) ) / pow( max( 1.0 + g2 - 2.0 * g * cosT, 1e-4 ), 1.5 );
}

float SprayCloudShadow( float2 xz ) { return 1.0; }

// Sun visibility (0..1) for a spray particle made by one of the crests (Breakers.sprayShadowModule): in front of the wave with the
// sun behind it (a beach view into the sun) the particles below the crest line are in the shadow of the wave (and of the overhanging
// lip while it plunges). seedTag: the particle's tag.
float SprayWaveShadow( float3 p, float seedTag )
{
	float outv = 1.0;
	float idx = floor( seedTag ) - 1.0;
	if ( idx >= 0.0 && idx < _BrkParams.x )
	{
		uint k = ( uint ) idx * 3u;
		float4 c0 = _BrkCrest[ k ];
		float4 c1 = _BrkCrest[ k + 1u ];
		float4 c2 = _BrkCrest[ k + 2u ];
		float3 L = normalize( _TWSunDir.xyz );
		float2 d2 = c2.xy;
		float Ld = dot( L.xz, d2 ); // < 0: the sun is on the sea side of the wave
		if ( c2.w > 0.5 && Ld < -0.02 )
		{
			float3 root = c0.xyz;
			float b = c0.w;
			float H = c1.w;
			float q = saturate( b / 0.9 ) * ( 1.0 - smoothstep( 1.0, 1.3, b ) );
			// vertical plane through the crest (moved forward under the overhanging lip)
			float2 plane = root.xz + d2 * ( H * 0.8 * q * 0.6 );
			float s = dot( p.xz - plane, d2 ); // > 0: in front of it
			float tau = s / -Ld;
			float yRay = p.y + L.y * tau; // height of the ray toward the sun where it crosses the plane
			float top = root.y + 0.05;
			float shade = smoothstep( top, top - 0.4, yRay ) * smoothstep( -0.1, 0.1, s ) * smoothstep( 14.0, 6.0, s );
			outv = 1.0 - shade * 0.55;
		}
	}

	return outv;
}

struct Varyings
{
	float4 positionCS : SV_POSITION;
	float2 vUV : TEXCOORD0;
	float4 vCol : TEXCOORD1;      // premultiplied-ready radiance, opacity
	float4 vMisc : TEXCOORD2;     // kind (+ life fraction), water height, softness, seed
	float4 vFwd : TEXCOORD3;      // forward-scattered sun (thin parts glow with it), w: half-size in pixels
	float3 posSim : TEXCOORD4;
};

Varyings Vert( uint vid : SV_VertexID, uint iid : SV_InstanceID )
{
	// two triangles of one quad: 0 1 2, 0 2 3
	static const uint quadIdx[ 6 ] = { 0, 1, 2, 0, 2, 3 };
	static const float2 corners[ 4 ] = { float2( -1, -1 ), float2( 1, -1 ), float2( 1, 1 ), float2( -1, 1 ) };

	Varyings o = ( Varyings ) 0;
	float4 posA = _SprayPosR[ iid ];
	float4 velA = _SprayVelR[ iid ];
	float4 info = _SprayInfoR[ iid ];
	float3 p = posA.xyz;
	float age = posA.w;
	float3 vel = velA.xyz;
	float kind = info.x;
	float life = info.y;
	bool isDrop = kind < 0.5;
	bool isLig = kind > 1.5 && kind < 2.5;
	bool isMist = kind > 0.5 && kind < 1.5;
	bool water = isDrop || isLig; // clear water: drops and ligaments
	bool alive = life > 0.0 && age < life;

	float3 camSim = float3( _WorldSpaceCameraPos.x, _WorldSpaceCameraPos.y, -_WorldSpaceCameraPos.z );
	float exposure = GetCurrentExposureMultiplier();
	float3 skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	float3 sunColor = _TWSunColor.rgb * exposure;

	float3 toCam = camSim - p;
	float dist = max( length( toCam ), 0.05 );
	float3 Vd = toCam / dist;

	// pixel footprint at this distance: drops are drawn at least ~1.3 px wide
	float p11 = UNITY_MATRIX_P._m11;
	float pixel = dist * 2.0 / ( p11 * _ScreenSize.y );
	float r0 = velA.w; // radius (m)
	// dense spray is thrown out of the splash as a compact mass and spreads (grows from ~half size)
	float tAge = age / max( life, 1e-3 );
	float r = ( kind > 2.5 && kind < 3.5 ) ? r0 * lerp( 0.45, 1.0, smoothstep( 0.0, 0.25, tAge ) ) : r0;
	float size = max( r, pixel * 1.3 );

	// motion blur along the velocity projected on the view plane; ligaments are elongated anyway
	float3 vPerp = vel - Vd * dot( vel, Vd );
	float speed = length( vPerp );
	float stretchLen = speed * KIND_STRETCH( kind );
	float elong = isLig ? r * 2.0 : 0.0;
	float3 up = speed > 1e-3 ? vPerp / max( speed, 1e-3 ) : float3( 0.0, 1.0, 0.0 );
	// clouds: random rotation that slowly turns
	float rot = frac( info.w ) * 6.283 + age * ( frac( info.w ) - 0.5 );
	float3 camRight = normalize( cross( float3( 0.0, 1.0, 0.0 ), Vd ) + float3( 1e-5, 0.0, 0.0 ) );
	float3 camUp = cross( Vd, camRight );
	float3 mRight = camRight * cos( rot ) + camUp * sin( rot );
	float3 mUp = camUp * cos( rot ) - camRight * sin( rot );
	// torn sheets (dense spray) are drawn along their motion too, tilted a little at random and stretched by their speed: fibrous,
	// streaked silhouettes instead of round puffs
	bool isSheet = kind > 2.5;
	bool isClear = kind > 3.5; // clear sheet (bow sheet)
	float3 side = normalize( cross( Vd, up ) );
	float tilt = ( frac( info.w * 7.31 ) - 0.5 ) * 0.7;
	float3 sUp = up * cos( tilt ) + side * sin( tilt );
	float3 sSide = side * cos( tilt ) - up * sin( tilt );
	// mist streams with the air: drawn along its motion, stretched by its speed (wisps, not discs)
	bool alongMotion = isSheet || ( isMist && speed > 0.3 );
	float3 axisY = water ? up : ( alongMotion ? sUp : mUp );
	float3 axisX = water ? side : ( alongMotion ? sSide : mRight );
	float sheetLen = isSheet ? size * clamp( speed * 0.08, 0.0, 0.8 ) : 0.0;
	float mistLen = isMist ? size * clamp( ( speed - 0.3 ) * 0.35, 0.0, 1.4 ) : 0.0;
	float halfY = size + stretchLen * 0.5 + elong + sheetLen + mistLen;
	float halfX = isSheet ? size * 1.05 : size;
	float2 corner = corners[ quadIdx[ vid ] ];
	float3 world = p + axisX * ( corner.x * halfX ) + axisY * ( corner.y * halfY );

	// coverage that conserves the water's cross-section: the drop's projected area (and the time it spends on each pixel of its
	// streak) spread over the drawn footprint. For the gaussian footprint exp( -k d^2 ) the peak is k r (r + elong) / ( halfX halfY ).
	// Clouds: lost to the clamp.
	float kShape = isLig ? 4.5 : 3.5;
	float cover = water ? min( r * ( r + elong ) * kShape / ( halfX * halfY ), 1.0 ) : saturate( r / size );

	// ---- lighting (per particle)
	float3 L = normalize( _TWSunDir.xyz );
	float cosT = dot( -Vd, L ); // 1 = looking toward the sun through the particle
	float cosA = dot( Vd, L );
	float sunVis = SprayCloudShadow( p.xz ) * SprayWaveShadow( p, info.w );
	float3 sun = sunColor * sunVis;
	// clear water (drop, ligament): the bright sky it refracts and reflects, a strong forward lobe (diffraction + refraction) when
	// backlit, a small glint from any side
	float3 cWater = skyIrradiance * 0.9 + sun * ( SprayPhaseHG( cosT, 0.85 ) * 1.2 + 0.12 );
	// dense spray: multiply scattered, white from any side (a diffuse sphere: its far side is in its own shadow), plus a forward lobe
	float lambert = ( sqrt( max( 1.0 - cosA * cosA, 0.0 ) ) + ( PI - acos( clamp( cosA, -1.0, 1.0 ) ) ) * cosA ) / PI;
	// (the forward lobe is passed on separately: thin, torn parts glow with it, thick parts shade it)
	float3 cSpray = sun * ( ( lambert * 0.65 + 0.35 ) / PI ) + skyIrradiance * 1.15;
	float3 fSpray = sun * ( SprayPhaseHG( cosT, 0.6 ) * 0.9 );
	// mist: a thin veil of fine drops, strongly forward scattering, tinted by the sky
	float3 cMist = sun * ( 0.25 / PI ) + skyIrradiance * 0.9;
	float3 fMist = sun * SprayPhaseHG( cosT, 0.75 );
	// clear sheet: thin water, the sky it shows and a little sun off its surface; the sun shining through it (forward lobe) is passed
	// on: the thin torn parts glow when backlit
	float3 cClear = skyIrradiance * 0.95 + sun * 0.05;
	float3 fClear = sun * ( SprayPhaseHG( cosT, 0.8 ) * 1.1 );
	float3 col = water ? cWater : ( isMist ? cMist : ( isClear ? cClear : cSpray ) );
	float3 fwd = water ? 0.0 : ( isMist ? fMist : ( isClear ? fClear : fSpray ) );
	o.vFwd = float4( fwd, halfX / pixel );

	// opacity over the particle's life
	float t = age / max( life, 1e-3 );
	float fadeIn = smoothstep( 0.0, KIND_FADEIN( kind ), t );
	float fadeOut = 1.0 - smoothstep( KIND_FADEOUT( kind ), 1.0, t );
	float baseA = KIND_ALPHA( kind );
	// far: fade out; very near the eye: sheets and mist would fill the screen (and cost a lot of overdraw)
	float maxDistance = _SprayMat.y;
	float distFade = smoothstep( maxDistance, maxDistance * 0.55, dist ) * ( water ? 1.0 : smoothstep( 0.6, 3.0, dist ) );
	float a = baseA * fadeIn * fadeOut * cover * distFade * _SprayMat.x;

	o.vUV = corner;
	o.vCol = float4( col, a );
	// x: kind + 0.45 * life fraction (the kind tests below have 0.5 of margin)
	o.vMisc = float4( kind + saturate( t ) * 0.45, info.z, size, frac( info.w ) );

	// dead particles collapse off-screen
	if ( alive && a > 1e-4 )
	{
		float3 posRWS = float3( world.x, world.y, -world.z ) - _WorldSpaceCameraPos.xyz;
		o.positionCS = TransformWorldToHClip( posRWS );
	}
	else
	{
		o.positionCS = float4( 0.0, 0.0, 2.0, 1.0 );
	}

	o.posSim = world;
	return o;
}

float4 Frag( Varyings input ) : SV_Target
{
	float2 uv = input.vUV;
	float4 vMisc = input.vMisc;
	float kind = vMisc.x;
	bool isDrop = kind < 0.5;
	bool isLig = kind > 1.5 && kind < 2.5;
	bool isSheet = kind > 2.5;
	bool isClear = kind > 3.5;
	float t = saturate( frac( kind ) / 0.45 ); // life fraction
	float r2 = dot( uv, uv );
	float2 sd = float2( vMisc.w, vMisc.w * 1.7 );
	// drop: gaussian streak; ligament: a slightly sharper, beaded blob
	float drop = max( exp( r2 * -3.5 ) - 0.03, 0.0 );
	float lig = max( exp( r2 * -4.5 ) * ( sin( uv.y * 5.0 + vMisc.w * 40.0 ) * 0.2 + 0.9 ) - 0.03, 0.0 );
	// torn sheet: noise streaked along the motion (uv.y), eroded from its edges inward and more and more as it ages, so it tears into
	// strands and fragments instead of shrinking. Thick parts are dense white water, the torn edges thin and translucent.
	float env = saturate( 1.0 - r2 );
	float fib = SAMPLE_TEXTURE2D( _SprayPuff, sampler_SprayPuff, float2( uv.x * 0.5, uv.y * 0.26 ) + sd ).x;
	float fine = SAMPLE_TEXTURE2D( _SprayPuff, sampler_SprayPuff, float2( uv.x * 1.2, uv.y * 0.6 ) + sd * 2.3 ).x;
	float field = fib * 0.6 + fine * 0.4 + ( env - 0.55 ) * 0.75 - smoothstep( 0.8, 1.0, r2 );
	// a torn sheet only a few pixels across can't show its tears: a solid white dot, and a cluster of them reads as cauliflower puffs.
	// Small on screen, it is drawn thinner and more torn: a far splash-up is a ragged, see-through burst
	float farK = smoothstep( 14.0, 3.0, input.vFwd.w ) * ( ( isSheet && ! isClear ) ? 1.0 : 0.0 );
	float erode = lerp( 0.26, 0.7, t ) + farK * 0.16;
	float dens = saturate( ( field - erode ) * 3.0 );
	// torn edges are thin, translucent water: soft and see-through, the core dense white
	float torn = smoothstep( erode - 0.04, erode + 0.2, field ) * ( dens * 0.45 + 0.55 );
	// ... which breaks up into a cluster of drops: many small dots in a ragged envelope that thins out as it ages (sub-pixel drops
	// average out through the mipmaps: never a solid blob)
	float dots = SAMPLE_TEXTURE2D( _SprayDots, sampler_SprayDots, uv * float2( 0.5, 0.32 ) + sd * 3.7 ).x;
	float swarm = dots * smoothstep( 0.25, 0.55, fib + env * 0.45 - t * 0.2 ) * ( 1.0 - t * 0.5 );
	float sheet = max( torn * ( 1.0 - smoothstep( 0.15, 0.6, t ) ), swarm );
	// mist: a soft veil of low-frequency noise that drifts and thins, never a disc
	float m1 = SAMPLE_TEXTURE2D( _SprayPuff, sampler_SprayPuff, uv * 0.2 + sd ).x;
	float m2 = SAMPLE_TEXTURE2D( _SprayPuff, sampler_SprayPuff, uv * 0.55 + sd * 3.1 ).x;
	float veil = smoothstep( 0.28, 0.8, m1 * 0.7 + m2 * 0.3 + env * 0.3 - 0.12 ) * sqrt( env ) * ( 1.0 - t * 0.4 );
	float shape = isDrop ? drop : ( isLig ? lig : ( isSheet ? sheet : veil ) );
	// self-shadowing inside thick sheets; the forward-scattered sun lights up the thin parts
	float shade = ( isSheet && ! isClear ) ? 1.0 - dens * 0.15 : 1.0;
	float glow = isClear ? 1.0 - dens * 0.3 : ( isSheet ? 1.0 - dens * 0.6 : 1.0 );

	// soft intersections: opaque scene depth and the water surface under the particle
	float rawScene = LoadCameraDepth( input.positionCS.xy );
	float sceneEye = LinearEyeDepth( rawScene, _ZBufferParams );
	float posEye = input.positionCS.w;
	float soft = vMisc.z * 1.5 + 0.03;
	float fadeScene = saturate( ( sceneEye - posEye ) / soft );
	float fadeWater = saturate( ( input.posSim.y - vMisc.y ) / ( soft * 0.6 ) + 0.15 );
	float aOut = input.vCol.w * shape * fadeScene * fadeWater * ( 1.0 - farK * 0.4 );
	if ( aOut < 0.002 ) { discard; }
	return float4( ( input.vCol.rgb * shade + input.vFwd.xyz * glow ) * aOut, aOut );
}
