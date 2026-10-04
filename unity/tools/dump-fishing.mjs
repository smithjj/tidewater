// Oracle for the C# fishing port: runs the original JS Bites / CatchMinigame / Sonar / FishingRod and records what they do, for the comparer in
// unity/Assets/Tidewater/Editor/FishingOracle.cs:
//   node unity/tools/dump-fishing.mjs unity/Temp/oracle/fishing
//
//  - habitatAt over a grid, activity over every preference and hour, 600 bite rolls (pickSpecies / rollWeight / biteDelay with a seeded rng),
//  - 160 line fights (CatchMinigame, every species, line, reel speed; a scripted reeling pattern; the state every 6 frames),
//  - fishNear / schoolBite on synthetic school groups,
//  - six rod scenarios (cast, strike and fight to the three outcomes, a cast onto the sand, a reel-in in the air): the real FishingRod driven by a
//    scripted camera, a synthetic sea (polynomial waves, as the boat oracle) and sea floor, and scripted button events, recording the state every
//    2nd frame: pose, bend, tip, bobber, line, reel animation, and the matrix the mesh is drawn with.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );
const THREE = await import( root + '/src/engine/index.js' );
const { habitatAt, activity, pickSpecies, rollWeight, biteDelay } = await import( root + '/src/game/Bites.js' );
const { CatchMinigame } = await import( root + '/src/game/CatchMinigame.js' );
const { fishNear, schoolBite } = await import( root + '/src/game/Sonar.js' );
const { FishingRod } = await import( root + '/src/game/FishingRod.js' );
const { FISH, FISH_IDS } = await import( root + '/src/game/FishTable.js' );

// a deterministic generator both sides can run (no transcendentals)
const lcg = ( seed ) => () => ( seed = ( seed * 16807 ) % 2147483647 ) / 2147483647;
const rnd = lcg( 20260601 );
const HK = [ 'shallows', 'reef', 'pier', 'bay', 'deep' ];
const hv = ( h ) => HK.map( ( k ) => h[ k ] );

const result = {};

// ---- habitat, activity
{
	const depths = [ 0, 0.2, 0.25, 0.3, 0.8, 1, 1.5, 2.5, 3.5, 4, 8, 12, 16, 20, 28, 35 ], reefs = [ - 10, - 6, 0, 6, 12, 20 ], piers = [ 0, 2, 5, 9, 15 ];
	const rows = [];
	for ( const d of depths ) for ( const r of reefs ) for ( const p of piers ) rows.push( { in: [ d, r, p ], out: hv( habitatAt( { depth: d, reefDist: r, pierDist: p } ) ) } );
	result.habitat = rows;
	const act = [];
	for ( const pref of [ 'day', 'dawnDusk', 'night', 'any', 'other' ] ) for ( let h = 0; h <= 24; h += 0.25 ) act.push( { pref, hour: h, v: activity( pref, h ) } );
	result.activity = act;
}

// ---- bite rolls
{
	const hs = result.habitat.map( ( r ) => r.out ).filter( ( h, i ) => i % 7 === 0 );
	const ops = [];
	const SEED = 777;
	const rng = lcg( SEED );
	for ( let i = 0; i < 600; i ++ ) {

		const h = hs[ Math.floor( rnd() * hs.length ) ];
		const hour = rnd() * 24;
		const school = rnd() < 0.5 ? 0 : rnd();
		const bias = i % 3 === 0 ? { id: FISH_IDS[ Math.floor( rnd() * FISH_IDS.length ) ], k: 1 + rnd() * 3 } : null;
		const hab = Object.fromEntries( HK.map( ( k, j ) => [ k, h[ j ] ] ) );
		const species = pickSpecies( hab, hour, rng, bias );
		const kg = species ? rollWeight( species, rng ) : null;
		const delay = biteDelay( hab, hour, rng, school );
		ops.push( { hab: h, hour, school, bias, species, kg, delay: Number.isFinite( delay ) ? delay : 'inf' } );

	}

	result.bites = { seed: SEED, ops };
}

