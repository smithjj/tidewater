// The action layer: every input the game reads is a named action, and each action carries the inputs
// bound to it on each device (keyboard codes, mouse buttons, pad buttons/axes). Rebinding, the pad, the
// glyphs and the help sheet all read this one table, so they cannot drift apart.
//
// Binding entries are `{ v, sign }`: `v` is a KeyboardEvent.code, a mouse button name ('LMB', 'RMB',
// 'MMB') or a pad button/axis name ('A', 'RT', 'LSY'…); `sign` is only meaningful for `kind: 'axis'`
// actions, where it flips the direction (W is +1 on `forward`, S is -1). A bare string in a default is
// taken as sign +1.
//
// One input drives one action: where two meanings share a physical key today (Space is jump and fly-up,
// C is dive and fly-down) they are the same action with a context-dependent meaning, so a rebind can
// never steal from a sibling.
//
// `menu` says what happens while a panel or overlay owns the input: 'block' for the actions that would
// otherwise walk, look, or flip a switch behind the interface, 'allow' for the verbs the panels and the
// catch card themselves are closed with (interact, cancel, cooler, the rod).
const STORE_KEY = 'tidewater.controls.v1';

export const PAD_BUTTONS = [ 'A', 'B', 'X', 'Y', 'LB', 'RB', 'LT', 'RT', 'View', 'Menu', 'L3', 'R3', 'DUp', 'DDown', 'DLeft', 'DRight' ];
export const PAD_AXES = [ 'LSX', 'LSY', 'RSX', 'RSY', 'LT', 'RT' ];
export const MOUSE_BUTTONS = [ 'LMB', 'RMB', 'MMB' ];

// the groups the Controls tab and the help sheet are built from, in order
export const GROUPS = [ 'Movement', 'Fishing', 'Interact', 'Interface' ];

