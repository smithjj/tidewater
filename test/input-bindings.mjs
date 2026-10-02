// Plain-node tests of the input action layer: the binding table, the resolver, the pad maths and the
// saved-bindings round trip. No GPU, no DOM.
import { Bindings, ACTIONS, DEFAULT_OPTS, DEVICES, keyGlyph, padGlyph, entry } from '../src/core/Bindings.js';
import { Gamepad, applyDeadzone, detectLayout } from '../src/core/Gamepad.js';
import { Input } from '../src/core/Input.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

// a localStorage stand-in
const store = () => {

	const m = new Map();
	return { getItem: ( k ) => ( m.has( k ) ? m.get( k ) : null ), setItem: ( k, v ) => m.set( k, v ), _m: m };

};

// a pad stand-in with the standard mapping
const fakePad = ( { buttons = {}, axes = {} } = {} ) => ( {
	index: 0, id: 'Xbox Wireless Controller (STANDARD GAMEPAD Vendor: 045e Product: 0b13)',
	buttons: Array.from( { length: 17 }, ( _, i ) => {

		const v = Object.entries( { A: 0, B: 1, X: 2, Y: 3, LB: 4, RB: 5, LT: 6, RT: 7, View: 8, Menu: 9, L3: 10, R3: 11, DUp: 12, DDown: 13, DLeft: 14, DRight: 15 } )
			.find( ( [ , idx ] ) => idx === i );
		const name = v && v[ 0 ];
		const value = name && buttons[ name ] !== undefined ? buttons[ name ] : 0;
		return { pressed: value > 0.5, value };

	} ),
	axes: [ axes.LSX || 0, axes.LSY || 0, axes.RSX || 0, axes.RSY || 0 ],
} );

// ---- the table

{
	ok( new Set( ACTIONS.map( ( a ) => a.id ) ).size === ACTIONS.length, 'every action id is unique' );
	// one input drives one action: no default input is claimed twice
	const b = new Bindings( { store: store() } );
	const seen = new Set();
	let dupes = 0;
	for ( const a of ACTIONS ) for ( const dev of DEVICES ) for ( const e of b.list( a.id, dev ) ) {

		const key = dev + ':' + e.v;
		if ( seen.has( key ) ) { dupes ++; console.log( '     duplicate:', key, 'on', a.id ); }
		seen.add( key );

	}

	ok( dupes === 0, 'no default input is bound to two actions' );
	ok( ACTIONS.every( ( a ) => [ 'button', 'axis', 'look' ].includes( a.kind ) ), 'every action has a known kind' );
	ok( ACTIONS.every( ( a ) => [ 'block', 'allow' ].includes( a.menu ) ), 'and a menu policy' );
	ok( ACTIONS.every( ( a ) => a.group && a.label ), 'and a group and a label for the interface' );
}

// ---- defaults resolve to the keys the game used before the refactor

{
	const b = new Bindings( { store: store() } );
	const codes = ( id, dev = 'kb' ) => b.list( id, dev ).map( ( e ) => e.v ).sort().join( ',' );
	ok( codes( 'forward' ) === 'KeyS,KeyW' && codes( 'strafe' ) === 'KeyA,KeyD', 'WASD still moves' );
	ok( codes( 'sprint' ) === 'ShiftLeft,ShiftRight', 'either Shift sprints' );
	ok( codes( 'ascend' ) === 'Space' && codes( 'descend' ) === 'ControlLeft,KeyC,KeyQ', 'Space/C jump and dive (Q flies down in the free camera)' );
	ok( codes( 'interact' ) === 'KeyE', 'E still interacts (and sets and hauls)' );
	ok( codes( 'cooler' ) === 'KeyI,Tab', 'cooler on I or Tab' );
	ok( codes( 'rod' ) === 'KeyR' && b.list( 'rodUse', 'mouse' )[ 0 ].v === 'LMB' && b.list( 'rodIn', 'mouse' )[ 0 ].v === 'RMB', 'the rod keys and the mouse buttons' );
	ok( codes( 'cancel' ) === 'Escape' && codes( 'settings' ) === 'KeyH' && codes( 'photo' ) === 'KeyP' && codes( 'controls' ) === 'F1', 'the interface keys' );
	// the signs: W is forward, S is back
	const fwd = b.list( 'forward', 'kb' );
	ok( fwd.find( ( e ) => e.v === 'KeyW' ).sign === 1 && fwd.find( ( e ) => e.v === 'KeyS' ).sign === - 1, 'W is ahead, S is astern' );
	const st = b.list( 'strafe', 'kb' );
	ok( st.find( ( e ) => e.v === 'KeyD' ).sign === 1 && st.find( ( e ) => e.v === 'KeyA' ).sign === - 1, 'D is right, A is left' );
	// the pad defaults: stick for movement, triggers for the rod
	ok( b.list( 'forward', 'pad' )[ 0 ].v === 'LSY' && b.list( 'forward', 'pad' )[ 0 ].sign === - 1, 'the left stick drives throttle, up = ahead' );
	ok( b.list( 'look', 'pad' ).map( ( e ) => e.v ).join( ',' ) === 'RSX,RSY', 'the right stick looks' );
	ok( b.list( 'rodUse', 'pad' )[ 0 ].v === 'RT' && b.list( 'interact', 'pad' )[ 0 ].v === 'A', 'RT casts, A interacts' );
	ok( b.list( 'sprint', 'pad' )[ 0 ].v === 'LT' && b.list( 'cancel', 'pad' )[ 0 ].v === 'B', 'LT sprints, B backs out' );
	ok( b.list( 'freeCam', 'pad' ).length === 0, 'the free camera has no pad button by default' );
}

