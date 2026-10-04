// BoatController stability on the CPU (node, no GPU): the lobster boat's real hull lines and the Pelagic 30,
// each driven through the real controller against an analytic sea behind the WaterQuery API.
//
// Three things the boat got wrong, and that this keeps right:
//   - the righting moment ran out at ~40 degrees of heel (a steep wave plus a hard turn rolled it over);
//   - on a sloping surface it heeled by slope * BM / GM, ~1.7x the slope (2.4x in motion), so it
//     exaggerated every wave: a boat must ride the surface (heel = slope) in a long swell;
//   - and so it capsized in an ordinary heavy sea.
//   node test/boat-stability.mjs
import * as E from '../src/engine/index.js';
import { BoatController, ANCHOR } from '../src/player/BoatController.js';
import { HullLines, RHO_SEAWATER } from '../src/world/boat/HullLines.js';
import { Pelagic30 } from '../src/world/boats/Pelagic30.js';
import { MiniFishingBoat } from '../src/world/boats/MiniFishingBoat.js';
import { MAX_QUERIES, QUERY_WORKGROUP, queryWorkgroups } from '../src/ocean/QueryLimits.js';

let fails = 0;
const check = ( ok, msg ) => {

	console.log( ( ok ? 'ok   ' : 'FAIL ' ) + msg );
	if ( ! ok ) fails ++;

};
const DEG = 180 / Math.PI;
const dt = 1 / 60;

// ---- the boats: BoatModel's anchor / hydro API from the real hull lines, and the real Pelagic model
function lobsterModel() {

	const lines = new HullLines();
	const mass = Math.round( RHO_SEAWATER * lines.canoeVolume );
	const kRoll = 0.36 * 2.9, kPitch = 0.26 * lines.length, kYaw = 0.27 * lines.length;
	return {
		group: new E.Group(), lines, hullSamples: lines.buildHullSamples( 8 ),
		hydro: { suggestedMass: mass, centerOfMass: new E.Vector3( 0, 0.3, lines.centerOfBuoyancy.z ), centerOfBuoyancy: lines.centerOfBuoyancy.clone(), inertia: new E.Vector3( mass * kPitch ** 2, mass * kYaw ** 2, mass * kRoll ** 2 ) },
		helmEye: new E.Vector3( 0.4, 1.85, 0.3 ), boardPoint: new E.Vector3( 0, 0.9, - 1.75 ), exitPoints: [],
		propeller: new E.Vector3( 0, - 0.55, - 3.6 ), rudder: new E.Vector3( 0, - 0.5, - 3.9 ), bowZ: 4.3, sternZ: - 3.9,
		setThrottle() {}, setSteering() {}, setPropellerRPM() {},
	};

}

// ---- a sea: `h( x, z, t )` and its gradient. A plane (slope s along +x), or a Pierson-Moskowitz spectrum
// of Hs / Tp spread +-35 degrees about `dir` (radians, the direction the waves travel from +z)
function planeSea( slope ) {

	return { h: ( x ) => slope * x, grad: () => [ slope, 0 ] };

}

function irregularSea( Hs, Tp, dir ) {

	let seed = 12345;
	const rnd = () => ( seed = ( seed * 16807 ) % 2147483647 ) / 2147483647;
	const wp = 2 * Math.PI / Tp, N = 40, w0 = 0.5 * wp, w1 = 3.5 * wp, dw = ( w1 - w0 ) / N;
	const S = ( om ) => ( 5 / 16 ) * Hs * Hs * wp ** 4 / om ** 5 * Math.exp( - 1.25 * ( wp / om ) ** 4 );
	const comps = [];
	for ( let i = 0; i < N; i ++ ) {

		const om = w0 + ( i + rnd() ) * dw, kk = om * om / 9.81, th = dir + ( rnd() - 0.5 ) * 2 * 0.61;
		comps.push( { amp: Math.sqrt( 2 * S( om ) * dw ), om, kx: kk * Math.sin( th ), kz: kk * Math.cos( th ), ph: rnd() * 6.2832 } );

	}

	return {
		h: ( x, z, t ) => { let v = 0; for ( const c of comps ) v += c.amp * Math.sin( c.kx * x + c.kz * z - c.om * t + c.ph ); return v; },
		grad: ( x, z, t ) => {

			let gx = 0, gz = 0;
			for ( const c of comps ) { const q = c.amp * Math.cos( c.kx * x + c.kz * z - c.om * t + c.ph ); gx += q * c.kx; gz += q * c.kz; }
			return [ gx, gz ];

		},
	};

}

