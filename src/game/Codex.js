// The fish guide's knowledge rules: what the player has worked out about each species, from how many they
// have caught and sold. Pure functions over the fish table and a log entry (GameState.log[ species ]), so
// the tests can drive them without a GPU or a DOM; GameHUD draws the result.
//
// Knowledge grows with catches (level 0..4) and the price with fish sold (0..3):
//
//   level 0  never caught: a silhouette
//   level 1  first one: the name, a rough size range (about a factor of two either way), where and when YOU
//            caught it, a rough price
//   level 2  3 caught: the range tightens, its main habitat, how hard it fights, a coarse map of its water
//   level 3  6 caught: the range is close, all its habitats, when it bites, more of its story, a finer map
//   level 4  10 caught: the exact figures and the habitat map at full resolution
//
// A range never contradicts what you have already caught: it always includes your lightest and your best.
//
//   const k = knowledge( 'grouper', state.log.grouper )   -> everything the guide may show right now
import { FISH, FISH_IDS } from './FishTable.js';

export const CATCH_CAP = 40; // catches kept per species for the map and history (the counts keep going)
export const LEVEL_AT = [ 1, 3, 6, 10 ]; // caught for knowledge level 1..4
export const PRICE_LEVEL_AT = [ 1, 3, 6 ]; // fish sold for price level 1..3
export const LEVEL_NAMES = [ 'Unknown', 'Sighted', 'Familiar', 'Studied', 'Mastered' ];
export const MAX_LEVEL = LEVEL_AT.length;

export const HAB_LABEL = {
	shallows: 'Sandy shallows', reef: 'Reef', pier: 'Around the pier', bay: 'Open bay', deep: 'Deep water',
};
export const PERIOD_LABEL = { dawn: 'dawn', day: 'daytime', dusk: 'dusk', night: 'night' };

export const levelOf = ( caught ) => LEVEL_AT.filter( ( n ) => caught >= n ).length;
export const priceLevelOf = ( sold ) => PRICE_LEVEL_AT.filter( ( n ) => sold >= n ).length;
// catches still needed for the next level, or null at the top
export const catchesToNext = ( caught ) => {

	const next = LEVEL_AT.find( ( n ) => caught < n );
	return next === undefined ? null : next - caught;

};

// ---- what a log entry records (GameState.addFish writes it, this module only reads it)
export const emptyEntry = () => ( {
	count: 0, bestKg: 0, minKg: 0, first: null, catches: [], habs: {}, periods: {}, sold: 0, earned: 0,
} );

export function periodOf( hour ) {

	const h = ( ( hour % 24 ) + 24 ) % 24;
	return h >= 5 && h < 8 ? 'dawn' : h >= 8 && h < 17 ? 'day' : h >= 17 && h < 20 ? 'dusk' : 'night';

}

// the habitat type a spot mostly is (weights from Bites.habitatAt), or null on sand / when nothing stands out
export function dominantHabitat( weights ) {

	let best = null, bw = 0.15;
	for ( const k in weights ) if ( weights[ k ] > bw ) { bw = weights[ k ]; best = k; }
	return best;

}

// ---- rounding a range outward to a number of significant figures (0 = exact)
function floorSig( x, sig ) {

	if ( ! sig || ! ( x > 0 ) ) return x;
	const m = Math.pow( 10, Math.floor( Math.log10( x ) ) - ( sig - 1 ) );
	return Math.floor( x / m + 1e-9 ) * m;

}

function ceilSig( x, sig ) {

	if ( ! sig || ! ( x > 0 ) ) return x;
	const m = Math.pow( 10, Math.floor( Math.log10( x ) ) - ( sig - 1 ) );
	return Math.ceil( x / m - 1e-9 ) * m;

}

const clean = ( x ) => Number( x.toPrecision( 6 ) );

// ---- size (kg): wider than the truth until you have caught enough of them
const SIZE_SPREAD = [ Infinity, 2.2, 1.6, 1.25, 1 ];
const SIZE_SIG = [ 0, 1, 1, 2, 0 ];

