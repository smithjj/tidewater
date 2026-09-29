// Load a static glTF/GLB prop and merge its primitives into one mesh per material.
//
// An authored or scanned prop arrives as hundreds of primitives (the lobster trap is 516 of them)
// sharing a handful of materials; the draw calls are what costs, so everything a material touches is
// welded into a single geometry. Same approach as the boats (world/boats/Pelagic30.js), kept here as
// the general path for props loaded from a file.
//
//   const model = await loadStaticModel( url, { skip: ( materialName ) => skipIt } );
//   model.root     Group of merged meshes, in the model's own frame (move/rotate/scale the group)
//   model.bounds   Box3 of everything that was kept
//   model.materials  the kept engine materials, by glTF material index
import { Group, Mesh, Matrix3, Matrix4, Vector3, Quaternion, BufferGeometry, Float32BufferAttribute,
	Uint16BufferAttribute, Uint32BufferAttribute, DoubleSide, Color, Box3 } from '../engine/index.js';
import { loadGLB } from '../engine/loaders/GLTF.js';
import { physical } from '../materials/Materials.js';

export async function loadStaticModel( url, { skip = null, name = 'static', anchor = null } = {} ) {

	const gltf = await loadGLB( url );
	const buckets = new Map(); // material index -> { pos, nor, uv, idx }

	const walk = ( i, parent ) => {

		const n = gltf.nodes[ i ];
		const local = new Matrix4().compose(
			new Vector3( n.t[ 0 ], n.t[ 1 ], n.t[ 2 ] ),
			new Quaternion( n.r[ 0 ], n.r[ 1 ], n.r[ 2 ], n.r[ 3 ] ),
			new Vector3( n.s[ 0 ], n.s[ 1 ], n.s[ 2 ] ),
		);
		const world = parent ? new Matrix4().multiplyMatrices( parent, local ) : local;
		if ( n.mesh !== undefined ) for ( const prim of gltf.meshes[ n.mesh ] ) addPrim( prim, world, buckets, gltf, skip );
		for ( const c of n.children ) walk( c, world );

	};
	for ( const r of gltf.roots ) walk( r, null );

	const root = new Group();
	root.name = name;
	const materials = new Map();
	for ( const [ mi, b ] of buckets ) {

		const geo = new BufferGeometry();
		geo.setAttribute( 'position', new Float32BufferAttribute( b.pos, 3 ) );
		geo.setAttribute( 'normal', new Float32BufferAttribute( b.nor, 3 ) );
		geo.setAttribute( 'uv', new Float32BufferAttribute( b.uv, 2 ) );
		geo.setIndex( b.pos.length / 3 > 65535 ? new Uint32BufferAttribute( b.idx, 1 ) : new Uint16BufferAttribute( b.idx, 1 ) );
		geo.computeBoundingBox();
		geo.computeBoundingSphere();
		const mat = materialFor( gltf.materials[ mi ] );
		materials.set( mi, mat );
		const mesh = new Mesh( geo, mat );
		mesh.name = `${ name }-${ ( gltf.materials[ mi ] && gltf.materials[ mi ].name ) || mi }`;
		root.add( mesh );

	}

	if ( anchor ) root.position.copy( anchor );

	return { root, materials, bounds: boundsOf( root ), info: gltf };

}

function addPrim( prim, world, buckets, gltf, skip ) {

	if ( prim.mode !== 4 || ! prim.indices ) return;
	const mi = prim.material ?? 0;
	const g = gltf.materials[ mi ];
	if ( skip && skip( ( g && g.name ) || '' ) ) return;
	let b = buckets.get( mi );
	if ( ! b ) buckets.set( mi, b = { pos: [], nor: [], uv: [], idx: [] } );
	const e = world.elements, ne = new Matrix3().getNormalMatrix( world ).elements;
	const A = prim.attributes;
	const pos = A.POSITION.array, nor = A.NORMAL ? A.NORMAL.array : null, uv = A.TEXCOORD_0 ? A.TEXCOORD_0.array : null;
	const count = pos.length / 3;
	const base = b.pos.length / 3;
	for ( let i = 0; i < count; i ++ ) {

		const x = pos[ i * 3 ], y = pos[ i * 3 + 1 ], z = pos[ i * 3 + 2 ];
		b.pos.push( e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z + e[ 12 ], e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z + e[ 13 ], e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z + e[ 14 ] );
		if ( nor ) {

			const nx = nor[ i * 3 ], ny = nor[ i * 3 + 1 ], nz = nor[ i * 3 + 2 ];
			const tx = ne[ 0 ] * nx + ne[ 3 ] * ny + ne[ 6 ] * nz;
			const ty = ne[ 1 ] * nx + ne[ 4 ] * ny + ne[ 7 ] * nz;
			const tz = ne[ 2 ] * nx + ne[ 5 ] * ny + ne[ 8 ] * nz;
			const l = Math.hypot( tx, ty, tz ) || 1;
			b.nor.push( tx / l, ty / l, tz / l );

		} else b.nor.push( 0, 1, 0 );

		b.uv.push( uv ? uv[ i * 2 ] : 0, uv ? uv[ i * 2 + 1 ] : 0 );

	}

	for ( let i = 0; i < prim.indices.length; i ++ ) b.idx.push( base + prim.indices[ i ] );

}

// glTF material factors -> engine physical material (the props here are factor-based, no textures)
function materialFor( g ) {

	g = g || {};
	const pbr = g.pbrMetallicRoughness || {};
	const c = pbr.baseColorFactor || [ 0.8, 0.8, 0.8, 1 ];
	return physical( {
		color: new Color().setRGB( c[ 0 ], c[ 1 ], c[ 2 ] ),
		roughness: pbr.roughnessFactor ?? 1,
		metalness: pbr.metallicFactor ?? 1,
		side: DoubleSide, // the exports are all doubleSided
	} );

}

function boundsOf( root ) {

	const box = new Box3();
	box.makeEmpty();
	for ( const m of root.children ) {

		m.geometry.computeBoundingBox();
		box.union( m.geometry.boundingBox );

	}

	return box;

}
