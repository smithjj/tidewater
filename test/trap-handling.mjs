// Working the trap line from the boat, in plain node (no GPU): the rules that say which button does what, the
// real Game.updateTraps / trapPrompt / setTrap / haulTrap driven with stand-ins, and the real Traps
// animations (the deck stack, a pot hauled aboard, a pot over the stern).
//   node test/trap-handling.mjs
import * as E from '../src/engine/index.js';
import { Traps, trapTriggered, stackVisible, TRAP_MAX_SPEED, SET_ASTERN, SOAK_MIN } from '../src/game/Traps.js';
import { Game } from '../src/game/Game.js';
import { GameState } from '../src/game/GameState.js';
import { TRAPS } from '../src/world/boat/DeckGear.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};
const near = ( a, b, e = 1e-6 ) => Math.abs( a - b ) <= e;
globalThis.fetch = async () => { throw new Error( 'no network in the test' ); }; // the pot model: the procedural one stays
const warn = console.warn; console.warn = () => {};

// ---- the rule: which button works the pots
{

	const t = ( o ) => trapTriggered( { mode: 'deck', interact: false, work: false, live: true, modeChanged: false, ...o } );
	ok( t( { mode: 'boat', work: true } ), 'helm: the cast button (LMB / RT) works the pots' );
	ok( ! t( { mode: 'boat', interact: true } ), 'helm: E does not (it leaves the helm)' );
	ok( ! t( { mode: 'boat', work: true, live: false } ), 'helm: the click that captures the mouse is not a pot' );
	ok( t( { mode: 'deck', interact: true } ), 'deck: E works the pots' );
	ok( ! t( { mode: 'deck', work: true } ), 'deck: the cast button is the rod\'s, not the pots\'' );
	ok( ! t( { mode: 'deck', interact: true, modeChanged: true } ), 'deck: the E that just took the player off the helm is not also a pot' );
	ok( ! t( { mode: 'walk', interact: true } ) && ! t( { mode: 'swim', interact: true, work: true } ), 'ashore or swimming: no' );
	ok( [ 'true,true,true,false', 'false,false,false,false', 'true,true,true,true' ].join() === [ stackVisible( 3, 4 ), stackVisible( 0, 4 ), stackVisible( 6, 4 ) ].map( String ).join(), 'the stack shows a pot per pot aboard, up to its places' );
	ok( String( stackVisible( 3, 4, 2 ) ) === 'true,true,false,false', 'a pot on its way to a place leaves it empty until it lands' );

}

// ---- stand-ins for the app: a boat that moves and turns, a state with a licence, a spy for the traps
const mkBoat = ( { speed = 0, x = 100, z = - 50, yaw = 0 } = {} ) => {

	const q = new E.Quaternion().setFromAxisAngle( new E.Vector3( 0, 1, 0 ), yaw );
	return {
		position: new E.Vector3( x, 0, z ), quaternion: q, speed, model: { sternZ: - 3.9 },
		toWorld( l, out ) { return out.copy( l ).applyQuaternion( q ).add( this.position ); },
		getYaw: () => yaw, sampleWaterAt: () => 0,
	};

};
function mkGame( { mode = 'boat', device = 'kb', locked = true, hits = {}, speed = 0, aboard = 3, depth = 12, sets = [] } = {} ) {

	const state = new GameState();
	state.upgrades.trapLicence = 1; state.money = 5000; state.buyTraps( aboard );
	state.day = 0; state.clock = 12;
	for ( const s of sets ) state.setTrap( s.x, s.z, s.clock ), state.traps = aboard;
	const boat = mkBoat( { speed } );
	const player = { mode, boat };
	const g = Object.create( Game.prototype );
	const calls = { set: 0, haul: 0, visual: [], splash: 0, toast: [] };
	Object.assign( g, {
		app: { player, lobsterCtl: boat, terrainData: { heightAt: () => - depth }, audio: { splash: () => calls.splash ++ }, ui: null, settings: { timeOfDay: 12 } },
		state, hud: null, _haulCard: 0, _cardDismissed: false, _modeChanged: false, _tmp: new E.Vector3(),
		traps: { busy: false, stack: [ 0, 1, 2, 3 ], setVisual: ( b, slot ) => { calls.visual.push( slot ); return true; }, haulVisual: () => true },
		toast: ( t ) => calls.toast.push( t ), rumble() {}, habitatAtPoint: () => 'reef',
	} );
	const inp = { locked, device, menuMode: false, actHit: ( id ) => !! hits[ id ] };
	return { g, inp, p: player, calls, state, boat };

}
const fire = ( o ) => { const t = mkGame( o ); t.g.setTrap = () => { t.calls.set ++; }; t.g.haulTrap = () => { t.calls.haul ++; }; t.g.updateTraps( t.inp, t.p ); return t; };

