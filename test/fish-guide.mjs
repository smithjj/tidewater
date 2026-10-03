// The fish guide: what is revealed at each level of knowledge (Codex.js), what the log remembers about a
// catch (GameState), and the panel itself (FishGuide.js) driven against a small fake DOM, so the rendering
// code runs for real. No GPU.
//   node test/fish-guide.mjs
import { FISH, FISH_IDS } from '../src/game/FishTable.js';
import {
	levelOf, priceLevelOf, catchesToNext, LEVEL_AT, PRICE_LEVEL_AT, MAX_LEVEL, CATCH_CAP, emptyEntry, periodOf, dominantHabitat,
	sizeKnown, priceKnown, habitatKnown, timeKnown, factsFor, blurbFor, knowledge, suitability, habitatGrid, coarsen, mapBox,
	MAP_BLOCK, sizeText, priceText,
} from '../src/game/Codex.js';
import { GameState } from '../src/game/GameState.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};
const mkState = () => {

	const m = new Map();
	return new GameState( { getItem: ( k ) => m.get( k ) ?? null, setItem: ( k, v ) => m.set( k, v ) } );

};

// ---- levels
ok( LEVEL_AT.join() === '1,3,6,10' && PRICE_LEVEL_AT.join() === '1,3,6' && MAX_LEVEL === 4, 'four levels of knowledge, three of price' );
ok( [ 0, 1, 2, 3, 5, 6, 9, 10, 99 ].map( levelOf ).join() === '0,1,1,2,2,3,3,4,4', 'level follows catches (1, 3, 6, 10)' );
ok( [ 0, 1, 3, 5, 6, 40 ].map( priceLevelOf ).join() === '0,1,2,2,3,3', 'price level follows fish sold (1, 3, 6)' );
ok( catchesToNext( 0 ) === 1 && catchesToNext( 4 ) === 2 && catchesToNext( 10 ) === null, 'and says how many catches to the next' );
ok( periodOf( 6 ) === 'dawn' && periodOf( 12 ) === 'day' && periodOf( 18.5 ) === 'dusk' && periodOf( 23 ) === 'night' && periodOf( 2 ) === 'night' && periodOf( 30 ) === 'dawn', 'hours fall into dawn, day, dusk, night' );
ok( dominantHabitat( { reef: 0.9, bay: 0.4 } ) === 'reef' && dominantHabitat( { reef: 0.05 } ) === null && dominantHabitat( { shallows: 0, reef: 0, pier: 0, bay: 0, deep: 0 } ) === null, 'a spot\'s main habitat, or none on sand' );

// ---- size: wide at first, narrowing, never contradicting your catches
{

	let allNarrowing = true, allContain = true, exact = true, widest = 0;
	for ( const id of FISH_IDS ) {

		const [ a, b ] = FISH[ id ].kg;
		let prevW = Infinity;
		for ( let lv = 1; lv <= MAX_LEVEL; lv ++ ) {

			const r = sizeKnown( id, lv );
			const w = r.hi / r.lo;
			if ( w > prevW + 1e-9 ) allNarrowing = false;
			if ( r.lo > a + 1e-9 || r.hi < b - 1e-9 ) allContain = false;
			prevW = w;
			if ( lv === 1 ) widest = Math.max( widest, w );

		}

		const top = sizeKnown( id, MAX_LEVEL );
		if ( top.lo !== a || top.hi !== b || ! top.exact ) exact = false;

	}

	ok( sizeKnown( 'grouper', 0 ) === null, 'a fish you have not caught has no size' );
	ok( allNarrowing, 'the size range only ever narrows as you catch more' );
	ok( allContain, 'and always contains the true range until it is exact' );
	ok( exact, 'at the top level it is exactly the species\' range' );
	ok( widest > 3, `the first guess is vague (up to ${ widest.toFixed( 1 ) }x between ends)` );
	const e = emptyEntry();
	e.count = 1; e.bestKg = 60; e.minKg = 0.02; // heavier and lighter than the table says
	const r = sizeKnown( 'grouper', 1, e );
	ok( r.hi >= 60 && r.lo <= 0.02, 'a range never excludes a fish you have actually caught' );
	ok( /^~/.test( sizeText( sizeKnown( 'grouper', 2 ) ) ) && ! /^~/.test( sizeText( sizeKnown( 'grouper', 4 ) ) ), 'inexact ranges read with a ~' );

}

