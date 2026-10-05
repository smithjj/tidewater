// Resetting the boats (Game.resetBoats, B): aboard it sends the boat you are on back to its berth, upright and at rest; on foot it rights
// every capsized boat and leaves the others where they are. Plain node, the real Game / BoatController methods over stand-ins.
//   node test/boat-reset.mjs
import * as E from '../src/engine/index.js';
import { Game } from '../src/game/Game.js';
import { BoatController } from '../src/player/BoatController.js';
import { ACTIONS } from '../src/core/Bindings.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

const keyOf = ( k ) => k.v || k;
ok( ACTIONS.some( ( a ) => a.id === 'resetBoats' && a.kb.includes( 'KeyB' ) ), 'the reset action is bound to B' );
ok( ! ACTIONS.some( ( a ) => a.id !== 'resetBoats' && ( a.kb || [] ).some( ( k ) => keyOf( k ) === 'KeyB' ) ), 'B is not used by anything else' );

const mkBoat = ( dock, { roll = 0, x = 300, z = 200 } = {} ) => {

	const b = Object.create( BoatController.prototype );
	Object.assign( b, {
		position: new E.Vector3( x, 0, z ), quaternion: new E.Quaternion().setFromEuler( new E.Euler( 0, 0.7, roll, 'YXZ' ) ),
		velocity: new E.Vector3( 2, 0, 1 ), angular: new E.Vector3( 0.4, 0.2, 0.9 ), throttle: 0.6, steer: 0.3, rpm: 0.5, moored: false, driven: false,
		homeDock: dock, mooring: { anchor: new E.Vector3( x, 0, z ), heading: 0 }, anchor: { down: true, x: 1, z: 2, rode: 20, depth: 5, tension: 0 },
	} );
	b.weighAnchor = () => { b.anchor.down = false; };
	return b;

};

const docks = { lobster: { position: new E.Vector3( 10, 0.2, 20 ), heading: 1.2 }, pelagic: { position: new E.Vector3( 30, 0.2, 40 ), heading: 0.1 }, mini: { position: new E.Vector3( 50, 0.2, 60 ), heading: - 0.4 } };
const mkGame = ( mode, rolls = {} ) => {

	const ctl = {};
	for ( const id of Object.keys( docks ) ) ctl[ id ] = mkBoat( docks[ id ], { roll: rolls[ id ] || 0 } );
	const toasts = [];
	const g = Object.create( Game.prototype );
	Object.assign( g, {
		app: { lobsterCtl: ctl.lobster, pelagicCtl: ctl.pelagic, miniCtl: ctl.mini, player: { mode, boat: ctl.mini }, input: { label: () => 'B' } },
		toast: ( t ) => toasts.push( t ),
	} );
	return { g, ctl, toasts };

};

// ---- what counts as capsized
{

	const t = mkGame( 'walk', { mini: Math.PI / 2, lobster: 0.3, pelagic: Math.PI } );
	ok( t.ctl.mini.capsized && t.ctl.pelagic.capsized && ! t.ctl.lobster.capsized, 'on its side or keel up is capsized, a 17 degree heel is not' );
	ok( Math.abs( t.ctl.pelagic.uprightness + 1 ) < 1e-9 && Math.abs( mkBoat( docks.mini ).uprightness - 1 ) < 1e-9, 'uprightness is 1 level and -1 keel up' );

}

// ---- on foot: the capsized boats are righted, the others stay
{

	const t = mkGame( 'walk', { mini: Math.PI * 0.6 } );
	const r = t.g.resetBoats();
	const m = t.ctl.mini;
	ok( r.length === 1 && r[ 0 ] === m, 'only the capsized mini is reset' );
	ok( ! m.capsized && Math.abs( m.uprightness - 1 ) < 1e-9, 'it is upright again' );
	ok( m.position.distanceTo( docks.mini.position ) < 1e-9 && m.velocity.length() === 0 && m.angular.length() === 0 && m.throttle === 0 && m.moored, 'at its berth, at rest and tied up' );
	ok( ! m.anchor.down, 'with its anchor up' );
	ok( t.ctl.lobster.position.x === 300 && t.ctl.lobster.anchor.down && t.ctl.pelagic.anchor.down, 'the other boats are left where they were, anchors and all' );
	ok( /Mini fishing boat righted/.test( t.toasts[ 0 ] ), `it says so (${ t.toasts[ 0 ] })` );

	const u = mkGame( 'swim', { mini: Math.PI / 2, lobster: Math.PI / 2 } );
	ok( u.g.resetBoats().length === 2 && /Lobster boat and Mini fishing boat righted/.test( u.toasts[ 0 ] ), `swimming: both capsized boats at once (${ u.toasts[ 0 ] })` );

	const n = mkGame( 'walk' );
	ok( n.g.resetBoats().length === 0 && n.ctl.mini.position.x === 300 && /No boat is capsized/.test( n.toasts[ 0 ] ), 'nothing capsized: nothing moves, and it says what B does aboard' );

}

// ---- aboard: that boat goes home, whatever state it is in
{

	const t = mkGame( 'deck' );
	const r = t.g.resetBoats();
	ok( r.length === 1 && r[ 0 ] === t.ctl.mini && t.ctl.mini.position.x === 50 && t.ctl.lobster.position.x === 300, 'on deck: the boat you are on goes back to its berth, the others stay' );
	ok( t.ctl.mini.moored, 'tied up when you only stand on deck' );

	const h = mkGame( 'boat', { mini: Math.PI / 2 } );
	h.ctl.mini.driven = true;
	h.g.resetBoats();
	ok( ! h.ctl.mini.capsized && ! h.ctl.mini.moored && h.ctl.mini.driven, 'at the helm: righted, and not tied up (you are driving it)' );
	ok( /Mini fishing boat back at its berth/.test( h.toasts[ 0 ] ), `it says so (${ h.toasts[ 0 ] })` );

}

console.log( fails ? `boat-reset: ${ fails } FAILED` : 'boat-reset: all passed' );
process.exit( fails ? 1 : 0 );