export const ACTIONS = [
	// ---- movement. forward/strafe are axes: the stick, or the pair of keys, signed
	{
		id: 'forward', label: 'Move forward / back', group: 'Movement', kind: 'axis', menu: 'block',
		help: 'Move<small>W A S D, or the left stick</small>',
		kb: [ { v: 'KeyW', sign: 1 }, { v: 'KeyS', sign: - 1 } ],
		pad: [ { v: 'LSY', sign: - 1 } ], // standard mapping: pushing the stick up is -1
	},
	{
		id: 'strafe', label: 'Move left / right', group: 'Movement', kind: 'axis', menu: 'block',
		kb: [ { v: 'KeyD', sign: 1 }, { v: 'KeyA', sign: - 1 } ],
		pad: [ { v: 'LSX', sign: 1 } ],
	},
	{
		id: 'look', label: 'Look around', group: 'Movement', kind: 'look', menu: 'block', noRebind: true,
		help: 'Look around<small>Click to capture the mouse</small>',
		mouse: [ { v: 'Mouse', sign: 1 } ],
		pad: [ { v: 'RSX', sign: 1 }, { v: 'RSY', sign: 1 } ],
	},
	{ id: 'sprint', label: 'Sprint / boost', group: 'Movement', kind: 'button', menu: 'block', help: 'Sprint, boat boost', kb: [ 'ShiftLeft', 'ShiftRight' ], pad: [ 'LT' ] },
	{ id: 'ascend', label: 'Jump / swim up', group: 'Movement', kind: 'button', menu: 'block', help: 'Jump, swim up, fly up', kb: [ 'Space' ], pad: [ 'X' ] },
	// KeyQ is in here for the free camera, which has always used Q to fly down (it is otherwise unused)
	{ id: 'descend', label: 'Crouch / dive', group: 'Movement', kind: 'button', menu: 'block', help: 'Crouch, dive, fly down', kb: [ 'KeyC', 'ControlLeft', 'KeyQ' ], pad: [ 'Y' ] },

	// ---- fishing
	{ id: 'rod', label: 'Take out the rod', group: 'Fishing', kind: 'button', menu: 'allow', help: 'Fishing rod<small>Take out / put away</small>', kb: [ 'KeyR' ], pad: [ 'LB' ] },
	{ id: 'rodUse', label: 'Cast / strike / reel', group: 'Fishing', kind: 'button', menu: 'allow', help: 'Cast, strike, reel<small>Hold to wind up / reel. At the helm: set and haul pots</small>', mouse: [ 'LMB' ], pad: [ 'RT' ] },
	{ id: 'rodIn', label: 'Reel in an empty line', group: 'Fishing', kind: 'button', menu: 'allow', help: 'Reel in an empty line', mouse: [ 'RMB' ], pad: [ 'RB' ] },

	// ---- interact
	{ id: 'interact', label: 'Interact', group: 'Interact', kind: 'button', menu: 'allow', help: 'Interact<small>Board, helm, step ashore, trade, traps on deck</small>', kb: [ 'KeyE' ], pad: [ 'A' ] },
	{ id: 'cooler', label: 'Cooler and fish log', group: 'Interact', kind: 'button', menu: 'allow', help: 'Cooler and fish log', kb: [ 'KeyI', 'Tab' ], pad: [ 'R3' ] },
	{ id: 'anchor', label: 'Anchor', group: 'Interact', kind: 'button', menu: 'block', help: 'Anchor<small>Drop or weigh, aboard a boat</small>', kb: [ 'KeyX' ], pad: [] },
	{ id: 'resetBoats', label: 'Reset boats', group: 'Interact', kind: 'button', menu: 'block', help: 'Reset boats<small>Right a capsized boat and send it back to its berth; aboard, the boat you are on</small>', kb: [ 'KeyB' ], pad: [] },
	{ id: 'boatCamera', label: 'Boat camera', group: 'Interact', kind: 'button', menu: 'block', help: 'Boat camera<small>1st / 3rd person</small>', kb: [ 'KeyV' ], pad: [ 'L3' ] },

	// ---- interface
	{ id: 'codex', label: 'Fish guide', group: 'Interface', kind: 'button', menu: 'allow', help: 'Fish guide<small>What you have learned about each fish</small>', kb: [ 'KeyJ' ], pad: [] },
	{ id: 'settings', label: 'Settings panel', group: 'Interface', kind: 'button', menu: 'allow', help: 'Settings panel', kb: [ 'KeyH' ], pad: [ 'Menu' ] },
	{ id: 'controls', label: 'All controls', group: 'Interface', kind: 'button', menu: 'allow', help: 'This sheet', kb: [ 'F1' ], pad: [ 'View' ] },
	{ id: 'map', label: 'Large map', group: 'Interface', kind: 'button', menu: 'block', help: 'Large map<small>Open / close, north is up</small>', kb: [ 'KeyN' ], pad: [] },
	{ id: 'photo', label: 'Photo mode', group: 'Interface', kind: 'button', menu: 'block', help: 'Photo mode<small>Hides all interface</small>', kb: [ 'KeyP' ], pad: [ 'DRight' ] },
	{ id: 'cancel', label: 'Back / close', group: 'Interface', kind: 'button', menu: 'allow', help: 'Close, release the mouse', kb: [ 'Escape' ], pad: [ 'B' ] },
	{ id: 'pauseTime', label: 'Run or pause the day', group: 'Interface', kind: 'button', menu: 'block', help: 'Run or pause the day', kb: [ 'KeyT' ], pad: [ 'DUp' ] },
	{ id: 'flashlight', label: 'Flashlight', group: 'Interface', kind: 'button', menu: 'block', help: 'Flashlight', kb: [ 'KeyL' ], pad: [ 'DDown' ] },
	{ id: 'mute', label: 'Mute', group: 'Interface', kind: 'button', menu: 'block', help: 'Mute', kb: [ 'KeyM' ], pad: [ 'DLeft' ] },
	{ id: 'freeCam', label: 'Free camera', group: 'Interface', kind: 'button', menu: 'block', help: 'Free camera<small>Developer camera</small>', kb: [ 'KeyF' ], pad: [] },
	{ id: 'wildlife', label: 'Visit the wildlife', group: 'Interface', kind: 'button', menu: 'block', help: 'Visit the wildlife<small>Eagle ray, stingrays, turtle, whale: again for the next</small>', kb: [ 'KeyG' ], pad: [] },
];

const _byId = new Map( ACTIONS.map( ( a ) => [ a.id, a ] ) );

export const DEFAULT_OPTS = {
	padEnabled: true,
	deadzone: 0.15,
	lookSensitivity: 1,
	invertY: false,
	rumble: 0.7,
	flashlightOn: true, // the handheld torch: on by default (it is near-invisible in daylight)
};

// ---- glyphs

