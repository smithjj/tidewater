// Oracle for the C# boat controller port: runs the original JS BoatController (real BoatModel, real Colliders) against a synthetic sea
// (polynomial waves: no transcendentals, so both sides compute the same doubles) and a synthetic sea floor, delivering the water queries
// the way the GPU does (issued every 2nd frame, arriving 2 frames later), and records the state every 0.25 s.
//   node unity/tools/dump-boat-controller.mjs <outDir>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const THREE = await import( root + '/src/engine/index.js' );
const { BoatModel } = await import( root + '/src/world/BoatModel.js' );
const { BoatController } = await import( root + '/src/player/BoatController.js' );
const { Colliders } = await import( root + '/src/world/Colliders.js' );

const sstep = THREE.MathUtils.smoothstep;
const tri = ( p ) => { const q = p - Math.floor( p ); const k = q < 0.5 ? 2 * q : 2 - 2 * q; return k * k * ( 3 - 2 * k ); };
const waterAt = ( x, z, t ) => 0.55 * ( tri( x * 0.11 + z * 0.07 - 0.45 * t ) - 0.5 ) * 2 + 0.22 * ( tri( - x * 0.2 + z * 0.17 - 0.7 * t ) - 0.5 ) * 2;
const groundAt = ( x, z ) => - 6 + 0.05 * ( 60 - z ) + 9.5 * ( 1 - sstep( Math.hypot( x - 70, z - 95 ), 0, 25 ) );

class FakeQuery {

	constructor() { this.count = 1; this.latency = 0.05; this.cpuValid = false; this.version = 0; this.resultTime = 0; this.cpu = new Float32Array( 1024 ); this.resultInputs = new Float32Array( 1024 ); this.inputs = new Float32Array( 1024 ); this.pending = null; }
	allocate( name, n ) { const s = this.count; this.count += n; return s; }
	setPoint( i, x, z ) { this.inputs[ i * 4 ] = x; this.inputs[ i * 4 + 1 ] = z; }
	update( f, t ) {

		if ( this.pending && f >= this.pending.f ) {

			this.cpu.set( this.pending.res ); this.resultInputs.set( this.pending.inp ); this.resultTime = this.pending.t; this.cpuValid = true; this.version ++; this.pending = null;

		}

		if ( ! this.pending && f % 2 === 0 ) {

			const res = new Float32Array( 1024 ), e = 0.35;
			for ( let i = 1; i < this.count; i ++ ) {

				const x = this.inputs[ i * 4 ], z = this.inputs[ i * 4 + 1 ];
				const h = waterAt( x, z, t ), hx = waterAt( x + e, z, t ), hz = waterAt( x, z + e, t );
				const n = new THREE.Vector3( ( h - hx ) / e, 1, ( h - hz ) / e ).normalize();
				res[ i * 4 ] = h; res[ i * 4 + 1 ] = n.x; res[ i * 4 + 2 ] = n.z; res[ i * 4 + 3 ] = groundAt( x, z );

			}

			this.pending = { f: f + 2, res, inp: this.inputs.slice(), t };

		}

	}

}

const colliders = new Colliders();
colliders.addCylinder( 66.0, 35.6, 0.3, - 3, 2.3, 'pile' );
colliders.addBox( new THREE.Vector3( 60, 0, 38 ), new THREE.Vector3( 1, 2, 3 ), 0.3, { tag: 'box' } );
const terrain = { heightAt: groundAt };

const dt = 1 / 60;
const scenarios = {
	moored: { dock: { position: [ 64.5, 0, 36.5 ], heading: 0 }, seconds: 20, events: [] },
	drive: {
		dock: { position: [ 0, 0, 0 ], heading: 0.3 }, seconds: 72,
		events: [
			{ t: 1, driven: true, moored: false }, { t: 2, throttle: 1 }, { t: 10, steer: 0.4 }, { t: 20, steer: - 0.7 }, { t: 25, throttle: 0.3 },
			{ t: 28, throttle: - 1, steer: - 1 }, { t: 33, throttle: 0, steer: 0 }, { t: 50, drop: 5 }, { t: 51, throttle: 0.6 }, { t: 66, weigh: true },
		],
	},
	shoal: { dock: { position: [ 70, 0, 70 ], heading: 0 }, seconds: 25, events: [ { t: 0.5, driven: true, moored: false }, { t: 1, throttle: 1 } ] },
};

const result = {};
for ( const [ name, sc ] of Object.entries( scenarios ) ) {

	const model = new BoatModel();
	const query = new FakeQuery();
	const dock = { position: new THREE.Vector3( ...sc.dock.position ), heading: sc.dock.heading };
	const c = new BoatController( { model, query, terrain, colliders, dock } );
	const ctor = {
		mass: c.mass, com: c.com.toArray(), inertia: c.inertia.toArray(), BG: c.BG, pitchStiffness: c.pitchStiffness, nHull: c.nHull, n: c.samples.length, slot: c.slot,
		samples: c.samples.map( ( s ) => [ s.p.x, s.p.y, s.p.z, s.area, s.bottom, s.reserve ? 1 : 0, s.ref ?? - 1 ] ),
		bow: c.bowWorld.toArray(), stern: c.sternWorld.toArray(),
	};
	let throttle = 0, steer = 0;
	const rows = [], drops = [];
	const frames = Math.round( sc.seconds / dt );
	let evi = 0;
	const events = sc.events.map( ( e ) => ( { ...e, f: Math.round( e.t / dt ) } ) );
	for ( let f = 0; f < frames; f ++ ) {

		const t = f * dt;
		while ( evi < events.length && events[ evi ].f <= f ) {

			const e = events[ evi ++ ];
			if ( e.driven !== undefined ) c.driven = e.driven;
			if ( e.moored !== undefined ) c.moored = e.moored;
			if ( e.throttle !== undefined ) throttle = e.throttle;
			if ( e.steer !== undefined ) steer = e.steer;
			if ( e.drop !== undefined ) { const r = c.dropAnchor( e.drop ); drops.push( [ f, r.ok ? 1 : 0, r.rode ?? 0 ] ); }
			if ( e.weigh ) c.weighAnchor();

		}

		c.setInput( throttle, steer, dt );
		c.queueQueries();
		query.update( f, t );
		c.update( dt );
		if ( f % 15 === 0 ) {

			rows.push( [ t, ...c.position.toArray(), c.quaternion.x, c.quaternion.y, c.quaternion.z, c.quaternion.w, ...c.velocity.toArray(), ...c.angular.toArray(),
				c.throttle, c.steer, c.rpm, c.thrust, c.wetFraction, c.speed, c.anchor.tension, c.hasWater ? 1 : 0 ] );

		}

	}

	result[ name ] = { ...sc, ctor, rows, drops, throttleEvents: sc.events };
	console.log( name, 'rows', rows.length, 'final', rows[ rows.length - 1 ].slice( 0, 8 ).map( ( v ) => v.toFixed( 3 ) ).join( ' ' ) );

}

fs.writeFileSync( path.join( out, 'boatctl.json' ), JSON.stringify( result ) );
