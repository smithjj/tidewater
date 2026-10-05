// Oracle for the C# whale behaviour (Runtime/World/Marine/WhaleBrain.cs): src/world/marine/WhaleBrain.js run on the real terrain with no water query (the sea
// level stays 0), for comparison by unity/Assets/Tidewater/Editor/WhaleOracle.cs:
//   node unity/tools/dump-whale-brain.mjs unity/Temp/oracle/whale && unity/tools/run-oracle.sh WhaleOracle
// Scenarios: seed, frame time, frames, forceBreach. Every SAMPLE-th frame records the state (route position, depth, angles, the flippers, the events, the
// root quaternion and the path rotation 12 m behind), and every blow / counter change is listed with its frame.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname( fileURLToPath( import.meta.url ) );
const root = path.resolve( here, '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const { WhaleBrain } = await import( root + '/src/world/marine/WhaleBrain.js' );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const E = await import( root + '/src/engine/index.js' );

const SAMPLE = 20;
const SCENARIOS = [
	{ name: 'a', seed: 1, dt: 1 / 30, frames: 36000, forceBreach: false },
	{ name: 'breach', seed: 7, dt: 1 / 60, frames: 40000, forceBreach: true },
	{ name: 'slow', seed: 123456789, dt: 0.09, frames: 9000, forceBreach: false },
];

const terrain = new TerrainData();
const res = [];
for ( const sc of SCENARIOS ) {

	const b = new WhaleBrain( { terrain, query: null, seed: sc.seed } );
	b.forceBreach = !! sc.forceBreach;
	let blows = 0;
	b.onBlow = () => { blows ++; };
	const rows = [], events = [];
	const q = new E.Quaternion();
	const last = { breaches: 0, splashes: 0, slaps: 0, blows: 0, state: b.state };
	for ( let f = 0; f < sc.frames; f ++ ) {

		b.update( sc.dt );
		if ( b.breaches !== last.breaches || b.splashes !== last.splashes || b.slaps !== last.slaps || blows !== last.blows || b.state !== last.state ) {

			events.push( [ f, b.breaches, b.splashes, b.slaps, blows, b.state ] );
			last.breaches = b.breaches; last.splashes = b.splashes; last.slaps = b.slaps; last.blows = blows; last.state = b.state;

		}

		if ( f % SAMPLE === 0 ) {

			b.pathRotation( 12, q );
			rows.push( [ b.u, b.position.x, b.position.y, b.position.z, b.yaw, b.pitch, b.roll, b.breachRoll, b.speed, b.vy, b.arch, b.follow, b.strokePhase, b.strokeAmp,
				b.bob, b.headPitch, b.blow, b.flukeUp, b.yawRate, b.arc,
				b.fin[ 0 ].sweep, b.fin[ 0 ].lift, b.fin[ 0 ].twist, b.fin[ 1 ].sweep, b.fin[ 1 ].lift, b.fin[ 1 ].twist,
				b.quaternion.x, b.quaternion.y, b.quaternion.z, b.quaternion.w, q.x, q.y, q.z, q.w, b.wetAge ] );

		}

	}

	res.push( { ...sc, length: b.length, sample: SAMPLE, rows, events, blows } );
	console.log( sc.name, 'length', b.length.toFixed( 3 ), 'breaches', b.breaches, 'splashes', b.splashes, 'slaps', b.slaps, 'blows', blows, 'events', events.length );

}

fs.writeFileSync( path.join( out, 'whale-brain.json' ), JSON.stringify( res ) );
