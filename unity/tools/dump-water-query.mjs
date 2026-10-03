// Oracle for the C# WaterQuery port: the original OceanFFT + WaterQuery run headless (Dawn) without terrain, the FFT
// advanced 120 frames of 1/60 s, then 255 query points answered once. Writes the points and the results.
//   node unity/tools/dump-water-query.mjs <outDir>
// Points: x in [-300, 300], z in [900, 1000] (deep water; depth attenuation 1 everywhere, so the terrain does not matter).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
await import( root + '/test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
const { G } = await import( root + '/src/engine/render/Frame.js' );
const { OceanFFT } = await import( root + '/src/ocean/OceanFFT.js' );
const { CDLOD } = await import( root + '/src/core/CDLOD.js' );
const { WaterSurface } = await import( root + '/src/ocean/WaterSurface.js' );
const { WaterQuery } = await import( root + '/src/ocean/WaterQuery.js' );

const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );

await GPU.init( { headless: true } );
const fft = new OceanFFT( null );
G.dt.value = 1 / 60;
const cdlod = new CDLOD( { gridSize: 32, leafSize: 8, levels: 12, minY: - 25, maxY: 25 } );
const surface = new WaterSurface( { fft, cdlod, foamTexture: null } );
const q = new WaterQuery( null, surface );
const base = q.allocate( 'pts', 255 );

// deterministic scatter
let s = 12345;
const rnd = () => ( ( s = ( s * 1664525 + 1013904223 ) >>> 0 ) / 4294967296 );
const pts = [];
for ( let i = 0; i < 255; i ++ ) pts.push( [ - 300 + rnd() * 600, 900 + rnd() * 100 ] );
pts.forEach( ( p, i ) => q.setPoint( base + i, Math.fround( p[ 0 ] ), Math.fround( p[ 1 ] ) ) );
q.setCamera( 0, 950 );

for ( let f = 0; f < 120; f ++ ) {

	GPU.beginFrame();
	fft.update( 1 / 60 );
	GPU.submit();

}

await GPU.queue.onSubmittedWorkDone();
GPU.beginFrame();
q.update();
GPU.submit();
for ( let i = 0; i < 20 && ! q.cpuValid; i ++ ) {

	await GPU.queue.onSubmittedWorkDone();
	await new Promise( ( r ) => setTimeout( r, 50 ) );
	GPU.beginFrame();
	GPU.submit();

}

console.log( 'valid', q.cpuValid );
fs.writeFileSync( path.join( out, 'query_inputs.f32' ), Buffer.from( new Float32Array( q.resultInputs ).buffer ) );
fs.writeFileSync( path.join( out, 'query_results.f32' ), Buffer.from( new Float32Array( q.cpu ).buffer ) );
const h = []; for ( let i = 0; i < 255; i ++ ) h.push( q.cpu[ ( 1 + i ) * 4 ] );
console.log( 'heights: min', Math.min( ...h ).toFixed(3), 'max', Math.max( ...h ).toFixed(3), 'first', h.slice( 0, 4 ).map( ( v ) => v.toFixed( 4 ) ) );
process.exit( 0 );
