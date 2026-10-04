// The interactive boat wake as the water sees it: constants, the lattice noise shared with the simulation, and the readers
// (WakeSim.js: WGSL module `wake`). WakeSim.compute writes the textures this reads.
//
// Output of the simulation (display texture, RGBA16F): (h, dh/dx, dh/dz, foam), sampled linear / repeat; the aeration (bubbles in
// the water column, slower than the foam) and the boat's near-field template (the running mean of the wake height in the boat frame,
// subtracted under the hull so the boat does not feel its own steady wave) are read with Load.
//
//   WakeDisplacement( xz )      displacement of the wake surface at world xz (sim axes)
//   WakeFragment( xz, time )    slopes (dh/dx, dh/dz), foam, aeration (0..aerOut)
//   WakeHeight( xz ), WakeSample( xz ), WakeAeration( xz, time )
//
// Parameters (set by WakeSim): _TWWakeA = ( window centre xz, boat position xz ), _TWWakeB = ( boat rotation cos / sin yaw, residual
// dead zone ), _TWWakeC = ( amount, amplitude, aerOut ). amount 0 (no wake, or asleep) makes every reader return 0.
#ifndef TW_WAKE_INCLUDED
#define TW_WAKE_INCLUDED

#define WAKE_N 512u
#define WAKE_HALF 256u
#define WAKE_MASK 511u
#define WAKE_LOG2N 9u
#define WAKE_CELL 0.4
#define WAKE_SIZE 204.8
#define WAKE_TW 32u
#define WAKE_TH 88u
#define WAKE_TX0 (-1.6)
#define WAKE_TX1 1.6
#define WAKE_TZ0 (-4.4)
#define WAKE_TZ1 4.4

Texture2D<float4> _TWWakeDisplay;
Texture2D<float4> _TWWakeNear;
Texture2D<float4> _TWWakeAer;
SamplerState sampler_TWWakeLinearRepeat;
float4 _TWWakeA;
float4 _TWWakeB;
float4 _TWWakeC;

// integer lattice hash -> [0, 1), smooth value noise
float WakeHash( int ix, int iy )
{
	uint v = ( ( uint ) ix * 0x8da6b343u ) ^ ( ( uint ) iy * 0xd8163841u );
	v = ( v ^ ( v >> 13u ) ) * 0x5bd1e995u;
	v = v ^ ( v >> 15u );
	return ( float ) ( v >> 8u ) * ( 1.0 / 16777216.0 );
}

float WakeNoise( float2 q )
{
	float2 i = floor( q );
	float2 fr = frac( q );
	float2 u = fr * fr * ( 3.0 - 2.0 * fr );
	int ix = ( int ) i.x; int iy = ( int ) i.y;
	float a = WakeHash( ix, iy ); float b = WakeHash( ix + 1, iy );
	float c = WakeHash( ix, iy + 1 ); float d = WakeHash( ix + 1, iy + 1 );
	return lerp( lerp( a, b, u.x ), lerp( c, d, u.x ), u.y );
}

float WakeEdgeDist( float2 xz )
{
	float2 rel = abs( xz - _TWWakeA.xy );
	return WAKE_SIZE / 2.0 - max( rel.x, rel.y );
}
float WakeFade( float2 xz ) { return smoothstep( 4.0, 16.0, WakeEdgeDist( xz ) ) * _TWWakeC.x; }
// foam and aeration: a long fade toward the window edge, no visible end of the track
float WakeFadeLong( float2 xz ) { return smoothstep( 4.0, 45.0, WakeEdgeDist( xz ) ) * _TWWakeC.x; }

// template texel coordinates of world xz (boat frame)
float2 WakeNearCoord( float2 xz )
{
	float2 rel = xz - _TWWakeA.zw;
	float cs = _TWWakeB.x; float sn = _TWWakeB.y;
	float bx = rel.x * cs - rel.y * sn; float bz = rel.x * sn + rel.y * cs;
	return float2( ( bx - WAKE_TX0 ) * ( WAKE_TW / ( WAKE_TX1 - WAKE_TX0 ) ), ( bz - WAKE_TZ0 ) * ( WAKE_TH / ( WAKE_TZ1 - WAKE_TZ0 ) ) ) - 0.5;
}
bool WakeInTemplate( float2 tc ) { return tc.x > 0.0 && tc.y > 0.0 && tc.x < ( float ) ( WAKE_TW - 1u ) && tc.y < ( float ) ( WAKE_TH - 1u ); }

