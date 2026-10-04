import { FISH, FISH_IDS, fishValue, fishLengthCm } from './FishTable.js';
import { orderFor, orderMul } from './Orders.js';
import { emptyEntry, periodOf, CATCH_CAP } from './Codex.js';
import { defaultUpgrades, gearStats, nextLevel, UPGRADES, FUEL_PRICE, TRAP_PRICE, TRAP_LIMIT, BOATS, BOAT_IDS, START_BOATS } from './Gear.js';

const SAVE_KEY = 'tidewater.save.v1';

// Everything the player owns: wallet, the fish in the cooler / hold, the fish log and the gear
// levels. Saved to localStorage (per browser) after every change; storage can be missing or throw
// (private windows, blocked site data), so every access is guarded and the game runs without it.
export class GameState {

	constructor( storage = safeStorage() ) {

		this.storage = storage;
		this.money = 0;
		this.inventory = []; // { id, species, kg, cm, value, caughtAt (game hours), record }
		this.log = {}; // species -> { count, bestKg, bestCm, minKg, first, catches, habs, periods, sold, earned } (see Codex.emptyEntry)
		// the last addFish: { species, kg, cm, value, newSpecies, record, prevBestKg, prevBestCm, kept } (the catch card)
		this.lastCatch = null;
		this.upgrades = defaultUpgrades();
		this.boats = [ ...START_BOATS ]; // the boats you own (ids of Gear.BOATS), in BOAT_IDS order
		this.fuel = null; // litres left (null = full tank)
		// the world: which day it is, the time of day when the game was last saved, and the weather
		this.day = 1;
		this.clock = null;
		this.weather = null;
		// the trap line: traps aboard (bought, not yet set) and the ones fishing on the seabed
		this.traps = 0;
		this.sets = []; // { id, x, z, day, clock } — set at day * 24 + clock game hours
		// Joe's prices move with the day: { day, mul: { species: factor } }
		this.market = { day: 1, mul: {} };
		// Joe's order of the day (see Orders.js): { day, species, minKg, filled, bonus }, rolled on first look each day
		this.order = null;
		this._nextId = 1;
		this.listeners = new Set();

	}

	get stats() {

		return gearStats( this.upgrades );

	}

	get holdKg() {

		let kg = 0;
		for ( const f of this.inventory ) kg += f.kg;
		return kg;

	}

	get holdValue() {

		let v = 0;
		for ( const f of this.inventory ) v += this.priceOf( f );
		return v;

	}

	// ---- the market: what Joe is paying today
	// Prices are per species and per day, so holding a catch overnight is a real decision. Day 1
	// pays the standard rate (rollMarket leaves it neutral), and the roll is a pure function of the
	// day and the species, so it needs no random state in the save.

	mulFor( species ) {

		if ( this.market.day !== this.day ) this.rollMarket();
		return this.market.mul[ species ] ?? 1;

	}

	// ---- Joe's order: one species, a size or bigger, paid a multiplier all day (from day 2)

	get todaysOrder() {

		if ( ! this.order || this.order.day !== this.day ) {

			const o = orderFor( this.day, { lobster: this.mayTrap } );
			this.order = o ? { ...o, filled: 0, bonus: 0 } : null;

		}

		return this.order;

	}

	// ORDER_MULT when this fish fills today's order, else 1
	orderMulFor( f ) {

		return orderMul( this.todaysOrder, f );

	}

	// what a landed fish is worth at today's price and today's order (its own `value` is the standard price)
	priceOf( f ) {

		return Math.round( ( f.value ?? 0 ) * this.mulFor( f.species ) * this.orderMulFor( f ) );

	}