// ---- the resolver, on a stubbed pad

{
	const b = new Bindings( { store: store() } );
	const pad = new Gamepad();
	const inp = new Input( null, { bindings: b, pad } );
	let pads = [];
	pad.getGamepads = () => pads;

	const hold = ( ...codes ) => { inp.keys = new Set( codes ); inp.pressed = new Set( codes ); };
	// a keyboard press reads as an action
	hold( 'KeyW' );
	ok( inp.act( 'forward' ) && inp.axis( 'forward' ) === 1, 'W reads as forward, full speed' );
	ok( ! inp.act( 'ascend' ), 'and not as jump' );
	ok( inp.actHit( 'forward' ), 'a key pressed on an axis action still has a press edge' );
	hold( 'KeyE' );
	ok( inp.actHit( 'interact' ), 'E reads as an interact press' );
	hold( 'ShiftRight' );
	ok( inp.act( 'sprint' ), 'either Shift sprints' );
	hold( 'KeyW', 'KeyS' );
	ok( inp.axis( 'forward' ) === 0, 'W and S together cancel out' );
	hold( 'KeyW', 'ShiftLeft' );
	const mv = inp.move();
	ok( mv.y === 1 && mv.x === 0, 'move() gives forward on the y axis' );

	// the pad: buttons, edges, triggers and the stick
	pads = [ fakePad() ];
	inp.keys.clear();
	inp.pressed.clear();
	ok( inp.poll( 1 / 60 ) === false, 'an idle pad is not active' );
	pads = [ fakePad( { buttons: { A: 1 } } ) ];
	ok( inp.poll( 1 / 60 ) === true && inp.device === 'pad', 'a pad button switches the active device' );
	ok( inp.actHit( 'interact' ) && inp.act( 'interact' ), 'A reads as interact' );
	ok( inp.device === 'pad' && inp.label( 'interact' ) === 'A', 'and the glyph follows the device' );
	inp.endFrame();
	ok( ! inp.actHit( 'interact' ) && inp.act( 'interact' ), 'the press edge clears, the hold does not' );
	// the same pad button still pressed next frame: no repeat edge
	ok( inp.poll( 1 / 60 ) === true && ! inp.actHit( 'interact' ), 'holding a button does not repeat the edge' );

	// analogue stick
	pads = [ fakePad( { axes: { LSY: - 0.5 } } ) ];
	inp.poll( 1 / 60 );
	ok( inp.axis( 'forward' ) > 0.4 && inp.axis( 'forward' ) < 0.6, `a half-pushed stick is half throttle (${ inp.axis( 'forward' ).toFixed( 2 ) })` );
	ok( inp.act( 'forward' ), 'and past the held threshold' );
	const mv2 = inp.move();
	ok( Math.abs( Math.hypot( mv2.x, mv2.y ) - Math.abs( inp.axis( 'forward' ) ) ) < 1e-9, 'move() keeps the magnitude, so a stick walks slowly' );
	// triggers
	pads = [ fakePad( { buttons: { RT: 0.8, LT: 0.2 } } ) ];
	inp.poll( 1 / 60 );
	ok( inp.act( 'rodUse' ) && inp.axis( 'rodUse' ) > 0.7, 'a half-pulled RT is a cast' );
	ok( ! inp.act( 'sprint' ) && inp.axis( 'sprint' ) < 0.3, 'a light LT does not count as held' );
	// keyboard takes the device back
	inp.useKeyboard();
	ok( inp.device === 'kb' && inp.label( 'interact' ) === 'E', 'the keyboard takes the glyphs back' );

	// the pad and the keyboard drive the same action: a stick forward with S held cancels out
	pads = [ fakePad( { axes: { LSY: - 1 } } ) ];
	inp.keys = new Set( [ 'KeyS' ] );
	inp.poll( 1 / 60 );
	ok( inp.device === 'pad' && inp.axis( 'forward' ) === 0, 'the stick forward and S together cancel out' );
	inp.keys = new Set();
	inp.useKeyboard();

	// nothing is stuck down when the pad goes away
	pads = [];
	inp.poll( 1 / 60 );
	ok( ! inp.pad.connected && inp.pad.down.size === 0 && ! inp.act( 'interact' ), 'unplugging releases everything' );
	inp.endFrame();

	// suppression: a panel open kills the world actions but not the ones a panel is closed with
	inp.keys = new Set( [ 'KeyE', 'KeyH', 'Escape' ] );
	inp.pressed = new Set( [ 'KeyE', 'KeyH' ] );
	inp.menuMode = true;
	ok( ! inp.act( 'pauseTime' ) && inp.act( 'settings' ), 'a panel open stops the world keys but not the interface' );
	ok( inp.act( 'interact' ) && inp.act( 'cancel' ), 'the keys a panel is closed with stay live' );
	ok( inp.axis( 'forward' ) === 0, 'and stops the sticks' );
	inp.menuMode = false;
	ok( inp.act( 'interact' ), 'closing the panel brings the world back' );

	// capture: every action dead while a binding is being captured
	inp.capturing = true;
	ok( ! inp.act( 'interact' ) && ! inp.actHit( 'settings' ), 'a capturing row takes the input' );
	inp.capturing = false;

	// left/right look: the mouse and the stick both land in the same place
	inp.look.x = 10;
	inp.look.y = - 4;
	const l1 = inp.consumeLook();
	ok( l1.x === 10 && l1.y === - 4 && inp.consumeLook().x === 0, 'mouse deltas are consumed once' );
	pads = [ fakePad( { axes: { RSX: 1 } } ) ];
	inp.device = 'kb';
	inp.poll( 1 / 60 );
	const l2 = inp.consumeLook();
	ok( l2.x > 4 && l2.x < 12, `a full right stick turns the camera (${ l2.x.toFixed( 1 ) } px in a 60 fps frame ~ 420 px/s)` );
	// a long frame must not turn a *rate* into a jump: the step is capped, so a hitch cannot snap the view
	inp.poll( 0.5 );
	const l3 = inp.consumeLook();
	ok( l3.x <= 24, `a half-second hitch moves the view by at most the cap (${ l3.x.toFixed( 1 ) } px, not ${ ( 420 * 0.5 ).toFixed( 0 ) })` );
	inp.bindings.opts.invertY = true;
	inp.poll( 0.5 );
	ok( inp.consumeLook().y === 0 && ( pads = [ fakePad( { axes: { RSY: 1 } } ) ], inp.poll( 0.5 ), inp.consumeLook().y < 0 ), 'invert-Y flips the vertical stick' );

	// the deadzone default keeps a resting stick quiet
	const b2 = new Bindings( { store: store() } );
	ok( b2.opts.deadzone === DEFAULT_OPTS.deadzone && applyDeadzone( 0.1, b2.opts.deadzone ) === 0, 'a stick inside the deadzone reads zero' );
	ok( applyDeadzone( - 1, 0.15 ) === - 1 && applyDeadzone( 1, 0.15 ) === 1, 'and full deflection is still full' );
	ok( applyDeadzone( 0.5, 0.15 ) > 0 && applyDeadzone( 0.5, 0.15 ) < 0.5, 'and it scales from zero past the zone' );
}

