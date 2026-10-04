// Dumps the economy rules of the JS game (src/game: FishTable, Gear, Orders, Codex, GameState) for the Unity oracle (Editor/EconomyOracle.cs):
//   node unity/tools/dump-economy.mjs unity/Temp/oracle/economy
// A seeded script of ~700 operations (catches, sales, purchases, fuel, traps, day changes) is run on a GameState with an in-memory store; the file holds the
// operations, the result of each, the scalars after each, a full save snapshot every 25 operations and at the end, the market rolls and Joe's orders for
// days 1..400, and the guide's knowledge of every species at every level.
import fs from 'fs';
import path from 'path';
import { GameState } from '../../src/game/GameState.js';
import { FISH_IDS, fishValue, fishLengthCm } from '../../src/game/FishTable.js';
import { orderFor, orderPool, fmtKg } from '../../src/game/Orders.js';
import { knowledge, sizeText, priceText, mapBox, coarsen, habitatGrid, periodOf } from '../../src/game/Codex.js';
import { UPGRADES, BOAT_IDS } from '../../src/game/Gear.js';

const out = process.argv[ 2 ] || 'unity/Temp/oracle/economy';
fs.mkdirSync( out, { recursive: true } );

let seed = 12345;
const rnd = () => ( seed = ( seed * 16807 ) % 2147483647 ) / 2147483647;
const mem = new Map();
const store = { getItem: ( k ) => mem.has( k ) ? mem.get( k ) : null, setItem: ( k, v ) => mem.set( k, v ) };
const s = new GameState( store );

const keys = Object.keys( UPGRADES );
const habs = [ 'shallows', 'reef', 'pier', 'bay', 'deep', null ];
const ops = [], results = [], scalars = [], snaps = {};
const sc = () => ( { money: s.money, fuelL: s.fuelL, holdKg: s.holdKg, holdValue: s.holdValue, day: s.day, traps: s.traps, sets: s.sets.length, mayTrap: s.mayTrap, inv: s.inventory.length, boats: s.boats.join() } );
const slim = ( f ) => f && { id: f.id, species: f.species, kg: f.kg, cm: f.cm, value: f.value, caughtAt: f.caughtAt, record: f.record };

function run( o ) {

	ops.push( o );
	let r = null;
	switch ( o.op ) {

		case 'money': s.money = o.v; break;
		case 'day': s.day = o.v; break;
		case 'advanceDay': r = s.advanceDay(); break;
		case 'clock': s.setClock( o.v ); break;
		case 'addFish': r = { f: slim( s.addFish( o.species, o.kg, o.hour, o.where ) ), last: s.lastCatch }; break;
		case 'sell': r = s.sell( o.ids ); break;
		case 'release': s.release( o.id ); break;
		case 'buy': { const n = s.buy( o.key ); r = n && { index: n.index, cost: n.cost, label: n.label }; break; }
		case 'refuel': r = s.refuel(); break;
		case 'burn': r = s.burn( o.v ); break;
		case 'refuelCost': r = s.refuelCost(); break;
		case 'spend': r = s.spend( o.v ); break;
		case 'buyBoat': { const n = s.buyBoat( o.id ); r = n && { name: n.name, cost: n.cost }; break; }
		case 'buyTraps': r = s.buyTraps( o.n ); break;
		case 'setTrap': r = s.setTrap( o.x, o.z, o.hour ); break;
		case 'haulTrap': r = s.haulTrap( o.id ); break;
		case 'nearestSet': { const n = s.nearestSet( o.x, o.z, o.max ); r = n && n.id; break; }
		case 'priceOfInv': r = s.inventory.map( ( f ) => s.priceOf( f ) ); break;
		case 'mulFor': r = FISH_IDS.map( ( id ) => s.mulFor( id ) ); break;
		case 'order': r = s.todaysOrder; break;
		case 'reload': { const t = new GameState( store ); t.load(); r = t.toJSON(); break; }

	}

	results.push( r );
	scalars.push( sc() );
	if ( ops.length % 25 === 0 ) snaps[ ops.length ] = JSON.parse( JSON.stringify( s.toJSON() ) ); // a copy: toJSON hands out the live arrays

}

