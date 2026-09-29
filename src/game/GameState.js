import { FISH, FISH_IDS, fishValue, fishLengthCm } from './FishTable.js';
import { defaultUpgrades, gearStats, nextLevel, UPGRADES, FUEL_PRICE, TRAP_PRICE, TRAP_LIMIT } from './Gear.js';

const SAVE_KEY = 'tidewater.save.v1';

// Everything the player owns: wallet, the fish in the cooler / hold, the fish log and the gear
// levels. Saved to localStorage (per browser) after every change; storage can be missing or throw
// (private windows, blocked site data), so every access is guarded and the game runs without it.
export class GameState {

	constructor( storage = safeStorage() ) {

		this.storage = storage;
		this.money = 0;
		this.inventory = []; // { id, species, kg, cm, value, caughtAt (game hours), record }
		this.log = {}; // species -> { count, bestKg, bestCm }
		// the last addFish: { species, kg, cm, value, newSpecies, record, prevBestKg, prevBestCm, kept } (the catch card)
		this.lastCatch = null;
		this.upgrades = defaultUpgrades();
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

	// what a landed fish is worth at today's price (its own `value` is the standard price)
	priceOf( f ) {

		return Math.round( ( f.value ?? 0 ) * this.mulFor( f.species ) );

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
	addFish( species, kg, timeOfDay = 12 ) {

		kg = Math.round( kg * 100 ) / 100;
		const cm = Math.round( fishLengthCm( species, kg ) );
		const logEntry = this.log[ species ] || ( this.log[ species ] = { count: 0, bestKg: 0 } );
		const newSpecies = logEntry.count === 0;
		const prevBestKg = logEntry.bestKg, prevBestCm = logEntry.bestCm ?? ( prevBestKg > 0 ? Math.round( fishLengthCm( species, prevBestKg ) ) : 0 );
		const record = ! newSpecies && kg > prevBestKg;
		logEntry.count ++;
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

	// sell the given fish ids (all when omitted); returns the money made
	sell( ids = null ) {

		const keep = [], sold = [];
		for ( const f of this.inventory ) ( ids === null || ids.includes( f.id ) ? sold : keep ).push( f );
		let total = 0;
		for ( const f of sold ) total += this.priceOf( f );
		this.inventory = keep;
		this.money += total;
		this.save();
		this.emit();
		return { total, count: sold.length };

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
		const next = nextLevel( this.upgrades, key );
		if ( ! next || next.cost > this.money ) return null;
		this.money -= next.cost;
		this.upgrades[ key ] = next.index;
		if ( key === 'fuel' ) this.fuel = null; // a new tank comes full
		this.save();
		this.emit();
		return next;

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
			traps: this.traps, sets: this.sets, market: this.market };

	}

	fromJSON( d ) {

		if ( ! d || d.v !== 1 ) return false;
		this.money = Number.isFinite( d.money ) ? d.money : 0;
		this.inventory = Array.isArray( d.inventory ) ? d.inventory.filter( ( f ) => f && FISH[ f.species ] && Number.isFinite( f.kg ) ) : [];
		// saves from before lengths were recorded
		for ( const f of this.inventory ) if ( ! Number.isFinite( f.cm ) ) f.cm = Math.round( fishLengthCm( f.species, f.kg ) );
		this.log = d.log && typeof d.log === 'object' ? d.log : {};
		for ( const [ k, v ] of Object.entries( this.log ) ) if ( FISH[ k ] && v && v.bestKg > 0 && ! Number.isFinite( v.bestCm ) ) v.bestCm = Math.round( fishLengthCm( k, v.bestKg ) );
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
		this.fuel = null;
		this.day = 1;
		this.clock = null;
		this.weather = null;
		this.traps = 0;
		this.sets = [];
		this.market = { day: 1, mul: {} };
		this.save();
		this.emit();

	}

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
