// Writes the mini fishing boat's geometry for Unity: Assets/Tidewater/Resources/mini-fishing-boat.bytes, from the JS MiniFishingBoat.load()
// (the glb read with the node transforms baked in, the studio extras left out, one merged mesh per glTF material, the hull mask triangles),
// so the Unity boat is the very geometry the browser builds. Also writes the physics adapter's numbers as <outDir>/mini-boat.json (the oracle).
//   node unity/tools/dump-mini-boat.mjs [outDir]
// File: uint32 header length, the JSON header (materials: name, colour, roughness, metalness, emissive, clearcoat, clearcoatRoughness,
// vertices, indices, byte offset into the data; mask: vertices, indices, offset), then per block: float32 position, normal (3 each),
// uv (2), then uint32 indices.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/mini' );
fs.mkdirSync( outDir, { recursive: true } );

globalThis.__assetFile = async ( url ) => {

	const b = fs.readFileSync( path.join( root, 'public', url ) );
	return b.buffer.slice( b.byteOffset, b.byteOffset + b.byteLength );

};

const { MiniFishingBoat } = await import( root + '/src/world/boats/MiniFishingBoat.js' );
const boat = new MiniFishingBoat();
await boat.load();

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
		...push( mesh.geometry ),
	} );

}

header.mask = push( boat.createHullVolumeGeometry() );
const head = Buffer.from( JSON.stringify( header ) );
const len = Buffer.alloc( 4 ); len.writeUInt32LE( head.length );
const dest = path.join( root, 'unity/Assets/Tidewater/Resources/mini-fishing-boat.bytes' );
fs.mkdirSync( path.dirname( dest ), { recursive: true } );
fs.writeFileSync( dest, Buffer.concat( [ len, head, ...blocks ] ) );

// the physics adapter (what BoatController and the Player read), for MiniBoatOracle
const v = ( p ) => [ p.x, p.y, p.z ];
fs.writeFileSync( path.join( outDir, 'mini-boat.json' ), JSON.stringify( {
	hullSamples: boat.hullSamples.map( ( s ) => ( { p: v( s.position ), area: s.area, bottomY: s.bottomY } ) ),
	reserveSamples: boat.reserveSamples.map( ( s ) => ( { p: v( s.position ), area: s.area } ) ),
	hydro: { suggestedMass: boat.hydro.suggestedMass, centerOfMass: v( boat.hydro.centerOfMass ), inertia: v( boat.hydro.inertia ) },
	forceScale: boat.forceScale, propeller: v( boat.propeller ), rudderZ: boat.rudder.z, maxThrust: boat.maxThrust, pitchSpeed: boat.pitchSpeed,
	reverseFactor: boat.reverseFactor, stations: boat.stations, lateralY: boat.lateralY, hullLift: boat.hullLift, rudderLift: boat.rudderLift,
	contactPoints: boat.contactPoints.map( v ), outline: boat.outline.map( v ), bowZ: boat.bowZ, sternZ: boat.sternZ, chockY: boat.chockY,
	helmEye: v( boat.helmEye ), helmPoint: v( boat.helmPoint ), boardPoint: v( boat.boardPoint ), exitPoints: boat.exitPoints.map( v ),
} ) );
console.log( header.materials.map( ( m ) => `${ m.name }: ${ m.vertices } v, ${ m.indices / 3 } tris` ).join( '\n' ) );
console.log( `mask: ${ header.mask.vertices } v, ${ header.mask.indices / 3 } tris; ${ off } data bytes -> ${ dest }` );