// WaterQuery stub: heights and normals of `sea` at the queued points, a fresh result every frame
function makeQuery( sea ) {

	const q = {
		n: 0, slots: new Map(), points: new Float32Array( 1024 * 4 ), cpu: new Float32Array( 1024 * 4 ), resultInputs: new Float32Array( 1024 * 4 ),
		cpuValid: false, version: 0, resultTime: 0, latency: 2 / 60, time: 0,
		// the real WaterQuery.allocate: a name that is already taken gives back the same slots, and the
		// whole table is MAX_QUERIES long
		allocate( name, n ) {

			if ( this.slots.has( name ) ) return this.slots.get( name );
			if ( this.n + n > MAX_QUERIES ) throw new Error( 'WaterQuery: out of slots' );
			const s = this.n;
			this.slots.set( name, s );
			this.n += n;
			return s;

		},
		setPoint( i, x, z ) { this.points[ i * 4 ] = x; this.points[ i * 4 + 1 ] = z; },
		tick() {

			for ( let i = 0; i < this.n; i ++ ) {

				const x = this.points[ i * 4 ], z = this.points[ i * 4 + 1 ];
				const [ gx, gz ] = sea.grad( x, z, this.time ), l = Math.hypot( gx, 1, gz );
				this.cpu[ i * 4 ] = sea.h( x, z, this.time ); this.cpu[ i * 4 + 1 ] = - gx / l; this.cpu[ i * 4 + 2 ] = - gz / l;
				this.resultInputs[ i * 4 ] = x; this.resultInputs[ i * 4 + 1 ] = z;

			}

			this.cpuValid = true; this.version ++; this.resultTime = this.time;

		},
	};
	return q;

}

function makeBoat( model, sea, query = makeQuery( sea ), at = new E.Vector3( 0, 0, 0 ) ) {

	const boat = new BoatController( { model, query, terrain: { heightAt: () => - 100 }, colliders: null, dock: { position: at, heading: 0 } } );
	boat.moored = false;
	boat.driven = true;
	return { boat, query };

}

const frame = ( b, thr, steer ) => {

	b.query.time += dt;
	b.query.tick();
	b.boat.queueQueries();
	b.boat.setInput( thr, steer, dt );
	b.boat.update( dt );

};
const attitude = ( boat ) => { const e = new E.Euler().setFromQuaternion( boat.quaternion, 'YXZ' ); return { pitch: e.x * DEG, roll: e.z * DEG }; };

// roll acceleration at a given heel, with the heave found by bisection so the boat floats at that heel
function rollAccel( boat, phi ) {

	const h = 1e-7, axis = new E.Vector3( 0, 0, 1 );
	boat.quaternion.setFromAxisAngle( axis, phi );
	const settle = ( y ) => { boat.position.set( 0, y, 0 ); boat.velocity.set( 0, 0, 0 ); boat.angular.set( 0, 0, 0 ); boat.step( h ); };
	let lo = - 2.5, hi = 2.0;
	for ( let i = 0; i < 60; i ++ ) { const m = ( lo + hi ) / 2; settle( m ); if ( boat.velocity.y > 0 ) lo = m; else hi = m; }
	settle( ( lo + hi ) / 2 );
	return boat.angular.z / h; // + = the +x side going up

}

const boats = { 'lobster boat': lobsterModel, 'Pelagic 30': () => new Pelagic30(), 'mini fishing boat': () => new MiniFishingBoat() };