// ---- Game.updateTraps: the buttons, at the helm and on deck
{

	let t = fire( { mode: 'boat', hits: { interact: true } } );
	ok( t.calls.set === 0 && t.calls.haul === 0, 'helm: E sets and hauls nothing' );
	t = fire( { mode: 'boat', hits: { rodUse: true } } );
	ok( t.calls.set === 1 && t.calls.haul === 0, 'helm: the cast button sets a pot' );
	t = fire( { mode: 'boat', locked: false, hits: { rodUse: true } } );
	ok( t.calls.set === 0, 'helm: a click that is only capturing the mouse sets nothing' );
	t = fire( { mode: 'boat', locked: false, device: 'pad', hits: { rodUse: true } } );
	ok( t.calls.set === 1, 'helm: a pad trigger needs no mouse capture' );
	t = fire( { mode: 'deck', hits: { interact: true } } );
	ok( t.calls.set === 1, 'deck: E sets a pot' );
	t = mkGame( { mode: 'deck', hits: { interact: true } } );
	t.g._modeChanged = true; t.g.setTrap = () => t.calls.set ++; t.g.updateTraps( t.inp, t.p );
	ok( t.calls.set === 0, 'deck: the E that has just left the helm sets nothing' );
	t = fire( { mode: 'boat', hits: { rodUse: true }, sets: [ { x: 102, z: - 50, clock: 8 } ] } );
	ok( t.calls.haul === 1 && t.calls.set === 0, 'helm: a soaked pot alongside is hauled, not another set' );
	t = mkGame( { mode: 'boat', hits: { rodUse: true } } );
	t.g.traps.busy = true; t.g.setTrap = () => t.calls.set ++; t.g.updateTraps( t.inp, t.p );
	ok( t.calls.set === 0 && t.g.trapPrompt( t.p ) === null, 'while a pot is going over or coming up, nothing else starts and no prompt is offered' );
	t = mkGame( { mode: 'boat', hits: { rodUse: true } } );
	t.g._haulCard = 5; t.g.setTrap = () => t.calls.set ++; t.g.updateTraps( t.inp, t.p );
	ok( t.calls.set === 0, 'the catch card is up: the cast button closes it, it sets nothing' );

}

// ---- the prompt: it says when to slow down, and the helm shows the pot action (rodUse) beside "leave helm"
{

	let t = mkGame( { mode: 'boat', speed: 0 } );
	ok( t.g.trapPrompt( t.p ).text === 'Set a trap · 3 aboard', `prompt at a standstill: "${ t.g.trapPrompt( t.p ).text }"` );
	t = mkGame( { mode: 'boat', speed: TRAP_MAX_SPEED + 1 } );
	ok( / slow down$/.test( t.g.trapPrompt( t.p ).text ), 'prompt under way: ends with "slow down"' );

}