	rollMarket( day = this.day ) {

		const mul = {};
		for ( const id of FISH_IDS ) {

			if ( day <= 1 ) { mul[ id ] = 1; continue; }
			const x = Math.sin( day * 127.1 + ( hashStr( id ) % 977 ) ) * 43758.5453;
			const r = x - Math.floor( x ); // 0..1, same for this day and species every time
			mul[ id ] = Math.round( ( 0.75 + 0.6 * r ) * 20 ) / 20; // 0.75 .. 1.35 in 5% steps

		}

		this.market = { day, mul };
		return this.market;

	}

	// room in the cooler / hold for a fish of `kg`?
	fits( kg ) {

		return this.holdKg + kg <= this.stats.holdKg + 1e-6;

	}

	// store a caught fish; returns the entry, or null when the hold is full (it is logged either way).
	// A record beats an earlier catch of the species; the first one of a species is a new species.
	// where: { x, z, hab } the spot it came from (the bobber, or the pot), for the fish guide's map
	addFish( species, kg, timeOfDay = 12, where = null ) {

		kg = Math.round( kg * 100 ) / 100;
		const cm = Math.round( fishLengthCm( species, kg ) );
		const logEntry = this.entryFor( species );
		const newSpecies = logEntry.count === 0;
		const prevBestKg = logEntry.bestKg, prevBestCm = logEntry.bestCm ?? ( prevBestKg > 0 ? Math.round( fishLengthCm( species, prevBestKg ) ) : 0 );
		const record = ! newSpecies && kg > prevBestKg;
		logEntry.count ++;
		this.noteCatch( logEntry, kg, timeOfDay, where );
		if ( kg > prevBestKg ) {

			logEntry.bestKg = kg;
			logEntry.bestCm = cm;

		}

		const value = fishValue( species, kg );
		const kept = this.fits( kg );
		this.lastCatch = { species, kg, cm, value, newSpecies, record, prevBestKg, prevBestCm, kept };
		if ( ! kept ) {

			this.save();
			this.emit();
			return null;

		}

		const f = { id: this._nextId ++, species, kg, cm, value, caughtAt: timeOfDay, record };
		this.inventory.push( f );
		this.save();
		this.emit();
		return f;

	}

	// the species' log entry, made (with every field the fish guide reads) if it is the first
	entryFor( species ) {

		const e = this.log[ species ] || ( this.log[ species ] = emptyEntry() );
		// entries from before the guide have a count and a best, and gain the rest as they are used
		if ( ! Array.isArray( e.catches ) ) Object.assign( e, { ...emptyEntry(), ...e, catches: [] } );
		return e;

	}

	// the guide's memory of one catch: the lightest, the first, when (day and hour), where and in what water
	noteCatch( e, kg, timeOfDay, where ) {

		e.minKg = e.minKg > 0 ? Math.min( e.minKg, kg ) : kg;
		if ( ! e.first ) e.first = { day: this.day, hour: timeOfDay };
		const p = periodOf( timeOfDay );
		e.periods[ p ] = ( e.periods[ p ] || 0 ) + 1;
		if ( where && where.hab ) e.habs[ where.hab ] = ( e.habs[ where.hab ] || 0 ) + 1;
		const c = { kg, day: this.day, hour: Math.round( timeOfDay * 100 ) / 100 };
		if ( where && Number.isFinite( where.x ) && Number.isFinite( where.z ) ) { c.x = Math.round( where.x ); c.z = Math.round( where.z ); }
		if ( where && where.hab ) c.hab = where.hab;
		e.catches.push( c );
		if ( e.catches.length > CATCH_CAP ) e.catches.splice( 0, e.catches.length - CATCH_CAP );

	}

