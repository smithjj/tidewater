// Plain-node tests of the fishing game logic (no GPU): bites, the catch fight, inventory, save.
import { FISH, FISH_IDS, fishValue, fishLengthCm } from '../src/game/FishTable.js';
import { habitatAt, pickSpecies, rollWeight, biteDelay } from '../src/game/Bites.js';
import { CatchMinigame } from '../src/game/CatchMinigame.js';
import { GameState } from '../src/game/GameState.js';
import { gearStats, defaultUpgrades, UPGRADES, BOATS, BOAT_IDS, START_BOATS } from '../src/game/Gear.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};
let seed = 12345;
const rng = () => ( ( seed = ( seed * 1664525 + 1013904223 ) >>> 0 ) / 4294967296 );

// ---- habitats and species
const spots = {
	sand: { depth: 0, reefDist: 200, pierDist: 200 },
	shallows: { depth: 1.5, reefDist: 200, pierDist: 80 },
	pier: { depth: 4, reefDist: 150, pierDist: 1 },
	reef: { depth: 5, reefDist: - 10, pierDist: 150 },
	deep: { depth: 40, reefDist: 300, pierDist: 400 },
};
for ( const [ name, s ] of Object.entries( spots ) ) {

	const h = habitatAt( s );
	const counts = {};
	for ( let i = 0; i < 2000; i ++ ) {

		const id = pickSpecies( h, 12, rng );
		if ( id ) counts[ id ] = ( counts[ id ] || 0 ) + 1;

	}

	const top = Object.entries( counts ).sort( ( a, b ) => b[ 1 ] - a[ 1 ] ).slice( 0, 5 ).map( ( [ k, v ] ) => `${ k } ${ ( v / 20 ).toFixed( 0 ) }%` ).join( ', ' );
	console.log( `     ${ name }: delay ~${ biteDelay( h, 12, () => 0.5 ).toFixed( 1 ) } s; ${ top || 'nothing' }` );

}
ok( pickSpecies( habitatAt( spots.sand ), 12, rng ) === null, 'nothing bites on dry sand' );
ok( biteDelay( habitatAt( spots.sand ), 12, rng ) === Infinity, 'no bite delay on sand' );
{

	let deepOnly = 0;
	for ( let i = 0; i < 500; i ++ ) if ( [ 'tuna', 'mahi', 'redSnapper', 'grouper', 'barracuda' ].includes( pickSpecies( habitatAt( spots.deep ), 12, rng ) ) ) deepOnly ++;
	ok( deepOnly === 500, 'deep water gives offshore species only' );
	let reefFish = 0;
	for ( let i = 0; i < 500; i ++ ) if ( FISH[ pickSpecies( habitatAt( spots.reef ), 12, rng ) ].habitat.reef ) reefFish ++;
	ok( reefFish > 400, `the reef gives mostly reef fish (${ reefFish / 5 }%)` );

}
{

	let tarponNight = 0, tarponDay = 0;
	for ( let i = 0; i < 4000; i ++ ) {

		if ( pickSpecies( habitatAt( spots.pier ), 22, rng ) === 'tarpon' ) tarponNight ++;
		if ( pickSpecies( habitatAt( spots.pier ), 12, rng ) === 'tarpon' ) tarponDay ++;

	}

	ok( tarponNight > tarponDay * 2, `tarpon bite at night (${ tarponNight } vs ${ tarponDay } by day)` );

}
for ( const id of FISH_IDS ) {

	const w = rollWeight( id, rng );
	if ( w < FISH[ id ].kg[ 0 ] || w > FISH[ id ].kg[ 1 ] ) ok( false, `weight in range for ${ id }` );

}
ok( fishValue( 'redSnapper', 5 ) > fishValue( 'redSnapper', 2 ), 'bigger fish is worth more' );

// ---- the fight: three players
const policies = {
	careful: ( g ) => g.tension < 0.68 && g.surge < 0.6,
	mash: () => true,
	idle: () => false,
};
const fight = ( species, kg, policy, lineKg = 7, reelSpeed = 1.1 ) => {

	const g = new CatchMinigame( { species, kg, lineKg, reelSpeed, distance: 18, rng } );
	let st = 'fighting';
	for ( let i = 0; i < 60 * 180 && st === 'fighting'; i ++ ) st = g.update( 1 / 60, policy( g ) );
	return { st, t: g.time };

};
const table = {};
for ( const [ species, kg ] of [ [ 'grunt', 0.8 ], [ 'yellowtail', 1.2 ], [ 'jack', 6 ], [ 'redSnapper', 5 ], [ 'tuna', 6 ], [ 'tuna', 13 ], [ 'tarpon', 35 ] ] ) {

	for ( const p of Object.keys( policies ) ) {

		const r = fight( species, kg, policies[ p ] );
		table[ `${ species }/${ p }` ] = r;
		console.log( `     ${ species } ${ kg } kg, ${ p }: ${ r.st } after ${ r.t.toFixed( 1 ) } s` );

	}

}
ok( table[ 'grunt/careful' ].st === 'caught' && table[ 'yellowtail/careful' ].st === 'caught', 'careful reeling lands small fish' );
ok( table[ 'jack/careful' ].st === 'caught', 'careful reeling lands a 6 kg jack on the starter line' );
ok( table[ 'tuna/mash' ].st === 'snapped' && table[ 'tarpon/mash' ].st === 'snapped', 'holding reel on a big fish snaps the line' );
ok( table[ 'tuna/careful' ].st !== 'caught' && fight( 'tuna', 13, policies.careful, 26, 1.6 ).st === 'caught', 'a 13 kg tuna needs the 30 lb line' );
ok( [ 'escaped' ].includes( table[ 'grunt/idle' ].st ), 'never reeling loses the fish' );
ok( fight( 'tarpon', 35, policies.careful ).st !== 'caught', 'a 35 kg tarpon beats the starter line' );
ok( fight( 'tarpon', 35, policies.careful, 50, 2.2 ).st === 'caught', 'the top line and reel land it' );