const KB_GLYPHS = {
	Space: 'Space', Escape: 'Esc', Tab: 'Tab', Enter: 'Enter', Backspace: 'Backspace', Delete: 'Del',
	ShiftLeft: 'Shift', ShiftRight: 'Shift', ControlLeft: 'Ctrl', ControlRight: 'Ctrl', AltLeft: 'Alt', AltRight: 'Alt',
	ArrowUp: '↑', ArrowDown: '↓', ArrowLeft: '←', ArrowRight: '→',
	Slash: '/', Backslash: '\\', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Backquote: '`',
	Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']',
	CapsLock: 'Caps', NumpadEnter: 'Enter',
};

export function keyGlyph( code ) {

	if ( KB_GLYPHS[ code ] ) return KB_GLYPHS[ code ];
	if ( /^Key([A-Z])$/.test( code ) ) return code.slice( 3 );
	if ( /^Digit(\d)$/.test( code ) ) return code.slice( 5 );
	if ( /^Numpad(\w+)$/.test( code ) ) return 'Num ' + code.slice( 6 );
	if ( /^F\d{1,2}$/.test( code ) ) return code;
	return code;

}

const XBOX_GLYPHS = {
	A: 'A', B: 'B', X: 'X', Y: 'Y', LB: 'LB', RB: 'RB', LT: 'LT', RT: 'RT',
	View: 'View', Menu: 'Menu', L3: 'L3', R3: 'R3',
	DUp: '↑', DDown: '↓', DLeft: '←', DRight: '→',
	LSX: 'LS', LSY: 'LS', RSX: 'RS', RSY: 'RS',
};

const PS_GLYPHS = {
	A: '✕', B: '○', X: '□', Y: '△', LB: 'L1', RB: 'R1', LT: 'L2', RT: 'R2',
	View: 'Share', Menu: 'Options', L3: 'L3', R3: 'R3',
	DUp: '↑', DDown: '↓', DLeft: '←', DRight: '→',
	LSX: 'LS', LSY: 'LS', RSX: 'RS', RSY: 'RS',
};

export function padGlyph( v, layout = 'xbox' ) {

	return ( layout === 'ps' ? PS_GLYPHS : XBOX_GLYPHS )[ v ] || v;

}

export function mouseGlyph( v ) {

	return v === 'Mouse' ? 'Mouse' : v;

}

// a stored/typed entry into `{ v, sign }`
export function entry( e ) {

	if ( typeof e === 'string' ) return { v: e, sign: 1 };
	return { v: e.v, sign: e.sign === - 1 ? - 1 : 1 };

}

function isCode( v ) {

	return typeof v === 'string' && /^[A-Za-z][A-Za-z0-9]*$/.test( v );

}

// Which device a stored entry belongs to: 'kb' | 'mouse' | 'pad'
function validEntry( device, e ) {

	if ( ! e || typeof e.v !== 'string' ) return false;
	if ( device === 'kb' ) return isCode( e.v );
	if ( device === 'mouse' ) return MOUSE_BUTTONS.includes( e.v ) || e.v === 'Mouse';
	return PAD_BUTTONS.includes( e.v ) || PAD_AXES.includes( e.v );

}

export const DEVICES = [ 'kb', 'mouse', 'pad' ];

export class Bindings {

	constructor( { store = null, actions = ACTIONS } = {} ) {

		this.actions = actions;
		this.byId = new Map( actions.map( ( a ) => [ a.id, a ] ) );
		this.store = store === null ? safeStore() : store;
		this.opts = { ...DEFAULT_OPTS };
		this.map = {}; // id -> { kb: [entry], mouse: [entry], pad: [entry] }
		// bumped by every change, so interface rows can re-read themselves cheaply (see BindingControl)
		this.rev = 0;
		for ( const a of actions ) this.map[ a.id ] = { kb: [], mouse: [], pad: [] };
		this.load();

	}

	// ---- reading

	get( id ) {

		return this.map[ id ] || { kb: [], mouse: [], pad: [] };

	}

	list( id, device ) {

		return this.get( id )[ device ] || [];

	}

	kind( id ) {

		const a = this.byId.get( id );
		return a ? a.kind : 'button';

	}

	// 'block' | 'allow' while a panel owns the input
	menuPolicy( id ) {

		const a = this.byId.get( id );
		return a ? a.menu : 'block';

	}

	meta( id ) {

		return this.byId.get( id ) || null;

	}