// ---- price: learned from sales, not from catching
{

	const base = FISH.grouper.price;
	ok( priceKnown( 'grouper', 0, 0 ) === null, 'no price for an unknown fish' );
	const noSales = priceKnown( 'grouper', 4, 0 ), one = priceKnown( 'grouper', 4, 1 ), three = priceKnown( 'grouper', 4, 2 ), six = priceKnown( 'grouper', 4, 3 );
	ok( noSales.lo < base && noSales.hi > base, 'before any sale the price is a wide guess, even when the fish is fully studied' );
	ok( ( one.hi - one.lo ) < ( noSales.hi - noSales.lo ) && ( three.hi - three.lo ) < ( one.hi - one.lo ), 'each tier of sales narrows it' );
	ok( six.exact && six.lo === base && six.hi === base && priceText( six ) === `$${ base }/kg`, 'six sales: the exact price' );
	ok( [ noSales, one, three ].every( ( p ) => p.lo <= base && p.hi >= base && p.lo >= 1 ), 'a guess always contains the real price' );
	ok( /^~\$/.test( priceText( one ) ), 'and reads as a guess' );

}

// ---- habitat and time: what you found, then what you have worked out
{

	const e = emptyEntry();
	e.count = 1; e.habs = { pier: 1 };
	const keys = ( lv, en ) => habitatKnown( 'grouper', lv, en ).list.map( ( x ) => x.key );
	ok( keys( 1, e ).join() === 'pier' && habitatKnown( 'grouper', 1, e ).list[ 0 ].seen, 'level 1: only where you caught it' );
	ok( keys( 2, e ).includes( 'pier' ) && keys( 2, e ).includes( 'reef' ), 'level 2: and its main habitat' );
	ok( keys( 3, e ).includes( 'deep' ), 'level 3: and every habitat it clearly uses' );
	const top = habitatKnown( 'tarpon', 4, null );
	ok( top.exact && top.list.length === Object.keys( FISH.tarpon.habitat ).length && top.list.every( ( x ) => x.strength > 0 ), 'level 4: all of them, with how strongly' );
	ok( habitatKnown( 'lobster', 4, null ).none && habitatKnown( 'grouper', 0, null ) === null, 'a trap-only fish has no habitat list; an unknown fish none either' );

	const t = emptyEntry();
	t.count = 2; t.periods = { dusk: 2, night: 1 };
	ok( timeKnown( 'tarpon', 2, t ).seen.join() === 'dusk,night' && timeKnown( 'tarpon', 2, t ).text === null, 'level 2: only when you caught it, not when it bites' );
	ok( timeKnown( 'tarpon', 3, t ).text === 'Bites at night only', 'level 3: tarpon are revealed as night biters' );
	ok( timeKnown( 'jack', 3, null ).text === 'Bites best at dawn and dusk' && timeKnown( 'mullet', 3, null ).text === 'A daytime feeder', 'and the others\' hours' );

}

// ---- the story
{

	ok( FISH_IDS.every( ( id ) => blurbFor( id ).length > 20 ), 'every species has a line of its own' );
	ok( factsFor( 'lobster', 1 ).some( ( t ) => /pot/.test( t ) ) && ! factsFor( 'grouper', 1 ).length, 'facts come out with learning: lobster\'s pot at once, a grouper\'s later' );
	ok( factsFor( 'tarpon', 3 ).some( ( t ) => /Joe is shut/.test( t ) ) && ! factsFor( 'tarpon', 2 ).some( ( t ) => /Joe is shut/.test( t ) ), 'the night biter\'s trade-off only comes at level 3' );
	ok( FISH_IDS.every( ( id ) => factsFor( id, 4 ).length >= factsFor( id, 2 ).length ), 'facts never go away' );

}