const pick = ( a ) => a[ Math.floor( rnd() * a.length ) ];
run( { op: 'money', v: 40 } );
for ( let i = 0; i < 700; i ++ ) {

	const r = rnd();
	if ( r < 0.34 ) {

		const id = pick( FISH_IDS ), f = FISH_IDS.includes( id ) && ( await import( '../../src/game/FishTable.js' ) ).FISH[ id ];
		const kg = f.kg[ 0 ] + ( f.kg[ 1 ] - f.kg[ 0 ] ) * Math.pow( rnd(), 1.6 ) * ( rnd() < 0.03 ? 1.3 : 1 );
		const hour = Math.round( rnd() * 2400 ) / 100;
		const h = pick( habs );
		const where = rnd() < 0.6 ? { x: rnd() * 800 - 400, z: rnd() * 800 - 600, hab: h } : null;
		run( { op: 'addFish', species: id, kg, hour, where: where && ( h === null ? { x: where.x, z: where.z } : where ) } );

	} else if ( r < 0.46 ) {

		const ids = rnd() < 0.5 ? null : s.inventory.filter( () => rnd() < 0.5 ).map( ( f ) => f.id );
		run( { op: 'sell', ids } );

	} else if ( r < 0.50 && s.inventory.length ) run( { op: 'release', id: pick( s.inventory ).id } );
	else if ( r < 0.56 ) run( { op: 'buy', key: pick( keys ) } );
	else if ( r < 0.58 ) {

		// a boat (now and then an unknown id, or a boat already owned); usually with the money to pay for it
		const id = rnd() < 0.08 ? 'dinghy' : pick( BOAT_IDS );
		if ( rnd() < 0.7 ) run( { op: 'money', v: Math.round( s.money + 900 + rnd() * 2000 ) } );
		run( { op: 'buyBoat', id } );

	}
	else if ( r < 0.64 ) run( { op: 'burn', v: rnd() * 9 } );
	else if ( r < 0.69 ) run( { op: 'refuel' } );
	else if ( r < 0.71 ) run( { op: 'refuelCost' } );
	else if ( r < 0.74 ) run( { op: 'buyTraps', n: 1 + Math.floor( rnd() * 3 ) } );
	else if ( r < 0.78 ) run( { op: 'setTrap', x: rnd() * 500 - 250, z: rnd() * 500 - 400, hour: Math.round( rnd() * 2400 ) / 100 } );
	else if ( r < 0.81 && s.sets.length ) run( { op: 'haulTrap', id: pick( s.sets ).id } );
	else if ( r < 0.83 ) run( { op: 'nearestSet', x: rnd() * 500 - 250, z: rnd() * 500 - 400, max: 12 + Math.floor( rnd() * 200 ) } );
	else if ( r < 0.88 ) run( { op: 'advanceDay' } );
	else if ( r < 0.90 ) run( { op: 'money', v: Math.round( s.money + rnd() * 400 ) } );
	else if ( r < 0.92 ) run( { op: 'clock', v: rnd() * 24 } );
	else if ( r < 0.94 ) run( { op: 'spend', v: Math.round( rnd() * 80 ) } );
	else if ( r < 0.96 ) run( { op: 'priceOfInv' } );
	else if ( r < 0.98 ) run( { op: 'mulFor' } );
	else if ( r < 0.99 ) run( { op: 'order' } );
	else run( { op: 'reload' } );

}

run( { op: 'reload' } );
snaps.final = JSON.parse( JSON.stringify( s.toJSON() ) );

