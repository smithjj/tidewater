// The weather: a slow walk up and down the sea-state ladder on in-game time, so the sea turns
// over a couple of times a day and a blow is something you can see coming. Purely atmospheric —
// nothing here can damage the player, the boat or the gear.
//
// Cadence (see ocean/Conditions.js for why): the light uniforms follow a fractional level
// smoothly, while the wave spectrum and the cloud cover are only rebuilt when the level crosses
// a whole step. A spectrum rebuild clears the foam buffer and a coverage change drops the clouds'
// temporal history, so both stay rare — one write per step, no more than a settings preset click.
import { CONDITIONS, SEA, conditionAt, writeConditions } from '../ocean/Conditions.js';

const LIGHT_WRITE = 0.25; // real seconds between light-uniform writes
const STEP_HOURS = 0.35; // in-game hours to cross one step of the ladder
const HOLD = [ 0.45, 1.7 ]; // in-game hours a condition holds before the next pick

export class Weather {

	constructor( app ) {

		this.app = app;
		this.refs = { fft: app.fft, shore: app.shore, clouds: app.clouds };
		this.enabled = true;
		this.pace = 1;
		this.level = 1; // Breezy
		this.target = 1;
		this.seed = ( Math.random() * 1e9 ) | 0;
		this._rnd = mulberry32( this.seed );
		this.windDir = this.refs.fft.local.windDirection;
		this._dirBase = this.windDir;
		this._dirTarget = this.windDir;
		this._hold = this._nextHold();
		this._step = 1; // last whole step written to the spectrum
		this._t = 0;
		this.onAnnounce = null; // ( text ) => void, set by the app: the game layer toasts it
		// match the sea to the level from the very first frame (nothing else applies a preset at
		// startup, which otherwise leaves the wind on the water disagreeing with the waves)
		this.write( { spectrum: true } );

	}

	// the condition to show in the HUD
	get name() {

		return CONDITIONS[ Math.max( 0, Math.min( CONDITIONS.length - 1, Math.round( this.level ) ) ) ];

	}

	update( dt ) {

		if ( ! this.enabled ) return;
		// weather runs on in-game time: pausing the clock holds the weather too
		const hours = Math.max( 0, dt * this.app.settings.timeSpeed * this.pace );
		if ( hours > 0 ) {

			const k = 1 - Math.exp( - hours / STEP_HOURS );
			this.level += ( this.target - this.level ) * k;
			if ( Math.abs( this.target - this.level ) < 0.015 ) this.level = this.target;
			this.windDir += ( this._dirTarget - this.windDir ) * k;
			this._hold -= hours;
			if ( this._hold <= 0 ) this._pick();

		}

		// a whole step: the wave field and the sky step with it
		const step = Math.round( this.level );
		if ( step !== this._step ) {

			this._step = step;
			this.write( { spectrum: true } );
			return;

		}

		this._t += dt;
		if ( this._t >= LIGHT_WRITE ) {

			this._t = 0;
			this.write( { spectrum: false } );

		}

	}

	write( { spectrum } ) {

		const v = conditionAt( this.level, this.windDir );
		// the spectrum inputs (and the cover that goes with them) come from the whole step
		if ( spectrum ) Object.assign( v, SEA[ CONDITIONS[ this._step ] ], { windDir: this.windDir } );
		writeConditions( this.refs, v, { spectrum } );

	}

	// pick the next condition: the island's own rhythm, calm mornings and storms rare
	_pick() {

		const hour = this.app.settings.timeOfDay;
		const w = [ 0, 1, 2, 3 ].map( ( l ) => this._weight( l, hour ) * ( l === this._step ? 0.55 : 1 ) );
		let r = this._rnd() * ( w[ 0 ] + w[ 1 ] + w[ 2 ] + w[ 3 ] );
		let next = 1;
		for ( let l = 0; l < CONDITIONS.length; l ++ ) {

			r -= w[ l ];
			if ( r <= 0 ) { next = l; break; }

		}

		const rise = next - this._step;
		this.target = next;
		this._hold = this._nextHold();
		// the wind backs or veers a few degrees as the weather turns
		this._dirTarget = this._dirBase + ( this._rnd() * 2 - 1 ) * 22;
		if ( this.onAnnounce && Math.abs( rise ) >= 2 ) this.onAnnounce( rise > 0
			? 'The wind\'s backing — it\'s blowing up out there'
			: 'The wind\'s dropping away' );

	}

	_weight( level, hour ) {

		switch ( level ) {

			case 0: return hour < 11 ? 3 : 1.1;
			case 1: return 3;
			case 2: return hour >= 10 && hour < 20 ? 1.7 : 0.7;
			default: return hour >= 11 && hour < 19 ? 0.14 : 0.02;

		}

	}

	_nextHold() {

		return HOLD[ 0 ] + this._rnd() * ( HOLD[ 1 ] - HOLD[ 0 ] );

	}

	setEnabled( on ) {

		this.enabled = !! on;
		if ( this.app.ui ) {

			this.app.ui.s.dynamic = this.enabled;
			this.app.ui.ui.refresh();

		}

	}

	// a manual change to the sea controls hands the wheel back to the player
	setManual() {

		if ( ! this.enabled ) return false;
		this.setEnabled( false );
		return true;

	}

	state() {

		return { enabled: this.enabled, pace: this.pace, seed: this.seed, level: this.level,
			target: this.target, hold: this._hold, windDir: this.windDir, dirBase: this._dirBase };

	}

	restore( d ) {

		if ( ! d ) return;
		if ( typeof d.enabled === 'boolean' ) this.enabled = d.enabled;
		if ( Number.isFinite( d.pace ) ) this.pace = d.pace;
		if ( Number.isFinite( d.seed ) ) { this.seed = d.seed | 0; this._rnd = mulberry32( this.seed ); }
		if ( Number.isFinite( d.level ) ) this.level = d.level;
		if ( Number.isFinite( d.target ) ) this.target = d.target;
		if ( Number.isFinite( d.hold ) ) this._hold = d.hold;
		if ( Number.isFinite( d.windDir ) ) { this.windDir = d.windDir; this._dirTarget = d.windDir; }
		if ( Number.isFinite( d.dirBase ) ) this._dirBase = d.dirBase;
		this._step = Math.round( this.level );
		this.write( { spectrum: true } );

	}

}

function mulberry32( seed ) {

	let a = seed >>> 0;
	return function () {

		a = ( a + 0x6D2B79F5 ) >>> 0;
		let t = a;
		t = Math.imul( t ^ ( t >>> 15 ), t | 1 );
		t ^= t + Math.imul( t ^ ( t >>> 7 ), t | 61 );
		return ( ( t ^ ( t >>> 14 ) ) >>> 0 ) / 4294967296;

	};

}