export function sizeKnown( id, level, entry = null ) {

	if ( level < 1 ) return null;
	const [ a, b ] = FISH[ id ].kg;
	const w = SIZE_SPREAD[ level ];
	let lo = a / w, hi = b * w;
	if ( level < MAX_LEVEL && entry ) {

		if ( entry.minKg > 0 ) lo = Math.min( lo, entry.minKg );
		if ( entry.bestKg > 0 ) hi = Math.max( hi, entry.bestKg );

	}

	const sig = SIZE_SIG[ level ];
	return { lo: clean( floorSig( lo, sig ) ), hi: clean( ceilSig( hi, sig ) ), exact: level >= MAX_LEVEL };

}

// ---- price ($ per kg at the stand): from fish sold, not from the fish you merely caught
const PRICE_SPREAD = [ 2.5, 1.6, 1.25, 1 ];

export function priceKnown( id, level, soldLevel ) {

	if ( level < 1 ) return null;
	const p = FISH[ id ].price;
	const w = PRICE_SPREAD[ soldLevel ];
	if ( soldLevel >= 3 ) return { lo: p, hi: p, exact: true };
	// 1 figure with no sales at all, 2 after the first, whole dollars after three
	const sig = soldLevel >= 2 ? 0 : soldLevel === 1 ? 2 : 1;
	let lo = p / w, hi = p * w;
	lo = sig ? floorSig( lo, sig ) : Math.floor( lo );
	hi = sig ? ceilSig( hi, sig ) : Math.ceil( hi );
	return { lo: Math.max( 1, clean( lo ) ), hi: clean( hi ), exact: false };

}

// ---- where it lives: what you found it in, then what you have worked out
export function habitatKnown( id, level, entry = null ) {

	if ( level < 1 ) return null;
	const table = FISH[ id ].habitat || {};
	const observed = entry ? Object.keys( entry.habs || {} ).filter( ( k ) => entry.habs[ k ] > 0 ) : [];
	const keys = new Set( observed );
	const ranked = Object.keys( table ).sort( ( a, b ) => table[ b ] - table[ a ] );
	if ( level >= 2 && ranked.length ) keys.add( ranked[ 0 ] );
	if ( level >= 3 ) for ( const k of ranked ) if ( table[ k ] >= 0.3 ) keys.add( k );
	if ( level >= MAX_LEVEL ) for ( const k of ranked ) keys.add( k );
	const list = [ ...keys ].sort( ( a, b ) => ( table[ b ] || 0 ) - ( table[ a ] || 0 ) )
		.map( ( key ) => ( { key, label: HAB_LABEL[ key ] || key, seen: observed.includes( key ), strength: level >= MAX_LEVEL ? ( table[ key ] || 0 ) : null } ) );
	return { list, exact: level >= MAX_LEVEL, none: ranked.length === 0 };

}

// ---- when it bites: when you caught it, then what the table says
const TIME_TEXT = {
	night: 'Bites at night only',
	dawnDusk: 'Bites best at dawn and dusk',
	day: 'A daytime feeder',
	any: 'Bites at any hour',
};

export function timeKnown( id, level, entry = null ) {

	if ( level < 1 ) return null;
	const periods = [ 'dawn', 'day', 'dusk', 'night' ].filter( ( p ) => entry && entry.periods && entry.periods[ p ] > 0 );
	const t = FISH[ id ].time;
	return { seen: periods, pref: level >= 3 ? t : null, text: level >= 3 ? TIME_TEXT[ t ] || null : null };

}

