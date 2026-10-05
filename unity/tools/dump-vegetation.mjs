// Oracle for the C# vegetation placement (Runtime/World/Vegetation/VegScatter.cs): the JS VegSite + scatterVegetation + buildGrassMask on the real
// terrain with the village's footprints and boardwalks (as Vegetation.js gives them), for comparison by Editor/VegetationOracle.cs.
//   node unity/tools/dump-vegetation.mjs unity/Temp/oracle/vegetation [seed]
// Writes records.f64 (per type, 10 doubles per plant: x y z s sy yaw la l H seed) with the counts in summary.json, the cover() of a grid of sample
// points (cover.f64) and the grass mask (grass.u8, RGBA).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
const seed = + ( process.argv[ 3 ] ?? 7 );
fs.mkdirSync( out, { recursive: true } );

// the detail texture (the land cover reads it on the CPU) is made through a texture object: a headless GPU
await import( root + '/test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
await GPU.init( { headless: true } );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );
const { FishProps } = await import( root + '/src/world/fish/FishProps.js' );
const { Group } = await import( root + '/src/engine/index.js' );
const { Village } = await import( root + '/src/world/Village.js' );
const { VegSite, scatterVegetation, buildGrassMask } = await import( root + '/src/world/vegetation/Scatter.js' );

FishProps.prototype.build = function () { return new Group(); };
const T = new TerrainData( seed );
const village = new Village( { scene: { add() {} }, terrain: T, colliders: new Colliders() } );

// Vegetation.js villageObstacles()
const footprints = village.getFootprints();
const paths = [];
for ( const p of [ village.path, ...( village.sidePaths || [] ) ] ) {

	if ( ! p || ! p.samples ) continue;
	const pts = [];
	for ( let i = 0; i < p.samples.length; i += 5 ) pts.push( [ p.samples[ i ].p.x, p.samples[ i ].p.z ] );
	const last = p.samples[ p.samples.length - 1 ];
	pts.push( [ last.p.x, last.p.z ] );
	paths.push( { points: pts, width: p.width ?? 1.8 } );

}

const t0 = performance.now();
const site = new VegSite( T, { footprints, paths: paths.length ? paths : null } );
const recs = scatterVegetation( site );
const t1 = performance.now();
const mask = buildGrassMask( site );
const t2 = performance.now();

const TYPES = [ 'palms', 'trees', 'bananas', 'shrubs', 'youngPalms', 'ferns', 'monsteras', 'elephantEars', 'heliconias', 'strelitzias' ];
const KEYS = [ 'x', 'y', 'z', 's', 'sy', 'yaw', 'la', 'l', 'H', 'seed' ];
const counts = {}, all = [];
for ( const t of TYPES ) {

	counts[ t ] = recs[ t ].length;
	for ( const r of recs[ t ] ) for ( const k of KEYS ) all.push( r[ k ] ?? 0 );

}

const w = ( name, arr ) => fs.writeFileSync( path.join( out, name ), Buffer.from( arr.buffer, arr.byteOffset, arr.byteLength ) );
w( 'records.f64', new Float64Array( all ) );
w( 'grass.u8', mask.data );

// cover() on a jittered grid of points over the island (every field), to compare the land classification by itself
const cov = [], c = {}, CK = [ 'h', 'ny', 'slope', 'macro', 'mA', 'mB', 'gully', 'forest', 'rock', 'bare', 'sand', 'path', 'scarp' ];
for ( let z = - 800; z <= 300; z += 37 ) for ( let x = - 640; x <= 640; x += 37 ) {

	site.cover( x + ( z % 7 ), z + ( x % 5 ), c );
	cov.push( x + ( z % 7 ), z + ( x % 5 ) );
	for ( const k of CK ) cov.push( c[ k ] );

}

w( 'cover.f64', new Float64Array( cov ) );
fs.writeFileSync( path.join( out, 'summary.json' ), JSON.stringify( { seed, types: TYPES, keys: KEYS, counts, coverKeys: CK, coverCount: cov.length / ( 2 + CK.length ), grassRes: mask.res, villagePalms: recs.villagePalms,
	footprints: footprints.length, paths: paths.length, ms: { scatter: Math.round( t1 - t0 ), mask: Math.round( t2 - t1 ) } }, null, 1 ) );
console.log( 'vegetation:', JSON.stringify( counts ), 'grass mask', mask.res, '->', out, Math.round( t2 - t0 ) + ' ms' );