// ---- rebinding

{
	const s = store();
	const b = new Bindings( { store: s } );
	ok( b.isDefault( 'interact' ), 'a fresh table is on its defaults' );
	// stealing: A is interact, bind it to jump instead
	const lost = b.add( 'ascend', 'pad', 'A' );
	ok( lost.includes( 'interact' ) && b.list( 'ascend', 'pad' ).some( ( e ) => e.v === 'A' ), 'binding a taken input takes it (the row can then hold both)' );
	ok( b.list( 'interact', 'pad' ).length === 0, 'and the action that had it loses it' );
	ok( ! b.isDefault( 'ascend' ) && ! b.isDefault( 'interact' ), 'both rows now read as changed' );
	// persistence: only what differs from the defaults is stored
	const raw = JSON.parse( s.getItem( 'tidewater.controls.v1' ) );
	ok( raw.actions.ascend && ! raw.actions.forward, 'only the changed actions are saved' );
	const b2 = new Bindings( { store: s } );
	ok( b2.list( 'ascend', 'pad' ).some( ( e ) => e.v === 'A' ) && b2.list( 'interact', 'pad' ).length === 0, 'the change survives a reload' );
	ok( b2.isDefault( 'forward' ), 'and the untouched defaults are still there' );
	// reset
	b2.reset( 'ascend' );
	ok( b2.isDefault( 'ascend' ) && b2.list( 'ascend', 'pad' )[ 0 ].v === 'X', 'a row resets to its default' );
	b2.resetAll();
	ok( b2.list( 'interact', 'pad' )[ 0 ].v === 'A' && b2.isDefault( 'interact' ), 'and resetting all brings everything back' );
	// clearing
	b.clear( 'mute' );
	ok( b.list( 'mute', 'kb' ).length === 0 && b.label( 'mute', 'kb' ) === '—', 'an action can be cleared (and shows no key)' );
	ok( b.ownerOf( 'kb', 'KeyM' ) === null, 'a cleared input belongs to nobody' );

	// a stored file with rubbish in it
	const bad = store();
	bad.setItem( 'tidewater.controls.v1', JSON.stringify( {
		v: 1,
		actions: {
			forward: { kb: [ { v: 'KeyI', sign: 1 } ], pad: [ { v: 'Nope', sign: 1 } ] }, // KeyI is the cooler
			ghost: { kb: [ 'KeyZ' ] }, // an action that does not exist
			interact: { kb: 'not an array', pad: [ 'B' ] }, // B is cancel
		},
		opts: { deadzone: 0.3, lookSensitivity: 'lots', nowhere: 5 },
	} ) );
	const b3 = new Bindings( { store: bad } );
	ok( b3.list( 'forward', 'kb' )[ 0 ].v === 'KeyI' && b3.list( 'forward', 'pad' ).length === 0, 'stored bindings load, invalid inputs are dropped' );
	ok( b3.list( 'interact', 'kb' )[ 0 ].v === 'KeyE', 'a device list that is not a list keeps the default' );
	ok( b3.ownerOf( 'pad', 'B' ) === 'interact', 'a valid one loads, taking the input from whoever had it' );
	ok( b3.opts.deadzone === 0.3 && b3.opts.lookSensitivity === DEFAULT_OPTS.lookSensitivity && ! ( 'nowhere' in b3.opts ), 'options load with the right types only' );
	ok( b3.ownerOf( 'kb', 'KeyI' ) === 'forward', 'the moved key changed hands' );
	// a file that binds one input twice keeps the first and drops the rest
	const dup = store();
	dup.setItem( 'tidewater.controls.v1', JSON.stringify( { v: 1, actions: { ascend: { kb: [ 'KeyE' ] }, descend: { kb: [ 'KeyE' ] } } } ) );
	const b4 = new Bindings( { store: dup } );
	ok( b4.list( 'ascend', 'kb' )[ 0 ].v === 'KeyE' && b4.list( 'descend', 'kb' ).length === 0, 'a duplicate in the store is dropped' );
	// unreadable json
	const junk = store();
	junk.setItem( 'tidewater.controls.v1', '{not json' );
	const b5 = new Bindings( { store: junk } );
	ok( b5.isDefault( 'interact' ) && b5.opts.deadzone === DEFAULT_OPTS.deadzone, 'unreadable bindings fall back to the defaults' );
}

