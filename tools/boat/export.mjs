// Export the procedural lobster boat to a .glb, for Blender.
//
//   node tools/boat/export.mjs [out.glb]
//
// The boat is built entirely in code (src/world/BoatModel.js + src/world/boat/*), and the builders
// depend only on the engine's maths/geometry layer — no scene, no renderer — so they run headless
// here. Each per-material bucket and each animated part becomes one glTF mesh, named as the game
// names it, with its transform baked in (the wheel, throttle, radar, propeller and rudder are all
// animated at run time: this is the boat at rest).
//
// Carried through per vertex: position, normal, uv, color (linear albedo, the paint) and aux
// (roughness, metalness, a pattern id and an animation weight) as a custom `_AUX` attribute. What can
// NOT come across is the look: the seven materials are WGSL (procedural non-skid, panel seams, grain,
// scuffs, the live radar/chart screens), so the vertex colours give the painted shape, not the finish.
//
// Coordinates: glTF is Y-up (as the game is: +Z forward, +Y up, +X port, metres), so Blender's
// importer lands it the right way up with no manual rotation. y = 0 is the design waterline.
import '../../test/headless.mjs';
import { writeFileSync } from 'node:fs';
import { BoatModel } from '../../src/world/BoatModel.js';

const OUT = process.argv[ 2 ] || 'assets/lobster_boat.glb';

const boat = new BoatModel();
boat.group.updateWorldMatrix( true, false );

// ---- collect every mesh, transform baked
const LAYOUT = { POSITION: 3, NORMAL: 3, TEXCOORD_0: 2, COLOR_0: 4, _AUX: 4 };
const meshes = [];
const accessors = [];
const views = [];
const chunks = [];
let offset = 0;

function push( data, ArrayType, type, target, extra = {} ) {

	const bytes = new Uint8Array( data.buffer, data.byteOffset, data.byteLength );
	const pad = ( 4 - offset % 4 ) % 4;
	if ( pad ) { chunks.push( new Uint8Array( pad ) ); offset += pad; }
	const view = views.push( { buffer: 0, byteOffset: offset, byteLength: bytes.length, ...( target ? { target } : {} ) } ) - 1;
	chunks.push( bytes );
	offset += bytes.length;
	const count = data.length / ( { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 } )[ extra.type || { 3: 'VEC3', 2: 'VEC2', 4: 'VEC4', 1: 'SCALAR' }[ LAYOUT[ extra.name ] || 1 ] || 'VEC3' ];
	accessors.push( { bufferView: view, componentType: ArrayType, count, type: extra.type || ( { 3: 'VEC3', 2: 'VEC2', 4: 'VEC4', 1: 'SCALAR' } )[ LAYOUT[ extra.name ] || 1 ] || 'VEC3', ...( extra.min ? { min: extra.min, max: extra.max } : {} ) } );
	return accessors.length - 1;

}