for ( const [ name, build ] of Object.entries( boats ) ) {

	// ---- 1. stability range: restoring (acceleration opposes the heel) well past the old ~40 degree limit
	{

		const b = makeBoat( build(), { h: () => 0, grad: () => [ 0, 0 ] } );
		for ( let i = 0; i < 30; i ++ ) frame( b, 0, 0 );
		let vanish = null;
		for ( let d = 2; d <= 120 && vanish === null; d += 2 ) if ( rollAccel( b.boat, d / DEG ) > 0 ) vanish = d;
		check( vanish === null || vanish >= 70, `${ name }: righting moment lasts to ${ vanish === null ? '120+' : vanish } degrees of heel (>= 70)` );

	}

	// ---- 2. a long swell is a sloping surface: the hull rides it (heel = slope), neither 1.7x nor 0.5x
	for ( const slope of [ 0.05, 0.1 ] ) {

		const b = makeBoat( build(), planeSea( slope ) );
		for ( let i = 0; i < 30; i ++ ) frame( b, 0, 0 );
		let lo = - 0.4, hi = 0.4;
		for ( let i = 0; i < 40; i ++ ) { const m = ( lo + hi ) / 2; if ( rollAccel( b.boat, m ) > 0 ) lo = m; else hi = m; }
		const phi = ( lo + hi ) / 2, ratio = phi / Math.atan( slope );
		check( ratio > 0.9 && ratio < 1.1, `${ name }: rides a ${ ( Math.atan( slope ) * DEG ).toFixed( 1 ) } degree slope at ${ ( phi * DEG ).toFixed( 1 ) } degrees (ratio ${ ratio.toFixed( 2 ) }, want ~1)` );

	}

	// ---- 3. a heavy sea from every side, through idle, cruise, hard turns at both throttles: nobody rolls over
	// (a 3 m punt is not out in a 3 m sea: its heavy weather is a metre of short chop)
	for ( const [ Hs, Tp ] of name === 'mini fishing boat' ? [ [ 0.5, 3.5 ], [ 1, 4.5 ] ] : [ [ 2, 6.5 ], [ 3, 8 ] ] ) {

		let worstRoll = 0, worstPitch = 0;
		for ( const dir of [ Math.PI, Math.PI / 2, Math.PI / 4, 0 ] ) {

			const b = makeBoat( build(), irregularSea( Hs, Tp, dir ) );
			const seg = ( sec, thr, st ) => {

				for ( let t = 0; t < sec; t += dt ) {

					frame( b, thr, st );
					const a = attitude( b.boat );
					worstRoll = Math.max( worstRoll, Math.abs( a.roll ) );
					worstPitch = Math.max( worstPitch, Math.abs( a.pitch ) );

				}

			};
			seg( 25, 0, 0 ); seg( 20, 0.7, 0 ); seg( 15, 0.7, 1 ); seg( 10, 0.7, 0 ); seg( 15, 1, - 1 ); seg( 10, 1, 0 );

		}

		check( worstRoll < 45 && worstPitch < 30, `${ name }: Hs ${ Hs } m from every side: worst roll ${ worstRoll.toFixed( 0 ) }, pitch ${ worstPitch.toFixed( 0 ) } degrees (never over)` );

	}

}

