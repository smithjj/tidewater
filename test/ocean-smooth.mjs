// The wave field must not step when the sea state changes.
//
// A spectrum recomputed from slightly different weather is only a per cent or so different, but as a
// *step* it moves the whole surface coherently in one frame — about a centimetre of water at a point, four
// times a second — which the boat reads as a jolt. OceanFFT.smoothH0Kernel eases the live field towards
// the newly computed spectrum instead, spreading the change over a fraction of a second. This watches the
// spectrum itself, frame by frame, with the easing off (what it used to do) and on.
//
// The amplitudes live near the centre of each cascade's tile (the low-frequency end of its band), so the
// sample window is taken from there rather than the edge, where the band is empty.
import './headless.mjs';
import { readBuffer } from '../src/engine/gpu/Readback.js';
import { makeOceanScene } from './ocean-scene.mjs';
import { conditionAt, writeConditions } from '../src/ocean/Conditions.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

const s = await makeOceanScene( { W: 64, H: 48, caustics: false, floor: false } );
const refs = { fft: s.fft, shore: { amplitude: { value: 0.34 }, period: { value: 9 } }, clouds: null };
const N = 256;
// a row of texels just either side of the tile centre: inside the cascade's band
const offset = ( 128 * N + 118 ) * 16;
const bytes = 20 * 16;
const read = async () => new Float32Array( await readBuffer( s.fft.h0, bytes, offset ) );

async function run( smoothTau ) {

	s.fft.smoothTau = smoothTau;
	s.fft._h0Ready = false; // the next install lands outright: start each phase from a known field
	writeConditions( refs, conditionAt( 1.5, 25 ), { spectrum: true, resetFoam: false, cover: false } );
	s.frame( 1 / 60 );
	let prev = await read();
	let amp = 0, worst = 0, moved = 0;
	for ( let f = 1; f <= 240; f ++ ) {

		// the weather's cadence: a new sea every 0.25 s, walking up the ladder
		if ( f % 15 === 0 ) writeConditions( refs, conditionAt( 1.5 + f / 480, 25 ), { spectrum: true, resetFoam: false, cover: false } );
		s.frame( 1 / 60 );
		const now = await read();
		for ( let i = 0; i < now.length; i += 4 ) {

			worst = Math.max( worst, Math.abs( now[ i ] - prev[ i ] ) );
			amp = Math.max( amp, Math.abs( now[ i ] ) );
			moved += Math.abs( now[ i ] - prev[ i ] );

		}

		prev = now;

	}

	return { worst, amp, moved };

}

const hard = await run( 1e-6 ); // an instant install: the old behaviour, a step per write
const eased = await run( 0.6 );
const rel = ( r ) => ( r.amp > 0 ? ( r.worst / r.amp ) * 100 : 0 );
console.log( `     instant install: worst frame moves ${ rel( hard ).toFixed( 2 ) }% of the amplitude   |   eased: ${ rel( eased ).toFixed( 2 ) }%` );

ok( hard.amp > 1e-6 && eased.amp > 1e-6, 'the spectrum is populated (the sample window is in the band)' );
ok( hard.moved > 0 && eased.moved > 0, 'and it keeps moving' );
ok( rel( eased ) < rel( hard ) / 3, `the sea eases towards a new spectrum instead of stepping to it (${ rel( eased ).toFixed( 2 ) }% vs ${ rel( hard ).toFixed( 2 ) }% of its amplitude in a frame)` );
ok( rel( eased ) < 1, `and no frame moves it more than 1% of its amplitude (${ rel( eased ).toFixed( 3 ) }%)` );

console.log( fails ? `\n${ fails } FAILED` : '\nocean-smooth: all passed' );
process.exit( fails ? 1 : 0 );
