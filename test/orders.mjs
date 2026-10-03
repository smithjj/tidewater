// Joe's order of the day: the pure rule (Orders.js), what it does to prices and sales in GameState, the save,
// the board and the price tags the HUD draws, and the toast on a sale. Plain node, no GPU.
//   node test/orders.mjs
import { orderFor, orderPool, orderMul, fmtKg, ORDER_MULT, ORDER_FIRST_DAY, ORDER_MIN_VALUE } from '../src/game/Orders.js';
import { FISH } from '../src/game/FishTable.js';
import { GameState } from '../src/game/GameState.js';
import { Game } from '../src/game/Game.js';
import { priceTag, orderBoard } from '../src/game/GameHUD.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};
const mkState = () => {

	const m = new Map();
	return new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );

};

// ---- the rule
ok( orderFor( 1 ) === null && orderFor( 0 ) === null && orderFor( NaN ) === null, 'day 1 has no order (it pays the standard rate, like the market)' );
ok( ORDER_FIRST_DAY === 2 && orderFor( 2 ) !== null, 'the first order is on day 2' );
ok( JSON.stringify( orderFor( 17 ) ) === JSON.stringify( orderFor( 17 ) ) && JSON.stringify( orderFor( 17.9 ) ) === JSON.stringify( orderFor( 17 ) ), 'the order is the same every time for a given day (nothing random in the save)' );
ok( ORDER_MULT > 1 && ORDER_MULT <= 1.5, `the multiplier is modest (x${ ORDER_MULT })` );

{

	const plain = orderPool(), licensed = orderPool( { lobster: true } );
	ok( ! plain.includes( 'lobster' ) && licensed.includes( 'lobster' ) && licensed.length === plain.length + 1, 'lobster is only asked for once there is a trap licence' );
	const cheap = [ 'silverside', 'chromis', 'sergeant', 'tang' ];
	ok( cheap.every( ( id ) => ! licensed.includes( id ) ), 'and fish too small to be worth a bonus are never asked for' );
	ok( ! licensed.includes( 'tarpon' ) && licensed.every( ( id ) => FISH[ id ].time !== 'night' ), 'nor a night biter: Joe shuts at dusk, so it could not be sold the day it is asked for' );
	ok( licensed.every( ( id ) => FISH[ id ].price * ( FISH[ id ].kg[ 0 ] + FISH[ id ].kg[ 1 ] ) / 2 >= ORDER_MIN_VALUE ), 'every species in the pool fetches the minimum at a typical size' );

	let repeats = 0, bad = 0;
	const seen = new Map();
	for ( const lobster of [ false, true ] ) {

		let prev = null;
		for ( let d = 2; d < 1500; d ++ ) {

			const o = orderFor( d, { lobster } );
			const f = FISH[ o.species ];
			if ( prev && prev.species === o.species ) repeats ++;
			if ( o.day !== d || ! f || o.minKg < f.kg[ 0 ] - 1e-9 || o.minKg > f.kg[ 1 ] + 1e-9 || ! ( o.minKg > 0 ) ) bad ++;
			if ( lobster ) seen.set( o.species, ( seen.get( o.species ) || 0 ) + 1 );
			prev = o;

		}

	}

	ok( repeats === 0, 'never the same species two days running (over 1500 days, with and without a licence)' );
	ok( bad === 0, 'the size is always inside the species\' own range' );
	ok( seen.size === licensed.length, `every species in the pool comes up (${ seen.size } of ${ licensed.length })` );
	const share = Math.max( ...seen.values() ) / 1498;
	ok( share < 0.12, `none dominates (the most common is ${ ( share * 100 ).toFixed( 1 ) }% of days)` );

}

{

	const o = { day: 5, species: 'grouper', minKg: 4 };
	ok( orderMul( o, { species: 'grouper', kg: 4 } ) === ORDER_MULT && orderMul( o, { species: 'grouper', kg: 9 } ) === ORDER_MULT, 'the right species at the size or bigger fills it' );
	ok( orderMul( o, { species: 'grouper', kg: 3.9 } ) === 1, 'too small does not' );
	ok( orderMul( o, { species: 'tuna', kg: 9 } ) === 1 && orderMul( null, { species: 'grouper', kg: 9 } ) === 1, 'another species, or no order, does not' );
	ok( fmtKg( 0.45 ) === '0.45 kg' && fmtKg( 3 ) === '3.0 kg' && fmtKg( 14.04 ) === '14.0 kg', 'sizes read as kg' );

}

