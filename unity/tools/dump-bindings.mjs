// Dumps the rebinding half of the JS action layer (src/core/Bindings.js) for Unity:
//   node unity/tools/dump-bindings.mjs [outDir]
// <outDir>/bindings.json for the oracle (Editor/BindingsOracle.cs): a script of operations (add with a steal, remove, clear, reset, set, the options, save, reload from the
// store, resetAll, and stored files that are hand-edited, duplicated, mistyped or not JSON at all) run on the real class over a memory store, with after every step the
// result, the lists of every action, isDefault, the labels and summaries for both layouts, ownerOf and the stored blob.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Bindings, ACTIONS } from '../../src/core/Bindings.js';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const outDir = process.argv[ 2 ] || path.join( root, 'unity/Temp/oracle/bindings' );
fs.mkdirSync( outDir, { recursive: true } );

const KEY = 'tidewater.controls.v1';
const mem = () => { const m = new Map(); return { m, getItem: ( k ) => m.has( k ) ? m.get( k ) : null, setItem: ( k, v ) => { m.set( k, String( v ) ); } }; };
const store = mem();
let b = new Bindings( { store } );

const DEV = [ 'kb', 'mouse', 'pad' ];
const entries = ( l ) => l.map( ( e ) => ( { v: e.v, sign: e.sign } ) );

function snapshot() {

	const actions = {};
	for ( const a of ACTIONS ) {

		const o = { isDefault: b.isDefault( a.id ), summaryXbox: b.summary( a.id, 'xbox' ), summaryPs: b.summary( a.id, 'ps' ) };
		for ( const d of DEV ) o[ d ] = entries( b.list( a.id, d ) );
		for ( const d of DEV ) { o[ 'label_' + d ] = b.label( a.id, d, 'xbox' ); o[ 'labelPs_' + d ] = b.label( a.id, d, 'ps' ); }
		actions[ a.id ] = o;

	}

	return {
		actions, opts: { ...b.opts }, rev: b.rev,
		owners: { KeyE: b.ownerOf( 'kb', 'KeyE' ), KeyG: b.ownerOf( 'kb', 'KeyG' ), LMB: b.ownerOf( 'mouse', 'LMB' ), MMB: b.ownerOf( 'mouse', 'MMB' ), A: b.ownerOf( 'pad', 'A' ), RSX: b.ownerOf( 'pad', 'RSX' ), Nope: b.ownerOf( 'kb', 'Nope' ) },
		stored: store.getItem( KEY ),
	};

}

const steps = [];
function run( op ) {

	let result = null;
	switch ( op.op ) {

		case 'init': break;
		case 'add': result = b.add( op.id, op.device, op.sign === undefined ? op.v : { v: op.v, sign: op.sign } ); break;
		case 'remove': result = b.remove( op.id, op.device, op.v ); break;
		case 'clear': result = b.clear( op.id ); break;
		case 'reset': result = b.reset( op.id ); break;
		case 'resetAll': b.resetAll(); break;
		case 'set': result = b.set( op.id, op.device, op.entries ); break;
		case 'opt': b.opts[ op.key ] = op.value; break;
		case 'save': b.save(); break;
		case 'reload': b = new Bindings( { store } ); break;
		case 'raw': store.m.set( KEY, op.value ); break;
		case 'rawNone': store.m.delete( KEY ); break;
		case 'load': result = b.load(); break;
		default: throw new Error( 'op ' + op.op );

	}

	steps.push( { ...op, result, snap: snapshot() } );

}