// ---- the story: a line of flavour, and facts that come out as you learn the fish
const BLURB = {
	silverside: 'Tiny schooling baitfish that glitter in the shallows. Everything bigger eats them.',
	mullet: 'Grazes the sandy bottom and leaps clear of the water when startled.',
	needlefish: 'A long, toothy surface hunter that skims and leaps after small fish.',
	sergeant: 'A bold, banded damselfish that crowds around pilings and reef edges.',
	grunt: 'Grinds its teeth to make the sound it is named for. Schools in the shade of piles and reef.',
	yellowtail: 'A slim snapper with a bright yellow stripe and tail, hunting above the reef.',
	chromis: 'A small, electric-blue reef fish that feeds in clouds above the coral.',
	tang: 'A blue surgeonfish with a scalpel-sharp spine at the base of its tail. It grazes the reef.',
	wrasse: 'A pig-snouted wrasse that roots shellfish out of the sand. Prized at the table.',
	parrot: 'Crunches coral with its beak-like teeth, and sleeps in a cocoon of its own mucus at night.',
	angel: 'A regal reef angelfish with a crown-like marking on its forehead.',
	jack: 'A powerful, hard-running predator that hunts in packs around the bay.',
	barracuda: 'A silver ambusher with a mouthful of fangs. It strikes at flash and glitter.',
	grouper: 'A big, long-lived grouper that ambushes from caves. Slow-growing, and protected in many places.',
	redSnapper: 'A deep-water snapper, red from head to tail, that keeps to ledges and wrecks.',
	tuna: 'A small, fast, warm-blooded tuna that chases baitfish offshore.',
	mahi: 'Brilliant gold and blue, always on the move. It follows floating debris offshore.',
	tarpon: 'The silver king: a huge, armoured fish that leaps and runs. It hunts in the dark.',
	lobster: 'A clawless spiny lobster that hides in crevices by day. It only walks into pots.',
};

// the facts unlocked at this level, in the order they came out
export function factsFor( id, level ) {

	const f = FISH[ id ];
	const out = [];
	const add = ( at, text ) => { if ( level >= at ) out.push( text ); };
	if ( f.kind === 'lobster' || ! Object.keys( f.habitat || {} ).length ) add( 1, 'Never takes a hook: it only comes up in a pot' );
	add( 2, f.fight >= 0.8 ? 'Fights ferociously: long runs, and it takes a lot to tire' : f.fight >= 0.55 ? 'A strong fighter' : f.fight <= 0.2 ? 'Gives up without much of a fight' : 'A fair fight on light gear' );
	if ( f.time === 'night' ) add( 3, 'Joe is shut by the time it bites: sell it the next day' );
	add( 3, f.rarity > 0 && f.rarity <= 0.3 ? 'Uncommon: it takes patience to find one' : f.rarity >= 0.9 ? 'Common: you will not go long without one' : 'Neither common nor rare' );
	if ( f.kind === 'lobster' ) add( 3, 'Pots do best in 2–35 m of water: shallower and deeper ground holds fewer' );
	if ( f.price >= 15 ) add( 3, 'Fetches a good price for its weight' );
	else if ( f.price <= 5 ) add( 3, 'Worth little per kg: only the big ones pay' );
	return out;

}

export const blurbFor = ( id ) => BLURB[ id ] || '';

// ---- the habitat map: how well a spot suits a species, and a version of it blurred by how little is known
// `sample( x, z )` returns the habitat weights at a point (Bites.habitatAt) or null where there is no water.
export function suitability( id, weights ) {

	if ( ! weights ) return 0;
	const table = FISH[ id ].habitat || {};
	let s = 0;
	for ( const k in table ) s += table[ k ] * ( weights[ k ] || 0 );
	return Math.min( 1, s );

}

// an n x n grid over the square { x0, z0, size } (row-major, z down), 0..1
export function habitatGrid( id, box, n, sample ) {

	const g = new Float32Array( n * n );
	const cell = box.size / n;
	for ( let j = 0; j < n; j ++ ) for ( let i = 0; i < n; i ++ ) {

		g[ j * n + i ] = suitability( id, sample( box.x0 + ( i + 0.5 ) * cell, box.z0 + ( j + 0.5 ) * cell ) );

	}

	return g;

}

// cells per block the map is drawn in at each level (0: no map). The finest is the grid itself.
export const MAP_BLOCK = [ 0, 0, 12, 4, 1 ];

