// The anchor, from the player's side: the key (Game.toggleAnchor), the hint when you drive against it, the
// rules it gives, stepping ashore, and what it draws (AnchorGear). The physics is in test/boat-stability.mjs.
// Plain node, the real Game / BoatController / AnchorGear methods over stand-ins.
//   node test/anchor.mjs
import * as E from '../src/engine/index.js';
import { Game } from '../src/game/Game.js';
import { BoatController, ANCHOR } from '../src/player/BoatController.js';
import { Player } from '../src/player/Player.js';
import { AnchorGear } from '../src/game/AnchorGear.js';
import { ACTIONS } from '../src/core/Bindings.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};
const near = ( a, b, e = 1e-6 ) => Math.abs( a - b ) <= e;

ok( ACTIONS.some( ( a ) => a.id === 'anchor' && a.kb.includes( 'KeyX' ) ), 'the anchor action is bound to X' );
ok( ! ACTIONS.some( ( a ) => a.id !== 'anchor' && ( a.kb || [] ).some( ( k ) => ( k.v || k ) === 'KeyX' ) ), 'X is not used by anything else' );

// a boat that has the real BoatController's anchor methods (chock, toWorld, canAnchor, dropAnchor, weighAnchor)
const mkBoat = ( { x = 100, z = - 50, yaw = 0, speed = 0, bowZ = 4.3, deckY = 0.9 } = {} ) => {

	const b = Object.create( BoatController.prototype );
	Object.assign( b, {
		position: new E.Vector3( x, 0, z ), quaternion: new E.Quaternion().setFromAxisAngle( new E.Vector3( 0, 1, 0 ), yaw ),
		model: { bowZ, lines: { deckY } }, speed, throttle: 0, anchor: { down: false, x: 0, z: 0, rode: 0, depth: 0, tension: 0 },
	} );
	return b;

};

const mkGame = ( { mode = 'boat', depth = 6, speed = 0, yaw = 0 } = {} ) => {

	const boat = mkBoat( { speed, yaw } );
	const toasts = [], splashes = [];
	const g = Object.create( Game.prototype );
	Object.assign( g, {
		app: { player: { mode, boat, boats: [ boat ] }, terrainData: { heightAt: () => - depth }, audio: { splash: ( v ) => splashes.push( v ) }, input: { label: () => 'X' } },
		toast: ( t ) => toasts.push( t ), _tmp: new E.Vector3(),
	} );
	return { g, boat, toasts, splashes };

};

// ---- the key
{

	let t = mkGame( { mode: 'walk' } );
	ok( t.g.toggleAnchor() === null && ! t.boat.anchor.down && t.toasts.length === 0, 'ashore: nothing happens' );
	t = mkGame( { mode: 'swim' } );
	ok( t.g.toggleAnchor() === null && ! t.boat.anchor.down, 'swimming: nothing happens' );

	t = mkGame( { mode: 'boat' } );
	const r = t.g.toggleAnchor();
	ok( r && r.ok && t.boat.anchor.down && t.toasts[ 0 ].includes( '34 m of line' ), `at the helm in 6 m of water it goes down with its line (${ t.toasts[ 0 ] })` );
	ok( t.splashes.length === 1, 'with a splash' );
	const c = t.boat.toWorld( t.boat.chock, new E.Vector3() );
	ok( near( t.boat.anchor.x, c.x ) && near( t.boat.anchor.z, c.z ) && near( c.z, - 50 + 4.3 ), 'and it lands under the bow chock' );
	ok( t.g.toggleAnchor().weighed === true && ! t.boat.anchor.down && t.toasts[ 1 ] === 'Anchor up', 'the same key weighs it' );

	t = mkGame( { mode: 'deck' } );
	ok( t.g.toggleAnchor() && t.boat.anchor.down, 'it works from the deck too, not only the helm' );

	t = mkGame( { mode: 'boat', depth: ANCHOR.maxDepth + 5 } );
	ok( t.g.toggleAnchor() === null && ! t.boat.anchor.down && /deep/.test( t.toasts[ 0 ] ) && t.splashes.length === 0, 'too deep: refused, with the reason, and no splash' );
	t = mkGame( { mode: 'boat', depth: 0.2 } );
	ok( t.g.toggleAnchor() === null && /shallow/.test( t.toasts[ 0 ] ), 'too shallow: refused' );
	t = mkGame( { mode: 'boat', speed: ANCHOR.maxSpeed + 1 } );
	ok( t.g.toggleAnchor() === null && /Slow down/.test( t.toasts[ 0 ] ), 'under way: refused' );

}