	// is this action on its defaults?
	isDefault( id ) {

		const d = this.defaults( id );
		for ( const device of DEVICES ) {

			const a = this.list( id, device ), b = d[ device ];
			if ( a.length !== b.length ) return false;
			for ( let i = 0; i < a.length; i ++ ) if ( a[ i ].v !== b[ i ].v || a[ i ].sign !== b[ i ].sign ) return false;

		}

		return true;

	}

	// the default entries for an action, normalized
	defaults( id ) {

		const a = this.byId.get( id );
		const out = { kb: [], mouse: [], pad: [] };
		if ( ! a ) return out;
		for ( const device of DEVICES ) for ( const e of a[ device ] || [] ) out[ device ].push( entry( e ) );
		return out;

	}

	// which action currently owns an input, or null
	ownerOf( device, v ) {

		for ( const a of this.actions ) for ( const e of this.list( a.id, device ) ) if ( e.v === v ) return a.id;
		return null;

	}

	// ---- writing

	set( id, device, entries ) {

		if ( ! this.map[ id ] || ! DEVICES.includes( device ) ) return false;
		this.map[ id ][ device ] = entries.map( entry ).filter( ( e ) => validEntry( device, e ) );
		this.save();
		this.rev ++;
		return true;

	}

	// bind one input to an action, taking it from any other action that had it. Returns the actions
	// that lost an input, so the interface can say so.
	add( id, device, e ) {

		const one = entry( e );
		if ( ! this.map[ id ] || ! validEntry( device, one ) ) return [];
		const lost = [];
		for ( const a of this.actions ) {

			if ( a.id === id ) continue;
			const list = this.list( a.id, device );
			const at = list.findIndex( ( x ) => x.v === one.v );
			if ( at >= 0 ) {

				list.splice( at, 1 );
				lost.push( a.id );

			}

		}

		const mine = this.list( id, device );
		if ( ! mine.some( ( x ) => x.v === one.v ) ) mine.push( one );
		this.save();
		this.rev ++;
		return lost;

	}

	remove( id, device, v ) {

		if ( ! this.map[ id ] ) return false;
		const list = this.list( id, device );
		const at = list.findIndex( ( x ) => x.v === v );
		if ( at < 0 ) return false;
		list.splice( at, 1 );
		this.save();
		this.rev ++;
		return true;

	}

	clear( id ) {

		if ( ! this.map[ id ] ) return false;
		this.map[ id ] = { kb: [], mouse: [], pad: [] };
		this.save();
		this.rev ++;
		return true;

	}

	reset( id ) {

		if ( ! this.map[ id ] ) return false;
		this.map[ id ] = this.defaults( id );
		this.save();
		this.rev ++;
		return true;

	}

	resetAll() {

		for ( const a of this.actions ) this.map[ a.id ] = this.defaults( a.id );
		this.opts = { ...DEFAULT_OPTS };
		this.save();
		this.rev ++;

	}

	// ---- glyphs

	// 'E', 'A', 'RT', 'LMB', 'W / S' … for the active device
	label( id, device = 'kb', layout = 'xbox' ) {

		let dev = device;
		if ( dev === 'mouse' && this.list( id, 'mouse' ).length === 0 ) dev = 'kb';
		if ( dev === 'kb' && this.list( id, 'kb' ).length === 0 ) dev = 'mouse';
		if ( this.list( id, dev ).length === 0 ) {

			// nothing on this device: show whatever else the action has, so a row is never blank
			for ( const other of DEVICES ) {

				const list = this.list( id, other );
				if ( list.length ) return this._glyphs( list, other, layout );

			}

			return '—';

		}

		return this._glyphs( this.list( id, dev ), dev, layout );

	}

	_glyphs( list, device, layout ) {

		const one = ( e ) => {

			if ( device === 'kb' ) return keyGlyph( e.v );
			if ( device === 'mouse' ) return mouseGlyph( e.v );
			return padGlyph( e.v, layout );

		};

		return list.map( one ).join( ' / ' );

	}

	// every binding of an action, per device, for the rebinding rows
	summary( id, layout = 'xbox' ) {

		const parts = [];
		for ( const dev of DEVICES ) {

			const list = this.list( id, dev );
			if ( list.length ) parts.push( this._glyphs( list, dev, layout ) );

		}

		return parts.join( '  ·  ' ) || '—';

	}

	// ---- storage

