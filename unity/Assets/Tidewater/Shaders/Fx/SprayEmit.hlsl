// The emitter API of the spray particles (the `spray` module of src/fx/Spray.js) for GPU kernels (Breakers): reserve ring slots
// of the GPU part through an atomic head and write particles into them.
//   uint  SprayReserve( n )                       atomic add on the GPU head, returns the base
//   uint  SpraySlot( base, i )                    ring slot of the i-th reserved particle
//   void  SprayWrite( slot, p, v, size, kind, life, seed )
//   float SprayRand( a, b )                       hash with this frame's seed, [0, 1)
//   SPRAY_DROPLET / SPRAY_MIST / SPRAY_LIGAMENT / SPRAY_SPRAY / SPRAY_SHEET
// The includer binds the buffers with Spray.Bind( cs, kernel ). Particle layout (float4 per slot):
//   pos: xyz, age (s) | vel: xyz, radius (m) | info: kind, life (s, 0 = dead), water height, tag
// (tag: fraction = random seed, integer part = which breaking crest made it, 0 = none)
#ifndef TW_SPRAY_EMIT_INCLUDED
#define TW_SPRAY_EMIT_INCLUDED

#define SPRAY_DROPLET 0.0
#define SPRAY_MIST 1.0
#define SPRAY_LIGAMENT 2.0
#define SPRAY_SPRAY 3.0
#define SPRAY_SHEET 4.0

RWStructuredBuffer<float4> _SprayPos;
RWStructuredBuffer<float4> _SprayVel;
RWStructuredBuffer<float4> _SprayInfo;
RWStructuredBuffer<uint> _SprayHead;
uint4 _SprayCap;              // GPU capacity (power of two), CPU capacity, total, frame seed
uint SprayNG() { return _SprayCap.x; }

// PCG hash of a uint -> [0, 1)
float SprayHash( uint seed )
{
	uint state = seed * 747796405u + 2891336453u;
	uint word = ( ( state >> ( ( state >> 28u ) + 4u ) ) ^ state ) * 277803737u;
	return float( ( word >> 22u ) ^ word ) * ( 1.0 / 4294967296.0 );
}

uint SprayReserve( uint n )
{
	uint b;
	InterlockedAdd( _SprayHead[ 0 ], n, b );
	return b;
}

uint SpraySlot( uint b, uint i ) { return ( b + i ) & ( _SprayCap.x - 1u ); }

void SprayWrite( uint slot, float3 p, float3 v, float size, float kind, float life, float seed )
{
	_SprayPos[ slot ] = float4( p, 0.0 );
	_SprayVel[ slot ] = float4( v, size );
	_SprayInfo[ slot ] = float4( kind, life, p.y, seed );
}

float SprayRand( uint a, uint b )
{
	return SprayHash( a + b * 1664525u + _SprayCap.w * 2654435761u );
}

#endif
