// Oracle for the C# Pier port: builds ONLY the pier (fresh mulberry32(90210), unflattened terrain) in Node and writes
// the per-material batches (<key>.{pos,nrm,uv,tint,data}.f32, <key>.idx.u32), the sign / hung-lantern builders,
// the colliders, lights and the pier info.   node unity/tools/dump-pier.mjs <outDir> [terrainSeed=7]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );
const { mulberry32 } = await import( root + '/src/util/Noise.js' );
const { Rand, InstancedProps } = await import( root + '/src/world/Props.js' );
const { Builder } = await import( root + '/src/world/village/GeoBuilder.js' );
const { buildPier } = await import( root + '/src/world/Pier.js' );
const { buildBoardwalk } = await import( root + '/src/world/village/Boardwalk.js' );

const T = new TerrainData( seed ), C = new Colliders();
const rand = new Rand( mulberry32( 90210 ) );
const B = new Builder(), signB = new Builder(), hungB = [];
const inst = new InstancedProps( B );
const lights = [];
const info = buildPier( { B, terrain: T, colliders: C, rand, lights, inst, signB, hang: () => { const b = new Builder(); hungB.push( b ); return b; } } );

const checks = [];
const foot = info.stepFoot;
const walk = buildBoardwalk( { B, terrain: T, colliders: C, rand, lights, inst, checks }, [
	[ foot.x, foot.z + 0.05 ], [ 54.6, - 72 ], [ 52.4, - 82 ], [ 48.4, - 92 ], [ 44.8, - 100.5 ], [ 42.6, - 107.2 ],
], { width: 1.8, startY: foot.y + 0.24, lightEvery: 70 } );

const f32 = ( a ) => Float32Array.from( a ), w = ( n, a ) => fs.writeFileSync( path.join( out, n ), Buffer.from( a.buffer, a.byteOffset, a.byteLength ) );
const batches = {};
const dump = ( name, bs ) => {
	for ( const k in bs.batches ) {
		const b = bs.batches[ k ];
		batches[ name + '_' + k ] = { vertices: b.vcount, indices: b.idx.length };
		w( `${ name }_${ k }.pos.f32`, f32( b.pos ) ); w( `${ name }_${ k }.nrm.f32`, f32( b.nrm ) ); w( `${ name }_${ k }.uv.f32`, f32( b.uv ) );
		w( `${ name }_${ k }.tint.f32`, f32( b.tint ) ); w( `${ name }_${ k }.data.f32`, f32( b.data ) ); w( `${ name }_${ k }.idx.u32`, Uint32Array.from( b.idx ) );
	}
};
dump( 'main', B ); dump( 'sign', signB ); hungB.forEach( ( b, i ) => dump( 'hung' + i, b ) );
fs.writeFileSync( path.join( out, 'pier.json' ), JSON.stringify( {
	seed, batches,
	boxes: C.boxes.map( ( b ) => ( { c: b.center.toArray(), h: b.half.toArray(), rotY: b.rotY, walkable: b.walkable, solid: b.solid, tag: b.tag } ) ),
	cylinders: C.cylinders.map( ( c ) => ( { x: c.x, z: c.z, r: c.radius, y0: c.yMin, y1: c.yMax, tag: c.tag } ) ),
	lights: lights.map( ( l ) => ( { p: l.position.toArray(), c: [ l.color.r, l.color.g, l.color.b ], i: l.intensity, kind: l.kind } ) ),
	info: { stepFoot: info.stepFoot.toArray(), ladder: info.ladder.toArray(), signPivot: info.signPivot.toArray(), lamps: info.lamps.map( ( p ) => p.toArray() ), bollards: info.bollards.map( ( p ) => p.toArray() ), hung: info.hung.map( ( h ) => ( { pivot: h.pivot.toArray(), rest: h.rest.toArray() } ) ) },
	checks, path: { length: walk.length, deck: Array.from( walk.deck ) },
	nextRand: rand.next(), instCount: inst.count,
} ) );
console.log( JSON.stringify( batches ), 'boxes', C.boxes.length, 'cyl', C.cylinders.length, 'lights', lights.length );
