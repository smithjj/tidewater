// The weather: a slow walk up and down the sea-state ladder on in-game time, so the sea turns over a
// couple of times a day and a blow is something you can see coming. Purely atmospheric — nothing here
// can damage the player, the boat or the gear. A day is 24 in-game hours (20 real minutes), so the
// constants below are in in-game hours and the real-time figures in the comments are at the default
// pace: one rung takes about two minutes of real time, and a condition then holds for three to ten.
//
// Cadence (see ocean/Conditions.js): the wave field follows the *fractional* level, so it drifts
// with the weather instead of stepping up a rung at a time, and it keeps the foam while it does
// (only a jump clears that). Cloud cover is the one thing still written on a whole step, because
// changing it drops the volumetric clouds' temporal history.
import { CONDITIONS, conditionAt, writeConditions } from '../ocean/Conditions.js';

const STEP_HOURS = 2.2; // in-game hours to cross one step of the ladder (~110 s of real time)
const HOLD = [ 3.5, 12 ]; // in-game hours a condition holds before the next pick (~3 to 10 real minutes)

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

		// a whole step: only the cloud cover rides on it (the sea itself drifts with the level)
		const step = Math.round( this.level );
		const crossed = step !== this._step;
		if ( crossed ) this._step = step;

		{
			// The whole sea state is written every frame, not on a slow cadence. The level is already
			// smooth in time, but a 0.25 s cadence still *steps* every value it carries: the whitecap
			// edges (foamBias/foamDecay), the crest folding (choppiness), the wind the surface detail and
			// the streaks follow, and the surf height. Four jumps a second of a per cent or so each reads
			// as the water flickering, even though the wave heights themselves are smooth. Writing every
			// frame costs a handful of uniform values and one spectrum install, which the field eases
			// towards anyway (see OceanFFT.smoothH0Kernel).
			this.write( { spectrum: true, resetFoam: false, cover: crossed } );

		}

	}

	// `spectrum`: rebuild the wave field from the current level (see the header). `resetFoam` is for a
	// jump — a load — where the accumulated foam belongs to the old sea.
	write( { spectrum, resetFoam = true, cover = spectrum } ) {

		const v = conditionAt( this.level, this.windDir );
		writeConditions( this.refs, v, { spectrum, resetFoam, cover } );

	}

	// pick the next condition: the island's own rhythm, calm mornings and storms rare
	_pick() {

		const hour = this.app.settings.timeOfDay;
		// the next condition: nearby levels are likely and a bigger swing is rare (a turn in the weather
		// should be something that happens to you now and then, not every time)
		const away = ( l ) => {

			const d = Math.abs( l - this._step );
			return d === 0 ? 0.5 : d === 1 ? 1 : d === 2 ? 0.22 : 0.06;

		};
		const w = [ 0, 1, 2, 3 ].map( ( l ) => this._weight( l, hour ) * away( l ) );
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