// the market rolls and Joe's orders, day by day
const market = {}, orders = [];
for ( const d of [ 1, 2, 3, 7, 30, 99, 365 ] ) { const t = new GameState( null ); t.day = d; market[ d ] = FISH_IDS.map( ( id ) => t.mulFor( id ) ); }
for ( let d = 1; d <= 400; d ++ ) orders.push( { day: d, a: orderFor( d, { lobster: false } ), b: orderFor( d, { lobster: true } ) } );
const pools = [ orderPool( { lobster: false } ), orderPool( { lobster: true } ) ];

// the guide's knowledge: one log entry per species, with growing catches / sales
const know = {};
const flat = ( k ) => k && ( { ...k, size: k.size, price: k.price } );
for ( const id of FISH_IDS ) {

	for ( const [ count, sold ] of [ [ 0, 0 ], [ 1, 0 ], [ 2, 1 ], [ 3, 3 ], [ 5, 4 ], [ 6, 6 ], [ 9, 7 ], [ 10, 12 ], [ 25, 40 ] ] ) {

		const t = new GameState( null );
		t.day = 3;
		for ( let i = 0; i < count; i ++ ) t.addFish( id, 0.5 + i * 0.37 + ( i % 3 ) * 0.11, [ 6, 12, 18, 23.5 ][ i % 4 ], i % 2 ? { x: 10 * i, z: - 20 * i, hab: [ 'reef', 'pier', 'deep' ][ i % 3 ] } : null );
		const e = t.log[ id ];
		if ( e ) { e.sold = sold; e.earned = sold * 7.5; }
		const k = knowledge( id, e );
		know[ id + ':' + count + ':' + sold ] = { k, sizeText: sizeText( k.size ), priceText: priceText( k.price ) };

	}

}

const grid = habitatGrid( 'tuna', { x0: 0, z0: 0, size: 100 }, 6, ( x, z ) => ( { deep: x / 100, reef: z / 100 } ) );
const maps = { grid: Array.from( grid ), coarse: Array.from( coarsen( Float32Array.from( { length: 36 }, ( _, i ) => i % 7 ), 6, 4 ) ),
	box: [ mapBox( [] ), mapBox( [ { x: 100, z: - 200 }, { x: 160, z: - 150 } ] ), mapBox( [ { x: - 600, z: 300 }, { x: 600, z: - 800 } ] ) ],
	periods: [ 0, 4.99, 5, 7.9, 8, 16.99, 17, 19.99, 20, 24, - 1, - 5 ].map( periodOf ),
	fmt: [ 0.5, 0.999, 1, 1.04, 1.05, 12.345, 100 ].map( fmtKg ) };

const values = [];
for ( const id of FISH_IDS ) for ( const kg of [ 0.03, 0.5, 1, 2.2, 6.4, 13.9, 45 ] ) values.push( [ id, kg, fishValue( id, kg ), fishLengthCm( id, kg ) ] );

// boats in the save: an older save (no list) owns all three, a hand-edited list is cleaned up, an empty one falls back to the mini, a reset starts with the mini
const rawSaves = [
	{ v: 1, money: 5 }, { v: 1, boats: [ 'pelagic', 'dinghy', 'pelagic' ] }, { v: 1, boats: [] }, { v: 1, boats: [ 'lobster', 'mini' ] },
	{ v: 1, boats: 'mini' }, { v: 1, boats: null }, { v: 1, boats: [ 'mini', 7, null, 'lobster' ] }, { v: 1, boats: [ 'pelagic' ] },
];
const boatLoads = rawSaves.map( ( raw ) => {

	const t = new GameState( { getItem: () => JSON.stringify( raw ), setItem: () => {} } );
	const loaded = t.load();
	const boats = [ ...t.boats ];
	t.reset();
	return { raw, loaded, boats, afterReset: [ ...t.boats ] };

} );

fs.writeFileSync( path.join( out, 'economy.json' ), JSON.stringify( { ops, results, scalars, snaps, market, orders, pools, know, maps, values, boatLoads } ) );
console.log( 'ops', ops.length, 'snaps', Object.keys( snaps ).length, 'know', Object.keys( know ).length, 'money', s.money, 'day', s.day, 'inv', s.inventory.length );