// ---- the knowledge object
{

	const k0 = knowledge( 'grouper', null );
	ok( k0.level === 0 && k0.name === '???' && k0.sci === '' && k0.size === null && k0.price === null && k0.blurb === '' && k0.mapBlock === 0, 'an unknown fish is a ???, with nothing shown' );
	const e = emptyEntry();
	e.count = 7; e.bestKg = 9; e.bestCm = 80; e.minKg = 3.1; e.sold = 3; e.first = { day: 2, hour: 6.5 };
	const k = knowledge( 'grouper', e );
	ok( k.level === 3 && k.levelName === 'Studied' && k.name === 'Nassau grouper' && k.toNext === 3 && k.soldLevel === 2 && k.mapBlock === MAP_BLOCK[ 3 ], 'a studied fish shows its name and what is left to learn' );
	ok( knowledge( 'nope' ) === null, 'an unknown id is nothing' );
	ok( [ 0, 0, 12, 4, 1 ].join() === MAP_BLOCK.join(), 'the habitat map goes blocky -> fine as the level rises' );

}

// ---- the habitat estimate
{

	const sample = ( x ) => x > 0 ? { reef: 1, bay: 0, shallows: 0, pier: 0, deep: 0 } : null; // reef on the east half
	ok( suitability( 'grouper', null ) === 0 && suitability( 'grouper', { reef: 1 } ) === FISH.grouper.habitat.reef && suitability( 'lobster', { reef: 1 } ) === 0, 'how well a spot suits a species: from its own habitat table' );
	const n = 24;
	const g = habitatGrid( 'grouper', { x0: - 100, z0: 0, size: 200 }, n, sample );
	ok( g.length === n * n && g[ 5 ] === 0 && Math.abs( g[ n - 1 ] - FISH.grouper.habitat.reef ) < 1e-6, 'the grid covers the box: nothing on the west, the reef\'s weight on the east' );
	const mean = ( a ) => a.reduce( ( s, v ) => s + v, 0 ) / a.length;
	const c = coarsen( g, n, 12 );
	ok( Math.abs( mean( c ) - mean( g ) ) < 1e-6 && c[ 0 ] === c[ 1 ] && c[ 0 ] === c[ n ], 'a coarser map keeps the average but is blocky' );
	ok( coarsen( g, n, 1 ) === g, 'the finest level is the grid itself' );
	const box = mapBox( [ { x: 40, z: 30 }, { x: 80, z: 90 } ] );
	ok( box.size >= 440 && 40 >= box.x0 && 80 <= box.x0 + box.size && 30 >= box.z0 && 90 <= box.z0 + box.size, 'the map frames every catch' );
	const edge = mapBox( [ { x: 620, z: - 810 } ] );
	ok( edge.x0 >= - 640 && edge.x0 + edge.size <= 640 && edge.z0 >= - 820 && edge.z0 + edge.size <= 460, 'and stays inside the baked map' );
	ok( mapBox( [] ).size >= 440 && mapBox( null ).size >= 440, 'with no catches it looks at the pier' );

}