// ---- the mini fishing boat in calm water: it floats at its design draft, level, and drives like a light punt
{

	const b = makeBoat( new MiniFishingBoat(), { h: () => 0, grad: () => [ 0, 0 ] } );
	for ( let i = 0; i < 600; i ++ ) frame( b, 0, 0 );
	const a = attitude( b.boat );
	// the hull bottom is 0.11 m below the origin (the design waterline): the boat sinks to about that
	check( Math.abs( b.boat.position.y ) < 0.06, `mini boat floats with its waterline at the origin (heave ${ b.boat.position.y.toFixed( 3 ) } m, want |y| < 0.06)` );
	check( Math.abs( a.roll ) < 0.5 && Math.abs( a.pitch ) < 1.5, `and level (roll ${ a.roll.toFixed( 2 ) }, pitch ${ a.pitch.toFixed( 2 ) } degrees)` );
	let top = 0;
	for ( let i = 0; i < 1500; i ++ ) { frame( b, 1, 0 ); top = Math.max( top, b.boat.forwardSpeed ); }
	check( top > 3 && top < 6.5, `full ahead tops out at ${ top.toFixed( 1 ) } m/s (a light punt: 3 .. 6.5)` );
	const h0 = Math.atan2( b.boat.forward( new E.Vector3() ).x, b.boat.forward( new E.Vector3() ).z );
	let turned = 0, last = h0;
	for ( let i = 0; i < 600; i ++ ) {

		frame( b, 0.7, 1 );
		const f = b.boat.forward( new E.Vector3() ), h = Math.atan2( f.x, f.z );
		turned += Math.atan2( Math.sin( h - last ), Math.cos( h - last ) ); last = h;

	}

	const rate = Math.abs( turned ) / 10 * DEG;
	check( rate > 15 && rate < 120 && Math.abs( attitude( b.boat ).roll ) < 20, `hard over at 70 % turns ${ rate.toFixed( 0 ) } degrees/s, heeling ${ attitude( b.boat ).roll.toFixed( 0 ) } degrees (15 .. 120 deg/s, under 20 deg heel)` );

}

// ---- 4. two boats in one world, as the game has them: each one's hull samples must read the water *under that
// hull*. The controllers once shared one block of query slots (the same allocation name), so the second boat's
// sample positions overwrote the first's every frame and the lobster boat read water heights measured at the
// Pelagic's mooring: it floated a metre out of the water, or a metre under it. Compared with the same boat
// alone in the same sea, it must not move.
{

	const sea = irregularSea( 1.5, 6, Math.PI / 3 );
	const alone = makeBoat( lobsterModel(), sea );
	const query = makeQuery( sea );
	const lobster = makeBoat( lobsterModel(), sea, query );
	const pelagic = makeBoat( new Pelagic30(), sea, query, new E.Vector3( 380, 0, 260 ) );
	// the rest of the game's water queries (traps, wildlife, the rod, the player, the whale ...), allocated after
	// the boats: the table has to hold them too
	let overflow = null;
	try { for ( const [ name, n ] of [ [ 'bobber', 1 ], [ 'traps', 6 ], [ 'player', 1 ], [ 'whale', 1 ], [ 'wildlife', 8 ], [ 'pelagic', 1 ] ] ) query.allocate( name, n ); } catch ( e ) { overflow = e.message; }
	check( overflow === null, `two boats and the rest of the game fit in the query slots (${ query.n } used${ overflow ? ': ' + overflow : '' })` );
	// the table is bigger than one workgroup: the kernel has to be dispatched over every slot in use, or the
	// slots past the first workgroup read back zeros without any error
	check( MAX_QUERIES === 256, `the query table is 256 slots (${ MAX_QUERIES })` );
	let uncovered = null;
	for ( const used of [ 1, 2, 63, 64, 65, 100, 128, 129, 255, 256 ] ) if ( queryWorkgroups( used ) * QUERY_WORKGROUP < used && uncovered === null ) uncovered = used;
	check( uncovered === null && queryWorkgroups( 65 ) === 2 && queryWorkgroups( 64 ) === 1 && queryWorkgroups( 256 ) === 4, `the query kernel is dispatched over every slot in use (first gap at ${ uncovered })` );
	check( queryWorkgroups( 0 ) === 1 && queryWorkgroups( 9999 ) === 4, 'and always at least the camera slot, never past the table' );

	let worstY = 0, worstTilt = 0;
	for ( let t = 0; t < 70; t += dt ) {

		alone.query.time += dt; alone.query.tick(); alone.boat.queueQueries(); alone.boat.setInput( 0, 0, dt ); alone.boat.update( dt );
		// the order App.update uses: both boats step, then both queue
		query.time += dt; query.tick();
		lobster.boat.setInput( 0, 0, dt ); lobster.boat.update( dt );
		pelagic.boat.setInput( 0, 0, dt ); pelagic.boat.update( dt );
		lobster.boat.queueQueries(); pelagic.boat.queueQueries();
		if ( t < 20 ) continue;
		worstY = Math.max( worstY, Math.abs( lobster.boat.position.y - alone.boat.position.y ) );
		const a = attitude( lobster.boat ), b = attitude( alone.boat );
		worstTilt = Math.max( worstTilt, Math.abs( a.roll - b.roll ), Math.abs( a.pitch - b.pitch ) );

	}

	check( worstY < 0.1 && worstTilt < 1.5, `lobster boat beside the Pelagic rides the same as alone: heave off by ${ worstY.toFixed( 2 ) } m (< 0.1), tilt by ${ worstTilt.toFixed( 1 ) } degrees (< 1.5)` );

}