const S = ( id, device, v, sign ) => ( { op: 'add', id, device, v, sign } );
const script = [
	{ op: 'init' },
	S( 'interact', 'kb', 'KeyG' ), // a new key: nothing is taken
	S( 'rod', 'kb', 'KeyE' ), // E was interact's before the step above kept it: taken
	S( 'forward', 'kb', 'KeyW', - 1 ), // already on forward with sign +1: stays
	S( 'strafe', 'kb', 'KeyW', - 1 ), // taken from forward, with the sign
	S( 'sprint', 'pad', 'A' ), // the pad button of interact
	S( 'ascend', 'mouse', 'MMB' ),
	S( 'rodIn', 'mouse', 'LMB' ), // taken from rodUse
	S( 'ascend', 'kb', '1bad' ), // not a code
	S( 'nope', 'kb', 'KeyZ' ), // not an action
	S( 'ascend', 'pad', 'ZZ' ), // not a pad input
	S( 'ascend', 'mouse', 'Mouse' ), // the look entry is allowed on the mouse device
	{ op: 'remove', id: 'sprint', device: 'kb', v: 'ShiftRight' },
	{ op: 'remove', id: 'sprint', device: 'kb', v: 'Nope' },
	{ op: 'remove', id: 'nope', device: 'kb', v: 'KeyA' },
	{ op: 'clear', id: 'descend' },
	{ op: 'clear', id: 'nope' },
	{ op: 'reset', id: 'descend' },
	{ op: 'reset', id: 'nope' },
	{ op: 'set', id: 'cooler', device: 'kb', entries: [ 'KeyK', { v: 'KeyL', sign: - 1 }, { v: 'bad 1' }, { v: 'F5', sign: 7 } ] },
	{ op: 'set', id: 'cooler', device: 'tape', entries: [] },
	{ op: 'opt', key: 'deadzone', value: 0.3 },
	{ op: 'opt', key: 'padEnabled', value: false },
	{ op: 'opt', key: 'invertY', value: true },
	{ op: 'opt', key: 'lookSensitivity', value: 2.25 },
	{ op: 'opt', key: 'rumble', value: 0 },
	{ op: 'opt', key: 'flashlightOn', value: false },
	{ op: 'save' },
	{ op: 'reload' },
	S( 'map', 'kb', 'KeyG' ), // after the reload: still steals
	{ op: 'reload' },
	{ op: 'resetAll' },
	{ op: 'reload' },
	// stored files
	{ op: 'rawNone' }, { op: 'load' },
	{ op: 'raw', value: JSON.stringify( { v: 1, actions: { interact: { kb: [ 'KeyG', { v: 'KeyH', sign: - 1 } ], pad: [] }, nope: { kb: [ 'KeyQ' ] }, rod: 'x', map: { kb: 'KeyN' } }, opts: { deadzone: 0.25, invertY: 'yes', rumble: 0.1, lookSensitivity: null, padEnabled: false } } ) }, { op: 'load' },
	// duplicates across actions and within one: the earlier action keeps it; inside one list the later entry is the one kept
	{ op: 'raw', value: JSON.stringify( { v: 1, actions: { rod: { kb: [ 'KeyJ', 'KeyJ' ] }, interact: { kb: [ 'KeyJ', 'KeyU' ] }, cooler: { kb: [ 'KeyU' ], pad: [ 'A', 'A' ] } }, opts: {} } ) }, { op: 'load' },
	// signs: only -1 counts
	{ op: 'raw', value: JSON.stringify( { v: 1, actions: { forward: { kb: [ { v: 'KeyW', sign: - 1 }, { v: 'KeyS', sign: 2 }, { v: 'KeyT', sign: '-1' }, { v: 'KeyY' } ] } } } ) }, { op: 'load' },
	{ op: 'raw', value: 'not json {' }, { op: 'load' },
	{ op: 'raw', value: '[]' }, { op: 'load' },
	{ op: 'raw', value: 'null' }, { op: 'load' },
	{ op: 'raw', value: '{"v":1}' }, { op: 'load' },
	{ op: 'raw', value: JSON.stringify( { v: 1, actions: {}, opts: { deadzone: 0.05 } } ) }, { op: 'load' },
	{ op: 'reload' },
	S( 'flashlight', 'kb', 'KeyG' ),
	{ op: 'reload' },
];

for ( const op of script ) run( op );
fs.writeFileSync( path.join( outDir, 'bindings.json' ), JSON.stringify( { steps, actionIds: ACTIONS.map( ( a ) => a.id ) } ) );
console.log( `bindings: ${ steps.length } steps, ${ ACTIONS.length } actions -> ${ path.join( outDir, 'bindings.json' ) }` );
