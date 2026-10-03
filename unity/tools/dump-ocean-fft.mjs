// Oracle for the C# ocean FFT port: runs the original OceanFFT headless (Dawn) with a fixed time step and writes
// the displacement / derivative maps of all cascades (rgba float32, layer-major) plus a summary.
//   node unity/tools/dump-ocean-fft.mjs <outDir> [frames=120]
// Frame f runs update( 1 / 60 ) with G.dt = 1 / 60; the dump is taken after the last frame. A dump after
// frame 1 is written too (fft_disp_f1.f32): it isolates the spectrum + one transform from time evolution.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
await import( root + '/test/headless.mjs' );
const { GPU } = await import( root + '/src/engine/gpu/GPU.js' );
const { G } = await import( root + '/src/engine/render/Frame.js' );
const { OceanFFT } = await import( root + '/src/ocean/OceanFFT.js' );
const { readFloatTexture } = await import( root + '/test/ocean-util.mjs' );

const out = process.argv[ 2 ] || '.';
const frames = + ( process.argv[ 3 ] ?? 120 );
fs.mkdirSync( out, { recursive: true } );

await GPU.init( { headless: true } );
const fft = new OceanFFT( null );
G.dt.value = 1 / 60;

const dump = async ( tag ) => {

	await GPU.queue.onSubmittedWorkDone();
	for ( const [ name, tex ] of [ [ 'disp', fft.displacementTexture ], [ 'deriv', fft.derivativeTexture ] ] ) {

		const layers = [];
		for ( let c = 0; c < fft.cascades; c ++ ) layers.push( ( await readFloatTexture( tex, { layer: c } ) ).data );
		const all = new Float32Array( layers.reduce( ( a, l ) => a + l.length, 0 ) );
		let o = 0;
		for ( const l of layers ) { all.set( l, o ); o += l.length; }
		fs.writeFileSync( path.join( out, `fft_${ name }_${ tag }.f32` ), Buffer.from( all.buffer ) );

	}

};

for ( let f = 1; f <= frames; f ++ ) {

	GPU.beginFrame();
	fft.update( 1 / 60 );
	GPU.submit();
	if ( f === 1 ) await dump( 'f1' );

}

await dump( 'final' );
fs.writeFileSync( path.join( out, 'fft_summary.json' ), JSON.stringify( { frames, sizes: fft.sizes, depth: fft.depth, time: fft.time.value, choppiness: fft.choppiness.value } ) );
console.log( 'dumped', frames, 'frames ->', out );
process.exit( 0 );