// ---- the anchor: holds the boat against a steady push, swings it bow to the line, and has rules
{

	const sea = irregularSea( 0.6, 4.5, 0.5 );
	const depth = 6;
	// a steady push along +x for `secs` (a squall: ~0.35 m/s^2 on the whole boat), the engine off; the boat
	// starts heading +z with the chock's anchor, if any, dropped first
	const blow = ( model, anchored, secs = 90, accel = 0.35, throttle = 0 ) => {

		const b = makeBoat( model, sea );
		b.boat.driven = throttle > 0;
		for ( let i = 0; i < 90; i ++ ) frame( b, 0, 0 ); // settle on the water
		let drop = null;
		if ( anchored ) drop = b.boat.dropAnchor( depth );
		const start = b.boat.position.clone(), A = b.boat.anchor;
		let far = 0, maxT = 0;
		for ( let i = 0; i < secs * 60; i ++ ) {

			if ( throttle === 0 ) b.boat.velocity.x += accel * dt; // the wind (the engine case is thrust alone)
			frame( b, throttle, 0 );
			if ( anchored ) {

				const c = b.boat.toWorld( b.boat.chock, new E.Vector3() );
				far = Math.max( far, Math.hypot( c.x - A.x, c.z - A.z ) );
				maxT = Math.max( maxT, A.tension );

			}

		}

		return { b, drop, far, maxT, moved: b.boat.position.distanceTo( start ), speed: b.boat.speed };

	};

	for ( const [ name, model ] of [ [ 'lobster boat', lobsterModel() ], [ 'Pelagic 30', new Pelagic30() ] ] ) {

		const free = blow( model, false ), held = blow( model, true );
		const rode = held.drop.rode;
		check( rode === Math.max( ANCHOR.minRode, depth * ANCHOR.scope + ANCHOR.spare ), `${ name }: ${ depth } m of water pays out ${ rode } m of rode` );
		check( free.moved > 3 * rode, `${ name }: without the anchor a squall carries it ${ free.moved.toFixed( 0 ) } m in 90 s (more than 3x the rode)` );
		check( held.far < rode + 3, `${ name }: on the anchor the bow never gets more than ${ ( held.far - rode ).toFixed( 1 ) } m past the rode (< 3 m)` );
		check( held.speed < 0.4, `${ name }: and comes to rest (${ held.speed.toFixed( 2 ) } m/s)` );
		check( held.maxT > 0 && held.maxT < ANCHOR.maxTension, `${ name }: the line takes up to ${ ( held.maxT / 1000 ).toFixed( 1 ) } kN, under its limit` );
		// it lies bow to the anchor: the anchor is up-wind (the wind goes +x), so the bow ends up pointing -x
		const f = held.b.boat.forward( new E.Vector3() );
		const off = Math.acos( Math.min( 1, Math.max( - 1, - f.x / Math.hypot( f.x, f.z ) ) ) ) * DEG;
		check( off < 40, `${ name }: it swings to lie bow to the anchor (heading ${ off.toFixed( 0 ) } degrees off the line)` );

	}

	// slack inside the rode: nothing pulls
	{

		const b = makeBoat( lobsterModel(), sea );
		for ( let i = 0; i < 90; i ++ ) frame( b, 0, 0 );
		b.boat.dropAnchor( depth );
		let maxT = 0;
		for ( let i = 0; i < 600; i ++ ) { frame( b, 0, 0 ); maxT = Math.max( maxT, b.boat.anchor.tension ); }
		check( maxT === 0, 'inside its rode the anchor line is slack: no pull on a boat at rest in a seaway' );

	}

	// full ahead against the anchor: the line holds, the boat stays on its rode
	{

		const r = blow( lobsterModel(), true, 40, 0, 1 );
		check( r.far < r.drop.rode + 7 && Number.isFinite( r.b.boat.position.x ), `full throttle against the anchor stays within ${ ( r.far - r.drop.rode ).toFixed( 1 ) } m of the rode end (< 7 m)` );
		check( r.maxT <= ANCHOR.maxTension, `and the line never takes more than its cap (peak ${ ( r.maxT / 1000 ).toFixed( 1 ) } kN of ${ ANCHOR.maxTension / 1000 })` );

	}

	// the rules
	{

		const b = makeBoat( lobsterModel(), sea );
		for ( let i = 0; i < 90; i ++ ) frame( b, 0, 0 );
		const c = b.boat;
		check( c.canAnchor( 0.3 ).ok === false && /shallow/.test( c.canAnchor( 0.3 ).reason ), 'too shallow: refused, and says so' );
		check( c.canAnchor( ANCHOR.maxDepth + 1 ).ok === false && /deep/.test( c.canAnchor( ANCHOR.maxDepth + 1 ).reason ), 'too deep: refused, and says so' );
		c.velocity.set( 3, 0, 0 ); c.speed = c.velocity.length();
		check( c.canAnchor( depth ).ok === false && /Slow down/.test( c.canAnchor( depth ).reason ) && c.dropAnchor( depth ).ok === false && ! c.anchor.down, 'too fast: refused' );
		c.velocity.set( 0, 0, 0 ); c.speed = 0;
		const d = c.dropAnchor( depth );
		check( d.ok && c.anchor.down, 'at rest in a fair depth: it goes down' );
		const ch = c.toWorld( c.chock, new E.Vector3() );
		check( Math.hypot( c.anchor.x - ch.x, c.anchor.z - ch.z ) < 0.01, 'and lands where the bow is' );
		check( c.dropAnchor( depth ).ok === false, 'it cannot go down twice' );
		check( c.weighAnchor() === true && ! c.anchor.down && c.weighAnchor() === false, 'weighing it brings it up (once)' );
		c.dropAnchor( depth );
		c.reset();
		check( ! c.anchor.down, 'a boat put back at its berth has no anchor down' );

	}

	// weighed anchor: the boat is free again
	{

		const b = makeBoat( lobsterModel(), sea );
		for ( let i = 0; i < 90; i ++ ) frame( b, 0, 0 );
		b.boat.dropAnchor( depth );
		for ( let i = 0; i < 20 * 60; i ++ ) { b.boat.velocity.x += 0.35 * dt; frame( b, 0, 0 ); }
		const heldAt = b.boat.position.clone();
		b.boat.weighAnchor();
		for ( let i = 0; i < 30 * 60; i ++ ) { b.boat.velocity.x += 0.35 * dt; frame( b, 0, 0 ); }
		check( b.boat.position.distanceTo( heldAt ) > 30, `weighed, it drifts off again (${ b.boat.position.distanceTo( heldAt ).toFixed( 0 ) } m in 30 s)` );

	}

}

console.log( fails ? `\nboat-stability: ${ fails } FAILED` : '\nboat-stability: all passed' );
process.exit( fails ? 1 : 0 );
