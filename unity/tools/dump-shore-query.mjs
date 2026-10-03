// Oracle for the shore waves + WaterQuery on the real island: the original TerrainData, shore field, ShoreWaves, OceanFFT and
// WaterQuery run headless (Dawn); the FFT and the shore clock advanced 120 frames of 1/60 s, then 255 points in the surf zone
// answered once. Writes the points and the results.
//   node unity/tools/dump-shore-query.mjs <outDir>
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
// ShoreWaves with an exact integer hash for the per-wave random (the original sin() * 43758 amplifies GPU differences in sin);
// the C# side does the same (WaterQuery.oracleHash). Loaded from a temporary patched copy of the source.
const { default: fsd } = await import( 'node:fs' );
const { tmpdir } = await import( 'node:os' );
let src = fsd.readFileSync( root + '/src/ocean/ShoreWaves.js', 'utf8' );
const hashOld = 'fn shoreHash1( x: f32 ) -> f32 { return fract( sin( x * 127.1 + 311.7 ) * 43758.5453 ); }';
if ( ! src.includes( hashOld ) ) throw new Error( 'shoreHash1 not found' );
src = src.replace( hashOld, 'fn shoreHash1( x: f32 ) -> f32 { return u32ToUnit( pcg( bitcast<u32>( x ) ) ); }' );
src = src.replace( /from '\.\.\/engine\//g, `from '${ root }/src/engine/` );
const patched = path.join( tmpdir(), 'tw-shore-waves.mjs' );
fsd.writeFileSync( patched, src );
const { ShoreWaves } = await import( patched );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { TerrainGPU } = await import( root + '/src/world/TerrainGPU.js' );
const { computeShoreField } = await import( root + '/src/world/ShoreField.js' );
const { WORLD } = await import( root + '/src/world/WorldLayout.js' );

const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );

await GPU.init( { headless: true } );
const T = new TerrainData( 7 );
const shoreField = computeShoreField( T, { res: 512, swellDir: [ WORLD.swellDir.x, WORLD.swellDir.y ] } );
const tgpu = new TerrainGPU( T, shoreField );
const fft = new OceanFFT( null );
const shore = new ShoreWaves( tgpu );
G.dt.value = 1 / 60;
const cdlod = new CDLOD( { gridSize: 32, leafSize: 8, levels: 12, minY: - 25, maxY: 25 } );
const surface = new WaterSurface( { fft, cdlod, foamTexture: null } );
surface.terrain = tgpu;
surface.shore = shore;
const q = new WaterQuery( null, surface );
const base = q.allocate( 'pts', 255 );

let s = 777;
const rnd = () => ( ( s = ( s * 1664525 + 1013904223 ) >>> 0 ) / 4294967296 );
const pts = [];
for ( let i = 0; i < 255; i ++ ) pts.push( [ - 100 + rnd() * 250, - 75 + rnd() * 70 ] );
pts.forEach( ( p, i ) => q.setPoint( base + i, Math.fround( p[ 0 ] ), Math.fround( p[ 1 ] ) ) );
q.setCamera( 0, - 30 );

for ( let f = 0; f < 120; f ++ ) {

	GPU.beginFrame();
	fft.update( 1 / 60 );
	shore.update( 1 / 60 );
	GPU.submit();

}

// one query round: dispatch, wait for the read-back
const ask = async () => {

	const v0 = q.version;
	await GPU.queue.onSubmittedWorkDone();
	GPU.beginFrame();
	q.update();
	GPU.submit();
	for ( let i = 0; i < 60 && q.version === v0; i ++ ) {

		await GPU.queue.onSubmittedWorkDone();
		await new Promise( ( r ) => setTimeout( r, 50 ) );
		GPU.beginFrame();
		GPU.submit();

	}

	return Float32Array.from( q.cpu );

};

shore.enabled.value = 0;
const resOff = await ask();
shore.enabled.value = 1;
const resOn = await ask();
fs.writeFileSync( path.join( out, 'shorequery_results_off.f32' ), Buffer.from( resOff.buffer ) );
console.log( 'valid', q.cpuValid, 'shore phase', shore.phase.value );
fs.writeFileSync( path.join( out, 'shorequery_inputs.f32' ), Buffer.from( new Float32Array( q.resultInputs ).buffer ) );
fs.writeFileSync( path.join( out, 'shorequery_results.f32' ), Buffer.from( resOn.buffer ) );
const h = []; const fl = [];
for ( let i = 0; i < 255; i ++ ) { h.push( resOn[ ( 1 + i ) * 4 ] ); fl.push( resOn[ ( 1 + i ) * 4 + 3 ] ); }
console.log( 'heights: min', Math.min( ...h ).toFixed( 3 ), 'max', Math.max( ...h ).toFixed( 3 ), '| floor min', Math.min( ...fl ).toFixed( 2 ), 'max', Math.max( ...fl ).toFixed( 2 ) );
process.exit( 0 );