	toJSON() {

		const actions = {};
		for ( const a of this.actions ) {

			const own = this.get( a.id );
			// only store what differs from the defaults: keeps the blob small and lets new defaults land
			const d = this.defaults( a.id );
			const diff = {};
			for ( const dev of DEVICES ) {

				const same = own[ dev ].length === d[ dev ].length && own[ dev ].every( ( e, i ) => e.v === d[ dev ][ i ].v && e.sign === d[ dev ][ i ].sign );
				if ( ! same ) diff[ dev ] = own[ dev ];

			}

			if ( Object.keys( diff ).length ) actions[ a.id ] = diff;

		}

		return { v: 1, actions, opts: this.opts };

	}

	save() {

		if ( ! this.store ) return;
		try {

			this.store.setItem( STORE_KEY, JSON.stringify( this.toJSON() ) );

		} catch ( e ) { /* quota or private mode: controls just do not persist */ }

	}

	load() {

		this.opts = { ...DEFAULT_OPTS };
		for ( const a of this.actions ) this.map[ a.id ] = this.defaults( a.id );
		if ( ! this.store ) return false;
		let raw = null;
		try {

			raw = this.store.getItem( STORE_KEY );

		} catch ( e ) { return false; }

		if ( ! raw ) return false;
		let data = null;
		try {

			data = JSON.parse( raw );

		} catch ( e ) {

			console.warn( 'controls: the saved bindings are not readable, using the defaults' );
			return false;

		}

		if ( ! data || typeof data !== 'object' ) return false;
		if ( data.opts && typeof data.opts === 'object' ) {

			for ( const k of Object.keys( DEFAULT_OPTS ) ) {

				const v = data.opts[ k ];
				if ( typeof v === typeof DEFAULT_OPTS[ k ] ) this.opts[ k ] = v;

			}

		}

		if ( ! data.actions || typeof data.actions !== 'object' ) return false;
		for ( const id of Object.keys( data.actions ) ) {

			if ( ! this.byId.has( id ) ) continue; // an action that no longer exists
			const own = data.actions[ id ];
			if ( ! own || typeof own !== 'object' ) continue;
			for ( const dev of DEVICES ) {

				if ( ! Array.isArray( own[ dev ] ) ) continue;
				this.map[ id ][ dev ] = own[ dev ].map( entry ).filter( ( e ) => validEntry( dev, e ) );

			}

		}

		this._dedupe();
		this.rev ++;
		return true;

	}

	// one input drives one action: a stored file with a duplicate (hand-edited, or written by an older
	// version) keeps the first in table order and drops the rest
	_dedupe() {

		const seen = new Set();
		for ( const a of this.actions ) for ( const dev of DEVICES ) {

			const list = this.map[ a.id ][ dev ];
			for ( let i = list.length - 1; i >= 0; i -- ) {

				const key = dev + ':' + list[ i ].v;
				if ( seen.has( key ) ) {

					console.warn( `controls: "${ list[ i ].v }" was bound twice; dropped from "${ a.id }"` );
					list.splice( i, 1 );

				} else seen.add( key );

			}

		}

	}

}

function safeStore() {

	try {

		return typeof localStorage === 'undefined' ? null : localStorage;

	} catch ( e ) { return null; }

}

export function actionMeta( id ) {

	return _byId.get( id ) || null;

}

// Fill in every input name inside a piece of DOM: `[data-bind="interact"]` gets the glyph for the device
// in hand (the text already inside stays for a keyboard player, so the markup degrades gracefully and the
// templates stay readable). `[data-word]` is for prose that names the mouse: on a pad it becomes the glyph,
// otherwise the words are left alone. `[data-stick]` is the movement cluster, one stick on a pad.
// Used by the guide, the HUD panels and the loader tips.
export function resolveLabels( root, { label, device = 'kb' } = {} ) {

	if ( ! root || ! root.querySelectorAll || ! label ) return;

	for ( const el of root.querySelectorAll( '[data-bind]' ) ) {

		const v = label( el.dataset.bind );
		if ( v ) el.textContent = v;

	}

	for ( const el of root.querySelectorAll( '[data-word]' ) ) {

		if ( device !== 'pad' ) continue;
		const v = label( el.dataset.word );
		if ( v ) el.textContent = v;

	}

	for ( const el of root.querySelectorAll( '[data-stick]' ) ) {

		if ( device === 'pad' ) {

			el.dataset.keyboard = el.dataset.keyboard || el.innerHTML;
			el.innerHTML = `<kbd>${ label( 'forward' ) }</kbd>`;

		} else if ( el.dataset.keyboard ) el.innerHTML = el.dataset.keyboard;

	}

}
