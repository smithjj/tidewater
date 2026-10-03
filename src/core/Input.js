// Keyboard / mouse / gamepad input. The game reads *actions* (`act`, `actHit`, `axis`, `move`, `look`)
// which resolve through the binding table, so any action can live on any input and the pad works
// everywhere without the gameplay code knowing a pad exists. The raw `down`/`hit` code API stays as the
// primitive underneath (the resolver uses it, and the UI demo pages still call it).
//
// Synthetic events dispatched by the pad's menu layer (`ui/PadUI.js`) are marked `__pad` and ignored
// here, so a synthetic Space cannot make the player jump.
import { Bindings } from './Bindings.js';
import { Gamepad } from './Gamepad.js';

// a stick at full deflection turns this many pixels' worth of camera per second (mouse deltas are
// pixels, so this keeps the pad and the mouse in the same units)
const PAD_LOOK_PX = 420;
const AXIS_HELD = 0.25; // an axis action counts as "held" past this
// A stick turns the camera at a *rate*, so a long frame must not turn into a jump: the time step is
// capped, and with it the step per frame (a resting stick at full deflection does about 7 px at 60 fps).
// A mouse delta is a distance already, but the odd enormous event does arrive from the driver — after a
// cursor wrap, say — and one of those is a snap that has nothing to do with the hand on the mouse.
const PAD_LOOK_MAX_DT = 1 / 30;
const PAD_LOOK_MAX_STEP = 24;
// A fast flick arrives as a series of ordinary events, so clamping *one* event well above a plausible
// per-event distance costs a fast turn nothing while it stops a driver spike (which arrives as a single
// enormous event) from teleporting the view. 200 px at this game's 0.0022 rad/px is 25°.
const MOUSE_MAX_STEP = 200;

export class Input {

	constructor( dom = null, { bindings = null, pad = null } = {} ) {

		this.dom = dom;
		this.keys = new Set();
		this.pressed = new Set();
		this.look = { x: 0, y: 0 }; // mouse deltas (as before); the pad adds to _padLook
		this._padLook = { x: 0, y: 0 };
		// the biggest mouse movement seen in one event, and in one frame's worth of them: a spike shows up
		// in the first, a frame where the mouse was not read shows up in the second (see lookNow)
		this.lookPeak = { event: 0, frame: 0, dt: 0, at: 0 };
		this._dt = 1 / 60;
		this.wheel = 0;
		this.mouseDown = false;
		this.rightDown = false;
		this.middleDown = false;
		this.mousePressed = new Set();
		this.mouseReleased = new Set();
		this.locked = false;
		this.enabled = true;
		this.bindings = bindings || new Bindings();
		this.pad = pad || new Gamepad( { deadzone: this.bindings.opts.deadzone } );
		this.device = 'kb';
		this.onDeviceChange = null;
		// while a rebinding row is capturing, every action is dead so the pressed key does not also
		// drive the game
		this.capturing = false;
		// set by the pad's menu layer while a panel or overlay owns the input: gameplay actions go dead,
		// interface actions (close, cooler, settings) keep working
		this.menuMode = false;
		this._bind();

	}

	// ---- events

