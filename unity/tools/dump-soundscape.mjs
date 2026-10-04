// Oracle for the C# SoundScape (Runtime/Audio/SoundScape.cs): runs the original src/audio/SoundScape.js against a recording stand-in for the Web Audio
// graph and writes every decision it makes (the ramps, the beds it starts, the one-shots it plays with slice, gain, rate, time and placement, the
// voices it fades), for the comparer in unity/Assets/Tidewater/Editor/SoundScapeOracle.cs:
//   node unity/tools/dump-soundscape.mjs unity/Temp/oracle/soundscape
//
// Two scripted runs of 120 s (a walk from the trees over the beach, wading, swimming, a dive, the pier with the rod, a boat run with the engine
// and the whale around it), one with the game's shore waves, terrain, wildlife and whale, one without (the fallback paths: a fake surf, no real
// birds). Both sides share a seeded generator (the same LCG as the other oracles) in place of Math.random, so the event streams must be equal.
// The terrain and the shore field are synthetic (polynomial, so both sides compute the same bits); the clip lengths and channel counts are the
// real files' (tools/clip-info.json).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname( fileURLToPath( import.meta.url ) );
const root = path.resolve( here, '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const clipInfo = JSON.parse( fs.readFileSync( path.join( here, 'clip-info.json' ), 'utf8' ) );
const { SoundScape, MIX } = await import( root + '/src/audio/SoundScape.js' );
const { BANK } = await import( root + '/src/audio/soundBank.js' );
const { WORLD } = await import( root + '/src/world/WorldLayout.js' );

const lcg = ( seed ) => () => ( seed = ( seed * 16807 ) % 2147483647 ) / 2147483647;
const clamp = ( v, a, b ) => Math.min( b, Math.max( a, v ) );

// ---------------------------------------------------------------- the stand-in for Web Audio

let NOW = 0;
let EV = [];
const warns = [];

class Param {

	constructor() { this.value = 0; }
	setTargetAtTime( v, t, tau ) {

		if ( this.__voice ) EV.push( [ 'fade', this.__voice.bank, this.__voice.start, t ] );
		else EV.push( [ 'ramp', this, t, v, tau ] );

	}
	cancelScheduledValues() {}
	setValueAtTime() {}
	linearRampToValueAtTime() {}

}

class Node {

	constructor( kind ) { this.kind = kind; this.out = []; }
	connect( d ) { this.out = [ d ]; return d; }
	disconnect() {}

}

class Gain extends Node { constructor() { super( 'gain' ); this.gain = new Param(); } }
class Biquad extends Node { constructor() { super( 'biquad' ); this.frequency = new Param(); this.Q = new Param(); this.type = ''; } }
class Panner extends Node {

	constructor() { super( 'panner' ); this.positionX = new Param(); this.positionY = new Param(); this.positionZ = new Param(); }

}
class Stereo extends Node { constructor() { super( 'stereo' ); this.pan = new Param(); } }
class Compressor extends Node {

	constructor() { super( 'comp' ); this.threshold = new Param(); this.knee = new Param(); this.ratio = new Param(); this.attack = new Param(); this.release = new Param(); }

}

const active = []; // voices playing: { src, end, order }
let order = 0;

class Source extends Node {

	constructor() { super( 'source' ); this.playbackRate = new Param(); this.loop = false; this.buffer = null; this.onended = null; }
	start( t, off = 0, dur = 0 ) {

		let n = this.out[ 0 ];
		const chain = [];
		while ( n && ! n.__n ) { chain.push( n ); n = n.out[ 0 ]; }
		if ( ! n ) throw new Error( 'unnamed destination for ' + this.buffer.__name );
		const panner = chain.find( ( c ) => c.kind === 'panner' ), air = chain.find( ( c ) => c.kind === 'biquad' );
		const gn = chain[ 0 ] && chain[ 0 ].kind === 'gain' ? chain[ 0 ] : null;
		if ( this.loop ) {

			EV.push( [ 'bed', this.buffer.__name, n.__n, t, off, this.playbackRate.value ] );
			gn.gain.__n = 'bed.' + this.buffer.__name + '.gain';
			this.playbackRate.__n = 'bed.' + this.buffer.__name + '.rate';
			return;

		}

		let dest = n.__n, p = [ 0, 0, 0, 0, 0, 0 ];
		if ( panner ) {

			if ( ! air || n.__n !== 'Above' ) throw new Error( 'placed one-shot not through air -> panner -> above' );
			dest = 'Air';
			p = [ panner.positionX.value, panner.positionY.value, panner.positionZ.value, panner.refDistance, panner.rolloffFactor, air.frequency.value ];

		}

		EV.push( [ 'shot', this.buffer.__name, dest, !! panner, off, dur, gn.gain.value, this.playbackRate.value, t, ...p ] );
		gn.gain.__voice = { bank: this.buffer.__name, start: off };
		active.push( { src: this, end: t + dur / this.playbackRate.value, order: order ++ } );

	}
	stop() {}

}

const listener = { positionX: new Param(), positionY: new Param(), positionZ: new Param(), forwardX: new Param(), forwardY: new Param(), forwardZ: new Param(), upX: new Param(), upY: new Param(), upZ: new Param() };

class Ctx {

	constructor() { this.state = 'running'; this.destination = new Node( 'dest' ); this.listener = listener; }
	get currentTime() { return NOW; }
	createGain() { return new Gain(); }
	createBiquadFilter() { return new Biquad(); }
	createPanner() { return new Panner(); }
	createStereoPanner() { return new Stereo(); }
	createDynamicsCompressor() { return new Compressor(); }
	createBufferSource() { return new Source(); }
	async decodeAudioData( a ) {

		const file = String( a ).split( '/' ).pop(), name = Object.keys( BANK ).find( ( k ) => BANK[ k ].file === file );
		const ci = clipInfo[ name ];
		return { duration: ci.dur, numberOfChannels: ci.ch, __name: name };

	}
	close() { return Promise.resolve(); }
	resume() { return Promise.resolve(); }

}

globalThis.AudioContext = Ctx;
globalThis.fetch = async ( url ) => ( { ok: true, arrayBuffer: async () => url } );
console.warn = ( ...a ) => warns.push( a.map( String ).join( ' ' ) );

// ---------------------------------------------------------------- the synthetic world

// the shoreline z(x), land at z < zc; heights in m (> 0 land)
const zc = ( x ) => - 42 + 0.0003 * x * x - 0.02 * x;
const heightAt = ( x, z ) => { const c = zc( x ); return z < c ? Math.min( 8, ( c - z ) * 0.04 ) : Math.max( - 25, - ( z - c ) * 0.06 ); };
const coastD = ( x, z ) => z - zc( x );
const terrain = { origin: - 400, size: 800, heightAt, coastDistance: ( x, z ) => ( { d: coastD( x, z ), beachZone: false } ) };

// the shore field: [ travel time T, direction x exposure (x, z), time to the shoreline Ts ] on a 128 x 128 grid, as float32
const RES = 128;
const field = { res: RES, data: new Float32Array( RES * RES * 4 ) };
for ( let j = 0; j < RES; j ++ ) {

	for ( let i = 0; i < RES; i ++ ) {

		const x = terrain.origin + ( i + 0.5 ) / RES * terrain.size, z = terrain.origin + ( j + 0.5 ) / RES * terrain.size;
		const dx = - 0.12 + 0.0001 * x, dz = - 1, l = Math.hypot( dx, dz );
		const ex = 0.9 - 0.25 * Math.min( 1, Math.abs( x ) / 300 );
		const k = ( j * RES + i ) * 4;
		field.data[ k ] = ( 400 - z ) / 6;
		field.data[ k + 1 ] = dx / l * ex;
		field.data[ k + 2 ] = dz / l * ex;
		field.data[ k + 3 ] = ( 400 - zc( x ) ) / 6;

	}

}

const shoreParams = { enabled: 1, amplitude: 0.6, variation: 0.35, period: 9, gamma: 0.78 };

// ---------------------------------------------------------------- the scripted run

const T_END = 120;

// piecewise-linear keyframes: [ t, x, y, z, u, depth ]
const KEYS = [
	[ 0, - 30, 1.7, - 80, 0, 0 ], [ 20, - 25, 1.7, - 56, 0, 0 ], [ 40, 20, 1.7, - 43, 0, 0 ], [ 50, 28, 1.3, - 36, 0, 0 ], [ 62, 36, 0.1, - 28, 0, 0 ],
	[ 64, 36, - 1.5, - 26, 1, 1.6 ], [ 70, 38, - 3, - 18, 1, 3.1 ], [ 72, 38, - 0.5, - 15, 0.5, 0.6 ], [ 73, 38, 0.1, - 14, 0, 0 ],
	[ 76, 55, 3.8, - 40, 0, 0 ], [ 90, 55, 3.8, 34, 0, 0 ],
];

const lerpKeys = ( t ) => {

	let a = KEYS[ 0 ], b = KEYS[ KEYS.length - 1 ];
	for ( let i = 0; i < KEYS.length - 1; i ++ ) if ( t >= KEYS[ i ][ 0 ] && t < KEYS[ i + 1 ][ 0 ] ) { a = KEYS[ i ]; b = KEYS[ i + 1 ]; break; }
	const f = b[ 0 ] === a[ 0 ] ? 0 : clamp( ( t - a[ 0 ] ) / ( b[ 0 ] - a[ 0 ] ), 0, 1 );
	return a.slice( 1 ).map( ( v, i ) => v + ( b[ i + 1 ] - v ) * f );

};

const smooth = ( a, b, x ) => { const t = clamp( ( x - a ) / ( b - a ), 0, 1 ); return t * t * ( 3 - 2 * t ); };
const surfaceAt = ( x, z, u, wading ) => u > 0 ? 'water' : wading ? 'water' : z > 0 && Math.abs( x - 55 ) < 3 ? 'wood' : coastD( x, z ) < - 22 ? ( x < 0 ? 'grass' : 'rock' ) : coastD( x, z ) < - 3 ? 'sand' : 'wetsand';

function scenario( bare ) {

	const rnd = lcg( bare ? 424242 : 171717 );
	const steps = [];
	let t = 0, stepT = 0, nextStep = 0.4, crank = 0;
	const dts = [ 1 / 60, 1 / 60, 1 / 60, 1 / 30, 1 / 60, 1 / 45, 1 / 60, 1 / 20 ];
	let k = 0, fi = 0;
	while ( t < T_END ) {

		const dt = dts[ k ++ % dts.length ];
		t += dt;
		const calls = [];
		const at = ( t0 ) => t >= t0 && t - dt < t0;
		let [ x, y, z, u, depth ] = lerpKeys( t );
		const boatOn = t >= 90, onBoat = t >= 90;
		const bt = Math.max( 0, t - 90 );
		const boat = { active: boatOn, rpm: boatOn ? clamp( 0.15 + bt / 25, 0, 0.95 ) : 0, speed: boatOn ? clamp( ( bt - 4 ) * 0.5, 0, 9.5 ) : 0, listenerInside: t >= 100 && t < 110 };
		if ( boatOn ) { boat.position = { x: 62 + bt * 2.5, y: 0.2, z: 20 + bt * 3 }; }
		else if ( t >= 60 ) boat.position = { x: 62, y: 0.2, z: 20 };
		if ( onBoat ) { x = boat.position.x - 1; y = 1.9; z = boat.position.z + 0.5; u = 0; depth = 0; }
		if ( t >= 20 && t < 22 ) u = 0;
		const yaw = t * 0.3, pitch = Math.sin( t * 0.2 ) * 0.4;
		const hour = 5 + t * 0.13;
		const daylight = clamp( ( hour - 4.6 ) / 0.8, 0, 1 ) * clamp( ( 19.8 - hour ) / 1.2, 0, 1 );
		const coast = coastD( x, z );
		const state = {
			listener: { position: { x, y, z }, forward: { x: Math.sin( yaw ) * Math.cos( pitch ), y: Math.sin( pitch ), z: - Math.cos( yaw ) * Math.cos( pitch ) }, up: { x: 0, y: 1, z: 0 } },
			underwater: u, depthBelowSurface: depth, surfIntensity: 0.5 + 0.3 * Math.sin( t * 0.4 ), distanceToShore: Math.abs( coast ), coastDistance: coast,
			windSpeed: 6 + 4 * Math.sin( t * 0.3 ), daylight, nearPier: Math.abs( x - 55 ) < 12 && z > - 69 && z < 48,
			timeOfDay: bare && t < 60 ? null : hour, boat,
		};
		if ( bare && t >= 100 ) delete state.boat.position;

		// the player's and the game's sounds
		stepT += dt;
		if ( t < 60 && ! ( t >= 44 && t < 52 ) && stepT >= 0.55 ) { stepT = 0; calls.push( [ 'footstep', surfaceAt( x, z, u, t >= 50 ) ] ); }
		else if ( t >= 76 && t < 90 && stepT >= 0.5 ) { stepT = 0; calls.push( [ 'footstep', surfaceAt( x, z, u, false ) ] ); }
		if ( at( 44 ) ) calls.push( [ 'splash', 0.3 ] );
		if ( at( 47 ) ) calls.push( [ 'splash', 0.9 ] );
		if ( at( 55 ) ) calls.push( [ 'splash', 0.6 ] );
		if ( ( ( t >= 52 && t < 62 ) || ( t >= 65 && t < 71 ) ) && stepT >= 0.7 ) { stepT = 0; calls.push( [ 'swimStroke' ] ); }
		if ( at( 62.5 ) ) calls.push( [ 'submerge' ] );
		if ( at( 72.2 ) ) calls.push( [ 'emerge' ] );
		if ( at( 30 ) ) calls.push( [ 'setMuted', true ] );
		if ( at( 31 ) ) calls.push( [ 'setMuted', false ] );
		if ( at( 58 ) ) calls.push( [ 'setMasterVolume', 0.5 ] );
		if ( at( 59 ) ) calls.push( [ 'setMasterVolume', 0.8 ] );
		if ( at( 77 ) ) calls.push( [ 'rodReady' ] );
		if ( at( 78 ) ) calls.push( [ 'bail', true ] );
		if ( at( 79 ) ) calls.push( [ 'whoosh', 0.4 ] );
		if ( at( 79.5 ) ) calls.push( [ 'whoosh', 1 ] );
		if ( at( 80 ) ) calls.push( [ 'lineOut', 0.7 ] );
		if ( at( 81 ) ) calls.push( [ 'plop', { x: 61, y: 0, z: 25 } ] );
		if ( at( 82 ) ) calls.push( [ 'bail', false ] );
		if ( at( 84 ) ) calls.push( [ 'fishSplash', { x: 60, y: 0.1, z: 22 }, 0.35 ] );
		if ( at( 85 ) ) calls.push( [ 'fishSplash', { x: 60, y: 0.1, z: 22 }, 0.8 ] );
		if ( at( 86 ) ) calls.push( [ 'fishFlop' ] );
		if ( at( 87 ) ) calls.push( [ 'lineSnap' ] );
		if ( at( 88 ) || at( 89.5 ) ) calls.push( [ 'coin' ] );
		if ( at( 90.05 ) ) calls.push( [ 'engineStart' ] );
		for ( const [ ts, s ] of [ [ 92, 0.3 ], [ 95, 0.9 ], [ 99, 0.6 ], [ 101, 1 ], [ 104, 0.2 ], [ 108, 0.7 ], [ 111, 0.5 ] ] ) if ( at( ts ) ) calls.push( [ 'hullSlap', s ] );
		if ( at( 117 ) ) calls.push( [ 'engineStop' ] );
		if ( t >= 77 && t < 89 ) calls.push( [ 'rodLoop', Math.max( 0, Math.sin( t * 1.3 ) ) * 6, Math.max( 0, Math.sin( t * 0.7 + 1 ) ) * 4, clamp( 0.5 + 0.5 * Math.sin( t * 0.5 ), 0, 1 ) ] );

		// the wildlife and the whale (the full run only)
		let whale = null, flock = null;
		if ( ! bare ) {

			const w = t >= 60 ? { ready: true, brain: {
				position: { x: x + 40 - ( t - 60 ) * 0.4, y: - 8, z: z + 30 }, state: t % 20 < 12 ? 'cruise' : 'surface', water: 0, yaw: t * 0.05,
				blow: t >= 94 && t < 96 ? 1 : 0, flukeUp: t >= 99 && t < 103 ? 0.6 : 0, breaches: t >= 96 ? ( t >= 105 ? 2 : 1 ) : 0, splashes: t >= 97 ? ( t >= 106 ? 2 : 1 ) : 0,
			} } : null;
			whale = w;
			flock = [];
			for ( let b = 0; b < 8; b ++ ) {

				const a = t * 0.05 + b * 0.8, r = 30 + b * 14;
				flock.push( { kind: b % 3 === 2 ? 'tern' : 'gull', visible: b !== 5 || t < 70, f: { P: { pos: [ x + Math.cos( a ) * r, y + 8 + b, z + Math.sin( a ) * r ] } } } );

			}

		}

		fi ++;
		steps.push( { dt, t, state, calls, whale, flock, shore: { time: 40 + t } } );

	}

	return steps;

}

// ---------------------------------------------------------------- run the real thing

const NAMES = ( sc ) => {

	const N = ( o, n ) => { o.__n = n; };
	N( sc.above, 'Above' ); N( sc.under, 'Under' ); N( sc.near, 'Near' ); N( sc.foot, 'Foot' ); N( sc.rod, 'Rod' ); N( sc.boatSum, 'BoatSum' ); N( sc.surfFar, 'SurfFar' );
	N( sc.windLP, 'WindLP' ); N( sc.engineLP, 'EngineLP' ); N( sc.pierPan, 'PierPan' ); N( sc.songPan, 'SongPan' );
	const P = ( p, n ) => { p.__n = n; };
	P( sc.master.gain, 'Master' ); P( sc.aboveOut.gain, 'AboveOut' ); P( sc.under.gain, 'Under' ); P( sc.muffle[ 0 ].frequency, 'Muffle0' ); P( sc.muffle[ 1 ].frequency, 'Muffle1' );
	P( sc.windLP.frequency, 'WindLP' ); P( sc.engineLP.frequency, 'EngineLP' ); P( sc.boatIn.gain, 'BoatIn' ); P( sc.boatOut.gain, 'BoatOut' );
	P( sc.songLP.frequency, 'SongLP' ); P( sc.songOut.gain, 'SongOut' );
	for ( const [ pn, node ] of [ [ 'Surf', sc.surfPan ], [ 'Boat', sc.boatPan ], [ 'Pier', sc.pierPan ], [ 'Song', sc.songPan ] ] ) {

		P( node.positionX, 'pos.' + pn + '.x' ); P( node.positionY, 'pos.' + pn + '.y' ); P( node.positionZ, 'pos.' + pn + '.z' );

	}

	for ( const [ n, p ] of [ [ 'px', 'positionX' ], [ 'py', 'positionY' ], [ 'pz', 'positionZ' ], [ 'fx', 'forwardX' ], [ 'fy', 'forwardY' ], [ 'fz', 'forwardZ' ], [ 'ux', 'upX' ], [ 'uy', 'upY' ], [ 'uz', 'upZ' ] ] ) P( listener[ p ], 'listener.' + n );

};

const flush = () => {

	const ev = EV.map( ( e ) => {

		if ( e[ 0 ] === 'ramp' ) {

			if ( ! e[ 1 ].__n ) throw new Error( 'ramp on an unnamed parameter' );
			return [ 'ramp', e[ 1 ].__n, e[ 2 ], e[ 3 ], e[ 4 ] ];

		}

		return e;

	} );
	EV = [];
	return ev;

};

const settle = () => new Promise( ( r ) => setImmediate( r ) );

async function run( bare, steps ) {

	NOW = 0; EV = []; active.length = 0; order = 0;
	for ( const p of Object.values( listener ) ) { p._t = undefined; }
	const rnd = lcg( bare ? 777 : 555 );
	Math.random = rnd;
	const sc = new SoundScape();
	sc._warn = ( e ) => warns.push( String( e && e.stack || e ) );
	const shore = { enabled: { value: shoreParams.enabled }, amplitude: { value: shoreParams.amplitude }, variation: { value: shoreParams.variation }, period: { value: shoreParams.period }, time: { value: 0 }, gamma: { value: shoreParams.gamma } };
	if ( ! bare ) sc.attachShore( { shore, field, terrain } );
	await sc.resume();
	NAMES( sc );
	await settle();
	const init = flush();
	const events = [];
	for ( const s of steps ) {

		NOW += s.dt;
		// voices that have played out
		const done = active.filter( ( v ) => v.end <= NOW ).sort( ( a, b ) => a.order - b.order );
		for ( const v of done ) { active.splice( active.indexOf( v ), 1 ); if ( v.src.onended ) v.src.onended(); }
		globalThis.__app = { whale: s.whale || undefined, wildlife: s.flock ? { birds: { agents: s.flock } } : undefined };
		shore.time.value = s.shore.time;
		for ( const c of s.calls ) sc[ c[ 0 ] ]( ...c.slice( 1 ) );
		sc.update( s.dt, s.state );
		await settle();
		events.push( flush() );

	}

	if ( warns.length ) throw new Error( 'SoundScape warned: ' + warns.slice( 0, 3 ).join( ' | ' ) );
	return { init, events };

}

const result = { clipInfo, mix: MIX, world: { pier: WORLD.pier, swellDir: WORLD.swellDir }, shore: { res: RES, origin: terrain.origin, size: terrain.size, data: Array.from( field.data ), params: shoreParams }, runs: [] };
result.bank = Object.fromEntries( Object.entries( BANK ).map( ( [ k, v ] ) => [ k, { file: v.file, loop: !! v.loop, lufs: v.lufs, slices: v.slices || null } ] ) );
for ( const bare of [ false, true ] ) {

	const steps = scenario( bare );
	const r = await run( bare, steps );
	const n = r.events.reduce( ( a, e ) => a + e.length, 0 );
	const kinds = {};
	for ( const e of r.events ) for ( const x of e ) kinds[ x[ 0 ] ] = ( kinds[ x[ 0 ] ] || 0 ) + 1;
	console.log( bare ? 'bare' : 'full', steps.length, 'steps,', n, 'events', JSON.stringify( kinds ) );
	const shots = {};
	for ( const e of r.events ) for ( const x of e ) if ( x[ 0 ] === 'shot' ) shots[ x[ 1 ] ] = ( shots[ x[ 1 ] ] || 0 ) + 1;
	console.log( '  shots by bank', JSON.stringify( shots ) );
	result.runs.push( { name: bare ? 'bare' : 'full', bare, steps, init: r.init, events: r.events } );

}

fs.writeFileSync( path.join( out, 'soundscape.json' ), JSON.stringify( result ) );
console.log( 'wrote', path.join( out, 'soundscape.json' ) );
