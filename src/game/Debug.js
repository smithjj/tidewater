// Console helpers for poking the game while it runs: `window.__tw`.
//
// The world is already reachable as window.__app, but a run of quick experiments wants shorter
// names and a few things that are otherwise several calls (aging a pot, re-rolling the market,
// landing a catch). Everything here goes through the game's own methods where they exist, so what
// you see is what the player would see: the HUD updates, the save is written, the market moves.
//
//   __tw.money()               what you have
//   __tw.money( 5000 )         set it (saves, and the purse updates)
//   __tw.add( 250 )            add to it (negative takes away)
//   __tw.day() / __tw.day( 3 ) the day (re-rolls the market for that day)
//   __tw.hour() / __tw.hour( 7.5 )   the clock, 0..24
//   __tw.weather( 'Storm' )    jump the sea state to a rung of the ladder (or null for the name)
//   __tw.fish( 'lobster', 1.4 )      land a catch (cooler, log and catch card)
//   __tw.traps() / __tw.traps( 6 )   pots aboard
//   __tw.setTrap()             set a pot where the boat is (or at x, z)
//   __tw.haul( id? )           haul a pot (the nearest, or that one)
//   __tw.clearTraps()          pull the whole gear out of the water and back aboard
//   __tw.soak( hours )         age every pot in the water by that many hours
//   __tw.help()                this list
//
// The objects themselves are here too: __tw.state, __tw.game, __tw.app, __tw.trapLine.
import { CONDITIONS, SEA, writeConditions } from '../ocean/Conditions.js';
import { FISH } from './FishTable.js';
import { TRAP_LIMIT } from './Gear.js';

export function installDebugGame( app ) {

	const g = () => app.game;
	const s = () => app.game.state;
	const tw = {
		app, game: app.game, get state() {

			return app.game.state;

		}, get trapLine() {

			return app.game.traps; // the Traps instance (pots, buoys, the haul animation)

		},

		money( n ) {

			const st = s();
			if ( n === undefined ) return st.money;
			st.money = Math.max( 0, Math.round( n ) );
			st.save();
			st.emit();
			return st.money;

		},

		add( n ) {

			return tw.money( s().money + n );

		},

		day( n ) {

			const st = s();
			if ( n === undefined ) return st.day;
			st.day = Math.max( 1, Math.floor( n ) );
			st.rollMarket( st.day ); // the day's prices follow the day
			st.save();
			st.emit();
			return st.day;

		},

		hour( h ) {

			if ( h === undefined ) return app.settings.timeOfDay;
			app.settings.timeOfDay = ( ( h % 24 ) + 24 ) % 24;
			return app.settings.timeOfDay;

		},

		// sea state: a rung of the ladder, or the name of the one we are on
		weather( name ) {

			if ( name === undefined ) return app.weather ? app.weather.name : null;
			const i = CONDITIONS.indexOf( name );
			if ( i < 0 ) return CONDITIONS;
			if ( app.weather ) {

				app.weather.enabled = true;
				app.weather.level = i;
				app.weather.target = i;
				app.weather._step = i; // the rung we are standing on, so the write uses its own values
				app.weather.write( { spectrum: true } );

			} else {

				writeConditions( { fft: app.fft, shore: app.shore, clouds: app.clouds }, { ...SEA[ name ], windDir: app.fft.local.windDirection } );

			}

			return name;

		},

		// land a catch the way the rod would: cooler, log, records and the catch card
		fish( species, kg = null ) {

			const f = FISH[ species ];
			if ( ! f ) return Object.keys( FISH );
			const weight = kg ?? Math.round( ( f.kg[ 0 ] + f.kg[ 1 ] ) * 0.5 * 100 ) / 100;
			const entry = s().addFish( species, weight, app.settings.timeOfDay );
			if ( g().hud ) g().hud.showCatch( s().lastCatch, 7000 );
			return entry;

		},

		traps( n ) {

			if ( n === undefined ) return { aboard: s().traps, set: s().sets.length };
			s().traps = Math.max( 0, Math.min( TRAP_LIMIT, Math.floor( n ) ) );
			s().save();
			s().emit();
			return s().traps;

		},

		setTrap( x = null, z = null ) {

			const set = g().setTrap( x, z );
			return set;

		},

		haul( id = null ) {

			if ( id === null ) return g().haulTrap();
			const set = s().sets.find( ( x ) => x.id === id );
			return set ? g().haulTrap( set ) : null; // a bad id hauls nothing, it does not pick one

		},

		clearTraps() {

			const st = s();
			const n = st.sets.length;
			st.sets = [];
			st.traps = Math.min( st.traps + n, TRAP_LIMIT );
			st.save();
			st.emit();
			return n;

		},

		// age the gear on the water: the trap line runs on the world clock (soak = now - set), so
		// this moves each pot further into the past, which is the shortcut for "come back later"
		soak( hours ) {

			const st = s();
			st.clock = Number.isFinite( st.clock ) ? st.clock : app.settings.timeOfDay;
			for ( const set of st.sets ) {

				const at = set.day * 24 + set.clock - hours;
				set.day = Math.floor( at / 24 );
				set.clock = at - set.day * 24;

			}

			st.save();
			st.emit();
			return st.sets.length;

		},

		help() {

			const out = [
				'__tw.money( [n] )        wallet (set / read)',
				'__tw.add( n )            add to it',
				'__tw.day( [n] )          day (re-rolls the market)',
				'__tw.hour( [h] )         clock 0..24',
				`__tw.weather( [name] )   ${ CONDITIONS.join( ' | ' ) }`,
				'__tw.fish( species, kg? )  land a catch',
				'__tw.traps( [n] )        pots aboard',
				'__tw.setTrap( x?, z? )   set a pot (boat position by default)',
				'__tw.haul( [id] )        haul the nearest pot',
				'__tw.clearTraps()        pull the gear out of the water',
				'__tw.soak( hours )       age every pot in the water',
				'__tw.state / .game / .app / .trapLine',
			];
			console.log( out.join( '\n' ) );
			return out;

		},
	};
	window.__tw = tw;
	return tw;

}
