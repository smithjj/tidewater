// Joe's order of the day: each morning (from day 2) he is asking for one species, a decent size or bigger,
// and pays a multiplier on it for as long as the day lasts. Pure rules, so the tests can drive them without
// a GPU; the state keeps the day's order (and how it went) and the HUD shows it.
//
//   orderFor( day, { lobster } )   -> { day, species, minKg } | null   the same every time for a given day
//   orderMul( order, fish )        -> ORDER_MULT when the fish is the order (right species, big enough), else 1
import { FISH, FISH_IDS } from './FishTable.js';

export const ORDER_MULT = 1.25; // what Joe pays on a fish that fills the order, on top of the day's market rate
export const ORDER_FIRST_DAY = 2; // day 1 pays the standard rate, as the market does

// 0..1, the same for a given day and salt (no random state in the save, like the market roll)
const hash01 = ( day, salt ) => {

	const x = Math.sin( day * 78.233 + salt * 12.9898 ) * 43758.5453;
	return x - Math.floor( x );

};

// a species worth asking for: a fish of typical size fetches at least this much, so the multiplier is
// worth something (a 25% bonus on a silverside is a few cents)
export const ORDER_MIN_VALUE = 5;
const typicalValue = ( id ) => FISH[ id ].price * ( FISH[ id ].kg[ 0 ] + FISH[ id ].kg[ 1 ] ) / 2;

// what Joe can ask for: the species that take a hook and are worth something, and lobster once there is a
// trap licence to catch one. Not the night biters (tarpon): Joe shuts at dusk, so the order would be gone
// by the time one could be sold.
export function orderPool( { lobster = false } = {} ) {

	return FISH_IDS.filter( ( id ) => ( id === 'lobster' ? lobster : Object.keys( FISH[ id ].habitat ).length > 0 ) && FISH[ id ].time !== 'night' && typicalValue( id ) >= ORDER_MIN_VALUE );

}

// the commoner a fish, the likelier the ask (lobster has no bite rarity: it is asked for about as seldom as the rarest)
const weight = ( id ) => 0.4 + ( FISH[ id ].rarity || 0 );

function pick( day, pool ) {

	let total = 0;
	for ( const id of pool ) total += weight( id );
	let r = hash01( day, 1 ) * total;
	for ( let i = 0; i < pool.length; i ++ ) {

		r -= weight( pool[ i ] );
		if ( r <= 0 ) return i;

	}

	return pool.length - 1;

}

export function orderFor( day, { lobster = false } = {} ) {

	if ( ! ( day >= ORDER_FIRST_DAY ) ) return null;
	day = Math.floor( day );
	const pool = orderPool( { lobster } );
	// each day's pick, nudged on when it is yesterday's species (the walk from day 2 keeps "yesterday" the
	// actual order, so no species is asked for two days running; it runs about once per game day)
	let i = 0, yesterday = - 1;
	for ( let d = ORDER_FIRST_DAY; d <= day; d ++ ) {

		i = pick( d, pool );
		if ( i === yesterday ) i = ( i + 1 ) % pool.length;
		yesterday = i;

	}

	const species = pool[ i ];
	// big enough to be worth asking for, small enough to turn up: 5-35% of the way up the species' size range
	const [ lo, hi ] = FISH[ species ].kg;
	const raw = lo + ( hi - lo ) * ( 0.05 + 0.3 * hash01( day, 2 ) );
	const step = raw < 1 ? 0.05 : raw < 5 ? 0.1 : 0.5;
	const minKg = Math.min( hi, Math.max( lo, Math.round( raw / step ) * step ) );
	return { day, species, minKg: Math.round( minKg * 100 ) / 100 };

}

export function orderMul( order, fish ) {

	return order && fish && fish.species === order.species && fish.kg >= order.minKg - 1e-9 ? ORDER_MULT : 1;

}

export const fmtKg = ( kg ) => kg < 1 ? kg.toFixed( 2 ) + ' kg' : ( Math.round( kg * 10 ) / 10 ).toFixed( 1 ) + ' kg';