// ---- fights
const CODE = { fighting: 0, caught: 1, snapped: 2, escaped: 3 };
{
	const trials = [];
	for ( let i = 0; i < 160; i ++ ) {

		const sp = FISH_IDS[ i % FISH_IDS.length ];
		const f = FISH[ sp ];
		const kg = f.kg[ 0 ] + ( f.kg[ 1 ] - f.kg[ 0 ] ) * rnd();
		const lineKg = [ 3, 7, 12, 20, 40 ][ Math.floor( rnd() * 5 ) ];
		const reelSpeed = [ 0.9, 1.1, 1.4, 2 ][ Math.floor( rnd() * 4 ) ];
		const distance = [ 3, 10, 15, 30 ][ Math.floor( rnd() * 4 ) ];
		const seed = 1 + Math.floor( rnd() * 1e6 );
		// reeling: alternating segments (seconds); the style decides how much of the time
		const style = i % 4; // 0 mostly reeling, 1 half, 2 rarely, 3 never
		const segs = [];
		for ( let k = 0; k < 400; k ++ ) segs.push( [ style === 3 ? 0 : [ 2.5, 1.2, 0.5 ][ style ] * ( 0.3 + rnd() ), ( style === 0 ? 0.3 : 0.9 ) * ( 0.3 + rnd() ) ] );
		const g = new CatchMinigame( { species: sp, kg, lineKg, reelSpeed, distance, rng: lcg( seed ) } );
		const dt = 1 / 60;
		const rows = [];
		let state = 'fighting', f6 = 0, si = 0, left = segs[ 0 ][ 0 ], reeling = true;
		while ( state === 'fighting' && f6 < 60 * 240 ) {

			while ( left <= 0 ) { si ++; reeling = si % 2 === 0; left = ( segs[ Math.min( Math.floor( si / 2 ), segs.length - 1 ) ] || [ 1, 1 ] )[ reeling ? 0 : 1 ]; if ( si > 2000 ) break; }
			if ( style === 3 ) reeling = false;
			state = g.update( dt, reeling );
			left -= dt;
			if ( f6 % 6 === 0 || state !== 'fighting' ) rows.push( [ f6, g.tension, g.distance, g.stamina, g.surge, g.slack, g.overload, CODE[ state ] ] );
			f6 ++;

		}

		trials.push( { species: sp, kg, lineKg, reelSpeed, distance, seed, style, segs, power: g.power, staminaMax: g.staminaMax, maxDistance: g.maxDistance, frames: f6, state: CODE[ state ], rows } );

	}

	result.fights = trials;
}

// ---- sonar
{
	const modes = [ 'school', 'hover', 'lurk', 'escort', 'bait', 'cruise', 'remora' ];
	const models = [ 'grouper', 'yellowtail', 'silverside', 'eagleRay', null, 'tang', 'barracuda' ];
	const groups = [];
	for ( let i = 0; i < 18; i ++ ) groups.push( {
		center: { x: ( rnd() - 0.5 ) * 160, y: - rnd() * 14, z: ( rnd() - 0.5 ) * 160 }, count: Math.floor( 2 + rnd() * 70 ), radius: 2 + rnd() * 10,
		sp: { mode: modes[ i % modes.length ], model: models[ i % models.length ], name: 'g' + i },
	} );
	const queries = [];
	for ( let i = 0; i < 60; i ++ ) {

		const x = ( rnd() - 0.5 ) * 160, z = ( rnd() - 0.5 ) * 160, r = [ 12, 30, 65 ][ i % 3 ], max = [ 0, 1, 3, 5 ][ i % 4 ], minSep = [ 0, 10, 25 ][ i % 3 ];
		queries.push( {
			x, z, r, max, minSep,
			near: fishNear( groups, x, z, r, max, minSep ).map( ( q ) => [ q.x, q.z, q.depth, q.count, q.radius, q.dist, q.model, q.name ] ),
			bite: ( () => { const b = schoolBite( groups, x, z ); return b && [ b.model, b.name, b.dist, b.count, b.influence, b.bite, b.bias ]; } )(),
		} );

	}

	result.sonar = { groups, queries };
}

// ---- the rod
const sstep = THREE.MathUtils.smoothstep;
const tri = ( p ) => { const q = p - Math.floor( p ); const k = q < 0.5 ? 2 * q : 2 - 2 * q; return k * k * ( 3 - 2 * k ); };
const waterAt = ( x, z, t ) => 0.55 * ( tri( x * 0.11 + z * 0.07 - 0.45 * t ) - 0.5 ) * 2 + 0.22 * ( tri( - x * 0.2 + z * 0.17 - 0.7 * t ) - 0.5 ) * 2;
const groundAt = ( x, z ) => - 6 + 0.05 * ( 60 - z ) + 9.5 * ( 1 - sstep( Math.hypot( x - 70, z - 95 ), 0, 25 ) );

class FakeQuery {