// ---- the state: prices, sales, the save
{

	const st = mkState();
	const g1 = st.addFish( 'grunt', 1.0 );
	ok( st.todaysOrder === null && st.priceOf( g1 ) === g1.value, 'day 1: no order, the standard price' );

	st.day = 9;
	const o = st.todaysOrder;
	ok( o && o.day === 9 && o.filled === 0 && o.bonus === 0, 'a new day rolls an order' );
	ok( st.todaysOrder === o, 'and keeps it for the day' );
	const sp = o.species;
	const hit = st.addFish( sp, Math.max( o.minKg, FISH[ sp ].kg[ 0 ] ) + 0.001 );
	const small = st.addFish( sp, Math.max( FISH[ sp ].kg[ 0 ], 0.001 ) * 1 );
	const other = st.addFish( sp === 'grunt' ? 'mullet' : 'grunt', 1.0 );
	const market = ( f ) => Math.round( f.value * st.mulFor( f.species ) );
	ok( hit && st.priceOf( hit ) === Math.round( hit.value * st.mulFor( sp ) * ORDER_MULT ), `a fish that fills it is paid x${ ORDER_MULT } on top of the market` );
	ok( st.priceOf( other ) === market( other ), 'another species is paid the market rate' );
	if ( o.minKg > FISH[ sp ].kg[ 0 ] + 1e-6 ) ok( st.priceOf( small ) === market( small ), 'a fish under the size is paid the market rate' );
	const before = st.money;
	const expectedBonus = st.priceOf( hit ) - market( hit );
	const r = st.sell( [ hit.id ] );
	ok( r.filled === 1 && r.bonus === expectedBonus && r.total === st.priceOf( { ...hit } ) && st.money === before + r.total, 'selling it reports what the order added' );
	ok( st.order.filled === 1 && st.order.bonus === expectedBonus, 'and the day\'s order remembers it' );
	const r2 = st.sell( [ other.id ] );
	ok( r2.filled === 0 && r2.bonus === 0 && st.order.filled === 1, 'a fish that does not fill it adds nothing' );

	// saved with the game, and a stale one is rolled afresh
	const saved = JSON.parse( JSON.stringify( st.toJSON() ) );
	const st2 = mkState();
	st2.fromJSON( saved );
	ok( st2.todaysOrder.species === sp && st2.todaysOrder.filled === 1 && st2.todaysOrder.bonus === expectedBonus, 'the order and how it has gone survive a save' );
	st2.day = 10;
	ok( st2.todaysOrder.day === 10 && st2.todaysOrder.filled === 0, 'the next day brings a new one' );
	const old = mkState();
	ok( old.fromJSON( { v: 1, money: 5 } ) && old.todaysOrder === null, 'a save from before orders loads (day 1: none)' );
	const junk = mkState();
	junk.fromJSON( { v: 1, day: 4, order: { day: 4, species: 'unicorn', minKg: 1 } } );
	ok( junk.todaysOrder && junk.todaysOrder.species !== 'unicorn', 'a corrupt saved order is thrown away' );
	st.reset();
	ok( st.order === null, 'a reset clears it' );

}

{

	const st = mkState();
	st.day = 12;
	const before = st.todaysOrder.species;
	st.upgrades.trapLicence = 1;
	ok( st.mayTrap && st.todaysOrder.species === before, 'buying the licence mid-day does not change the day\'s order' );
	let lobsterDays = 0;
	for ( let d = 2; d < 400; d ++ ) { st.day = d; st.order = null; if ( st.todaysOrder.species === 'lobster' ) lobsterDays ++; }
	ok( lobsterDays > 0, `with the licence, lobster does come up (${ lobsterDays } days of 400)` );

}

// ---- what the HUD draws
{

	const st = mkState();
	st.day = 6;
	const o = st.todaysOrder;
	const f = st.addFish( o.species, Math.max( FISH[ o.species ].kg[ 0 ], o.minKg ) + 0.001 );
	const tag = priceTag( st, f );
	ok( tag.includes( '★' ) && tag.includes( '$' + st.priceOf( f ) ), 'a fish that fills the order carries a star in the price tag' );
	const board = orderBoard( st );
	ok( board.includes( FISH[ o.species ].name ) && board.includes( fmtKg( o.minKg ) ) && board.includes( '×' + ORDER_MULT ), 'Joe\'s board says what he wants, how big and the multiplier' );
	ok( ! board.includes( 'filled' ), 'and nothing filled yet' );
	st.sell( [ f.id ] );
	ok( orderBoard( st ).includes( 'filled once' ), 'after a sale it says it has been filled' );
	const d1 = mkState();
	ok( orderBoard( d1 ) === '' && ! priceTag( d1, d1.addFish( 'grunt', 1 ) ).includes( '★' ), 'day 1: no board line, no stars' );

}

// ---- the sale toast
{

	const st = mkState();
	st.day = 6;
	const o = st.todaysOrder;
	st.addFish( o.species, Math.max( FISH[ o.species ].kg[ 0 ], o.minKg ) + 0.001 );
	const toasts = [];
	const g = Object.create( Game.prototype );
	Object.assign( g, { state: st, toast: ( t ) => toasts.push( t ), app: {} } );
	const r = g.sellAll();
	ok( r.filled === 1 && /Joe's order \+\$\d+/.test( toasts[ 0 ] ) && toasts[ 0 ].includes( '$' + r.total ), `a sale that fills the order says so: "${ toasts[ 0 ] }"` );
	st.addFish( 'jack', 5 );
	st.order.species = 'tuna';
	g.sell( null );
	ok( ! /order/.test( toasts[ 1 ] ), 'an ordinary sale does not mention it' );

}

console.log( fails ? `${ fails } FAILED` : 'all ok' );
process.exit( fails ? 1 : 0 );