	// sell the given fish ids (all when omitted); returns the money made
	sell( ids = null ) {

		const keep = [], sold = [];
		for ( const f of this.inventory ) ( ids === null || ids.includes( f.id ) ? sold : keep ).push( f );
		let total = 0, bonus = 0, filled = 0;
		for ( const f of sold ) {

			const p = this.priceOf( f );
			total += p;
			const e = this.log[ f.species ] && this.entryFor( f.species ); // what the guide knows of its price
			if ( e ) { e.sold ++; e.earned += p; }
			if ( this.orderMulFor( f ) > 1 ) {

				filled ++;
				bonus += p - Math.round( ( f.value ?? 0 ) * this.mulFor( f.species ) ); // what the order added

			}

		}

		if ( filled ) {

			this.order.filled += filled;
			this.order.bonus += bonus;

		}

		this.inventory = keep;
		this.money += total;
		this.save();
		this.emit();
		return { total, count: sold.length, bonus, filled };

	}

	release( id ) {

		this.inventory = this.inventory.filter( ( f ) => f.id !== id );
		this.save();
		this.emit();

	}

	// spend money (upgrade shop); false when it can't be afforded
	spend( amount ) {

		if ( amount > this.money ) return false;
		this.money -= amount;
		this.save();
		this.emit();
		return true;

	}

	// buy the next level of an upgrade track; returns the new level entry or null
	buy( key ) {

		if ( ! UPGRADES[ key ] ) return null;
		if ( UPGRADES[ key ].boat && ! this.ownsBoat( UPGRADES[ key ].boat ) ) return null; // the lobster boat's gear needs the lobster boat
		const next = nextLevel( this.upgrades, key );
		if ( ! next || next.cost > this.money ) return null;
		this.money -= next.cost;
		this.upgrades[ key ] = next.index;
		if ( key === 'fuel' ) this.fuel = null; // a new tank comes full
		this.save();
		this.emit();
		return next;

	}

	ownsBoat( id ) {

		return this.boats.includes( id );

	}

	// buy a boat at the chandlery; returns its entry, or null (unknown, already yours, or not enough money)
	buyBoat( id ) {

		const b = BOATS[ id ];
		if ( ! b || this.ownsBoat( id ) || b.cost > this.money ) return null;
		this.money -= b.cost;
		this.boats = BOAT_IDS.filter( ( k ) => k === id || this.ownsBoat( k ) );
		this.save();
		this.emit();
		return b;

	}

	get fuelL() {

		return this.fuel === null ? this.stats.fuelL : Math.min( this.fuel, this.stats.fuelL );

	}

	// burn litres (no save: that happens when the boat stops or at the next sale / purchase)
	burn( litres ) {

		this.fuel = Math.max( 0, this.fuelL - litres );
		return this.fuel;

	}

	refuelCost() {

		return Math.ceil( ( this.stats.fuelL - this.fuelL ) * FUEL_PRICE );

	}

	// fill up as far as the money goes; returns litres bought
	refuel() {

		const missing = this.stats.fuelL - this.fuelL;
		const litres = Math.min( missing, Math.floor( this.money / FUEL_PRICE ) );
		if ( litres <= 0 ) return 0;
		this.money -= Math.ceil( litres * FUEL_PRICE );
		this.fuel = this.fuelL + litres;
		if ( this.fuel >= this.stats.fuelL - 1e-3 ) this.fuel = null;
		this.save();
		this.emit();
		return litres;

	}

	onChange( fn ) {

		this.listeners.add( fn );
		return () => this.listeners.delete( fn );

	}

	// ---- the trap line: buy traps, set them, haul them
	// A trap is stock until it goes over the side; setting one spends it and a set comes back aboard
	// when it is hauled, so a gear of traps is a fixed number of pots moving between the two.

	get trapsSet() {

		return this.sets.length;

	}

	get mayTrap() {

		return this.stats.trapLicence === true;

	}

	// buy traps (n at a time); false when they are not affordable
	buyTraps( n = 1 ) {

		const cost = TRAP_PRICE * n;
		if ( this.money < cost ) return false;
		this.money -= cost;
		this.traps = Math.min( this.traps + n, TRAP_LIMIT );
		this.save();
		this.emit();
		return true;

	}

