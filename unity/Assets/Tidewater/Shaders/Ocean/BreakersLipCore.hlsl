// Vertex and fragment of BreakersLip.shader.
#define BRK_NV 20.0           // profile vertices across the lip (2 on the back of the crest + 18 along the curtain)
#define BRK_LACE_TILE 3.5     // m per tile of the lace texture (LaceTexture.LACE_TILE)

StructuredBuffer<float4> _BrkCrest;      // per station and slot: (root.xyz, b) (back.xyz, H) (dir.xz, trough y, wave id)
float4 _BrkLip;                          // sheet opacity multiplier, station spacing (m), 0, 0
float4 _TWSunDir;                        // toward the sun, sim space
float4 _TWSunColor;                      // sun illuminance (lux) x colour
float4 _TWShoreE;                        // x = frame.time (s), y = sea level
TEXTURE2D(_BrkLace); SAMPLER(sampler_BrkLace);   // the lace pattern (repeat, trilinear, anisotropic)

// exact unpolarized dielectric Fresnel (the same as WaterMaterial's fresnelDielectric), cosI > 0, eta = n2/n1
float BrkFresnel( float cosI, float eta )
{
	float c = clamp( cosI, 0.0, 1.0 );
	float g2 = eta * eta - 1.0 + c * c;
	float g = sqrt( max( g2, 0.0 ) );
	float a = ( g - c ) / ( g + c );
	float b = ( c * ( g + c ) - 1.0 ) / ( c * ( g - c ) + 1.0 );
	return g2 < 0.0 ? 1.0 : 0.5 * a * a * ( b * b + 1.0 );
}

float3 BrkSkyRadiance( float3 dirSim )
{
	return SampleSkyTexture( float3( dirSim.x, dirSim.y, -dirSim.z ), 0.0, 0 ).rgb * GetCurrentExposureMultiplier();
}

struct Varyings
{
	float4 positionCS : SV_POSITION;
	float3 vLipN : TEXCOORD0;
	float4 vLip : TEXCOORD1;     // v, b, along, curtain length
	float vLipFade : TEXCOORD2;
	float3 posSim : TEXCOORD3;
};

Varyings Vert( uint vid : SV_VertexID )
{
	// strips of NV - 1 quads, two triangles each: (a, c, b), (b, c, d) with a = ( side 0, k ), b = ( 0, k + 1 ), c = ( 1, k ), d = ( 1, k + 1 )
	static const uint sideTbl[ 6 ] = { 0, 1, 0, 0, 1, 1 };
	static const uint kTbl[ 6 ] = { 0, 0, 1, 1, 0, 1 };
	uint qd = vid / 6u;
	uint r = vid % 6u;
	uint nq = ( uint ) BRK_NV - 1u;
	uint strip = qd / nq;
	uint seg = strip / 2u;
	uint slot = strip % 2u;
	uint side = sideTbl[ r ];
	float k = float( qd % nq + kTbl[ r ] );

	Varyings o = ( Varyings ) 0;
	uint st = seg + side;
	uint ot = seg + ( 1u - side );
	uint e = st * 6u + slot * 3u;
	uint eo = ot * 6u + slot * 3u;
	float4 c0 = _BrkCrest[ e ];
	float4 c1 = _BrkCrest[ e + 1u ];
	float4 c2 = _BrkCrest[ e + 2u ];
	float mOther = _BrkCrest[ eo + 2u ].w;
	float bOther = _BrkCrest[ eo ].w;
	// (the crests are followed until the bore reaches the shore; the sheet is gone after the plunge)
	// (port: both ends of a segment must pass the b test, else a vertex pair collapses to one side only and the quad between them
	// draws as a long sliver down to y = -1e5 where b jumps between stations)
	bool valid = c2.w > 0.5 && abs( mOther - c2.w ) < 0.5 && c0.w < 1.25 && bOther < 1.25;

	float3 root = c0.xyz;
	float b = c0.w;
	float3 back = c1.xyz;
	float H = c1.w;
	float3 d3 = float3( c2.x, 0.0, c2.y );
	float trough = c2.z;
	float q = clamp( b / 0.9, 0.0, 1.35 );
	float Xi = max( H * 0.8, 0.05 );
	float Yi = max( root.y - trough, 0.05 );
	// profile parameter: k = 0, 1 on the back of the crest (-1, -0.45), then 0..1 along the curtain
	float pv = k < 0.5 ? -1.0 : ( k < 1.5 ? -0.45 : ( k - 2.0 ) / ( BRK_NV - 3.0 ) );
	float xl = Xi * q * max( pv, 0.0 );
	float fl = xl / Xi;
	float yl = -Yi * ( fl * fl ); // ballistic: the jet leaves the crest horizontally
	float3 onLip = root + d3 * xl + float3( 0.0, yl, 0.0 );
	float3 onCap = lerp( root, back, max( -pv, 0.0 ) ) + float3( 0.0, 0.012, 0.0 );
	float3 P = pv < 0.0 ? onCap : onLip;
	// outward normal of the curtain (upper surface of the lip)
	float slope = Yi * 2.0 * xl / ( Xi * Xi );
	o.vLipN = normalize( float3( 0.0, 1.0, 0.0 ) + d3 * slope );
	o.vLip = float4( pv, b, float( st ) * _BrkLip.y, Xi * q + Yi * q * q ); // w: curtain length (m)
	// the cap fades in over the crest; the whole lip fades out once it has become whitewater
	float capA = smoothstep( -1.0, -0.1, pv );
	// once the jet has re-entered the water the curtain is gone (the splash and the roller take over)
	o.vLipFade = capA * smoothstep( 0.02, 0.12, q ) * ( 1.0 - smoothstep( 1.0, 1.2, b ) );

	if ( valid )
	{
		float3 posRWS = float3( P.x, P.y, -P.z ) - _WorldSpaceCameraPos.xyz;
		o.positionCS = TransformWorldToHClip( posRWS );
	}
	else
	{
		o.positionCS = float4( 0.0, 0.0, 2.0, 1.0 );
	}

	o.posSim = P;
	return o;
}