	constructor() { this.count = 1; this.latency = 0.05; this.cpuValid = false; this.version = 0; this.resultTime = 0; this.cpu = new Float32Array( 1024 ); this.resultInputs = new Float32Array( 1024 ); this.inputs = new Float32Array( 1024 ); this.pending = null; }
	allocate( name, n ) { const s = this.count; this.count += n; return s; }
	setPoint( i, x, z ) { this.inputs[ i * 4 ] = x; this.inputs[ i * 4 + 1 ] = z; }
	update( f, t ) {

		if ( this.pending && f >= this.pending.f ) { this.cpu.set( this.pending.res ); this.resultInputs.set( this.pending.inp ); this.resultTime = this.pending.t; this.cpuValid = true; this.version ++; this.pending = null; }
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

const SCENARIOS = [
	{ name: 'cast-retrieve', cam: { p: [ 10, 1.7, 5 ], v: [ 0.5, 0, 0 ], yaw: 0.2, dyaw: 0.03, pitch: - 0.08 }, frames: 700, ev: [
		{ f: 0, a: 'equip' }, { f: 30, a: 'windup' }, { f: 85, a: 'release' }, { f: 300, a: 'dip', v: 0.2 }, { f: 320, a: 'dip', v: 0.45 }, { f: 340, a: 'dip', v: 0 }, { f: 380, a: 'retrieve' } ] },
	{ name: 'fight-caught', cam: { p: [ 12, 1.7, 6 ], v: [ 0, 0, 0 ], yaw: - 0.15, dyaw: 0, pitch: - 0.1 }, frames: 2400, fight: { f: 260, species: 'grouper', kg: 2.5, lineKg: 15, reelSpeed: 1.4, seed: 4242, reel: 'mostly' }, ev: [
		{ f: 0, a: 'equip' }, { f: 20, a: 'windup' }, { f: 70, a: 'release' }, { f: 230, a: 'dip', v: 1.2 } ] },
	{ name: 'fight-snapped', cam: { p: [ 12, 1.7, 6 ], v: [ 0.2, 0, 0 ], yaw: 0.1, dyaw: 0.01, pitch: - 0.05 }, frames: 1500, fight: { f: 250, species: 'grouper', kg: 14, lineKg: 4, reelSpeed: 1.1, seed: 99, reel: 'always' }, ev: [
		{ f: 0, a: 'equip' }, { f: 20, a: 'windup' }, { f: 65, a: 'release' }, { f: 220, a: 'dip', v: 1.1 } ] },
	{ name: 'fight-escaped', cam: { p: [ 8, 1.7, 4 ], v: [ 0, 0, 0 ], yaw: 0.35, dyaw: 0, pitch: - 0.12 }, frames: 2400, fight: { f: 250, species: 'yellowtail', kg: 1.6, lineKg: 7, reelSpeed: 1.1, seed: 31337, reel: 'never' }, ev: [
		{ f: 0, a: 'equip' }, { f: 20, a: 'windup' }, { f: 50, a: 'release' }, { f: 220, a: 'dip', v: 1.0 } ] },
	{ name: 'ground', cam: { p: [ 70, 1.7, 75 ], v: [ 0, 0, 0 ], yaw: Math.PI, dyaw: 0, pitch: - 0.05 }, frames: 520, ev: [
		{ f: 0, a: 'equip' }, { f: 10, a: 'windup' }, { f: 70, a: 'release' } ] },
	{ name: 'fly-retrieve', cam: { p: [ 10, 1.7, 5 ], v: [ 0, 0, 0 ], yaw: 0, dyaw: - 0.05, pitch: 0.05 }, frames: 480, ev: [
		{ f: 0, a: 'equip' }, { f: 10, a: 'windup' }, { f: 120, a: 'release' }, { f: 135, a: 'retrieve' }, { f: 300, a: 'equip', v: false } ] },
];

const STATES = [ 'stowed', 'idle', 'windup', 'flick', 'flying', 'floating', 'fighting', 'retrieving', 'landing' ];
const dt = 1 / 60;
result.rod = SCENARIOS.map( ( S ) => {

	const camera = new THREE.PerspectiveCamera();
	const query = new FakeQuery();
	const terrain = { heightAt: groundAt };
	const rod = new FishingRod( { scene: { add() {} }, camera, query, terrain } );
	rod.setGear( { castM: S.castM ?? 22, reelSpeed: 1.1 } );
	const lands = [];
	rod.onLand = ( where ) => lands.push( where );
	const events = S.ev.slice();
	let fight = null, reeling = false, landEnd = -1, rows = [], fightLog = [];
	const reelAt = ( ev ) => events.push( ev );
	const reelPattern = ( t ) => S.fight.reel === 'always' ? true : S.fight.reel === 'never' ? false : ( t % 1.6 ) < 1.2;
	const euler = new THREE.Euler( 0, 0, 0, 'YXZ' );
	const camLog = [];
	for ( let f = 0; f < S.frames; f ++ ) {

		const t = f * dt, c = S.cam;
		camera.position.set( c.p[ 0 ] + c.v[ 0 ] * t, c.p[ 1 ] + c.v[ 1 ] * t, c.p[ 2 ] + c.v[ 2 ] * t );
		euler.set( c.pitch, c.yaw + c.dyaw * t, 0, 'YXZ' );
		camera.quaternion.setFromEuler( euler );
		const q = camera.quaternion;
		camLog.push( [ camera.position.x, camera.position.y, camera.position.z, q.x, q.y, q.z, q.w ] );
		for ( const e of events.filter( ( x ) => x.f === f ) ) {

			if ( e.a === 'equip' ) rod.equip( e.v ?? true );
			else if ( e.a === 'windup' ) rod.startWindup();
			else if ( e.a === 'release' ) rod.release();
			else if ( e.a === 'retrieve' ) rod.retrieve();
			else if ( e.a === 'dip' ) rod.dip = e.v;

		}

		if ( S.fight && f === S.fight.f && rod.state === 'floating' ) {

			const F = S.fight;
			fight = new CatchMinigame( { species: F.species, kg: F.kg, lineKg: F.lineKg, reelSpeed: F.reelSpeed, distance: Math.max( 3, rod.lineOut ), rng: lcg( F.seed ) } );
			rod.hook();
			fightLog.push( [ f, 'hook', rod.lineOut ] );

		}

		if ( fight ) {

			const st = fight.update( dt, reelPattern( ( f - S.fight.f ) * dt ) );
			if ( st !== 'fighting' ) {

				fightLog.push( [ f, st ] );
				fight = null; rod.dip = 0;
				if ( st === 'caught' ) { rod.land(); landEnd = f + 90; }
				else if ( st === 'snapped' ) rod.setState( 'idle' );
				else rod.endFight();

			}

		}

		if ( landEnd === f ) { if ( rod.state === 'landing' ) rod.setState( 'idle' ); landEnd = - 1; }
		query.update( f, t );
		rod.update( dt, { visible: true, fight } );
		if ( f % 2 === 0 ) {

			const m = rod.rodMesh.matrix.elements, rb = rod.rodMat.uniforms.rodBend.value, ra = rod.rodMat.uniforms.reelAnim.value, ra2 = rod.rodMat.uniforms.reelAnim2.value, lm = rod.lineMat.uniforms;
			rows.push( [
				f, STATES.indexOf( rod.state ), rod.t, rod.power, rod.pose.elev, rod.pose.side, rod.pose.hand.x, rod.pose.hand.y, rod.pose.hand.z,
				rod.bend, rod.load, rod.bendDir.x, rod.bendDir.z, rb.x, rb.y, rb.z, rb.w, rod.rodMat.uniforms.rodShape.value.x,
				ra.x, ra.y, ra.z, ra.w, ra2.x, ra2.y,
				rod.tip.x, rod.tip.y, rod.tip.z, rod.bobber.x, rod.bobber.y, rod.bobber.z, rod.bobberVel.x, rod.bobberVel.y, rod.bobberVel.z,
				rod.dip, rod.lineOut, rod.waterY, rod.depth, rod.fishPos.x, rod.fishPos.z,
				lm.lineA.value.x, lm.lineA.value.y, lm.lineA.value.z, lm.lineB.value.x, lm.lineB.value.y, lm.lineB.value.z, lm.lineCtl.value.x, lm.lineCtl.value.y, lm.lineCtl.value.z,
				rod.rodMesh.visible ? 1 : 0, rod.lineMesh.visible ? 1 : 0, rod.bobberMesh.visible ? 1 : 0, rod.bobberMesh.scale.x, rod.bobberMesh.rotation.x,
				...m,
				fight ? fight.distance : - 1, fight ? fight.tension : - 1, fight ? fight.surge : - 1, fight ? fight.stamina : - 1,
			] );

		}

	}

	return { name: S.name, cam: S.cam, frames: S.frames, events: S.ev, fight: S.fight || null, camLog, rows, lands, fightLog, castM: S.castM ?? 22 };

} );

fs.writeFileSync( path.join( out, 'fishing.json' ), JSON.stringify( result ) );
console.log( 'wrote', path.join( out, 'fishing.json' ), 'bites', result.bites.ops.length, 'fights', result.fights.length, 'rod rows', result.rod.map( ( r ) => r.rows.length ).join( '/' ) );
console.log( 'fight outcomes', [ 0, 1, 2, 3 ].map( ( c ) => result.fights.filter( ( t ) => t.state === c ).length ).join( ' ' ), 'rod fights', JSON.stringify( result.rod.map( ( r ) => r.fightLog.map( ( l ) => l[ 1 ] ).join( ',' ) ) ), 'lands', JSON.stringify( result.rod.map( ( r ) => r.lands ) ) );