	// put a trap over the side at (x, z); null when there are none aboard
	setTrap( x, z, timeOfDay ) {

		if ( this.traps <= 0 ) return null;
		this.traps --;
		const s = { id: this._nextId ++, x, z, day: this.day, clock: timeOfDay };
		this.sets.push( s );
		this.save();
		this.emit();
		return s;

	}

	// bring a trap back aboard; returns the set, or null when it is not there
	haulTrap( id ) {

		const i = this.sets.findIndex( ( s ) => s.id === id );
		if ( i < 0 ) return null;
		const [ s ] = this.sets.splice( i, 1 );
		this.traps = Math.min( this.traps + 1, TRAP_LIMIT );
		this.save();
		this.emit();
		return s;

	}

	// the set nearest to a point, or null when none is within `maxM`
	nearestSet( x, z, maxM = 12 ) {

		let best = null, bestD = maxM;
		for ( const s of this.sets ) {

			const d = Math.hypot( s.x - x, s.z - z );
			if ( d <= bestD ) { bestD = d; best = s; }

		}

		return best;

	}

	// ---- the world clock (written from the app each frame, saved with everything else)

	setClock( hours ) {

		if ( Number.isFinite( hours ) ) this.clock = hours;

	}

	setWeather( w ) {

		if ( w ) this.weather = w;

	}

	// midnight: a new day. Phase 2 re-rolls the fish market here.
	advanceDay() {

		this.day = ( this.day | 0 ) + 1;
		return this.day;

	}

	emit() {

		for ( const fn of this.listeners ) fn( this );

	}

	toJSON() {

		return { v: 1, money: this.money, inventory: this.inventory, log: this.log, upgrades: this.upgrades, fuel: this.fuel, nextId: this._nextId,
			day: this.day, clock: this.clock, weather: this.weather,
			traps: this.traps, sets: this.sets, market: this.market, order: this.order, boats: this.boats };

	}

	fromJSON( d ) {

		if ( ! d || d.v !== 1 ) return false;
		this.money = Number.isFinite( d.money ) ? d.money : 0;
		this.inventory = Array.isArray( d.inventory ) ? d.inventory.filter( ( f ) => f && FISH[ f.species ] && Number.isFinite( f.kg ) ) : [];
		// saves from before lengths were recorded
		for ( const f of this.inventory ) if ( ! Number.isFinite( f.cm ) ) f.cm = Math.round( fishLengthCm( f.species, f.kg ) );
		this.log = d.log && typeof d.log === 'object' ? d.log : {};
		for ( const [ k, v ] of Object.entries( this.log ) ) if ( FISH[ k ] && v && v.bestKg > 0 && ! Number.isFinite( v.bestCm ) ) v.bestCm = Math.round( fishLengthCm( k, v.bestKg ) );
		for ( const k of Object.keys( this.log ) ) {

			if ( ! FISH[ k ] || ! this.log[ k ] || typeof this.log[ k ] !== 'object' ) { delete this.log[ k ]; continue; }
			this.log[ k ] = sanitizeEntry( this.log[ k ] );

		}
		this.upgrades = { ...defaultUpgrades(), ...( d.upgrades || {} ) };
		this.fuel = Number.isFinite( d.fuel ) ? d.fuel : null;
		// saves from before the world clock existed simply start on day 1 at the default time
		this.day = Number.isFinite( d.day ) && d.day >= 1 ? Math.floor( d.day ) : 1;
		this.clock = Number.isFinite( d.clock ) ? d.clock : null;
		this.weather = d.weather && typeof d.weather === 'object' ? d.weather : null;
		// the trap line and the market (saves from before traps simply have none out)
		this.traps = Number.isFinite( d.traps ) && d.traps > 0 ? Math.floor( d.traps ) : 0;
		this.sets = Array.isArray( d.sets ) ? d.sets.filter( ( s ) => s && Number.isFinite( s.x ) && Number.isFinite( s.z ) && Number.isFinite( s.day ) && Number.isFinite( s.clock ) ) : [];
		this.market = d.market && typeof d.market === 'object' && d.market.mul && Number.isFinite( d.market.day )
			? { day: d.market.day, mul: d.market.mul } : { day: this.day, mul: {} };
		// today's order, if the save has one (a stale day is simply rolled afresh by the next look)
		const o = d.order;
		this.order = o && typeof o === 'object' && Number.isFinite( o.day ) && FISH[ o.species ] && Number.isFinite( o.minKg )
			? { day: o.day, species: o.species, minKg: o.minKg, filled: o.filled | 0, bonus: Number.isFinite( o.bonus ) ? o.bonus : 0 } : null;
		// the boats you own: a save from before boats were sold owns them all (nothing is taken away); an unknown id is dropped
		const owned = Array.isArray( d.boats ) ? BOAT_IDS.filter( ( k ) => d.boats.includes( k ) ) : [ ...BOAT_IDS ];
		this.boats = owned.length ? owned : [ ...START_BOATS ];
		this._nextId = Math.max( d.nextId | 0, ...this.inventory.map( ( f ) => f.id + 1 ), ...this.sets.map( ( s ) => ( s.id | 0 ) + 1 ), 1 );
		return true;

	}

