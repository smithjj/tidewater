// The gamepad backend: polls navigator.getGamepads() once a frame and exposes the standard-mapping
// buttons, sticks and triggers as named inputs, with edges. Nothing else in the game talks to the pad.
//
// Standard mapping (everything modern, wired or wireless):
//   buttons 0-3 A B X Y · 4-5 LB RB · 6-7 LT RT · 8-9 View Menu · 10-11 L3 R3 · 12-15 d-pad
//   axes 0-1 left stick, 2-3 right stick
// Older or exotic pads may report the triggers as axes 4-5 instead of buttons 6-7, and ancient Chrome
// reported trigger values as -1..1 rather than 0..1: both are handled below.
const BUTTON_INDEX = {
	A: 0, B: 1, X: 2, Y: 3, LB: 4, RB: 5, LT: 6, RT: 7,
	View: 8, Menu: 9, L3: 10, R3: 11, DUp: 12, DDown: 13, DLeft: 14, DRight: 15,
};

const AXIS_INDEX = { LSX: 0, LSY: 1, RSX: 2, RSY: 3 };

const BUTTON_NAMES = Object.keys( BUTTON_INDEX );

export class Gamepad {

	constructor( { deadzone = 0.15 } = {} ) {

		this.deadzone = deadzone;
		this.index = - 1;
		this.id = '';
		this.layout = 'xbox';
		this.connected = false;
		this.active = false; // any input past the deadzone this frame
		this.axes = { LSX: 0, LSY: 0, RSX: 0, RSY: 0 };
		this.triggers = { LT: 0, RT: 0 };
		this.down = new Set(); // held buttons, by name
		this.pressed = new Set(); // edges, cleared by endFrame()
		this.released = new Set();
		this._prev = new Set();
		this._rumbleAt = - 1e9;
		this._pad = null;

	}

	// the seam the tests drive: a fake getGamepads(), no browser needed
	getGamepads() {

		try {

			return typeof navigator !== 'undefined' && navigator.getGamepads ? navigator.getGamepads() : [];

		} catch ( e ) { return []; }

	}

	// ---- polling

	// returns true when the pad is connected and produced input this frame (used for device detection)
	poll() {

		const pads = this.getGamepads() || [];
		let pad = null;
		if ( this.index >= 0 && pads[ this.index ] ) pad = pads[ this.index ];
		else {

			for ( let i = 0; i < pads.length; i ++ ) if ( pads[ i ] ) { pad = pads[ i ]; break; }

		}

		if ( ! pad ) {

			if ( this.connected ) this.disconnect();
			return false;

		}

		this._pad = pad;
		this.index = pad.index != null ? pad.index : this.index;
		if ( ! this.connected ) {

			this.connected = true;
			this.id = pad.id || '';
			this.layout = detectLayout( this.id );

		}

		this._readAxes( pad );
		this._readButtons( pad );

		let active = false;
		for ( const n of BUTTON_NAMES ) if ( this.down.has( n ) ) { active = true; break; }

		if ( ! active ) for ( const k of Object.keys( this.axes ) ) if ( Math.abs( this.axes[ k ] ) > 0 ) { active = true; break; }

		this.active = active;
		return active;

	}

	_readAxes( pad ) {

		const a = pad.axes || [];
		const dz = this.deadzone;
		for ( const name of Object.keys( AXIS_INDEX ) ) {

			const raw = a[ AXIS_INDEX[ name ] ] || 0;
			this.axes[ name ] = applyDeadzone( raw, dz );

		}

		// triggers: the standard mapping reports them as buttons, but some pads put them on axes 4/5
		for ( const [ name, i ] of [ [ 'LT', 4 ], [ 'RT', 5 ] ] ) {

			const b = ( pad.buttons || [] )[ BUTTON_INDEX[ name ] ];
			if ( b ) this.triggers[ name ] = triggerValue( b.value );
			else this.triggers[ name ] = Math.max( 0, Math.min( 1, ( a[ i ] || 0 ) ) );

		}

	}