// ---- the hint when you drive against it
{

	const t = mkGame( { mode: 'boat' } );
	t.g.toggleAnchor();
	t.toasts.length = 0;
	const p = t.g.app.player;
	t.boat.throttle = 0.8; t.boat.anchor.tension = 0;
	t.g.anchorHint( 0.016, p );
	ok( t.toasts.length === 0, 'throttle up with a slack line: no hint (it is not holding anything yet)' );
	t.boat.anchor.tension = 9000;
	t.g.anchorHint( 0.016, p );
	ok( t.toasts.length === 1 && /holding/.test( t.toasts[ 0 ] ) && t.toasts[ 0 ].includes( 'X' ), `once the line is taut and you are on the throttle it says so: "${ t.toasts[ 0 ] }"` );
	for ( let i = 0; i < 100; i ++ ) t.g.anchorHint( 0.1, p );
	ok( t.toasts.length === 1, 'and does not say it every frame' );
	for ( let i = 0; i < 200; i ++ ) t.g.anchorHint( 0.1, p );
	ok( t.toasts.length >= 2, 'but reminds you after a while' );
	t.boat.throttle = 0; t.boat.anchor.down = false;
	const n = t.toasts.length;
	for ( let i = 0; i < 400; i ++ ) t.g.anchorHint( 0.1, p );
	ok( t.toasts.length === n, 'nothing when the engine is idle or the anchor is up' );

}

// ---- stepping off a boat: tied up at the dock, but never losing its anchor
{

	// leave the boat the way the player does: stepping ashore from the deck, or jumping over the side
	const leave = ( { anchored, atDock, overboard = false } ) => {

		const boat = mkBoat();
		boat.driven = true; boat.moored = false;
		boat.homeDock = { position: atDock ? new E.Vector3( 100, 0, - 50 ) : new E.Vector3( 400, 0, 300 ) };
		boat.mooring = { anchor: new E.Vector3(), heading: 0 }; boat.getYaw = () => 0;
		boat.model.lines = null; // a deckless boat: exitBoat goes straight ashore
		if ( anchored ) boat.dropAnchor( 6 );
		const pl = Object.create( Player.prototype );
		Object.assign( pl, { boat, mode: 'boat', position: new E.Vector3(), velocity: new E.Vector3(), audio: null, ashoreTarget: () => ( { out: new E.Vector3( 101, 0, - 50 ), g: 0 } ) } );
		try { overboard ? pl.exitBoat( null, 1 ) : pl.exitBoat(); } catch ( e ) { /* the stand-in has no camera: only the boat's state matters here */ }
		return boat;

	};

	let b = leave( { anchored: true, atDock: false, overboard: true } );
	ok( b.anchor.down && ! b.moored, 'jumping overboard at sea: the anchor stays down' );
	b = leave( { anchored: true, atDock: false } );
	ok( b.anchor.down && ! b.moored, 'stepping ashore from an anchored boat at sea: it stays down' );
	b = leave( { anchored: true, atDock: true, overboard: true } );
	ok( b.anchor.down && ! b.moored, 'jumping off an anchored boat next to the dock: the anchor stays down (it was being weighed here before)' );
	b = leave( { anchored: true, atDock: true } );
	ok( b.anchor.down && ! b.moored, 'stepping ashore from an anchored boat at the dock: the anchor holds it, no dock lines' );
	b = leave( { anchored: false, atDock: true } );
	ok( b.moored && ! b.anchor.down, 'without an anchor, stepping off at the dock still ties it up as before' );
	b = leave( { anchored: false, atDock: false } );
	ok( ! b.moored, 'and away from the dock nothing holds it' );

}