	save() {

		if ( ! this.storage ) return;
		try {

			this.storage.setItem( SAVE_KEY, JSON.stringify( this.toJSON() ) );

		} catch ( e ) { /* storage full or blocked: keep playing */ }

	}

	load() {

		if ( ! this.storage ) return false;
		try {

			const raw = this.storage.getItem( SAVE_KEY );
			return raw ? this.fromJSON( JSON.parse( raw ) ) : false;

		} catch ( e ) {

			return false;

		}

	}

	reset() {

		this.money = 0;
		this.inventory = [];
		this.log = {};
		this.upgrades = defaultUpgrades();
		this.boats = [ ...START_BOATS ];
		this.fuel = null;
		this.day = 1;
		this.clock = null;
		this.weather = null;
		this.traps = 0;
		this.sets = [];
		this.market = { day: 1, mul: {} };
		this.order = null;
		this.save();
		this.emit();

	}

}

// a saved log entry with every field the guide reads, clamped to what makes sense (a hand-edited or older save)
function sanitizeEntry( v ) {

	const num = ( x, d = 0 ) => Number.isFinite( x ) && x >= 0 ? x : d;
	const e = { ...emptyEntry(), ...v };
	e.count = Math.floor( num( v.count ) );
	e.bestKg = num( v.bestKg );
	e.minKg = num( v.minKg );
	e.sold = Math.floor( num( v.sold ) );
	e.earned = num( v.earned );
	e.first = v.first && Number.isFinite( v.first.day ) && Number.isFinite( v.first.hour ) ? { day: v.first.day, hour: v.first.hour } : null;
	const counts = ( o ) => Object.fromEntries( Object.entries( o && typeof o === 'object' ? o : {} ).filter( ( [ , n ] ) => Number.isFinite( n ) && n > 0 ) );
	e.habs = counts( v.habs );
	e.periods = counts( v.periods );
	e.catches = ( Array.isArray( v.catches ) ? v.catches : [] ).filter( ( c ) => c && Number.isFinite( c.kg ) && Number.isFinite( c.day ) && Number.isFinite( c.hour ) ).slice( - CATCH_CAP );
	return e;

}

// FNV-1a of a species id: the market roll needs a stable number per name, not a random seed
function hashStr( s ) {

	let h = 2166136261;
	for ( let i = 0; i < s.length; i ++ ) {

		h ^= s.charCodeAt( i );
		h = Math.imul( h, 16777619 );

	}

	return h >>> 0;

}

function safeStorage() {

	try {

		return typeof localStorage !== 'undefined' ? localStorage : null;

	} catch ( e ) {

		return null;

	}

}