// ---- Game.setTrap / haulTrap: from the stern, at a crawl
{

	let t = mkGame( { mode: 'boat' } );
	const set = t.g.setTrap();
	const want = t.boat.toWorld( new E.Vector3( 0, 0, - 3.9 - SET_ASTERN ), new E.Vector3() );
	ok( set && near( set.x, want.x ) && near( set.z, want.z ), `the pot goes in ${ SET_ASTERN } m astern of the transom, not under the keel (${ set && set.z.toFixed( 2 ) } vs boat at -50)` );
	ok( t.calls.visual.length === 1 && t.calls.visual[ 0 ] === 2, 'the pot that goes over is the top of the stack (3 aboard: place 2)' );
	ok( t.calls.splash === 0, 'no splash until it lands (the animation makes it)' );
	t = mkGame( { mode: 'boat', aboard: 6 } );
	t.g.setTrap();
	ok( t.calls.visual[ 0 ] === 3, 'six aboard, the stack has four places: the pot comes from the last one' );
	t = mkGame( { mode: 'boat', speed: TRAP_MAX_SPEED + 0.5 } );
	ok( t.g.setTrap() === null && t.state.sets.length === 0, 'setting is refused above the crawl' );
	t = mkGame( { mode: 'boat', speed: 0 } );
	t.boat.yaw = 1; // a boat heading off the north: astern is somewhere else
	const t2 = mkGame( { mode: 'boat' } );
	t2.g.app.lobsterCtl = t2.p.boat = mkBoat( { yaw: Math.PI / 2 } );
	const s2 = t2.g.setTrap();
	ok( s2 && near( s2.x, 100 - 4.8 ) && near( s2.z, - 50 ), `heading east (yaw 90): astern is to the west (${ s2 && s2.x.toFixed( 2 ) }, ${ s2 && s2.z.toFixed( 2 ) })` );

	// hauling: a pot that has soaked, the boat at a standstill / under way, and the console naming a set
	const pot = { x: 101, z: - 50, clock: 6 };
	t = mkGame( { mode: 'boat', sets: [ pot ] } );
	t.g.hud = null; t.g.traps.haulVisual = () => { t.calls.hauled = true; return true; };
	const got = t.g.haulTrap();
	ok( got !== null && t.calls.hauled && t.state.sets.length === 0, 'a soaked pot alongside comes up' );
	t = mkGame( { mode: 'boat', speed: TRAP_MAX_SPEED + 1, sets: [ pot ] } );
	ok( t.g.haulTrap() === null && t.state.sets.length === 1, 'hauling is refused above the crawl, and the pot stays down' );
	t = mkGame( { mode: 'boat', speed: TRAP_MAX_SPEED + 1, sets: [ pot ] } );
	t.g.traps.haulVisual = () => true;
	ok( t.g.haulTrap( t.state.sets[ 0 ] ) !== null, 'a caller that names the set (the console) is not held to the speed' );
	// what comes up is logged with where it came from, for the fish guide's map
	let logged = null;
	for ( let i = 0; i < 60 && ! logged; i ++ ) {

		t = mkGame( { mode: 'boat', sets: [ { x: 101, z: - 50, clock: 0 } ] } );
		t.g.habitatAtPoint = () => ( { reef: 0.9, bay: 0.2, shallows: 0, pier: 0, deep: 0 } );
		t.g.traps.haulVisual = () => true;
		t.g.haulTrap();
		const e = Object.values( t.state.log ).find( ( x ) => x.catches && x.catches.length );
		if ( e ) logged = e.catches[ 0 ];

	}

	ok( logged && logged.x === 101 && logged.z === - 50 && logged.hab === 'reef', `a pot's catch is logged at the pot, in its water (${ logged ? `${ logged.x }, ${ logged.z }, ${ logged.hab }` : 'nothing came up in 60 tries' })` );
	t = mkGame( { mode: 'boat', sets: [ pot ] } );
	t.g.traps.busy = true;
	ok( t.g.haulTrap() === null && t.state.sets.length === 1, 'a second haul cannot start while one is coming up' );

}