float4 Frag( Varyings input, bool isFront : SV_IsFrontFace ) : SV_Target
{
	float v = input.vLip.x;
	float b = input.vLip.y;
	float a = input.vLip.z;
	float len = input.vLip.w;
	float3 pos = input.posSim;
	float3 camSim = float3( _WorldSpaceCameraPos.x, _WorldSpaceCameraPos.y, -_WorldSpaceCameraPos.z );
	float exposure = GetCurrentExposureMultiplier();
	float3 skyIrradiance = EvaluateAmbientProbe( float3( 0.0, 1.0, 0.0 ) ) * exposure;
	float3 V = normalize( camSim - pos );
	float3 Nw = normalize( input.vLipN );
	float3 N0 = isFront ? Nw : -Nw;
	float t = _TWShoreE.x;
	// The water of the jet is stretched along the flow: streaks and ripples running down the curtain (the lace pattern stretched ~8x
	// along the jet and moving with it) in its surface (normal, from the screen-space gradient of a small relief) and in its thickness
	float flowS = v * len - t * 1.1;
	// along-crest coordinate warped by low-frequency noise (three incommensurate scales, slope kept below 1 so it never folds): the
	// streak spacing drifts along the crest, no fixed period, and the tiles of every pattern below never line up into a comb
	float aw = a + sin( a * 0.23 + 1.7 ) * 1.6 + sin( a * 0.61 + 4.2 ) * 0.4 + sin( a * 1.37 + 0.4 ) * 0.12;
	float4 sv = SAMPLE_TEXTURE2D( _BrkLace, sampler_BrkLace, float2( aw / 0.83, flowS / 3.0 ) );
	// and broad bands (sections of the lip thicker or thinner than others), visible from afar
	float4 sbv = SAMPLE_TEXTURE2D( _BrkLace, sampler_BrkLace, float2( aw / 2.9, flowS / 9.0 ) + float2( 0.37, 0.61 ) );
	float sb = sbv.z;
	float hS = sv.x * 0.02 + sv.z * 0.012;
	float3 dpx = ddx( pos );
	float3 dpy = ddy( pos );
	float3 r1 = cross( dpy, N0 );
	float3 r2 = cross( N0, dpx );
	float det = dot( dpx, r1 );
	float3 grad = ( r1 * ddx( hS ) + r2 * ddy( hS ) ) * sign( det );
	float3 N = normalize( N0 * abs( det ) - grad + N0 * 1e-9 );
	float NdV = max( dot( N, V ), 1e-3 );
	float F = BrkFresnel( NdV, 1.333 );
	float3 L = normalize( _TWSunDir.xyz );
	float3 sun = _TWSunColor.rgb * exposure;   // (no clouds yet: no cloud shadow)

	// reflection: sky + sun glint
	float3 Rr = reflect( -V, N );
	float3 R = normalize( float3( Rr.x, max( Rr.y, 0.004 ), Rr.z ) );
	float3 refl = BrkSkyRadiance( R );
	float3 Hh = normalize( L + V );
	float3 spec = sun * ( pow( max( dot( N, Hh ), 0.0 ), 180.0 ) * 12.0 ) * F;

	// light through the thin sheet: turquoise when backlit
	float3 tint = float3( 0.16, 0.62, 0.56 );
	float back = pow( saturate( dot( -V, L ) * 0.5 + 0.5 ), 4.0 );
	// thick and deep green at the root, thin, bright and clear toward the tip, uneven along the streaks
	float thin = lerp( 1.5, 0.7, v ) * ( sv.z * 0.3 + 0.8 ) * ( sb * 0.8 + 0.6 );
	float3 glow = ( sun * tint * ( back * 0.9 + 0.08 ) + skyIrradiance * tint * 1.4 ) * thin;
	float aW = F + min( ( 1.0 - F ) * lerp( 0.42, 0.16, v ) * ( sv.x * 0.5 + 0.75 ) * ( sb * 0.7 + 0.65 ), 0.85 );
	float3 cW = refl * F + spec + glow * ( 1.0 - F ) * 0.42;

	// The jet stays clear, glassy water while it is in the air: only its leading edge tears into aerated fingers (filaments of the lace
	// pattern stretched along the flow, merging into a ragged white rim). Sections of the lip differ a little.
	float flowM = v * len - t * 1.1; // metres down the curtain, moving with the jet
	float2 fuv = float2( aw / 1.9 + 0.53, flowM / 1.8 );
	float4 fl = SAMPLE_TEXTURE2D( _BrkLace, sampler_BrkLace, fuv );
	float sect = fl.z; // slowly varying along the crest
	float vary = sect - 0.5 + sin( aw * 0.29 + b * 2.1 ) * 0.15;
	float reach = v + vary * 0.3;
	// slightly irregular leading edge (sections of the lip reach a little further than others)
	float tipN = sin( aw * 1.3 + t * 0.3 ) * 0.6 + sin( aw * 4.7 + 1.3 ) * 0.4;
	float edge = v + tipN * 0.03 + vary * 0.04;
	float thrown = smoothstep( 0.35, 0.8, b );
	// a thin, translucent aerated rim along the leading edge (it tears into the drops and ligaments the spray system throws off it)
	float rim = smoothstep( 0.9, 0.96, edge ) * ( fl.z * 0.2 + 0.2 ) * thrown;
	// once the lip has landed the curtain is a falling mass of whitewater that dissolves (blotchy) into the splash-up and the roller
	// within a fraction of a second
	// (in streaks: white fingers run down the curtain ahead of the rest, so along a peeling crest the broken section feathers into the
	// clear one instead of ending on a vertical line)
	// (each finger its own length and brightness: the fine strands' per-cell random, gated and grouped by the broad pattern so fingers
	// cluster, merge and leave gaps instead of a regular row)
	float fing = ( sv.w * 0.55 + sv.z * 0.2 ) * ( smoothstep( 0.25, 0.75, sbv.w * 0.6 + sb * 0.4 ) * 0.8 + 0.2 ) * 0.34;
	float wh = smoothstep( ( 1.0 - v ) * 0.18 + 0.9 - fing, ( 1.0 - v ) * 0.18 + 1.0 - fing, b ); // from the tip up
	float4 lc = SAMPLE_TEXTURE2D( _BrkLace, sampler_BrkLace, float2( aw * 0.83 + 1.9, v * len - t * 1.3 ) / ( BRK_LACE_TILE * 0.8 ) );
	float blot = lc.z * 0.7 + lc.y * 0.3;
	float gone = smoothstep( 1.0, 1.25, b );
	float clumpW = smoothstep( gone - 0.12, gone + 0.12, blot );
	float clumps = saturate( ( blot - gone ) * 2.5 ); // thicker inside the blotches
	// thin aerated filaments stretched down the curtain (foam of the previous wave drawn up the face and thrown out with the lip), more
	// of them toward the tip
	float fil = ( 1.0 - smoothstep( 0.02, 0.12, sv.x ) ) * smoothstep( 0.15, 0.8, v ) * ( sv.w * 0.5 + 0.25 ) * ( sb * 0.9 + 0.3 ) * thrown;
	float aer = lerp( max( rim, fil ), 0.95, wh );
	// aerated water is a dense scatterer: bright from every side, glowing when backlit
	float3 foamLit = ( sun * ( max( dot( N, L ), 0.0 ) * 0.5 + 0.5 + back * 1.2 ) / PI + skyIrradiance ) * 0.9;

	float tip = 1.0 - smoothstep( 0.93, 1.0, edge );
	float alpha = input.vLipFade * tip * _BrkLip.x * lerp( 1.0, clumpW, wh );
	float aF = aer * 0.92;
	float3 col = foamLit * lerp( 1.0, clumps * 0.4 + 0.75, wh ) * aF + cW * ( 1.0 - aF );
	float aOut = ( aF + aW * ( 1.0 - aF ) ) * alpha;
	return float4( col * alpha, aOut );
}