	_bind() {

		if ( typeof window === 'undefined' ) return;
		const dom = this.dom;
		window.addEventListener( 'keydown', ( e ) => {

			if ( e.__pad || this.capturing ) return; // synthetic pad keys, or a binding being captured
			if ( e.target && ( e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT' || e.target.tagName === 'TEXTAREA' ) ) return;
			if ( ! this.keys.has( e.code ) ) this.pressed.add( e.code );
			this.keys.add( e.code );
			this.useKeyboard();
			if ( [ 'Space', 'ArrowUp', 'ArrowDown', 'Tab' ].includes( e.code ) ) e.preventDefault();

		} );
		window.addEventListener( 'keyup', ( e ) => {

			if ( e.__pad ) return;
			this.keys.delete( e.code );

		} );
		window.addEventListener( 'blur', () => this.keys.clear() );

		dom?.addEventListener?.( 'mousedown', ( e ) => {

			if ( e.__pad ) return;
			this._mouse( e.button, true );
			this.useKeyboard();

		} );
		window.addEventListener( 'mouseup', ( e ) => {

			if ( e.__pad ) return;
			this._mouse( e.button, false );

		} );
		dom?.addEventListener?.( 'contextmenu', ( e ) => e.preventDefault() );
		window.addEventListener( 'mousemove', ( e ) => {

			if ( this.locked || this.mouseDown || this.rightDown ) {

				const dx = Math.max( - MOUSE_MAX_STEP, Math.min( MOUSE_MAX_STEP, e.movementX ) );
				const dy = Math.max( - MOUSE_MAX_STEP, Math.min( MOUSE_MAX_STEP, e.movementY ) );
				this.look.x += dx;
				this.look.y += dy;
				const raw = Math.max( Math.abs( e.movementX ), Math.abs( e.movementY ) );
				if ( raw > this.lookPeak.event ) {

					this.lookPeak.event = Math.round( raw );
					this.lookPeak.at = Math.round( performance.now() );

				}
				if ( e.movementX || e.movementY ) this.useKeyboard();

			}

		} );
		dom?.addEventListener?.( 'wheel', ( e ) => {

			this.wheel += Math.sign( e.deltaY );
			e.preventDefault();

		}, { passive: false } );
		if ( typeof document !== 'undefined' ) document.addEventListener( 'pointerlockchange', () => {

			this.locked = document.pointerLockElement === dom;

		} );

	}

	_mouse( button, down ) {

		const was = button === 0 ? this.mouseDown : button === 2 ? this.rightDown : this.middleDown;
		if ( button === 0 ) this.mouseDown = down;
		else if ( button === 2 ) this.rightDown = down;
		else if ( button === 1 ) this.middleDown = down;
		const name = button === 0 ? 'LMB' : button === 2 ? 'RMB' : button === 1 ? 'MMB' : null;
		if ( ! name ) return;
		if ( down && ! was ) this.mousePressed.add( name );
		else if ( ! down && was ) this.mouseReleased.add( name );

	}

	// ---- device

	useKeyboard() {

		this._setDevice( 'kb' );

	}

	_setDevice( d ) {

		if ( this.device === d ) return;
		this.device = d;
		if ( this.onDeviceChange ) this.onDeviceChange( d );

	}

	// once a frame, before anything reads input
	poll( dt = 1 / 60 ) {

		this._dt = dt;
		const opts = this.bindings.opts;
		this.pad.deadzone = opts.deadzone;
		if ( ! opts.padEnabled ) {

			if ( this.pad.connected ) this.pad.disconnect();
			return false;

		}

		const active = this.pad.poll();
		if ( active ) this._setDevice( 'pad' );

		// right stick: the same units as the mouse, per second (capped, see above)
		if ( ! this.menuMode && this.pad.connected ) {

			const sens = PAD_LOOK_PX * opts.lookSensitivity;
			const flip = opts.invertY ? - 1 : 1;
			const step = Math.min( dt, PAD_LOOK_MAX_DT );
			const cap = PAD_LOOK_MAX_STEP;
			this._padLook.x += Math.max( - cap, Math.min( cap, this.pad.axes.RSX * sens * step ) );
			this._padLook.y += Math.max( - cap, Math.min( cap, this.pad.axes.RSY * sens * flip * step ) );

		}

		return active;

	}

	// ---- the raw code API (the primitive under the actions)

	requestLock() {

		if ( this.locked || ! this.dom?.requestPointerLock ) return;
		// Raw, unaccelerated deltas: with the OS pointer acceleration on (Windows' "enhance pointer
		// precision") a fast flick turns disproportionately far, which reads as the view jumping. Browsers
		// without the option ignore it; one that refuses it (a platform with no raw input) gets a plain lock.
		const plain = () => this.dom.requestPointerLock()?.catch?.( () => {} );
		try {

			const p = this.dom.requestPointerLock( { unadjustedMovement: true } );
			if ( p && p.catch ) p.catch( plain );

		} catch ( err ) {

			plain();

		}

	}

	down( code ) {

		return this.enabled && this.keys.has( code );

	}

	// true once per physical key press
	hit( code ) {

		return this.enabled && this.pressed.has( code );

	}

	consumeLook() {

		return this.lookNow();

	}

	consumeWheel() {

		const w = this.wheel;
		this.wheel = 0;
		return w;

	}

	// mouse deltas + right-stick motion this frame (alias of consumeLook, which the player still calls)
	lookNow() {

		const l = { x: this.look.x + this._padLook.x, y: this.look.y + this._padLook.y };
		const big = Math.max( Math.abs( l.x ), Math.abs( l.y ) );
		if ( big > this.lookPeak.frame ) {

			this.lookPeak.frame = Math.round( big );
			this.lookPeak.dt = Math.round( this._dt * 1000 ); // ms of the frame that carried it: a late frame, or a fast hand?

		}
		this.look.x = 0;
		this.look.y = 0;
		this._padLook.x = 0;
		this._padLook.y = 0;
		return l;

	}

	// ---- the action API

	// An action is live unless input is off, a binding is being captured, or a panel owns the input and
	// this action is one that would walk, look or flip a switch behind it (see the table's `menu`).
	_allowed( id ) {

		if ( ! this.enabled || this.capturing ) return false;
		if ( this.menuMode && this.bindings.menuPolicy( id ) === 'block' ) return false;
		return true;

	}

	act( id ) {

		if ( ! this._allowed( id ) ) return false;
		const kind = this.bindings.kind( id );
		if ( kind === 'axis' ) return Math.abs( this.axis( id ) ) > AXIS_HELD;
		if ( kind === 'look' ) return false;
		for ( const e of this.bindings.list( id, 'kb' ) ) if ( this.keys.has( e.v ) ) return true;
		for ( const e of this.bindings.list( id, 'mouse' ) ) if ( this._mouseDown( e.v ) ) return true;
		for ( const e of this.bindings.list( id, 'pad' ) ) if ( this.pad.isDown( e.v ) ) return true;
		return false;

	}

	actHit( id ) {

		if ( ! this._allowed( id ) ) return false;
		for ( const e of this.bindings.list( id, 'kb' ) ) if ( this.pressed.has( e.v ) ) return true;
		for ( const e of this.bindings.list( id, 'mouse' ) ) if ( this.mousePressed.has( e.v ) ) return true;
		for ( const e of this.bindings.list( id, 'pad' ) ) if ( this.pad.isPressed( e.v ) ) return true;
		return false;

	}

	actReleased( id ) {

		if ( ! this._allowed( id ) ) return false;
		for ( const e of this.bindings.list( id, 'mouse' ) ) if ( this.mouseReleased.has( e.v ) ) return true;
		for ( const e of this.bindings.list( id, 'pad' ) ) if ( this.pad.released.has( e.v ) ) return true;
		return false;

	}

	// -1..1: the stick (or trigger) value, or ±1 from the bound keys. Signed entries add up, so
	// `forward` is W(+1) + S(-1) and the left stick (already signed in the table).
	axis( id ) {

		if ( ! this._allowed( id ) ) return 0;
		let v = 0;
		for ( const e of this.bindings.list( id, 'kb' ) ) if ( this.keys.has( e.v ) ) v += e.sign;
		for ( const e of this.bindings.list( id, 'mouse' ) ) if ( this._mouseDown( e.v ) ) v += e.sign;
		for ( const e of this.bindings.list( id, 'pad' ) ) v += this.pad.axis( e.v ) * e.sign;
		return Math.max( - 1, Math.min( 1, v ) );

	}

	// { x, y } in local space: x = strafe (right +), y = forward
	move( out = { x: 0, y: 0 } ) {

		out.x = this.axis( 'strafe' );
		out.y = this.axis( 'forward' );
		return out;

	}

	// the glyph for the active device ('E', 'A', 'RT', 'LMB')
	label( id ) {

		return this.bindings.label( id, this.device, this.pad.layout );

	}

	rumble( o ) {

		return this.pad.rumble( { ...o, strength: this.bindings.opts.rumble } );

	}

	_mouseDown( v ) {

		if ( v === 'LMB' ) return this.mouseDown;
		if ( v === 'RMB' ) return this.rightDown;
		if ( v === 'MMB' ) return this.middleDown;
		return false;

	}

	endFrame() {

		this.pressed.clear();
		this.mousePressed.clear();
		this.mouseReleased.clear();
		this.pad.endFrame();

	}

}