// ---- inventory, wallet, save round trip
const mem = new Map();
const storage = { getItem: ( k ) => mem.get( k ) ?? null, setItem: ( k, v ) => mem.set( k, v ) };
const s = new GameState( storage );
ok( s.stats.holdKg === 30, 'cooler holds 30 kg' );
const a = s.addFish( 'grunt', 0.84, 9.5 );
const b = s.addFish( 'yellowtail', 1.31, 10 );
ok( a && b && s.inventory.length === 2, 'fish go into the cooler' );
ok( s.addFish( 'tarpon', 40, 22 ) === null && s.log.tarpon.count === 1, 'a fish too big for the hold is logged but not kept' );
const value = s.holdValue;
const sale = s.sell( [ a.id ] );
ok( sale.count === 1 && s.money === a.value && s.inventory.length === 1, 'selling one fish pays for it' );
s.upgrades.hold = 1;
const s2 = new GameState( storage );
s.save();
ok( s2.load() && s2.money === s.money && s2.inventory.length === 1 && s2.log.grunt.bestKg === 0.84 && s2.stats.holdKg === 70, 'save / load round trip' );
ok( s2.addFish( 'grunt', 0.5 ).id > b.id, 'ids keep counting after a load' );
// ---- lengths and the catch card's record logic
{

	let sane = true;
	for ( const id of FISH_IDS ) {

		const f = FISH[ id ];
		const lo = fishLengthCm( id, f.kg[ 0 ] ), hi = fishLengthCm( id, f.kg[ 1 ] );
		if ( ! ( lo > 5 && hi < 200 && hi > lo && typeof f.sci === 'string' ) ) sane = false;

	}

	ok( sane, 'every species has a scientific name and a plausible, increasing length (5–200 cm)' );
	ok( Math.abs( fishLengthCm( 'mahi', 10 ) - 108 ) < 3 && Math.abs( fishLengthCm( 'grunt', 0.84 ) - 36 ) < 2, 'length-weight: a 10 kg mahi ~108 cm, a 0.84 kg grunt ~36 cm' );
	const m = new Map();
	const st = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	const c1 = st.addFish( 'jack', 3.2 ), i1 = st.lastCatch;
	ok( c1 && i1.newSpecies && ! i1.record && c1.cm === i1.cm && i1.cm > 50, 'first of a species: new species, not a record, length stored' );
	st.addFish( 'jack', 2.1 );
	const i2 = st.lastCatch;
	ok( ! i2.newSpecies && ! i2.record && i2.prevBestKg === 3.2 && st.log.jack.bestKg === 3.2, 'a smaller one: no record, best unchanged' );
	st.addFish( 'jack', 4.05 );
	const i3 = st.lastCatch;
	ok( i3.record && i3.prevBestKg === 3.2 && i3.prevBestCm === i1.cm && st.log.jack.bestKg === 4.05 && st.log.jack.bestCm === i3.cm, 'a bigger one: new record, previous best reported, log updated' );
	st.addFish( 'tarpon', 40 );
	ok( st.lastCatch.kept === false && st.lastCatch.newSpecies, 'a fish that does not fit: logged, card says released' );
	// a save from before lengths: inventory and log get lengths on load
	const old = { v: 1, money: 5, inventory: [ { id: 1, species: 'grunt', kg: 0.84, value: 6, caughtAt: 9 } ], log: { grunt: { count: 1, bestKg: 0.84 } }, upgrades: {}, fuel: null, nextId: 2 };
	const st2 = new GameState( { getItem: () => JSON.stringify( old ), setItem: () => {} } );
	ok( st2.load() && st2.inventory[ 0 ].cm === 36 && st2.log.grunt.bestCm === 36, 'old saves load with lengths filled in' );

}
ok( new GameState( { getItem: () => { throw new Error( 'blocked' ); }, setItem: () => { throw new Error( 'blocked' ); } } ).load() === false, 'blocked storage does not throw' );
ok( s.spend( 1e9 ) === false, 'cannot spend more than you have' );
ok( Object.keys( defaultUpgrades() ).length === Object.keys( UPGRADES ).length && gearStats( defaultUpgrades() ).finder === false, 'gear stats' );
{

	const m = new Map();
	const st = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	st.money = 100;
	ok( st.buy( 'reel' ) && st.upgrades.reel === 1 && st.money === 10 && st.stats.reelSpeed === 1.6, 'buying the reel upgrade' );
	ok( st.buy( 'reel' ) === null && st.upgrades.reel === 1, 'cannot buy what you cannot afford' );
	ok( st.fuelL === 40 && st.burn( 30 ) === 10 && st.refuelCost() === 45, 'fuel burns and costs to refill' );
	st.money = 20;
	ok( st.refuel() === 13 && st.money === 0 && Math.abs( st.fuelL - 23 ) < 1e-6, 'refuel stops when the money runs out' );
	st.money = 1000;
	ok( st.buy( 'fuel' ) && st.fuelL === 80, 'a new tank comes full' );
	ok( st.buy( 'fishFinder' ) && st.stats.finder === true && st.buy( 'fishFinder' ) === null, 'fish finder: one level' );

}
// ---- boats: you start with the mini, buy the others at the chandlery
{

	const m = new Map();
	const mk = () => new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	const st = mk();
	ok( START_BOATS.length === 1 && START_BOATS[ 0 ] === 'mini' && st.ownsBoat( 'mini' ) && ! st.ownsBoat( 'lobster' ) && ! st.ownsBoat( 'pelagic' ), 'a new game owns the mini only' );
	st.money = 899;
	ok( st.buyBoat( 'lobster' ) === null && st.money === 899 && ! st.ownsBoat( 'lobster' ), 'the lobster boat cannot be bought for $899' );
	ok( st.buyBoat( 'nope' ) === null && st.buyBoat( 'mini' ) === null, 'an unknown boat, or one you own, cannot be bought' );
	st.money = 3000;
	ok( st.buyBoat( 'pelagic' ) === BOATS.pelagic && st.money === 500 && st.ownsBoat( 'pelagic' ) && ! st.ownsBoat( 'lobster' ), 'buying the Pelagic 30 takes its price' );
	ok( st.buyBoat( 'pelagic' ) === null && st.money === 500, 'a boat is bought once' );
	ok( st.boats.join() === 'mini,pelagic', 'owned boats stay in the fixed order' );
	// the lobster boat's own gear needs the lobster boat
	st.money = 5000;
	ok( st.buy( 'engine' ) === null && st.buy( 'lights' ) === null && st.buy( 'trapLicence' ) === null && st.money === 5000, 'engine, lights and the trap licence need the lobster boat' );
	ok( st.buyBoat( 'lobster' ) && st.buy( 'engine' ) && st.buy( 'lights' ) && st.buy( 'trapLicence' ) && st.mayTrap, 'and are for sale once you have it' );
	const back = mk();
	ok( back.load() && back.boats.join() === 'mini,lobster,pelagic' && back.upgrades.engine === 1, 'the boats come back from the save' );
	// a save from before boats were sold owns all three; a hand-edited list is cleaned up; an empty one falls back to the mini
	const legacy = new GameState( { getItem: () => JSON.stringify( { v: 1, money: 5, inventory: [], log: {}, upgrades: {}, fuel: null, nextId: 1 } ), setItem: () => {} } );
	ok( legacy.load() && legacy.boats.join() === BOAT_IDS.join(), 'an older save keeps all three boats' );
	const odd = new GameState( { getItem: () => JSON.stringify( { v: 1, boats: [ 'pelagic', 'dinghy', 'pelagic' ] } ), setItem: () => {} } );
	ok( odd.load() && odd.boats.join() === 'pelagic', 'unknown boat ids are dropped' );
	const none = new GameState( { getItem: () => JSON.stringify( { v: 1, boats: [] } ), setItem: () => {} } );
	ok( none.load() && none.boats.join() === 'mini', 'an empty list falls back to the mini' );
	st.reset();
	ok( st.boats.join() === 'mini', 'a reset goes back to the mini' );

}
// ---- the island day: the world clock in the save, and shop hours
import { Vendor } from '../src/game/Vendor.js';
import { CONDITIONS, SEA, conditionAt } from '../src/ocean/Conditions.js';
import { Weather } from '../src/world/Weather.js';
{
	// the clock, the day and the weather ride in the save
	const m = new Map();
	const st = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	st.advanceDay();
	st.setClock( 5.5 );
	st.setWeather( { level: 2, target: 2 } );
	st.save();
	const st2 = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	ok( st2.load() && st2.day === 2 && st2.clock === 5.5 && st2.weather.level === 2, 'day, clock and weather survive a save / load' );
	// a save from before the clock existed: day 1, no clock, weather fresh
	const before = { v: 1, money: 3, inventory: [], log: {}, upgrades: {}, fuel: null, nextId: 1 };
	const st3 = new GameState( { getItem: () => JSON.stringify( before ), setItem: () => {} } );
	ok( st3.load() && st3.day === 1 && st3.clock === null && st3.weather === null, 'older saves start on day 1 with no clock' );
	const st4 = new GameState( { getItem: () => JSON.stringify( { ...before, day: 0, clock: 'noon' } ), setItem: () => {} } );
	ok( st4.load() && st4.day === 1 && st4.clock === null, 'a nonsense day / clock in a save falls back' );

	// island hours, including one that wraps midnight
	const joe = Vendor.prototype.openAt.bind( { hours: [ 6, 19 ] } );
	ok( joe( 5.99 ) === false && joe( 6 ) === true && joe( 18.99 ) === true && joe( 19 ) === false, 'shop hours: open 6 am, shut at 7 pm' );
	const night = Vendor.prototype.openAt.bind( { hours: [ 22, 6 ] } );
	ok( night( 23 ) === true && night( 2 ) === true && night( 7 ) === false, 'shop hours wrap midnight' );
	ok( Vendor.prototype.openAt.call( { hours: null }, 3 ) === true, 'a vendor with no hours is always open' );
}
{
	// The weather walks the ladder on in-game time. The wave field follows the *fractional* level so the
	// sea drifts continuously (the foam is kept), and only a jump — a load — clears the foam. Cloud cover
	// is the one thing still written on a whole step (a coverage change drops the clouds' history).
	const cov = { _v: 0.45, writes: 0, get value() { return this._v; }, set value( x ) { this._v = x; this.writes ++; } };
	const fft = {
		local: { windSpeed: 7, windDirection: 25, fetch: 120 }, swell: { scale: 0.48 },
		choppiness: { value: 0.9 }, foamBias: { value: 0.5 }, foamDecay: { value: 0.6 },
		spectra: 0, foamResets: 0,
		updateSpectrumUniforms( { resetFoam = true } = {} ) { this.spectra ++; if ( resetFoam ) this.foamResets ++; this.needsSpectrum = true; },
	};
	const app = {
		fft, shore: { amplitude: { value: 0.34 }, period: { value: 9 } }, clouds: { coverage: cov },
		settings: { timeOfDay: 9, timeSpeed: 0.02 }, ui: null,
	};
	const w = new Weather( app );
	ok( fft.spectra === 1 && fft.foamResets === 1, 'the weather applies the sea state once at startup, clearing the foam' );
	// a day at 60 fps: 1200 real seconds
	let bad = 0, moved = 0, picks = 0, prevTarget = w.target, prevFetch = fft.local.fetch, prevHold = w._hold, biggest = 0;
	const coverWrites = () => cov.writes;
	for ( let i = 0; i < 60 * 1200; i ++ ) {

		w.update( 1 / 60 );
		// a pick resets the hold upward (it counts down otherwise): the weather's *decision* rate, which
		// is what "not constantly" is about (a pick may legitimately decide to stay on the same rung)
		if ( w._hold > prevHold ) picks ++;
		prevHold = w._hold;
		if ( ! ( w.level >= 0 && w.level <= CONDITIONS.length - 1 ) || ! Number.isFinite( w.level ) ) bad ++;
		if ( ! Number.isFinite( fft.local.windSpeed ) || ! Number.isFinite( fft.foamBias.value ) || ! Number.isFinite( app.shore.amplitude.value ) ) bad ++;
		if ( ! Number.isFinite( cov.value ) || cov.value < 0 || cov.value > 1 ) bad ++;
		if ( w.target !== prevTarget ) { moved ++; prevTarget = w.target; }
		const d = Math.abs( fft.local.fetch - prevFetch );
		if ( d > biggest ) biggest = d;
		prevFetch = fft.local.fetch;

	}
	ok( bad === 0, 'a full day of weather stays on the ladder with no NaN anywhere' );
	// a couple of decisions a day, not one every few seconds (the step used to be 0.35 in-game hours)
	ok( picks >= 2 && picks <= 14, `the weather decides a few times a day, not constantly (${ picks } picks, ${ moved } of them a change of rung)` );
	// the whole sea is written every frame (the values are smooth in time; a slower cadence steps them,
	// see the header of world/Weather.js), and the *smoothing* is what keeps the water from stepping
	ok( fft.spectra > 70000 && fft.spectra <= 72001, `the sea is written every frame, not on a slow cadence (${ fft.spectra } writes in a day of 72000 frames)` );
	// the point of the whole cadence: the sea drifts, it does not step. Fetch spans 40..900 m; the
	// biggest single write over a whole day must be a small fraction of that (it used to be a whole rung)
	ok( biggest < 90, `the sea drifts rather than lurching (biggest single change in fetch: ${ biggest.toFixed( 1 ) } km)` );
	// ...and drifting must not scrub the foam off the water
	ok( fft.foamResets === 1, 'the drifting writes never clear the foam (only the startup jump did)' );
	// the cloud cover is the one thing that still rides on a whole step: about one write per rung crossed
	// (a couple per weather change), nowhere near the ~4800 the sea writes
	ok( coverWrites() >= 1 && coverWrites() < 60, `the cloud cover is written on whole steps only (${ coverWrites() } writes against ${ fft.spectra } for the sea)` );
	// the written spectrum is the interpolated level, not the nearest rung
	w.level = 2.5;
	w.write( { spectrum: true, resetFoam: false, cover: false } );
	const want = conditionAt( 2.5, w.windDir );
	ok( Math.abs( fft.local.windSpeed - want.wind ) < 1e-6 && Math.abs( fft.swell.scale - want.swell ) < 1e-6,
		`a fractional level writes the interpolated sea, not the rung (wind ${ fft.local.windSpeed.toFixed( 2 ) } m/s vs the rung ${ SEA.Choppy.wind })` );
	ok( CONDITIONS.includes( w.name ), 'the HUD gets a real condition name' );
	// a load is a jump: it clears the foam
	const resets = fft.foamResets;
	w.restore( w.state() );
	ok( fft.foamResets === resets + 1, 'loading a saved sea clears the foam (a jump, not a drift)' );
	// pausing the clock holds the weather
	const held = w.level;
	app.settings.timeSpeed = 0;
	for ( let i = 0; i < 60 * 120; i ++ ) w.update( 1 / 60 );
	ok( w.level === held, 'a paused clock holds the weather' );
	// the level never leaves the ladder, whichever preset the interpolation is asked for
	for ( let l = 0; l <= 3; l += 0.1 ) {

		const v = conditionAt( l, 90 );
		if ( ! ( v.wind >= SEA.Calm.wind - 1e-6 && v.wind <= SEA.Storm.wind + 1e-6 ) ) bad ++;

	}
	ok( bad === 0, 'interpolated conditions stay between the calmest and the worst preset' );
}
// ---- the trap line and the market
import { soakHours, haulYield, SOAK_MIN, MAX_KEEP } from '../src/game/Traps.js';
import { TRAP_PRICE, TRAP_LIMIT } from '../src/game/Gear.js';
{
	// traps soak on the world clock (day * 24 + hour), across midnight and across days
	ok( soakHours( { day: 1, clock: 20 }, { day: 1, hour: 21.5 } ) === 1.5, 'soak: an hour and a half' );
	ok( soakHours( { day: 1, clock: 20 }, { day: 2, hour: 6 } ) === 10, 'soak: across midnight' );
	ok( soakHours( { day: 2, clock: 6 }, { day: 1, hour: 20 } ) === 0, 'soak never goes negative' );

	const bay = { shallows: 0.2, reef: 0.3, pier: 0, bay: 1, deep: 0 };
	const sand = { shallows: 1, reef: 0, pier: 0, bay: 0, deep: 0 };
	ok( haulYield( { soak: 0.5, depth: 10, habitat: bay, hour: 12 } ).length === 0, 'too soon: the pot is empty' );
	// a long soak on good ground fills up, and the animals are all real, in-range catches
	let n = 0, lobsters = 0, offRange = 0, overCap = 0;
	for ( let i = 0; i < 600; i ++ ) {

		const y = haulYield( { soak: 14, depth: 12, habitat: bay, hour: 12 } );
		n += y.length;
		if ( y.length > MAX_KEEP ) overCap ++;
		for ( const a of y ) {

			if ( a.species === 'lobster' ) lobsters ++;
			const f = FISH[ a.species ];
			if ( ! f || ! ( a.kg >= f.kg[ 0 ] - 1e-9 && a.kg <= f.kg[ 1 ] + 1e-9 ) ) offRange ++;

		}

	}
	ok( offRange === 0, 'every animal hauled is a real species at a weight it can be' );
	ok( overCap === 0 && n / 600 > 2, `a long soak fills the pot (${ ( n / 600 ).toFixed( 2 ) } animals)` );
	ok( lobsters / n > 0.5, `lobsters prefer the deeper ground (${ Math.round( 100 * lobsters / n ) }%)` );
	// a pot on the sand shallows is a poor bet for lobster but still fishes
	let shallow = 0;
	for ( let i = 0; i < 600; i ++ ) shallow += haulYield( { soak: 6, depth: 1.2, habitat: sand, hour: 12 } ).length;
	ok( shallow / 600 < n / 600, `shallow sand hauls less than the bay (${ ( shallow / 600 ).toFixed( 2 ) })` );
	// the lobster is a trap catch only: it must never bite a rod, in any water, at any hour
	let bitten = false;
	for ( const [ name, spot ] of Object.entries( spots ) ) for ( const hour of [ 3, 7, 13, 19, 22 ] )
		for ( let i = 0; i < 600; i ++ ) if ( pickSpecies( habitatAt( spot ), hour, rng ) === 'lobster' ) bitten = true;
	ok( ! bitten, 'a lobster never takes a hook' );
	// and the weighted pick never falls through to a species with no weight at all
	const onlyGrunt = { pier: 1, shallows: 0, reef: 0, bay: 0, deep: 0 };
	const high = () => 0.999999;
	ok( pickSpecies( onlyGrunt, 12, high ) !== 'lobster', 'the fall-through picks a species that fishes there' );
}
{
	// the market: standard on day 1, moving after that, and what Joe pays follows it
	const m = new Map();
	const st = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	st.money = 0;
	const f = st.addFish( 'grunt', 1.0 );
	ok( st.mulFor( 'grunt' ) === 1 && st.priceOf( f ) === f.value, 'day 1 pays the standard rate' );
	ok( st.holdValue === f.value, 'the cooler is worth the standard rate on day 1' );
	const sold = st.sell( [ f.id ] );
	ok( sold.total === f.value && st.money === f.value, 'selling on day 1 pays the standard rate' );

	st.advanceDay();
	st.advanceDay();
	st.rollMarket();
	st.day = 3;
	const f2 = st.addFish( 'grunt', 1.0 );
	const m2 = st.mulFor( 'grunt' );
	ok( m2 >= 0.75 && m2 <= 1.35 && Math.abs( m2 * 20 - Math.round( m2 * 20 ) ) < 1e-6, `day 3 moves the price in 5% steps (${ m2 })` );
	ok( st.priceOf( f2 ) === Math.round( f2.value * m2 ), 'a fish is worth today\'s rate' );
	ok( st.rollMarket().mul.grunt === m2, 'the roll is the same every time for a given day' );
	const before = st.money;
	const s2 = st.sell( [ f2.id ] );
	ok( s2.total === st.priceOf( { ...f2 } ) || s2.total === Math.round( f2.value * m2 ), 'selling pays today\'s rate' );
	ok( st.money === before + s2.total, 'the money matches the sale' );
	// over a stretch of days the market actually moves around
	const seen = new Set();
	for ( let d = 2; d < 30; d ++ ) seen.add( st.rollMarket( d ).mul.grunt );
	ok( seen.size > 5, `prices vary across days (${ seen.size } different rates in a month)` );
	ok( [ ... seen ].every( ( v ) => v >= 0.75 && v <= 1.35 ), 'and stay inside the band' );
}
{
	// the gear of pots: buy, set, haul, and all of it in the save
	const m = new Map();
	const store = { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) };
	const st = new GameState( store );
	st.money = 1000;
	ok( st.mayTrap === false, 'no trap licence, no trapping' );
	ok( st.buyTraps( 1 ) && st.traps === 1 && st.money === 1000 - TRAP_PRICE, 'buying a pot' );
	st.money = 0;
	ok( st.buyTraps( 1 ) === false && st.traps === 1, 'cannot buy what you cannot afford' );
	st.upgrades.trapLicence = 1;
	ok( st.mayTrap === true, 'the licence unlocks the trap line' );
	const set = st.setTrap( - 20, 140, 9.5 );
	ok( set && st.sets.length === 1 && st.traps === 0, 'setting a pot spends it' );
	ok( st.nearestSet( - 22, 141, 12 ) === set && st.nearestSet( 0, 0, 12 ) === null, 'the nearest set within reach' );
	ok( st.setTrap( 0, 0, 10 ) === null, 'no pots aboard, nothing to set' );
	ok( st.haulTrap( set.id ) === set && st.sets.length === 0 && st.traps === 1, 'hauling brings the pot back aboard' );
	ok( st.haulTrap( set.id ) === null, 'a pot cannot be hauled twice' );
	// a full gear of pots is the limit
	st.traps = TRAP_LIMIT;
	for ( let i = 0; i < TRAP_LIMIT; i ++ ) st.setTrap( i * 10, 200, 12 );
	ok( st.sets.length === TRAP_LIMIT, `${ TRAP_LIMIT } pots in the water` );

	// round trip: the line, the market and the day all come back
	st.setClock( 7.25 );
	st.save();
	const st2 = new GameState( store );
	ok( st2.load() && st2.sets.length === TRAP_LIMIT && st2.sets[ 0 ].x === 0 && st2.sets[ 0 ].clock === 12, 'the trap line survives a save' );
	ok( st2.traps === 0 && st2.mulFor( 'grunt' ) === st.mulFor( 'grunt' ), 'the stock and the market survive too' );
	// saves from before the trap line
	const before2 = { v: 1, money: 3, inventory: [], log: {}, upgrades: {}, fuel: null, nextId: 1 };
	const st3 = new GameState( { getItem: () => JSON.stringify( before2 ), setItem: () => {} } );
	ok( st3.load() && st3.traps === 0 && st3.sets.length === 0 && st3.mulFor( 'grunt' ) === 1, 'older saves start with no pots out' );
}
// ---- what E does on the boat's deck: the prompt decides between setting and hauling (at the helm it is
// the cast button instead, and E leaves the helm: see test/trap-handling.mjs)
import { Game } from '../src/game/Game.js';
import { Vector3 as V3 } from '../src/engine/index.js';
{
	const m = new Map();
	const st = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	st.money = 0;
	st.upgrades.trapLicence = 1;
	st.traps = 3;
	// a boat heading +z: a pot goes in 0.9 m astern of its 3.9 m transom, 4.8 m behind its position
	const boat = {
		position: { x: - 20, z: 140 }, speed: 0, model: { sternZ: - 3.9 },
		toWorld( l, out ) { out.x = l.x + this.position.x; out.y = l.y; out.z = l.z + this.position.z; return out; },
	};
	const toasts = [];
	const game = Object.create( Game.prototype );
	Object.assign( game, {
		state: st,
		_haulCard: 0,
		_modeChanged: false,
		_tmp: new V3(),
		hud: null,
		app: {
			lobsterCtl: boat,
			player: { boat, mode: 'deck', position: boat.position },
			settings: { timeOfDay: 9 }, // game.hour reads this
			terrainData: { heightAt: () => - 12 }, // 12 m of water everywhere
			audio: null,
		},
		traps: { haulVisual() {}, setVisual() { return true; }, stack: [ 0, 1, 2, 3 ], busy: false },
		habitatAtPoint: () => ( { bay: 1, shallows: 0, reef: 0, pier: 0, deep: 0 } ),
		toast: ( t ) => toasts.push( t ),
	} );
	const p = game.app.player;
	const E = { actHit: ( id ) => id === 'interact' }; // E / A on the action layer
	const act = () => { const pr = game.trapPrompt( p ); return pr && pr.act; };
	const press = () => game.updateTraps( E, p );

	ok( act() === 'set', 'clear water: E offers to set' );
	press();
	ok( st.sets.length === 1 && st.traps === 2, 'E puts a pot over the side' );
	ok( Math.abs( st.sets[ 0 ].x - - 20 ) < 1e-9 && Math.abs( st.sets[ 0 ].z - ( 140 - 3.9 - 0.9 ) ) < 1e-9, 'over the stern, astern of the transom' );
	// the pot is now 4.8 m astern, inside haul range: E must not take it back
	ok( act() === 'set', 'with the pot just set astern, E still offers to set the next one' );
	press();
	ok( st.sets.length === 1 && st.traps === 2, 'pressing E again does not pick the pot back up' );
	ok( toasts.some( ( t ) => /already a pot here/.test( t ) ), 'it says the pot is already there' );
	// drifting along the line, still inside the 12 m haul range of the last pot
	boat.position.x = - 10;
	ok( act() === 'set', '10 m along, E keeps laying the line' );
	press();
	ok( st.sets.length === 2 && st.traps === 1, 'the next pot goes in beside the first' );
	boat.position.x = - 8.4; // 1.6 m from the pot at -10 m: inside the set guard
	press();
	ok( st.sets.length === 2 && st.traps === 1, 'too close to set, E refuses rather than hauling' );

	// come back later and the same pots read as hauls
	game.app.settings.timeOfDay = 20; // set at 09:00, so 11 h on the bottom
	boat.position.x = - 10;
	ok( act() === 'haul', 'a soaked pot is a haul' );
	const animals = game.haulTrap();
	ok( Array.isArray( animals ) && animals.length > 0, 'hauling a soaked pot brings animals up' );
	ok( st.sets.length === 1 && st.traps === 2, 'and takes it out of the water' );
	ok( act() === 'haul', 'the pot left behind is a haul too, from 10 m away' );

	// nothing left to set: E recovers the pot under the boat instead of doing nothing
	st.traps = 1;
	boat.position.x = - 40;
	ok( act() === 'set', 'clear water again: E offers to set' );
	press();
	ok( st.sets.length === 2 && st.traps === 0, 'the last pot goes in' );
	ok( act() === 'haul', 'with no pots left to set, E offers to take the one under the boat back' );
	ok( game.haulTrap().length === 0 && st.sets.length === 1, 'hauling a pot set moments ago comes up empty' );
	ok( toasts.some( ( t ) => /Nothing yet/.test( t ) ), 'and says so' );
}
// ---- the console helpers (window.__tw): a stubbed window and app, the real state
import { installDebugGame } from '../src/game/Debug.js';
{
	globalThis.window = {};
	const m = new Map();
	const state = new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );
	state.money = 10;
	let setTrapArgs = null, hauled = null;
	const fft = { local: { windSpeed: 7, windDirection: 25, fetch: 120 }, swell: { scale: 0.48 },
		choppiness: { value: 0.9 }, foamBias: { value: 0.5 }, foamDecay: { value: 0.6 }, updateSpectrumUniforms() {} };
	const app = {
		settings: { timeOfDay: 12, timeSpeed: 0 },
		fft, shore: { amplitude: { value: 0.34 }, period: { value: 9 } }, clouds: { coverage: { value: 0.45 } },
		weather: null,
		game: {
			state, hud: null,
			traps: { pot: null, stack: [], loadModels() {} },
			// the real haulTrap takes the pot out of the water; both it and the real game find the
			// nearest pot themselves when they are not handed one
			setTrap( x, z ) { setTrapArgs = [ x, z ]; return state.setTrap( - 30, 160, app.settings.timeOfDay ); },
			haulTrap( set ) {

				const pot = set || state.sets[ 0 ] || null;
				if ( ! pot ) return [];
				state.haulTrap( pot.id );
				hauled = pot;
				return [ { species: 'lobster', kg: 1.2 } ];

			},
		},
	};
	const tw = installDebugGame( app );
	ok( globalThis.window.__tw === tw && tw.state === state, 'the console helpers install on window' );
	ok( tw.money() === 10 && tw.money( 5000 ) === 5000 && state.money === 5000, 'money sets and reads' );
	ok( JSON.parse( m.get( 'tidewater.save.v1' ) ).money === 5000, 'setting money saves it' );
	ok( tw.add( - 4000 ) === 1000 && tw.money( - 50 ) === 0, 'add and a negative floor at zero' );
	ok( tw.day( 5 ) === 5 && state.day === 5, 'the day can be jumped' );
	ok( tw.day() === 5 && tw.hour( 6.5 ) === 6.5 && app.settings.timeOfDay === 6.5, 'hour sets and reads' );
	ok( tw.hour( 30 ) === 6, 'the hour wraps into a day' );
	ok( tw.weather() === null && JSON.stringify( tw.weather( 'nope' ) ) === JSON.stringify( CONDITIONS ), 'an unknown condition lists the ladder' );
	// with a weather system present it takes the level
	let wrote = 0;
	app.weather = { name: 'Breezy', level: 1, target: 1, _step: 1, enabled: false, write() { wrote ++; } };
	ok( tw.weather( 'Storm' ) === 'Storm' && app.weather.level === 3 && app.weather.target === 3 && app.weather.enabled === true, 'weather jumps the ladder' );
	ok( wrote === 1 && tw.weather() === 'Breezy', 'jumping the weather writes the conditions once' );
	// with no weather system at all it writes the sea state straight through
	const before = fft.local.windSpeed;
	app.weather = null;
	tw.weather( 'Calm' );
	ok( fft.local.windSpeed === 3.5 && before === 7, 'without a weather system it writes the preset directly' );
	// traps: stock, set, haul, soak, clear
	ok( tw.traps( 3 ) === 3 && tw.traps().aboard === 3, 'traps sets the stock' );
	ok( tw.traps( 99 ) === TRAP_LIMIT && tw.traps( - 5 ) === 0, 'the stock is clamped to the licence maximum' );
	tw.traps( 3 );
	const set = tw.setTrap();
	ok( !! set && setTrapArgs !== null, 'setTrap goes through the game' );
	ok( tw.traps().set === 1 && tw.traps().aboard === 2, 'setting one moves it into the water' );
	// soak ages a pot: its soak grows by exactly that many hours, whatever the clock says
	const was = soakHours( state.sets[ 0 ], { day: state.day, hour: app.settings.timeOfDay } );
	ok( tw.soak( 9 ) === 1 && Math.abs( soakHours( state.sets[ 0 ], { day: state.day, hour: app.settings.timeOfDay } ) - ( was + 9 ) ) < 1e-9, 'soak ages the gear by game hours' );
	ok( ( tw.haul() || [] )[ 0 ].species === 'lobster' && hauled !== null, 'haul goes through the game' );
	ok( state.sets.length === 0 && tw.traps().aboard === 3, 'hauling brings the pot back aboard' );
	ok( tw.haul( 9999 ) === null, 'a haul by a bad id brings up nothing rather than guessing' );
	// a second pot, by id, and only once
	tw.traps( 3 );
	const second = tw.setTrap();
	ok( ( tw.haul( second.id ) || [] ).length === 1, 'hauling a pot by id' );
	ok( tw.haul( second.id ) === null, 'the same pot cannot be hauled twice' );
	tw.traps( 3 );
	tw.setTrap();
	ok( tw.clearTraps() === 1 && state.sets.length === 0 && state.traps === 3, 'clearTraps empties the water' );
	// fish lands a catch in the cooler (no HUD here, so no card)
	const caught = tw.fish( 'grunt' );
	ok( caught && FISH[ caught.species ].kg[ 0 ] <= caught.kg && caught.kg <= FISH[ caught.species ].kg[ 1 ], 'fish lands a catch of a sensible size' );
	ok( Array.isArray( tw.fish( 'nope' ) ), 'an unknown species lists them instead of guessing' );
	ok( Array.isArray( tw.help() ) && tw.help().length > 8, 'help lists the commands' );
}
console.log( `value check ${ value }` );
console.log( fails ? `${ fails } FAILED` : 'all passed' );
process.exit( fails ? 1 : 0 );
