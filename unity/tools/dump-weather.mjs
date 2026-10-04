// Oracle for the C# weather (Runtime/World/Weather.cs): src/world/Weather.js, run for real against stand-ins for the sea (fft, shore, clouds), over scripted
// scenarios, for comparison by unity/Assets/Tidewater/Editor/WeatherOracle.cs:
//   node unity/tools/dump-weather.mjs unity/Temp/oracle/weather && unity/tools/weather-oracle.sh
// Each scenario is a script (seed, start wind, start hour, dt and clock speed per frame, the pace, restores) that both sides run frame by frame:
//   hour = ( hour + dt * speed + 24 ) % 24 (not while the speed is 0), then weather.update( dt ), as App.frame does.
// Every frame records the walk (level, target, hold, wind direction, the whole step, whether the cloud cover was written); every SAMPLE-th frame also the
// values the sea received (what writeConditions put into fft / shore / clouds / G), and every announcement is listed with its frame.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname( fileURLToPath( import.meta.url ) );
const root = path.resolve( here, '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const { Weather } = await import( root + '/src/world/Weather.js' );
const { G } = await import( root + '/src/core/Globals.js' );

const SAMPLE = 25;

// the same generator Weather.js uses, for the scripts' random frame times
function mulberry32( seed ) {

	let a = seed >>> 0;
	return function () {

		a = ( a + 0x6D2B79F5 ) >>> 0;
		let t = a;
		t = Math.imul( t ^ ( t >>> 15 ), t | 1 );
		t ^= t + Math.imul( t ^ ( t >>> 7 ), t | 61 );
		return ( ( t ^ ( t >>> 14 ) ) >>> 0 ) / 4294967296;

	};

}

const SCENARIOS = [
	// a day at the real pace
	{ name: 'day', seed: 1, windDir0: 25, startHour: 16.2, frames: 36000, dt: { kind: 'const', dt: 1 / 30 }, speed: [ [ 0, 0.02 ] ], pace: 1, events: [] },
	// fast clock, uneven frames, from the small hours
	{ name: 'fast', seed: 987654321, windDir0: 25, startHour: 3.2, frames: 40000, dt: { kind: 'rand', lo: 0.005, hi: 0.1, seed: 77 }, speed: [ [ 0, 0.3 ] ], pace: 1, events: [] },
	// a seed past 2^31 (restored: the JS does `| 0`), a faster pace, the clock paused and resumed
	{ name: 'paused', seed: 3, windDir0: 80, startHour: 9, frames: 20000, dt: { kind: 'const', dt: 0.1 }, speed: [ [ 0, 1.0 ], [ 3000, 0 ], [ 3500, 1.0 ], [ 9000, 0 ], [ 9400, 0.7 ] ], pace: 1.5,
		events: [ { frame: 0, restore: { seed: 3000000000 } } ] },
	// a save loaded, then the wheel handed back and taken again, then a second load
	{ name: 'restore', seed: 7, windDir0: 25, startHour: 20, frames: 12000, dt: { kind: 'const', dt: 1 / 30 }, speed: [ [ 0, 0.5 ] ], pace: 1,
		events: [
			{ frame: 0, restore: { enabled: true, pace: 0.8, seed: 99, level: 2.4, target: 3, hold: 5, windDir: 40, dirBase: 10 } },
			{ frame: 3000, enabled: false }, { frame: 3500, enabled: true },
			{ frame: 6000, restore: { level: 0.3, target: 0, hold: 0.01, windDir: 300, dirBase: 320, seed: 123456, junk: 'x', pace: 'fast' } },
		] },
];

function run( sc ) {

	// a seed the constructor draws: Math.random() * 1e9 | 0
	const realRandom = Math.random;
	Math.random = () => ( sc.seed + 0.5 ) / 1e9;
	const cov = { calls: 0, v: NaN };
	const fft = {
		local: { windDirection: sc.windDir0, windSpeed: 0, fetch: 0 }, swell: { scale: 0 },
		choppiness: { value: 0 }, foamBias: { value: 0 }, foamDecay: { value: 0 }, resetFoam: null, spectrumCalls: 0,
		updateSpectrumUniforms( o ) { this.resetFoam = o.resetFoam; this.spectrumCalls ++; },
	};
	const shore = { amplitude: { value: 0 }, period: { value: 0 } };
	const clouds = { coverage: { get value() { return cov.v; }, set value( v ) { cov.v = v; cov.calls ++; } } };
	const app = { fft, shore, clouds, settings: { timeSpeed: 0, timeOfDay: sc.startHour }, ui: null };
	const w = new Weather( app );
	Math.random = realRandom;
	w.pace = sc.pace;
	const announcements = [];
	let frame = 0;
	w.onAnnounce = ( text ) => announcements.push( [ frame, text ] );
	// (the constructor's own write is the first thing C# does too)
	const rnd = sc.dt.kind === 'rand' ? mulberry32( sc.dt.seed ) : null;
	const frames = [], samples = [];
	let speedIdx = 0;
	for ( frame = 0; frame < sc.frames; frame ++ ) {

		while ( speedIdx < sc.speed.length && sc.speed[ speedIdx ][ 0 ] <= frame ) app.settings.timeSpeed = sc.speed[ speedIdx ++ ][ 1 ];
		for ( const e of sc.events ) {

			if ( e.frame !== frame ) continue;
			if ( e.restore ) w.restore( e.restore );
			if ( e.enabled !== undefined ) w.setEnabled( e.enabled );

		}

		const dt = rnd ? sc.dt.lo + rnd() * ( sc.dt.hi - sc.dt.lo ) : sc.dt.dt;
		if ( app.settings.timeSpeed !== 0 ) app.settings.timeOfDay = ( app.settings.timeOfDay + dt * app.settings.timeSpeed + 24 ) % 24;
		const before = cov.calls;
		w.update( dt );
		frames.push( w.level, w.target, w._hold, w.windDir, w._step, cov.calls - before, app.settings.timeOfDay );
		if ( frame % SAMPLE === 0 ) {

			samples.push( { frame, name: w.name, wind: fft.local.windSpeed, windDir: fft.local.windDirection, fetch: fft.local.fetch, swell: fft.swell.scale,
				chop: fft.choppiness.value, foamBias: fft.foamBias.value, foamDecay: fft.foamDecay.value, surf: shore.amplitude.value, period: shore.period.value,
				cover: cov.v, gx: G.windDir.value.x, gy: G.windDir.value.y, gspeed: G.windSpeed.value, resetFoam: fft.resetFoam, spectrumCalls: fft.spectrumCalls } );

		}

	}

	return { spec: sc, stride: 7, frames, samples, announcements, finalState: w.state(), spectrumCalls: fft.spectrumCalls, coverCalls: cov.calls };

}

const result = SCENARIOS.map( run );
fs.writeFileSync( path.join( out, 'weather.json' ), JSON.stringify( { sample: SAMPLE, scenarios: result } ) );
for ( const r of result ) console.log( `${ r.spec.name }: ${ r.frames.length / r.stride } frames, ${ r.announcements.length } announcements, ${ r.coverCalls } cover writes, level ${ r.finalState.level.toFixed( 3 ) }` );