// ---- glyphs

{
	ok( keyGlyph( 'KeyW' ) === 'W' && keyGlyph( 'Digit4' ) === '4' && keyGlyph( 'ShiftLeft' ) === 'Shift', 'key codes print as keycaps' );
	ok( keyGlyph( 'Escape' ) === 'Esc' && keyGlyph( 'Space' ) === 'Space' && keyGlyph( 'ArrowUp' ) === '↑', 'special keys have names' );
	ok( padGlyph( 'A' ) === 'A' && padGlyph( 'A', 'ps' ) === '✕' && padGlyph( 'RT', 'ps' ) === 'R2', 'pad glyphs follow the layout' );
	ok( detectLayout( 'DualSense Wireless Controller' ) === 'ps' && detectLayout( 'Xbox Wireless Controller (STANDARD GAMEPAD)' ) === 'xbox', 'the layout is sniffed from the pad id' );
	const b = new Bindings( { store: store() } );
	ok( b.label( 'interact', 'kb' ) === 'E' && b.label( 'interact', 'pad' ) === 'A', 'a label follows the device' );
	ok( b.label( 'rodUse', 'mouse' ) === 'LMB' && b.label( 'rodUse', 'pad' ) === 'RT', 'the rod action shows the mouse or the trigger' );
	ok( b.label( 'forward', 'kb' ) === 'W / S', 'a two-key action shows both' );
	ok( b.label( 'freeCam', 'pad' ) === 'F', 'an action with no pad button falls back to the keyboard glyph' );
	ok( b.summary( 'interact' ).includes( 'E' ) && b.summary( 'interact' ).includes( 'A' ), 'the row summary shows every device' );
}