// average the grid over block x block cells and paint the average back, so a low-level map is blocky
export function coarsen( grid, n, block ) {

	if ( block <= 1 ) return grid;
	const out = new Float32Array( n * n );
	for ( let bj = 0; bj < n; bj += block ) for ( let bi = 0; bi < n; bi += block ) {

		let sum = 0, cnt = 0;
		for ( let j = bj; j < Math.min( n, bj + block ); j ++ ) for ( let i = bi; i < Math.min( n, bi + block ); i ++ ) { sum += grid[ j * n + i ]; cnt ++; }
		const v = sum / cnt;
		for ( let j = bj; j < Math.min( n, bj + block ); j ++ ) for ( let i = bi; i < Math.min( n, bi + block ); i ++ ) out[ j * n + i ] = v;

	}

	return out;

}

// the square on the map (world metres) that frames your catches, or a default around the pier head
export function mapBox( catches, { extent = 1280, x0 = - 640, z0 = - 820, home = { x: 55, z: 20 }, min = 220 } = {} ) {

	let lo = { x: Infinity, z: Infinity }, hi = { x: - Infinity, z: - Infinity };
	for ( const c of catches || [] ) {

		if ( ! Number.isFinite( c.x ) || ! Number.isFinite( c.z ) ) continue;
		lo = { x: Math.min( lo.x, c.x ), z: Math.min( lo.z, c.z ) };
		hi = { x: Math.max( hi.x, c.x ), z: Math.max( hi.z, c.z ) };

	}

	if ( lo.x === Infinity ) { lo = { x: home.x, z: home.z }; hi = { x: home.x, z: home.z }; }
	const cx = ( lo.x + hi.x ) / 2, cz = ( lo.z + hi.z ) / 2;
	const size = Math.min( extent, Math.max( min * 2, Math.max( hi.x - lo.x, hi.z - lo.z ) * 1.35 + 120 ) );
	// keep the box inside the baked map
	const bx = Math.min( x0 + extent - size, Math.max( x0, cx - size / 2 ) );
	const bz = Math.min( z0 + extent - size, Math.max( z0, cz - size / 2 ) );
	return { x0: bx, z0: bz, size };

}

// ---- everything the guide shows for one species, in one object
export function knowledge( id, entry = null ) {

	const f = FISH[ id ];
	if ( ! f ) return null;
	const e = entry || emptyEntry();
	const caught = e.count || 0;
	const level = levelOf( caught );
	const soldLevel = priceLevelOf( e.sold || 0 );
	return {
		id, name: level >= 1 ? f.name : '???', sci: level >= 1 ? f.sci : '',
		caught, level, levelName: LEVEL_NAMES[ level ], toNext: catchesToNext( caught ),
		sold: e.sold || 0, soldLevel, earned: e.earned || 0,
		blurb: level >= 1 ? blurbFor( id ) : '',
		size: sizeKnown( id, level, e ), price: priceKnown( id, level, soldLevel ),
		habitat: habitatKnown( id, level, e ), time: timeKnown( id, level, e ), facts: factsFor( id, level ),
		best: caught ? { kg: e.bestKg, cm: e.bestCm ?? null } : null,
		first: e.first || null, catches: e.catches || [], mapBlock: MAP_BLOCK[ level ],
	};

}

export const GUIDE_IDS = FISH_IDS;

// ---- text for the numbers
const num = ( n ) => n < 1 ? String( clean( Math.round( n * 100 ) / 100 ) ) : n < 10 ? String( clean( Math.round( n * 10 ) / 10 ) ) : String( Math.round( n ) );

export function sizeText( s ) {

	return s ? `${ s.exact ? '' : '~' }${ num( s.lo ) }–${ num( s.hi ) } kg` : '';

}

export function priceText( p ) {

	if ( ! p ) return '';
	return p.exact ? `$${ p.lo }/kg` : `~$${ p.lo }–$${ p.hi }/kg`;

}