// ---- the real Traps: the stack, a pot hauled aboard, a pot over the stern
{

	const mkTraps = ( aboard, boat ) => {

		const state = new GameState();
		state.upgrades.trapLicence = 1; state.money = 5000; state.buyTraps( aboard );
		const group = new E.Group();
		const tr = new Traps( { scene: new E.Group(), terrain: { heightAt: () => - 10 }, query: null, state, boat: { group, lines: { deckY: 0.35 } }, toast: () => {}, splash: () => { tr.splashes = ( tr.splashes || 0 ) + 1; } } );
		tr.splashes = 0;
		return { tr, state, group };

	};
	const vis = ( tr ) => tr.stack.map( ( m ) => m.visible ? 1 : 0 ).join( '' );
	const run = ( tr, max = 600 ) => { let n = 0; while ( tr.busy && n ++ < max ) tr.update( 1 / 60 ); return n; };
	const slotWorld = ( boat, i ) => boat.toWorld( new E.Vector3( TRAPS[ i ][ 0 ], 0.35 + 0.03 + TRAPS[ i ][ 1 ] * ( 0.37 + 0.035 ), TRAPS[ i ][ 2 ] ), new E.Vector3() );

	let { tr, state } = mkTraps( 3 );
	ok( vis( tr ) === '1110', `the deck stack follows the pots aboard (3 aboard: ${ vis( tr ) })` );
	state.buyTraps( 1 ); ok( vis( tr ) === '1111', 'buying one fills the last place' );
	state.traps = 0; state.emit(); ok( vis( tr ) === '0000', 'none aboard, none on deck' );

	// a pot over the stern
	const boat = mkBoat( { x: 20, z: 30, yaw: 0.7 } );
	( { tr, state } = mkTraps( 3 ) );
	const set = state.setTrap( 0, 0, 12 ); // the logic spends it first, as Game.setTrap does
	ok( set && vis( tr ) === '1100', 'setting a pot empties the top place at once' );
	ok( tr.setVisual( boat, 2 ) && tr.busy && tr.holder.visible, 'the pot is picked up' );
	let minZ = Infinity, maxY = - Infinity, n = 0;
	while ( tr.busy && n ++ < 600 ) {

		tr.update( 1 / 60 );
		const l = boat.position.clone().multiplyScalar( - 1 ).add( tr.holder.position ).applyQuaternion( boat.quaternion.clone().invert() ); // boat frame
		minZ = Math.min( minZ, l.z ); maxY = Math.max( maxY, l.y );

	}

	ok( ! tr.busy && ! tr.holder.visible, `the pot is over and gone in ${ ( n / 60 ).toFixed( 1 ) } s` );
	ok( tr.splashes === 1, 'one splash, when it reaches the water' );
	ok( minZ < - 3.9, `it went aft past the transom before it fell (${ minZ.toFixed( 2 ) } m, the transom is at -3.9)` );
	ok( maxY > 0.9, `and it was lifted clear of the stack and the rail (${ maxY.toFixed( 2 ) } m up)` );

	// a pot hauled aboard lands in the next empty place and only then fills it
	( { tr, state } = mkTraps( 2 ) );
	const pot = state.setTrap( 0, 0, 6 ); // one out, one aboard
	ok( vis( tr ) === '1000', 'one aboard, one on the seabed' );
	state.haulTrap( pot.id ); // the haul is in the state: two aboard again
	ok( tr.haulVisual( boat ), 'the pot comes up on the hauler' );
	ok( vis( tr ) === '1000', 'its place stays empty while it is in the air' );
	let landed = null;
	n = 0;
	while ( tr.busy && n ++ < 600 ) { tr.update( 1 / 60 ); if ( tr.busy ) landed = tr.holder.position.clone(); }
	tr.update( 1 / 60 );
	const end = slotWorld( boat, 1 );
	ok( vis( tr ) === '1100', 'it lands, and the place fills' );
	ok( landed && landed.distanceTo( end ) < 0.05, `it came down onto its place (${ landed && landed.distanceTo( end ).toFixed( 3 ) } m off, in a boat heading 0.7 rad)` );
	ok( n / 60 > 3 && n / 60 < 4.5, `the haul takes ${ ( n / 60 ).toFixed( 1 ) } s` );

	// the stack has only four places: a fifth pot has nowhere new to land
	( { tr, state } = mkTraps( 6 ) );
	const far = state.setTrap( 0, 0, 6 ); state.haulTrap( far.id );
	tr.haulVisual( boat );
	ok( vis( tr ) === '1111', 'six aboard: all four places stay full during the haul' );
	run( tr );
	ok( vis( tr ) === '1111', '... and after it' );

}

console.warn = warn;
console.log( fails ? `\ntrap-handling: ${ fails } FAILED` : '\ntrap-handling: all passed' );
process.exit( fails ? 1 : 0 );