// ---- what the log remembers
{

	const st = mkState();
	st.day = 3;
	const f = st.addFish( 'grouper', 5, 6.5, { x: 41.4, z: 77.6, hab: 'reef' } );
	st.day = 4;
	st.addFish( 'grouper', 3.2, 19, { x: 60, z: 90, hab: 'bay' } );
	st.addFish( 'grouper', 9, 12, null );
	const e = st.log.grouper;
	ok( e.count === 3 && e.bestKg === 9 && e.minKg === 3.2, 'count, best and lightest' );
	ok( e.first.day === 3 && e.first.hour === 6.5, 'the first one: when' );
	ok( e.habs.reef === 1 && e.habs.bay === 1 && e.periods.dawn === 1 && e.periods.dusk === 1 && e.periods.day === 1, 'the waters and the times of day' );
	ok( e.catches.length === 3 && e.catches[ 0 ].x === 41 && e.catches[ 0 ].z === 78 && e.catches[ 0 ].hab === 'reef' && e.catches[ 0 ].day === 3, 'each catch keeps its place (to the metre), day and water' );
	ok( e.catches[ 2 ].x === undefined, 'a catch with no place is kept without one' );
	ok( f && st.lastCatch.species === 'grouper', 'and the catch itself is unchanged' );
	for ( let i = 0; i < CATCH_CAP + 10; i ++ ) st.addFish( 'jack', 2, 8, { x: i, z: i, hab: 'bay' } );
	ok( st.log.jack.count === CATCH_CAP + 10 && st.log.jack.catches.length === CATCH_CAP && st.log.jack.catches[ 0 ].x === 10, `the history keeps the last ${ CATCH_CAP }, the count keeps going` );

	// selling teaches the price
	const fish = st.inventory.find( ( x ) => x.species === 'grouper' );
	const r = st.sell( [ fish.id ] );
	ok( e.sold === 1 && e.earned === r.total, 'selling counts toward its price knowledge' );
	st.release( st.inventory.find( ( x ) => x.species === 'grouper' ).id );
	ok( e.sold === 1, 'letting one go does not' );

	// save and load
	const st2 = mkState();
	ok( st2.fromJSON( JSON.parse( JSON.stringify( st.toJSON() ) ) ) && JSON.stringify( st2.log.grouper ) === JSON.stringify( e ), 'it all survives a save' );
	ok( knowledge( 'grouper', st2.log.grouper ).level === 2 && knowledge( 'grouper', st2.log.grouper ).sold === 1, 'and the guide reads it back' );

}

{

	// a save from before the guide, and a mangled one
	const old = mkState();
	old.fromJSON( { v: 1, log: { grunt: { count: 4, bestKg: 1.1 }, unicorn: { count: 9 }, tuna: 'x', mullet: { count: - 5, bestKg: 'a', catches: [ { kg: 1 }, { kg: 1, day: 1, hour: 2 } ], habs: { reef: 'x', bay: 2 }, first: { day: 'a' } } } } );
	ok( ! old.log.unicorn && ! old.log.tuna, 'unknown species and junk entries are dropped on load' );
	const g = old.log.grunt;
	ok( g.count === 4 && g.bestKg === 1.1 && Array.isArray( g.catches ) && g.catches.length === 0 && g.sold === 0 && g.first === null, 'an old entry gains the new fields, keeping its count and best' );
	const m = old.log.mullet;
	ok( m.count === 0 && m.bestKg === 0 && m.catches.length === 1 && m.habs.reef === undefined && m.habs.bay === 2 && m.first === null, 'garbage values are clamped, bad catches dropped' );
	old.day = 5;
	old.addFish( 'grunt', 0.8, 10, { x: 1, z: 2, hab: 'pier' } );
	ok( old.log.grunt.count === 5 && old.log.grunt.catches.length === 1, 'and an old entry keeps working' );
	const k = knowledge( 'grunt', old.log.grunt );
	ok( k.level === 2 && k.size && k.habitat.list.some( ( x ) => x.key === 'pier' ), 'the guide reads an upgraded entry' );

}