for ( const [ name, mesh ] of Object.entries( boat.meshes ) ) {

	const g = mesh.geometry, a = g.attributes;
	const n = a.position.count;
	const m = mesh.matrixWorld.elements;
	const xf = ( i, out ) => {

		const x = a.position.array[ i * 3 ], y = a.position.array[ i * 3 + 1 ], z = a.position.array[ i * 3 + 2 ];
		out[ 0 ] = m[ 0 ] * x + m[ 4 ] * y + m[ 8 ] * z + m[ 12 ];
		out[ 1 ] = m[ 1 ] * x + m[ 5 ] * y + m[ 9 ] * z + m[ 13 ];
		out[ 2 ] = m[ 2 ] * x + m[ 6 ] * y + m[ 10 ] * z + m[ 14 ];

	};
	const pos = new Float32Array( n * 3 ), nor = new Float32Array( n * 3 ), uv = new Float32Array( n * 2 );
	const col = new Float32Array( n * 4 ), aux = new Float32Array( n * 4 );
	const p = [ 0, 0, 0 ];
	const min = [ Infinity, Infinity, Infinity ], max = [ - Infinity, - Infinity, - Infinity ];
	for ( let i = 0; i < n; i ++ ) {

		xf( i, p );
		pos.set( p, i * 3 );
		for ( let k = 0; k < 3; k ++ ) {

			if ( p[ k ] < min[ k ] ) min[ k ] = p[ k ];
			if ( p[ k ] > max[ k ] ) max[ k ] = p[ k ];
			// rigid transform: the same 3x3 rotates the normal
			const nx = a.normal.array[ i * 3 ], ny = a.normal.array[ i * 3 + 1 ], nz = a.normal.array[ i * 3 + 2 ];
			nor[ i * 3 + k ] = [ m[ 0 ] * nx + m[ 4 ] * ny + m[ 8 ] * nz, m[ 1 ] * nx + m[ 5 ] * ny + m[ 9 ] * nz, m[ 2 ] * nx + m[ 6 ] * ny + m[ 10 ] * nz ][ k ];

		}

		if ( a.uv ) uv.set( [ a.uv.array[ i * 2 ], a.uv.array[ i * 2 + 1 ] ], i * 2 );
		if ( a.color ) col.set( [ a.color.array[ i * 3 ], a.color.array[ i * 3 + 1 ], a.color.array[ i * 3 + 2 ], 1 ], i * 4 );
		else col.set( [ 1, 1, 1, 1 ], i * 4 );
		if ( a.aux ) aux.set( [ a.aux.array[ i * 4 ], a.aux.array[ i * 4 + 1 ], a.aux.array[ i * 4 + 2 ], a.aux.array[ i * 4 + 3 ] ], i * 4 );

	}

	const idx = new Uint32Array( g.index ? g.index.array : n );
	if ( ! g.index ) for ( let i = 0; i < n; i ++ ) idx[ i ] = i;
	const tris = idx.length / 3;
	const prim = {
		attributes: {
			POSITION: push( pos, 5126, 'VEC3', 34962, { name: 'POSITION', min, max } ),
			NORMAL: push( nor, 5126, 'VEC3', 34962, { name: 'NORMAL' } ),
			TEXCOORD_0: push( uv, 5126, 'VEC2', 34962, { name: 'TEXCOORD_0' } ),
			COLOR_0: push( col, 5126, 'VEC4', 34962, { name: 'COLOR_0' } ),
			_AUX: push( aux, 5126, 'VEC4', 34962, { name: '_AUX' } ),
		},
		indices: push( idx, 5125, 'SCALAR', 34963, { type: 'SCALAR' } ),
		material: meshes.length,
	};
	meshes.push( { name, primitives: [ prim ], tris, verts: n } );
	console.log( `  ${ name.padEnd( 10 ) } ${ String( tris ).padStart( 6 ) } tris  ${ String( n ).padStart( 6 ) } verts` );

}

const bin = Buffer.concat( chunks.map( ( c ) => Buffer.from( c ) ) );
const gltf = {
	asset: { version: '2.0', generator: 'tidewater tools/boat/export.mjs' },
	scene: 0,
	scenes: [ { name: 'LobsterBoat', nodes: meshes.map( ( _, i ) => i ) } ],
	nodes: meshes.map( ( m ) => ( { name: 'boat-' + m.name, mesh: meshes.indexOf( m ) } ) ),
	meshes: meshes.map( ( m ) => ( { name: 'boat-' + m.name, primitives: m.primitives } ) ),
	// materials are placeholders: the paint is in COLOR_0, and the real look is WGSL that has no glTF
	materials: meshes.map( ( m ) => ( { name: 'boat-' + m.name, pbrMetallicRoughness: { baseColorFactor: [ 1, 1, 1, 1 ], metallicFactor: 0, roughnessFactor: 0.6 } } ) ),
	accessors, bufferViews: views, buffers: [ { byteLength: bin.length } ],
};
let json = Buffer.from( JSON.stringify( gltf ), 'utf8' );
while ( json.length % 4 ) json = Buffer.concat( [ json, Buffer.from( ' ' ) ] );
let binPad = bin;
while ( binPad.length % 4 ) binPad = Buffer.concat( [ binPad, Buffer.from( [ 0 ] ) ] );
const total = 12 + 8 + json.length + 8 + binPad.length;
const head = Buffer.alloc( 12 + 8 ); // magic, version, total, then the JSON chunk header
head.write( 'glTF', 0, 'ascii' );
head.writeUInt32LE( 2, 4 );
head.writeUInt32LE( total, 8 );
head.writeUInt32LE( json.length, 12 );
head.write( 'JSON', 16, 'ascii' );
const binHead = Buffer.alloc( 8 ); // the BIN chunk header sits after the JSON chunk, not in the head
binHead.writeUInt32LE( binPad.length, 0 );
binHead.write( 'BIN\0', 4, 'ascii' );
writeFileSync( OUT, Buffer.concat( [ head, json, binHead, binPad ] ) );

const totalTris = meshes.reduce( ( s, m ) => s + m.tris, 0 );
console.log( `\n${ meshes.length } meshes, ${ totalTris } triangles, ${ ( total / 1e6 ).toFixed( 2 ) } MB -> ${ OUT }` );
console.log( `boat.triangleCount = ${ boat.triangleCount }  (must match)` );
console.log( `LOA ${ boat.dimensions.length.toFixed( 2 ) } m, beam ${ boat.dimensions.beam.toFixed( 2 ) } m, draft ${ boat.dimensions.draft.toFixed( 2 ) } m` );
if ( totalTris !== boat.triangleCount ) { console.log( 'MISMATCH: the export is not the whole boat' ); process.exit( 1 ); }
