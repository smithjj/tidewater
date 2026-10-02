// The shore wave clock. The surf, the breakers and the swash all take their phase from one number, and
// it has to be an *accumulated* phase (the rate, integrated) rather than an absolute clock divided by the
// period: the weather moves the period continuously, and (t / period) then jumps by t * d(1/period) —
// 36% of a wave period, four times a second, after ten minutes of play — which reads as the surf
// stuttering. This is the regression guard for that.
import './headless.mjs'; // Dawn via the `webgpu` package: gives the module a device to build against
import { readFileSync } from 'node:fs';
import { GPU } from '../src/engine/gpu/GPU.js';
import { ShoreWaves } from '../src/ocean/ShoreWaves.js';
import { makeTerrain } from './ocean-shore-stubs.mjs';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

await GPU.init( { headless: true } );
const shore = new ShoreWaves( makeTerrain() );

// 1. the clock advances at dt / period
shore.period.value = 9;
let p = shore.phase.value;
shore.update( 1 / 60 );
ok( Math.abs( shore.phase.value - ( p + ( 1 / 60 ) / 9 ) ) < 1e-6, 'the shore clock advances by dt / period' );

// 2. a change of period changes the *rate*, not the phase
shore.period.value = 8.5;
p = shore.phase.value;
shore.update( 1 / 60 );
ok( Math.abs( shore.phase.value - ( p + ( 1 / 60 ) / 8.5 ) ) < 1e-6, 'a change of period bends the clock, it does not step it' );
// one write of the weather's cadence, the same step the shader would see
p = shore.phase.value;
shore.update( 0.25 );
const step = shore.phase.value - p;
ok( step > 0.025 && step < 0.035, `a sea write advances the surf by its natural ${ ( step * 100 ).toFixed( 1 ) }% of a period` );

// 3. the shader reads that clock. These two source checks are the only way to assert a WGSL expression
// from node: the symptom (a phase jump proportional to elapsed time) cannot be seen without a GPU frame
// diff, and the arithmetic of the alternative is what the comment above records.
const src = readFileSync( new URL( '../src/ocean/ShoreWaves.js', import.meta.url ), 'utf8' );
ok( /shoreP\.phase\s*-/.test( src ), 'the phase expressions count from the accumulated clock' );
const divided = src.match( /\(\s*frame\.time\s*-\s*[A-Za-z_][A-Za-z0-9_]*\s*\)\s*\/\s*[A-Za-z_][A-Za-z0-9_.]*/g );
ok( ! divided, `nothing divides an absolute clock by the period${ divided ? ': ' + divided.join( ', ' ) : '' }` );
// and the uniform exists, so the shader is not reading a phase that is never written
ok( /phase:\s*\[\s*'f32'/.test( src ) && ! /update\(\s*dt\s*\)\s*{\s*}/.test( src ), 'and the clock is a uniform the app advances' );

console.log( fails ? `\n${ fails } FAILED` : '\nocean-phase: all passed' );
process.exit( fails ? 1 : 0 );
