// Oracle for the C# sky maths (Runtime/Sky/SkyMath.cs): the sun, night, moon and key light that src/App.js updateSun() computes from the clock hour
// and the sun azimuth, and the key light colour of applyAtmosphereReadback(), for comparison by unity/Assets/Tidewater/Editor/SkyOracle.cs:
//   node unity/tools/dump-sky.mjs unity/Temp/oracle/sky
// sunDirectionFromTime is the real src/sky/Sky.js. updateSun / applyAtmosphereReadback are copied from App.js line for line (App.js itself cannot be
// imported: it builds the whole renderer). The sun's transmittance is read back from a GPU LUT in the game; here (and in C#) it is integrated on the CPU
// from Atmosphere.js's medium, so that part checks the two integrations against each other, not against the LUT.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname( fileURLToPath( import.meta.url ) );
const root = path.resolve( here, '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const { sunDirectionFromTime } = await import( root + '/src/sky/Sky.js' );
const { Vector3, MathUtils } = await import( root + '/src/engine/index.js' );

const _up = new Vector3( 0, 1, 0 );
const SUN_ILLUMINANCE = 11.0;
const RG = 6360.0, RT = 6460.0, VIEW = RG + 0.002;

// Atmosphere.js atmosphereMedium, integrated from the viewer to the top of the atmosphere
function transmittance( mu ) {

	const r = VIEW;
	const disc = r * r * ( mu * mu - 1 ) + RT * RT;
	const d = Math.max( 0, - r * mu + Math.sqrt( Math.max( disc, 0 ) ) );
	const N = 256, dt = d / N;
	let a = [ 0, 0, 0 ];
	for ( let i = 0; i < N; i ++ ) {

		const t = ( i + 0.5 ) * dt;
		const h = Math.sqrt( r * r + t * t + 2 * r * mu * t ) - RG;
		const ray = Math.exp( - h / 8.0 ), mie = Math.exp( - h / 1.2 ), ozone = Math.max( 0, 1 - Math.abs( h - 25 ) / 15 );
		const m = 4.440e-3 * mie;
		a[ 0 ] += 5.802e-3 * ray + m + 0.650e-3 * ozone;
		a[ 1 ] += 13.558e-3 * ray + m + 1.881e-3 * ozone;
		a[ 2 ] += 33.1e-3 * ray + m + 0.085e-3 * ozone;

	}

	return a.map( ( x ) => Math.exp( - x * dt ) );

}

// App.updateSun + applyAtmosphereReadback
function sky( hours, sunAzimuth ) {

	const dir = sunDirectionFromTime( hours ).applyAxisAngle( _up, MathUtils.degToRad( sunAzimuth || 0 ) );
	const night = MathUtils.smoothstep( - dir.y, 0.02, 0.18 );
	const moon = new Vector3( - dir.x, Math.abs( dir.y ) * 0.8 + 0.25, - dir.z ).normalize();
	const sunUp = dir.y > - 0.07;
	const light = sunUp ? dir : moon;
	const horizonFade = MathUtils.smoothstep( dir.y, - 0.03, 0.02 );
	const T = transmittance( dir.y );
	const c = sunUp ? [ T[ 0 ] * SUN_ILLUMINANCE * horizonFade, T[ 1 ] * SUN_ILLUMINANCE * horizonFade, T[ 2 ] * SUN_ILLUMINANCE * horizonFade ]
		: [ 0.6 * 0.12 * night, 0.7 * 0.12 * night, 1.0 * 0.12 * night ];
	const dark = 1.0 - MathUtils.smoothstep( dir.y, - 0.28, - 0.1 );
	return [ dir.x, dir.y, dir.z, moon.x, moon.y, moon.z, light.x, light.y, light.z, sunUp ? 1 : 0, night, c[ 0 ], c[ 1 ], c[ 2 ], 0.012 * night, dark, T[ 0 ], T[ 1 ], T[ 2 ] ];

}

const FIELDS = 19;
const cases = [];
// every 3 minutes of the day at three azimuth settings, plus the awkward spots: the key light switch, the moon's sign flip, midnight, noon
for ( const az of [ 0, 37.5, - 90 ] ) for ( let m = 0; m < 24 * 20; m ++ ) cases.push( [ m / 20, az ] );
for ( const h of [ 0, 5.5, 5.75, 6, 6.25, 12, 17.5, 17.75, 18, 18.25, 18.5, 23.999 ] ) cases.push( [ h, 0 ] );
const flat = [];
for ( const [ h, az ] of cases ) flat.push( h, az, ...sky( h, az ) );
// PostFX.js auto exposure meter kernel (the target multiplier), line for line, over a log grid of average luminances and some nights
const clamp = ( x, a, b ) => Math.min( b, Math.max( a, x ) );
const mix = ( a, b, t ) => a * ( 1 - t ) + b * t;
function exposureTarget( avg, night ) {

	const ae = { refLum: 0.25, min: 0.6, max: 6.0 };
	const ratio = ae.refLum / avg;
	const partial = ratio > 1.0 ? Math.pow( ratio, 0.8 ) : ratio;
	return clamp( partial, ae.min, mix( ae.max, 2.0, night ) );

}

const ae = [];
for ( const night of [ 0, 0.2, 0.5, 1 ] ) for ( let i = 0; i <= 120; i ++ ) { const avg = Math.pow( 2, - 12 + i * 0.2 ); ae.push( avg, night, exposureTarget( avg, night ) ); }
fs.writeFileSync( path.join( out, 'sky.json' ), JSON.stringify( { fields: FIELDS, cases: cases.length, data: flat, ae } ) );
console.log( `sky oracle: ${ cases.length } cases x ${ FIELDS } values -> ${ out }` );
