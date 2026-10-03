// The vertex side of src/core/CDLOD.js for instances that carry their own per-level morph row (the ocean). The
// terrain has the same functions with a fixed array (Terrain/TerrainFragment.hlsl).
//   TWCdlodMorph( node, grid, m, viewPos, y0 ): node = ( origin x, origin z, size, lod ) in sim space, grid = the
//   vertex of the [0,1]^2 grid mesh, m = ( morph start, 1 / morph range, grid spacing, 0 ) of the node's LOD,
//   viewPos = the morph centre (sim space), y0 = height of the vertex (for the morph distance; 0 for the sea).
#ifndef TW_CDLOD_INCLUDED
#define TW_CDLOD_INCLUDED

struct TWCdlodVertex { float2 worldXZ; float spacing; float morphK; float lod; float size; };

// Morph in world space on the LOD's own vertex lattice (spacing h). Quarter nodes of a partially subdivided parent
// carry the parent's LOD, so their extra vertices first snap onto that lattice; every node covering a point then
// computes the same position.
TWCdlodVertex TWCdlodMorph( float4 node, float2 grid, float4 m, float3 viewPos, float y0 )
{
	float h = m.z;
	float2 p = node.xy + grid * node.z;
	float2 idx = floor( p / h + 1e-3 );
	float2 snapped = idx * h;
	float dist = length( viewPos - float3( snapped.x, y0, snapped.y ) );
	float morphK = clamp( ( dist - m.x ) * m.y, 0.0, 1.0 );
	float2 odd = frac( idx * 0.5 ) * 2.0;
	TWCdlodVertex o;
	o.worldXZ = snapped - odd * h * morphK;
	o.spacing = h * ( morphK + 1.0 );
	o.morphK = morphK;
	o.lod = node.w;
	o.size = node.z;
	return o;
}

#endif