	_readButtons( pad ) {

		const bs = pad.buttons || [];
		this.down.clear();
		this.pressed.clear();
		this.released.clear();
		for ( const name of BUTTON_NAMES ) {

			const i = BUTTON_INDEX[ name ];
			const b = bs[ i ];
			const isTrigger = name === 'LT' || name === 'RT';
			const on = b ? ( isTrigger ? triggerValue( b.value ) > 0.5 : ( b.pressed === true || b.value > 0.5 ) ) : false;
			if ( on ) {

				this.down.add( name );
				if ( ! this._prev.has( name ) ) this.pressed.add( name );

			} else if ( this._prev.has( name ) ) this.released.add( name );

		}

		this._prev = new Set( this.down );

	}

	// a pad that went away: nothing may stay stuck down
	disconnect() {

		this.connected = false;
		this.active = false;
		this.index = - 1;
		this.id = '';
		this._pad = null;
		this.down.clear();
		this.pressed.clear();
		this.released.clear();
		this._prev.clear();
		for ( const k of Object.keys( this.axes ) ) this.axes[ k ] = 0;
		for ( const k of Object.keys( this.triggers ) ) this.triggers[ k ] = 0;

	}

	// ---- reading

	// a button name, or an axis/trigger name: `axis( 'RT' )`, `axis( 'LSY' )`, `down( 'A' )`
	axis( name ) {

		if ( name in this.axes ) return this.axes[ name ];
		if ( name in this.triggers ) return this.triggers[ name ];
		return this.down.has( name ) ? 1 : 0;

	}

	isDown( name ) {

		return this.down.has( name );

	}

	isPressed( name ) {

		return this.pressed.has( name );

	}

	endFrame() {

		this.pressed.clear();
		this.released.clear();

	}

	// ---- rumble

	// { strong, weak, ms }: magnitudes 0..1, either channel. No-op without the actuator, and pulses
	// closer together than the cooldown are dropped so a fight cannot rattle the pad continuously.
	rumble( { strong = 0, weak = 0, ms = 120, strength = 1, cooldown = 90 } = {} ) {

		const pad = this._pad;
		const now = typeof performance !== 'undefined' ? performance.now() : Date.now();
		if ( ! pad || ! pad.vibrationActuator || strength <= 0 ) return false;
		if ( now - this._rumbleAt < cooldown ) return false;
		this._rumbleAt = now;
		try {

			pad.vibrationActuator.playEffect( 'dual-rumble', {
				startDelay: 0,
				duration: Math.max( 20, ms ),
				weakMagnitude: clamp01( weak * strength ),
				strongMagnitude: clamp01( strong * strength ),
			} );
			return true;

		} catch ( e ) { return false; }

	}

}

function clamp01( v ) {

	return Math.max( 0, Math.min( 1, v ) );

}

// triggers report 0..1 in the standard mapping; ancient Chrome used -1..1
function triggerValue( v ) {

	const x = typeof v === 'number' ? v : 0;
	return x < 0 ? ( x + 1 ) / 2 : Math.min( 1, x );

}

// scaled deadzone: below the zone nothing, above it the value grows from 0 so a stick does not jump
export function applyDeadzone( v, dz ) {

	const x = typeof v === 'number' && Number.isFinite( v ) ? v : 0;
	const mag = Math.abs( x );
	if ( dz <= 0 || mag <= dz ) return 0;
	return Math.sign( x ) * Math.min( 1, ( mag - dz ) / ( 1 - dz ) );

}

export function detectLayout( id = '' ) {

	const s = String( id ).toLowerCase();
	// 054c is Sony's vendor id (Chrome prints "Vendor: 054c" for DualSense/DualShock). "Wireless
	// Controller" is deliberately *not* in this list: it is Chrome's generic name for Xbox pads too.
	if ( /dualsense|dualshock|playstation|ps[345]|054c/.test( s ) ) return 'ps';
	return 'xbox';

}
