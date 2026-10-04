// Oracle for the C# caustics: the original Caustics (fine + broad layers) after the FFT advanced 120 frames of 1/60 s, with a fixed
// sun direction; writes both layers' mip 0 (rgba float32).
//   node unity/tools/dump-caustics.mjs <outDir>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
await import( root + '/test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
const { G } = await import( root + '/src/engine/render/Frame.js' );
const { OceanFFT } = await import( root + '/src/ocean/OceanFFT.js' );
const { Caustics } = await import( root + '/src/ocean/Caustics.js' );
const { readFloatTexture } = await import( root + '/test/ocean-util.mjs' );

const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
await GPU.init( { headless: true } );
const fft = new OceanFFT( null );
G.dt.value = 1 / 60;
G.sunDir.value.set( 0.45, 0.55, - 0.7 ).normalize();
const cau = new Caustics( null, fft );
for ( let f = 0; f < 120; f ++ ) {

	GPU.beginFrame();
	fft.update( 1 / 60 );
	GPU.submit();

}

GPU.beginFrame();
cau.update();
GPU.submit();
await GPU.queue.onSubmittedWorkDone();
for ( const [ name, layer ] of [ [ 'fine', cau.fine ], [ 'broad', cau.broad ] ] ) {

	const img = await readFloatTexture( layer.texture, { mip: 0 } );
	fs.writeFileSync( path.join( out, `caustics_${ name }.f32` ), Buffer.from( img.data.buffer ) );
	let s = 0, s2 = 0; const n = img.data.length / 4;
	for ( let i = 0; i < n; i ++ ) { s += img.data[ i * 4 ]; s2 += img.data[ i * 4 ] ** 2; }
	console.log( name, img.width, 'R mean', ( s / n ).toFixed( 4 ), 'std', Math.sqrt( s2 / n - ( s / n ) ** 2 ).toFixed( 4 ) );

}

process.exit( 0 );
