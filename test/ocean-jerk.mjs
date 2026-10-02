// Diagnostic: does the water *jump*?  node test/ocean-jerk.mjs [static|weather|weather-hard|rebuild-only]
//
// Samples the surface height at fixed points (the read-back the boat uses) and diffs successive rendered
// frames, frame by frame, while the sea state changes the way the weather moves it. A step in the wave
// field shows up as a spike in the height's second difference; a step in the *look* of the water (foam
// edges, crest folding, wind detail) shows up as pixels that changed a lot in one frame while the frame's
// mean change stays ordinary. Prints medians and maxima so a variant can be compared with `static`.
//
// `static` is the control: the same water with the weather off, i.e. what "normal" measures.
import './headless.mjs';
import { readTexture } from '../src/engine/gpu/Readback.js';
import { makeOceanScene } from './ocean-scene.mjs';
import { WaterQuery } from '../src/ocean/WaterQuery.js';
import { Weather } from '../src/world/Weather.js';

const variant = process.argv[ 2 ] || 'weather';
const DT = 1 / 60, FRAMES = 420, N = 6;

const s = await makeOceanScene( { W: 256, H: 160, caustics: false, floor: false } );
const q = new WaterQuery( null, s.surface );
const base = q.allocate( 'probe', N );
for ( let i = 0; i < N; i ++ ) q.setPoint( base + i, - 60 + i * 14, - 30 );
q.setCamera( - 40, - 30 );
s.before.push( () => q.update() );

const app = {
	fft: s.fft, shore: { amplitude: { value: 0.34 }, period: { value: 9 } }, clouds: { coverage: { value: 0.45 } },
	settings: { timeOfDay: 9, timeSpeed: 0.02 }, ui: null,
};
const w = new Weather( app );
// a sea that is actually moving: the level eases from 0.8 towards 2.6, so every write changes it
w.restore( { seed: 7, level: 0.8, target: 2.6, hold: 4 } );
if ( variant === 'static' ) w.enabled = false;
if ( variant === 'weather-hard' ) s.fft.smoothTau = 1e-6; // no easing on the spectrum: the old field step

const heights = [], frames = [], writes = [];
for ( let f = 0; f < FRAMES; f ++ ) {

	if ( w.enabled ) w.update( DT );
	if ( s.fft.needsSpectrum ) writes.push( f );
	s.frame( DT );
	if ( f > 6 && q.cpuValid ) {

		const row = [];
		for ( let i = 0; i < N; i ++ ) row.push( q.get( base + i, {} ).height );
		heights.push( row );

	}

	frames.push( new Uint8Array( ( await readTexture( s.ldr.texture ) ).data ) );

}

const d1 = [], d2 = [], mad = [], worstPx = [], bigPx = [];
for ( let f = 2; f < heights.length; f ++ ) for ( let i = 0; i < N; i ++ ) {

	d1.push( Math.abs( heights[ f ][ i ] - heights[ f - 1 ][ i ] ) );
	d2.push( Math.abs( heights[ f ][ i ] - 2 * heights[ f - 1 ][ i ] + heights[ f - 2 ][ i ] ) );

}
for ( let f = 1; f < frames.length; f ++ ) {

	let sum = 0, worst = 0, big = 0;
	const a = frames[ f ], b = frames[ f - 1 ];
	for ( let k = 0; k < a.length; k += 4 ) {

		const x = Math.abs( a[ k ] - b[ k ] ), y = Math.abs( a[ k + 1 ] - b[ k + 1 ] ), z = Math.abs( a[ k + 2 ] - b[ k + 2 ] );
		const d = Math.max( x, y, z );
		sum += x + y + z;
		if ( d > worst ) worst = d;
		if ( d > 30 ) big ++;

	}

	mad.push( sum / ( a.length * 3 / 4 ) );
	worstPx.push( worst );
	bigPx.push( big );

}
const stat = ( a ) => {

	const s = [ ...a ].sort( ( x, y ) => x - y );
	return { med: s[ s.length >> 1 ], p99: s[ Math.floor( s.length * 0.99 ) ], max: s[ s.length - 1 ] };

};
const mm = ( v ) => ( v * 1000 ).toFixed( 2 );
const h1 = stat( d1 ), h2 = stat( d2 ), im = stat( mad ), wp = stat( worstPx ), bp = stat( bigPx );
console.log( `variant: ${ variant }   (${ heights.length } sampled frames, ${ writes.length } spectrum installs)` );
console.log( `  wave height step   med ${ mm( h1.med ) }   p99 ${ mm( h1.p99 ) }   max ${ mm( h1.max ) } mm` );
console.log( `  wave height accel  med ${ mm( h2.med ) }   p99 ${ mm( h2.p99 ) }   max ${ mm( h2.max ) } mm   (max/med ${ ( h2.max / Math.max( h2.med, 1e-9 ) ).toFixed( 1 ) }x)` );
console.log( `  frame diff (mean)  med ${ im.med.toFixed( 2 ) }   p99 ${ im.p99.toFixed( 2 ) }   max ${ im.max.toFixed( 2 ) }` );
console.log( `  worst pixel        med ${ wp.med }   p99 ${ wp.p99 }   max ${ wp.max }   |  pixels >30: med ${ bp.med }   p99 ${ bp.p99 }   max ${ bp.max }` );
process.exit( 0 );