// ---- the panel's CSS class names are its own: the first-play guide's overlay was once also called .gm-guide,
// and the panel inherited its `visibility: hidden` and sat off-screen without an error anywhere
{

	const fs = await import( 'node:fs' ), path = await import( 'node:path' );
	const root = new URL( '../src/', import.meta.url ).pathname;
	const walk = ( d ) => fs.readdirSync( d, { withFileTypes: true } ).flatMap( ( e ) => e.isDirectory() ? walk( path.join( d, e.name ) ) : [ path.join( d, e.name ) ] );
	const files = walk( root ).filter( ( f ) => /\.(js|css)$/.test( f ) );
	const own = path.join( root, 'game', 'FishGuide.js' );
	const mine = [ ...new Set( fs.readFileSync( own, 'utf8' ).match( /\.(gm-codex|gm-gi|gm-gd|gm-chip)[a-z0-9-]*/g ) ) ];
	const clash = [];
	for ( const f of files ) {

		if ( f === own ) continue;
		const text = fs.readFileSync( f, 'utf8' );
		for ( const sel of mine ) if ( new RegExp( sel.replace( '.', '\\.' ) + '(?![a-z0-9-])' ).test( text ) ) clash.push( `${ sel } in ${ path.relative( root, f ) }` );

	}

	ok( mine.length >= 8, `the panel has its own classes (${ mine.length })` );
	ok( clash.length === 0, `none of them is styled or used anywhere else in the game${ clash.length ? ': ' + clash.join( ', ' ) : '' }` );
	// and the names of the other overlays it must not take over
	const taken = [ 'gm-guide', 'gm-catch', 'gm-map', 'gm-market', 'gm-order' ];
	const text = fs.readFileSync( own, 'utf8' );
	ok( taken.every( ( c ) => ! new RegExp( `\\.${ c }(?![a-z0-9-])` ).test( text ) ), 'and it does not reuse the name of an existing overlay' );

}

// ---- the panel, against a fake DOM
const stub = () => {

	const els = new Map();
	const el = () => {

		const e = {
			innerHTML: '', className: '', style: {}, dataset: {}, children: [], width: 0, height: 0, src: '',
			classList: { on: new Set(), toggle( c, v ) { ( v === undefined ? ! this.on.has( c ) : v ) ? this.on.add( c ) : this.on.delete( c ); }, add( c ) { this.on.add( c ); }, remove( c ) { this.on.delete( c ); } },
			append( ...c ) { this.children.push( ...c ); }, remove() {}, scrollIntoView() {},
			querySelector( sel ) {

				if ( ! els.has( this ) ) els.set( this, new Map() );
				const m = els.get( this );
				if ( /^\.gm-gd-img$/.test( sel ) && ! /gm-gd-img/.test( this.innerHTML ) ) return null;
				if ( /canvas/.test( sel ) && ! /<canvas/.test( this.innerHTML ) ) return null;
				if ( ! m.has( sel ) ) m.set( sel, el() );
				return m.get( sel );

			},
			querySelectorAll( sel ) {

				return /data-fish/.test( sel ) ? [ ...this.innerHTML.matchAll( /data-fish="(\w+)"/g ) ].map( ( x ) => ( { dataset: { fish: x[ 1 ] } } ) ) : [];

			},
			getContext() { return ctx; },
		};
		return e;

	};

	const calls = { arc: 0, drawImage: 0, put: 0 };
	const ctx = new Proxy( {}, {
		get: ( t, k ) => k === 'arc' ? () => { calls.arc ++; } : k === 'drawImage' ? () => { calls.drawImage ++; } : k === 'putImageData' ? () => { calls.put ++; }
			: k === 'createImageData' ? ( w, h ) => ( { data: new Uint8ClampedArray( w * h * 4 ) } ) : t[ k ] ?? ( () => {} ),
		set: ( t, k, v ) => { t[ k ] = v; return true; },
	} );
	globalThis.document = { createElement: () => el(), head: el(), pointerLockElement: null };
	return { el, calls };

};

