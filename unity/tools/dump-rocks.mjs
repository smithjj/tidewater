// Oracle for the C# rocks (Runtime/World/Rocks): the JS Rocks._place on the real terrain with the village's footprints, and buildRockGeometry for the four styles at both levels of detail, for
// comparison by Editor/RocksOracle.cs.
//   node unity/tools/dump-rocks.mjs unity/Temp/oracle/rocks [seed]
// Writes instances.f64 ( per rock: x y z size sy style radius, then the 16 matrix elements ), geometry.bin ( per style x level: vertex count, index count, positions, normals, ao, indices ) and summary.json.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

await import( root + '/test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
await GPU.init( { headless: true } );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );
const { FishProps } = await import( root + '/src/world/fish/FishProps.js' );
const { Group } = await import( root + '/src/engine/index.js' );
const { Village } = await import( root + '/src/world/Village.js' );
const { Rocks } = await import( root + '/src/world/Rocks.js' );
const { buildRockGeometry, ROCK_STYLES } = await import( root + '/src/world/terrain/RockGeometry.js' );
const { mulberry32 } = await import( root + '/src/util/Noise.js' );

FishProps.prototype.build = function () { return new Group(); };
const T = new TerrainData( seed );
const village = new Village( { scene: { add() {} }, terrain: T, colliders: new Colliders() } );

const t0 = performance.now();
const rocks = Rocks.prototype._place.call( { terrainData: T, village }, mulberry32( 4242 ) );
const t1 = performance.now();
const all = [];
for ( const r of rocks ) all.push( r.x, r.y, r.z, r.size, r.sy, r.style, r.radius, ...r.matrix.elements );
fs.writeFileSync( path.join( out, 'instances.f64' ), Buffer.from( new Float64Array( all ).buffer ) );

const chunks = [], levels = [];
for ( let s = 0; s < ROCK_STYLES.length; s ++ ) {

	for ( const sub of [ 3, 2 ] ) {

		const g = buildRockGeometry( s, 17 + s * 101, sub );
		const pos = g.attributes.position.array, nor = g.attributes.normal.array, ao = g.attributes.ao.array, idx = Uint32Array.from( g.index.array );
		chunks.push( new Uint32Array( [ pos.length / 3, idx.length ] ), pos, nor, ao, idx );
		levels.push( { style: s, subdiv: sub, vertices: pos.length / 3, indices: idx.length } );

	}

}

fs.writeFileSync( path.join( out, 'geometry.bin' ), Buffer.concat( chunks.map( ( a ) => Buffer.from( a.buffer, a.byteOffset, a.byteLength ) ) ) );
const perStyle = ROCK_STYLES.map( ( _, s ) => rocks.filter( ( r ) => r.style === s ).length );
fs.writeFileSync( path.join( out, 'summary.json' ), JSON.stringify( { seed, count: rocks.length, perStyle, levels, ms: Math.round( t1 - t0 ), footprints: village.getFootprints().length }, null, 1 ) );
console.log( 'rocks:', rocks.length, JSON.stringify( perStyle ), '->', out, Math.round( t1 - t0 ) + ' ms' );