// ---- the pad backend on its own

{
	const g = new Gamepad();
	let pads = [];
	g.getGamepads = () => pads;
	pads = [ fakePad( { buttons: { DUp: 1 }, axes: { LSX: 1 } } ) ];
	ok( g.poll() && g.isDown( 'DUp' ) && g.axis( 'LSX' ) === 1, 'a button and a stick read through' );
	ok( g.isPressed( 'DUp' ) && ! g.isDown( 'DDown' ), 'the press edge is there once' );
	g.endFrame();
	ok( ! g.isPressed( 'DUp' ), 'and clears at the end of the frame' );
	// the triggers: 0..1 in the standard mapping, -1..1 in old Chrome
	pads = [ fakePad( { buttons: { RT: 1 } } ) ];
	g.poll();
	ok( g.axis( 'RT' ) === 1 && g.isDown( 'RT' ), 'RT reads as an axis and as held' );
	const old = fakePad();
	old.buttons[ 7 ] = { pressed: true, value: - 0.5 };
	pads = [ old ];
	g.poll();
	ok( g.axis( 'RT' ) === 0.25, 'an old-style -1..1 trigger value is normalized' );
	// a pad that reports the triggers as axes 4/5 instead of buttons
	const axesTrig = { index: 0, id: 'Generic', buttons: [], axes: [ 0, 0, 0, 0, 0.6, 0 ] };
	pads = [ axesTrig ];
	g.poll();
	ok( Math.abs( g.axis( 'LT' ) - 0.6 ) < 1e-9, 'a trigger on axis 4 still reads' );
	// rumble is a no-op without the actuator, and throttled when there is one
	pads = [ fakePad() ];
	g.poll();
	ok( g.rumble( { strong: 1 } ) === false, 'no actuator, no rumble' );
	let played = 0;
	const rumblePad = fakePad();
	rumblePad.vibrationActuator = { playEffect: ( kind, o ) => { played ++; return Promise.resolve(); } };
	pads = [ rumblePad ];
	g.poll();
	ok( g.rumble( { strong: 0.5, weak: 0.2, ms: 100 } ) === true && played === 1, 'a pulse plays through the actuator' );
	ok( g.rumble( { strong: 0.5 } ) === false && played === 1, 'and a second pulse too soon is dropped' );
	ok( g.rumble( { strong: 0.5, strength: 0, cooldown: 0 } ) === false, 'rumble strength 0 is off' );
	// a pad with 16 buttons and no id does not throw
	pads = [ { index: 0, buttons: [ { pressed: true, value: 1 } ], axes: [] } ];
	g.poll();
	ok( g.isDown( 'A' ) && g.axis( 'LSY' ) === 0, 'a bare-bones pad object still reads' );
}

console.log( fails ? `\n${ fails } FAILED` : '\ninput-bindings: all passed' );
process.exit( fails ? 1 : 0 );
