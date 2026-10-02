// Local lights on the sea surface.
//
// The water is deliberately outside the scene lighting model (IS_WATER, lightingHooks off, bespoke
// shading), so a torch or a deck flood used to light everything except the water it was pointed at.
// WaterMaterial now reads the packed local lights itself: a reflected glint plus the light that
// enters the water and scatters back out. This renders real frames at night with a lamp over the
// water and measures the pixels, because a WGSL term that compiles can still do nothing.
//
// The term's two knobs (lampSpec, lampGlow) are material uniforms, which upload once with the
// pipeline — so the negative case is a second scene built with them at zero, not a write into a
// live one (a scalar field written directly would silently never reach the GPU).
import './headless.mjs';
import { GPU } from '../src/engine/gpu/GPU.js';
import { readTexture } from '../src/engine/gpu/Readback.js';
import { makeOceanScene } from './ocean-scene.mjs';
import { G } from '../src/engine/render/Frame.js';
import { LocalLights } from '../src/materials/LocalLights.js';
import * as E from '../src/engine/index.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

// A comparison must hold the sea still: every frame advances the wave field, which moved pixels by
// 77/255 between two no-lamp frames (a wave, not the light). Stepping by ~a microsecond instead
// leaves the surface where it is while still re-rendering and repacking the lights.
const FREEZE = 1e-6;

async function run( { term } ) {

	const s = await makeOceanScene( { W: 320, H: 200, terrain: false, caustics: false, floor: false } );
	G.exposure.value = 1;
	// before the first frame: this is the value the pipeline uploads
	s.waterMaterial.uniformBlock.set( 'lampSpec', term ? 1 : 0 );
	s.waterMaterial.uniformBlock.set( 'lampGlow', term ? 1 : 0 );
	// above the water, looking down and out: the frame is sea, with the clear colour above the horizon
	s.camera.position.set( 0, 7, 16 );
	s.camera.lookAt( new E.Vector3( 0, 0, - 4 ) );

	const lights = new LocalLights();
	lights.add( {
		position: new E.Vector3( 0, 3.5, 4 ),
		color: new E.Color( 1.0, 0.71, 0.49 ),
		intensity: 60, range: 40,
		dir: new E.Vector3( 0, - 0.55, - 0.84 ),
		cosInner: Math.cos( 0.35 ), cosOuter: Math.cos( 0.9 ),
	} );
	s.before.push( () => lights.update( s.camera, 1 / 60 ) );

	const shoot = async () => {

		s.frame( FREEZE );
		await GPU.queue.onSubmittedWorkDone();
		const img = await readTexture( s.ldr.texture );
		return new Uint8Array( img.data );

	};

	G.night.value = 0;
	lights.enabled = false;
	for ( let i = 0; i < 4; i ++ ) s.frame( FREEZE );
	const dayOff = await shoot();
	lights.enabled = true;
	const dayOn = await shoot();
	const dayActive = lights.active;

	G.night.value = 1;
	for ( let i = 0; i < 4; i ++ ) s.frame( FREEZE );
	const nightOn = await shoot();
	const nightActive = lights.active;
	lights.enabled = false;
	const nightOff = await shoot();

	return { day: diff( dayOn, dayOff ), night: diff( nightOn, nightOff ), dayActive, nightActive };

}

function diff( a, b ) {

	let worst = 0, changed = 0, brighter = 0, darker = 0;
	for ( let i = 0; i < a.length; i += 4 ) {

		const d = Math.max( a[ i ] - b[ i ], a[ i + 1 ] - b[ i + 1 ], a[ i + 2 ] - b[ i + 2 ] );
		worst = Math.max( worst, Math.abs( d ) );
		if ( Math.abs( d ) > 4 ) {

			changed ++;
			if ( a[ i ] + a[ i + 1 ] + a[ i + 2 ] > b[ i ] + b[ i + 1 ] + b[ i + 2 ] ) brighter ++; else darker ++;

		}

	}
	return { worst, changed, px: a.length / 4, brighter, darker };

}

const lit = await run( { term: true } );
const unlit = await run( { term: false } );

const pct = ( r ) => ( 100 * r.changed / r.px ).toFixed( 0 );
console.log( `     with the term:    worst pixel +${ lit.night.worst }, ${ lit.night.changed } of ${ lit.night.px } px changed (${ pct( lit.night ) }%), ${ lit.night.brighter } brighter vs ${ lit.night.darker } darker` );
console.log( `     term at zero:     worst pixel +${ unlit.night.worst }, ${ unlit.night.changed } px changed` );
console.log( `     daylight (both):  worst pixel ${ lit.day.worst } and ${ unlit.day.worst }` );

ok( lit.dayActive === 0 && unlit.dayActive === 0, `no lamps are packed in daylight (active ${ lit.dayActive })` );
ok( lit.day.worst <= 1 && unlit.day.worst <= 1, `and the daylight frame is unchanged (worst pixel ${ lit.day.worst })` );
ok( lit.nightActive === 1, `the lamp is packed at night (active ${ lit.nightActive })` );
ok( lit.night.worst >= 6, `the lamp lights the water (worst pixel +${ lit.night.worst })` );
ok( lit.night.changed > 20 && lit.night.changed < lit.night.px * 0.55, `and it is a patch of the surface, not the whole frame (${ pct( lit.night ) }% of pixels)` );
ok( lit.night.brighter > lit.night.changed * 0.9, `the lit water gets brighter, not darker (${ lit.night.brighter } brighter vs ${ lit.night.darker } darker)` );
ok( unlit.night.worst <= 1, `the new term is what lit it: with lampSpec/lampGlow at zero the same lamp changes nothing (worst pixel ${ unlit.night.worst })` );

console.log( fails ? `\n${ fails } FAILED` : '\nwater-lights: all passed' );
process.exit( fails ? 1 : 0 );
