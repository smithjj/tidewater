// Shared by dump-mini-boat.mjs and dump-pelagic.mjs: writes the geometry of a glTF factor boat (MiniFishingBoat.js, Pelagic30.js) for Unity as
// Assets/Tidewater/Resources/<resource>.bytes, the very geometry the browser builds from the file (node transforms baked in, the studio extras
// left out, one merged mesh per glTF material, the hull mask triangles).
// File: uint32 header length, the JSON header (materials: name, colour, roughness, metalness, emissive, clearcoat, clearcoatRoughness, glass,
// opacity, vertices, indices, byte offset into the data; mask: vertices, indices, offset), then per block: float32 position, normal (3 each),
// uv (2), then uint32 indices.
import fs from 'node:fs';
import path from 'node:path';

export const v3 = ( p ) => [ p.x, p.y, p.z ];

// The water-exclusion mask reads positions only. Pelagic30.js collects it triangle by triangle (three vertices each, 614 k of them): weld equal
// positions so the file is a third of the size; the same triangles come out.
function weld( geo ) {

	const pos = geo.attributes.position.array, nor = geo.attributes.normal.array, idx = geo.index.array;
	const map = new Map(), P = [], N = [], I = new Uint32Array( idx.length );
	for ( let k = 0; k < idx.length; k ++ ) {

		const i = idx[ k ], key = pos[ i * 3 ] + ',' + pos[ i * 3 + 1 ] + ',' + pos[ i * 3 + 2 ];
		let j = map.get( key );
		if ( j === undefined ) { j = P.length / 3; map.set( key, j ); P.push( pos[ i * 3 ], pos[ i * 3 + 1 ], pos[ i * 3 + 2 ] ); N.push( nor[ i * 3 ], nor[ i * 3 + 1 ], nor[ i * 3 + 2 ] ); }
		I[ k ] = j;

	}

	return { attributes: { position: { array: P, count: P.length / 3 }, normal: { array: N } }, index: { array: I } };

}

export function writeFactorBoat( boat, root, resource, { weldMask = false } = {} ) {

	const blocks = [], header = { materials: [], mask: null };
	let off = 0;
	const push = ( geo ) => {

		const A = geo.attributes, n = A.position.count;
		const uv = A.uv ? Float32Array.from( A.uv.array ) : new Float32Array( n * 2 );
		const parts = [ Float32Array.from( A.position.array ), Float32Array.from( A.normal.array ), uv, Uint32Array.from( geo.index.array ) ];
		for ( const p of parts ) blocks.push( Buffer.from( p.buffer, p.byteOffset, p.byteLength ) );
		const o = off;
		off += parts.reduce( ( s, p ) => s + p.byteLength, 0 );
		return { vertices: n, indices: geo.index.array.length, offset: o };

	};

	for ( const mesh of boat.group.children ) {

		const m = mesh.material;
		header.materials.push( {
			name: m.name, color: [ m.color.r, m.color.g, m.color.b ], roughness: m.roughness, metalness: m.metalness,
			emissive: [ m.emissive.r, m.emissive.g, m.emissive.b ], clearcoat: m.clearcoat ?? 0, clearcoatRoughness: m.clearcoatRoughness ?? 0.1,
			glass: mesh.name === 'boat-glass' ? 1 : 0, opacity: m.opacity ?? 1,
			...push( mesh.geometry ),
		} );

	}

	header.mask = push( weldMask ? weld( boat.createHullVolumeGeometry() ) : boat.createHullVolumeGeometry() );
	const head = Buffer.from( JSON.stringify( header ) );
	const len = Buffer.alloc( 4 ); len.writeUInt32LE( head.length );
	const dest = path.join( root, 'unity/Assets/Tidewater/Resources', resource + '.bytes' );
	fs.mkdirSync( path.dirname( dest ), { recursive: true } );
	fs.writeFileSync( dest, Buffer.concat( [ len, head, ...blocks ] ) );
	console.log( header.materials.map( ( m ) => `${ m.name }${ m.glass ? ' (glass)' : '' }: ${ m.vertices } v, ${ m.indices / 3 } tris` ).join( '\n' ) );
	console.log( `mask: ${ header.mask.vertices } v, ${ header.mask.indices / 3 } tris; ${ off } data bytes -> ${ dest }` );

}