// (outside the simulated window, or asleep, both fades are 0: nothing to read)
bool WakeOff( float2 xz ) { return _TWWakeC.x <= 0.0 || WakeEdgeDist( xz ) <= 4.0; }

float4 WakeSample( float2 xz )
{
	if ( WakeOff( xz ) ) { return 0.0; }
	float4 s = _TWWakeDisplay.SampleLevel( sampler_TWWakeLinearRepeat, xz / WAKE_SIZE, 0.0 ) * float4( WakeFade( xz ).xxx, WakeFadeLong( xz ) );
	// the forced depression under the hull is covered by it: keep its edge out of the normals
	float2 tc = WakeNearCoord( xz );
	if ( WakeInTemplate( tc ) )
	{
		float m = _TWWakeNear.Load( int3( ( int2 ) ( tc + 0.5 ), 0 ) ).y;
		s = float4( s.x, s.yz * ( 1.0 - m ), s.w );
	}
	return s;
}

// aeration (0..aerOut): the aeration texture is read with Load (bilinear by hand), then mottled: a noise fixed in the water sets how
// milky each patch is and where the band's edge falls (soft, irregular), so it breaks into clouds and streaks instead of a uniform ribbon
float WakeAeration( float2 xz, float time )
{
	float o = 0.0;
	if ( WakeOff( xz ) ) { return o; }
	float2 tc = xz / WAKE_CELL - 0.5;
	int2 i = ( int2 ) floor( tc );
	float2 fr = frac( tc );
	int2 M = ( int2 ) WAKE_MASK;
	float a = lerp( lerp( _TWWakeAer.Load( int3( i & M, 0 ) ).x, _TWWakeAer.Load( int3( ( i + int2( 1, 0 ) ) & M, 0 ) ).x, fr.x ),
		lerp( _TWWakeAer.Load( int3( ( i + int2( 0, 1 ) ) & M, 0 ) ).x, _TWWakeAer.Load( int3( ( i + int2( 1, 1 ) ) & M, 0 ) ).x, fr.x ), fr.y );
	if ( a > 0.01 )
	{
		float m = WakeNoise( xz * 0.3 + float2( time * 0.02, 0.0 ) ) * 0.55 + WakeNoise( xz * 0.85 + 17.3 ) * 0.45;
		float aN = ( 1.0 - exp( a * -0.5 ) ) * ( m * 0.8 + 0.6 );
		o = smoothstep( m * 0.2 + 0.03, m * 0.2 + 0.55, aN ) * _TWWakeC.z * WakeFadeLong( xz );
	}

	return o;
}

float WakeHeight( float2 xz )
{
	if ( WakeOff( xz ) ) { return 0.0; }
	float h = _TWWakeDisplay.SampleLevel( sampler_TWWakeLinearRepeat, xz / WAKE_SIZE, 0.0 ).x;
	float2 tc = WakeNearCoord( xz );
	if ( WakeInTemplate( tc ) )
	{
		// under the hull only waves moving relative to it remain (an old wake being crossed); small residuals are sampling jitter of
		// the steep near field and are dropped (bilinear by hand)
		int2 i = ( int2 ) floor( tc );
		float2 fr = frac( tc );
		float4 a = _TWWakeNear.Load( int3( i, 0 ) ); float4 b = _TWWakeNear.Load( int3( i + int2( 1, 0 ), 0 ) );
		float4 c = _TWWakeNear.Load( int3( i + int2( 0, 1 ), 0 ) ); float4 d = _TWWakeNear.Load( int3( i + int2( 1, 1 ), 0 ) );
		float4 t = lerp( lerp( a, b, fr.x ), lerp( c, d, fr.x ), fr.y );
		float r = h - t.x;
		h = lerp( h, r * smoothstep( _TWWakeB.z, _TWWakeB.w, abs( r ) ), t.y );
	}

	return h * WakeFade( xz ) * _TWWakeC.y;
}

// Displacement (float3) of the wake surface at world xz (Lagrangian point of the ocean grid).
float3 WakeDisplacement( float2 xz ) { return float3( 0.0, WakeHeight( xz ), 0.0 ); }

// slopes: (dh/dx, dh/dz), foam, aeration (0..1, bubbles in the water column: the prop race and breaking; lingers ~25 s, widening)
struct WakeFrag { float2 slopes; float foam; float aeration; };
WakeFrag WakeFragment( float2 xz, float time )
{
	float4 s = WakeSample( xz );
	WakeFrag o;
	o.slopes = s.yz * _TWWakeC.y;
	o.foam = s.w;
	o.aeration = WakeAeration( xz, time );
	return o;
}

#endif