// ---- what it draws
{

	const scene = { add() {} };
	const bed = - 6;
	const early = mkBoat(), added = [];
	const pre = new AnchorGear( { scene: { add: ( o ) => added.push( o ) }, terrain: { heightAt: () => bed }, boats: [ early, null ] } );
	ok( pre.views.has( early ) && pre.views.size === 1 && pre.group.children.length === 3 && pre.group.children.every( ( m ) => ! m.visible ), 'the meshes (anchor, line, buoy) exist from the start, hidden (so the loading screen compiles their shaders)' );
	const gear = new AnchorGear( { scene, terrain: { heightAt: () => bed } } );
	const boat = mkBoat();
	gear.update( [ boat ], 0.016 );
	const v = gear.views.get( boat );
	ok( v && ! v.anchor.visible && ! v.line.visible && ! v.buoy.visible, 'no anchor down: nothing drawn' );

	boat.dropAnchor( 6 );
	gear.update( [ boat ], 0.45 );
	ok( ! v.buoy.visible, 'the buoy waits until the anchor has landed' );
	const mid = v.anchor.position.y;
	ok( v.anchor.visible && v.line.visible && mid < 1 && mid > bed, `dropping: the anchor is on its way down (y ${ mid.toFixed( 2 ) })` );
	for ( let i = 0; i < 20; i ++ ) gear.update( [ boat ], 0.1 );
	const A = boat.anchor, ch = boat.toWorld( boat.chock, new E.Vector3() );
	ok( near( v.anchor.position.x, A.x, 1e-6 ) && near( v.anchor.position.z, A.z, 1e-6 ) && near( v.anchor.position.y, bed + 0.1, 1e-6 ), 'it ends up on the bottom where it landed' );
	const len = Math.hypot( v.anchor.position.x - ch.x, v.anchor.position.y - ch.y, v.anchor.position.z - ch.z );
	ok( near( v.line.scale.y, len, 1e-4 ) && near( v.line.position.y, ( v.anchor.position.y + ch.y ) / 2, 1e-4 ), `the line runs from the chock to it (${ len.toFixed( 2 ) } m)` );

	ok( v.buoy.visible && near( v.buoy.position.x, A.x, 1e-6 ) && near( v.buoy.position.z, A.z, 1e-6 ) && near( v.buoy.position.y, - 0.06, 1e-6 ), 'once it has landed an orange buoy floats over it at the surface (no water data: sea level)' );

	// the boat swings: the line follows the chock while the anchor stays put
	boat.position.x += 12; boat.position.z += 6;
	const was = v.anchor.position.clone();
	gear.update( [ boat ], 0.016 );
	const ch2 = boat.toWorld( boat.chock, new E.Vector3() );
	ok( v.anchor.position.distanceTo( was ) < 1e-9 && near( v.line.scale.y, Math.hypot( was.x - ch2.x, was.y - ch2.y, was.z - ch2.z ), 1e-4 ), 'when the boat moves the anchor stays put and the line follows the bow' );

	// the buoy rides the water height read back for it
	const qs = new Map(), cpu = new Float32Array( 64 * 4 );
	const query = { allocate: ( n, k ) => { qs.set( n, k ); return 10; }, setPoint( i, x, z ) { this.last = [ i, x, z ]; }, cpu };
	const rider = new AnchorGear( { scene, terrain: { heightAt: () => bed }, query, boats: [ boat ] } );
	cpu[ 10 * 4 ] = 0.4;
	rider.update( [ boat ], 0.016 );
	for ( let i = 0; i < 20; i ++ ) rider.update( [ boat ], 0.1 );
	const rv = rider.views.get( boat );
	ok( qs.get( 'anchors' ) === 2 && rv.slot === 10 && query.last[ 0 ] === 10 && near( rv.buoy.position.y, 0.4 - 0.06, 1e-6 ), 'with water data the buoy rides the surface height it is given, from its own query slots' );

	boat.weighAnchor();
	gear.update( [ boat ], 0.45 );
	ok( v.anchor.visible && v.anchor.position.y > bed + 0.1, 'weighing: it comes back up' );
	for ( let i = 0; i < 20; i ++ ) gear.update( [ boat ], 0.1 );
	ok( ! v.anchor.visible && ! v.line.visible, 'and is gone when it is aboard' );
	let threw = false;
	try { gear.update( null, 0.016 ); gear.update( [ null, { } ], 0.016 ); } catch ( e ) { threw = true; }
	ok( ! threw, 'a missing or foreign boat is skipped' );

}

console.log( fails ? `${ fails } FAILED` : 'all ok' );
process.exit( fails ? 1 : 0 );