{

	const { el, calls } = stub();
	const { FishGuide } = await import( '../src/game/FishGuide.js' );
	const st = mkState();
	st.day = 6;
	const order = st.todaysOrder;
	const sp = order.species;
	st.addFish( 'jack', 4, 7, { x: 70, z: 60, hab: 'bay' } );
	for ( let i = 0; i < 7; i ++ ) st.addFish( 'grouper', 4 + i, 10 + i, { x: 60 + i * 5, z: 70, hab: 'reef' } );
	st.sell( st.inventory.filter( ( f ) => f.species === 'grouper' ).slice( 0, 3 ).map( ( f ) => f.id ) );
	for ( let i = 0; i < 6; i ++ ) st.addFish( sp, FISH[ sp ].kg[ 1 ], 10, { x: 50, z: 50, hab: 'bay' } );
	const closed = [];
	const game = { state: st, minimap: null, app: { terrainData: { heightAt: () => - 6 } }, habitatAtPoint: () => ( { reef: 0.8, bay: 0.5, shallows: 0, pier: 0, deep: 0 } ) };
	const hud = { ui: { root: el() }, game, portrait: { thumbUrl: () => null }, closeStand: () => closed.push( 'stand' ), toggleInventory: () => closed.push( 'inv' ), _resolve() {} };
	const guide = new FishGuide( hud );
	ok( ! guide.open, 'the guide starts closed' );
	guide.toggle( true );
	ok( guide.open && guide.el.classList.on.has( 'is-open' ) && closed.join() === 'stand,inv', 'opening it puts the stand and cooler away' );
	ok( FISH_IDS.every( ( id ) => guide.el.innerHTML.includes( `data-fish="${ id }"` ) ), 'every species is in the list' );
	ok( guide.el.innerHTML.includes( '???' ), 'species you have not caught are ???' );
	ok( guide.selected === 'grouper' || guide.selected === sp || st.log[ guide.selected ], 'it opens on a fish you know' );

	const detail = guide.el.querySelector( '.gm-codex-detail' );
	guide.select( 'grouper' );
	const html = detail.innerHTML;
	ok( html.includes( 'Nassau grouper' ) && html.includes( 'Studied' ), 'a known fish shows its name and level' );
	ok( /~[\d.]+–[\d.]+ kg/.test( html ), 'with a size range' );
	ok( /\$\d+–\$\d+\/kg/.test( html ) && ! /\$15\/kg/.test( html ), 'and a price that is still a guess after three sales' );
	ok( html.includes( 'Reef' ) && html.includes( 'is-seen' ), 'and where you found it' );
	ok( html.includes( 'Your biggest' ) && html.includes( 'First caught on day 6' ), 'and your own record' );
	ok( html.includes( '7 caught · 3 sold' ), 'and the counts' );
	ok( calls.arc >= 8 && calls.put >= 1, `the map drew its dots and the habitat estimate (${ calls.arc } arcs)` );

	guide.select( 'tuna' );
	const un = detail.innerHTML;
	ok( un.includes( '???' ) && un.includes( 'is-silhouette' ) && un.includes( 'have not caught one' ) && ! un.includes( 'Blackfin' ), 'an unknown fish is a black silhouette with nothing given away' );
	ok( ! un.includes( '<canvas' ), 'and has no map' );

	guide.select( sp );
	ok( detail.innerHTML.includes( 'Joe wants one of' ) && guide.el.innerHTML.includes( 'gm-star' ), 'the species Joe wants today is marked, in the list and the entry' );
	guide.select( 'jack' );
	ok( ! detail.innerHTML.includes( 'Joe wants one' ), 'another one is not' );
	ok( detail.innerHTML.includes( 'A guess: sell some to Joe' ), 'a fish you have not sold says its price is a guess' );

	guide.select( 'nonsense' );
	ok( guide.selected === 'jack', 'selecting something that is not a fish does nothing' );
	guide.toggle( false );
	ok( ! guide.open, 'and it closes' );
	let threw = false;
	try { guide.refresh(); guide.tick(); } catch ( e ) { threw = true; }
	ok( ! threw, 'refresh and tick are harmless when closed' );

	// a portrait that is not ready turns up later
	let url = null;
	hud.portrait = { thumbUrl: () => url };
	guide.toggle( true );
	guide.select( 'grouper' );
	const img = guide.el.querySelector( '.gm-codex-detail' ).querySelector( '.gm-gd-img' );
	guide.tick();
	ok( img.src === '', 'no portrait yet: nothing set' );
	url = 'blob:grouper';
	guide.tick();
	ok( img.src === 'blob:grouper', 'it appears when the portrait is rendered' );

}

console.log( fails ? `${ fails } FAILED` : 'all ok' );
process.exit( fails ? 1 : 0 );
