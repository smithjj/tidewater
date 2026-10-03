import { FISH, FISH_IDS } from './FishTable.js';
import { FishPortrait } from './FishPortrait.js';
import { fmtKg, ORDER_MULT } from './Orders.js';
import { MAP_GEOM } from './Minimap.js';
import { knowledge, sizeText, priceText, habitatGrid, coarsen, mapBox, MAX_LEVEL, PERIOD_LABEL } from './Codex.js';

// The fish guide (J): every species in a list, and the one you pick on the right, with only what you have
// worked out so far (Codex.js decides what that is: it grows with catches and with fish sold). The portrait
// is FishPortrait's cached studio view, a blacked-out silhouette until you have caught one. The map shows
// where you caught it, over an estimate of its water that sharpens as you learn it.
//
//   const guide = new FishGuide( hud );   guide.toggle();   guide.tick();   guide.refresh()
// `hud` is the GameHUD (its ui.root, game, portrait and panel conventions).

const GRID = 96; // cells across the habitat estimate
const MAP_PX = 320;

const CSS = /* css */`
.gm-codex { width: min(calc(860 * var(--tw-u)), 94vw); max-height: 82vh; }
.gm-codex-head { display: flex; align-items: baseline; justify-content: space-between; gap: var(--tw-3); }
.gm-codex-body { display: grid; grid-template-columns: calc(210 * var(--tw-u)) 1fr; gap: var(--tw-4); min-height: 0; flex: 1; overflow: hidden; }
.gm-codex-list, .gm-codex-detail { scrollbar-width: thin; scrollbar-color: rgba(255,255,255,0.22) transparent; }
.gm-codex-list { overflow: auto; display: flex; flex-direction: column; gap: 2px; padding-right: var(--tw-1); }
.gm-gi { display: flex; justify-content: space-between; align-items: center; gap: var(--tw-2); text-align: left; cursor: pointer;
	font: 500 var(--tw-fs-md) var(--tw-font); color: var(--tw-ink-2); background: transparent; border: 1px solid transparent; border-radius: var(--tw-r-md, 10px); padding: var(--tw-2) var(--tw-3); }
.gm-gi:hover, .gm-gi:focus-visible { background: var(--tw-fill); color: var(--tw-ink); outline: none; }
.gm-gi.is-sel { background: var(--tw-fill-2); color: var(--tw-ink); border-color: var(--tw-line); }
.gm-gi.is-unknown .gm-gi-name { color: var(--tw-ink-3); letter-spacing: 0.12em; }
.gm-gi-pips { font-size: var(--tw-fs-sm); letter-spacing: 1px; color: var(--tw-aqua); white-space: nowrap; }
.gm-gi-pips i { font-style: normal; color: var(--tw-ink-3); opacity: 0.5; }
.gm-gi .gm-star { margin: 0; }
.gm-codex-detail { overflow: auto; padding-right: var(--tw-2); display: flex; flex-direction: column; gap: var(--tw-3); }
.gm-gd-top { display: grid; grid-template-columns: calc(200 * var(--tw-u)) 1fr; gap: var(--tw-4); align-items: center; }
.gm-gd-img { width: 100%; aspect-ratio: 360 / 170; object-fit: contain; border-radius: var(--tw-r-md, 10px); background: rgba(255,255,255,0.04); }
.gm-gd-img.is-silhouette { filter: brightness(0) opacity(0.55); }
.gm-gd-top h3 { margin: 0; font-size: calc(20 * var(--tw-u)); font-weight: 600; }
.gm-gd-sci { color: var(--tw-ink-3); font-style: italic; font-size: var(--tw-fs-sm); }
.gm-gd-level { margin-top: var(--tw-2); font-size: var(--tw-fs-sm); color: var(--tw-ink-2); }
.gm-gd-level b { color: var(--tw-aqua); letter-spacing: 1px; font-weight: 500; }
.gm-gd-level b i { font-style: normal; opacity: 0.35; }
.gm-gd-level small { display: block; color: var(--tw-ink-3); margin-top: 2px; }
.gm-gd-blurb { color: var(--tw-ink-2); line-height: 1.45; margin: 0; }
.gm-gd-grid { display: grid; grid-template-columns: calc(96 * var(--tw-u)) 1fr; gap: var(--tw-2) var(--tw-3); font-size: var(--tw-fs-md); }
.gm-gd-grid dt { color: var(--tw-ink-3); }
.gm-gd-grid dd { margin: 0; color: var(--tw-ink); }
.gm-gd-grid dd small { display: block; color: var(--tw-ink-3); font-size: var(--tw-fs-sm); }
.gm-gd-grid .is-locked { color: var(--tw-ink-3); }
.gm-chip { display: inline-block; margin: 0 var(--tw-1) var(--tw-1) 0; padding: 1px var(--tw-2); border-radius: 999px; font-size: var(--tw-fs-sm); background: var(--tw-fill); border: 1px solid var(--tw-line); }
.gm-chip.is-seen { border-color: rgba(var(--tw-aqua-rgb), 0.6); }
.gm-chip.is-seen::before { content: '●'; color: var(--tw-aqua); font-size: 0.7em; margin-right: 4px; }
.gm-chip i { font-style: normal; color: var(--tw-ink-3); margin-left: 4px; }
.gm-gd-facts { margin: 0; padding-left: var(--tw-4); color: var(--tw-ink-2); font-size: var(--tw-fs-md); line-height: 1.5; }
.gm-gd-order { color: var(--tw-sun); font-size: var(--tw-fs-sm); }
.gm-gd-map { display: grid; grid-template-columns: ${ MAP_PX }px 1fr; gap: var(--tw-4); align-items: start; }
.gm-gd-map canvas { width: ${ MAP_PX }px; max-width: 100%; aspect-ratio: 1; border-radius: var(--tw-r-md, 10px); background: #0b2c48; }
.gm-gd-map p { margin: 0 0 var(--tw-2); color: var(--tw-ink-3); font-size: var(--tw-fs-sm); line-height: 1.45; }
.gm-gd-hist { color: var(--tw-ink-2); font-size: var(--tw-fs-sm); line-height: 1.6; }
@media (max-width: 760px) { .gm-codex-body { grid-template-columns: 1fr; } .gm-codex-list { max-height: 22vh; } .gm-gd-top, .gm-gd-map { grid-template-columns: 1fr; } }
`;

const h = ( tag, cls, html ) => {

	const e = document.createElement( tag );
	if ( cls ) e.className = cls;
	if ( html !== undefined ) e.innerHTML = html;
	return e;

};

const esc = ( s ) => String( s ).replace( /[&<>"]/g, ( c ) => ( { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[ c ] ) );
const pips = ( level ) => '●'.repeat( level ) + `<i>${ '●'.repeat( MAX_LEVEL - level ) }</i>`;
const clock = ( hours ) => `${ String( Math.floor( hours ) % 24 ).padStart( 2, '0' ) }:${ String( Math.floor( ( hours - Math.floor( hours ) ) * 60 ) ).padStart( 2, '0' ) }`;

function releaseMouse() {

	try {

		if ( document.pointerLockElement && document.exitPointerLock ) document.exitPointerLock();

	} catch ( e ) { /* ignore */ }

}

export class FishGuide {

	constructor( hud ) {

		this.hud = hud;
		this.open = false;
		this.selected = FISH_IDS[ 0 ];
		this._grid = new Map(); // species + box -> the habitat estimate grid
		const style = h( 'style' );
		style.textContent = CSS;
		document.head.append( style );
		this.el = h( 'div', 'gm-panel gm-codex tw-glass tw-interactive' );
		hud.ui.root.append( this.el );

	}

	get game() {

		return this.hud.game;

	}

	toggle( force ) {

		const on = force ?? ! this.open;
		if ( on === this.open ) return;
		this.open = on;
		if ( on ) {

			this.hud.closeStand();
			this.hud.toggleInventory( false );
			// start on something you know: the species you caught last, else the first you have
			const log = this.game.state.log;
			const last = this.game.state.lastCatch;
			if ( last && log[ last.species ] ) this.selected = last.species;
			else if ( ! ( log[ this.selected ] && log[ this.selected ].count ) ) this.selected = FISH_IDS.find( ( id ) => log[ id ] && log[ id ].count ) || FISH_IDS[ 0 ];
			this.render();

		}

		this.el.classList.toggle( 'is-open', on );
		if ( on ) releaseMouse();

	}

	select( id ) {

		if ( ! FISH[ id ] ) return;
		this.selected = id;
		this.render();

	}

	// the log or the order changed while it is open
	refresh() {

		if ( this.open ) this.render();

	}

	// per frame while open: a portrait that was not ready yet turns up
	tick() {

		if ( ! this.open || ! this._pendingThumb ) return;
		const p = this.hud.portrait;
		const url = p && p.thumbUrl( this._pendingThumb.id );
		if ( url ) {

			this._pendingThumb.img.src = url;
			this._pendingThumb = null;

		}

	}

	_portrait() {

		if ( ! this.hud.portrait ) {

			try { this.hud.portrait = new FishPortrait(); } catch ( e ) { /* no GPU here (the dev page): no portraits */ }

		}

		return this.hud.portrait;

	}

	render() {

		const s = this.game.state;
		const order = s.todaysOrder;
		const items = FISH_IDS.map( ( id ) => {

			const k = knowledge( id, s.log[ id ] );
			const star = order && order.species === id ? '<i class="gm-star" title="Joe\'s order today">★</i>' : '';
			return `<button class="gm-gi${ id === this.selected ? ' is-sel' : '' }${ k.level ? '' : ' is-unknown' }" data-fish="${ id }"><span class="gm-gi-name">${ esc( k.name ) }${ k.level ? star : '' }</span><span class="gm-gi-pips">${ pips( k.level ) }</span></button>`;

		} ).join( '' );
		const known = FISH_IDS.filter( ( id ) => s.log[ id ] && s.log[ id ].count > 0 ).length;
		this.el.innerHTML = `
			<div class="gm-codex-head"><h2>Fish guide</h2><p class="gm-sub">${ known } of ${ FISH_IDS.length } species caught</p></div>
			<div class="gm-codex-body"><div class="gm-codex-list">${ items }</div><div class="gm-codex-detail"></div></div>
			<div class="gm-foot"><span class="gm-sub">Catch more of a fish, and sell it, to learn more about it</span><button class="gm-btn is-ghost" data-close>Close (<span data-bind="codex">J</span>)</button></div>`;
		this.el.querySelector( '[data-close]' ).onclick = () => this.toggle( false );
		for ( const b of this.el.querySelectorAll( '[data-fish]' ) ) b.onclick = () => this.select( b.dataset.fish );
		this._detail( this.el.querySelector( '.gm-codex-detail' ), knowledge( this.selected, s.log[ this.selected ] ), order );
		this.hud._resolve( this.el );
		const sel = this.el.querySelector( '.gm-gi.is-sel' );
		if ( sel ) sel.scrollIntoView( { block: 'nearest' } );

	}

	_detail( el, k, order ) {

		const f = FISH[ k.id ];
		const unknown = k.level < 1;
		const next = k.toNext === null ? 'Fully studied' : `${ k.toNext } more ${ k.toNext === 1 ? 'catch' : 'catches' } to learn more`;
		const orderNote = ! unknown && order && order.species === k.id
			? `<div class="gm-gd-order">★ Joe wants one of ${ fmtKg( order.minKg ) } or bigger today: ×${ ORDER_MULT } on every one you sell.</div>` : '';
		let body;
		if ( unknown ) {

			body = '<p class="gm-gd-blurb">You have not caught one of these yet. Catch one to start its entry.</p>';

		} else {

			const where = k.habitat.none ? 'Only comes up in a pot'
				: k.habitat.list.map( ( x ) => `<span class="gm-chip${ x.seen ? ' is-seen' : '' }">${ esc( x.label ) }${ x.strength ? `<i>${ Math.round( x.strength * 100 ) }%</i>` : '' }</span>` ).join( '' );
			const seen = k.time.seen.map( ( p ) => PERIOD_LABEL[ p ] ).join( ', ' );
			const bestLine = k.best ? `Your biggest: ${ fmtKg( k.best.kg ) }${ k.best.cm ? ` · ${ k.best.cm } cm` : '' }` : '';
			const priceNote = k.price.exact ? 'Worked out from your sales' : k.sold ? `Sell more to firm this up (${ k.sold } sold)` : 'A guess: sell some to Joe to learn it';
			const histRows = [];
			if ( k.first ) histRows.push( `First caught on day ${ k.first.day } at ${ clock( k.first.hour ) }.` );
			histRows.push( `${ k.caught } caught · ${ k.sold } sold${ k.earned ? ` for $${ Math.round( k.earned ) }` : '' }.` );
			body = `
				<p class="gm-gd-blurb">${ esc( k.blurb ) }</p>
				<dl class="gm-gd-grid">
					<dt>Size</dt><dd>${ sizeText( k.size ) }<small>${ bestLine }</small></dd>
					<dt>Price</dt><dd>${ priceText( k.price ) }<small>${ priceNote }</small></dd>
					<dt>Found in</dt><dd>${ where || '<span class="is-locked">?</span>' }${ ! k.habitat.exact && ! k.habitat.none ? '<small>There may be more places</small>' : '' }</dd>
					<dt>Active</dt><dd>${ k.time.text ? esc( k.time.text ) : '<span class="is-locked">Not worked out yet</span>' }<small>${ seen ? `You have caught it at ${ seen }` : '' }</small></dd>
				</dl>
				${ k.facts.length ? `<ul class="gm-gd-facts">${ k.facts.map( ( t ) => `<li>${ esc( t ) }</li>` ).join( '' ) }</ul>` : '' }
				${ orderNote }
				<div class="gm-gd-map"><canvas width="${ MAP_PX }" height="${ MAP_PX }"></canvas><div><p>${ this._mapCaption( k ) }</p><div class="gm-gd-hist">${ histRows.join( '<br>' ) }</div></div></div>`;

		}

		el.innerHTML = `
			<div class="gm-gd-top">
				<img class="gm-gd-img${ unknown ? ' is-silhouette' : '' }" alt="" width="360" height="170">
				<div><h3>${ esc( k.name ) }</h3><div class="gm-gd-sci">${ esc( k.sci || '' ) }</div>
				<div class="gm-gd-level">${ k.levelName } <b>${ pips( k.level ) }</b><small>${ next }</small></div></div>
			</div>${ body }`;
		const img = el.querySelector( '.gm-gd-img' );
		this._pendingThumb = null;
		const p = this._portrait();
		const url = p && p.thumbUrl( k.id );
		if ( url ) img.src = url;
		else if ( p ) this._pendingThumb = { id: k.id, img };
		const canvas = el.querySelector( '.gm-gd-map canvas' );
		if ( canvas ) this._drawMap( canvas, k );

	}

	_mapCaption( k ) {

		const has = k.catches.some( ( c ) => Number.isFinite( c.x ) );
		const table = FISH[ k.id ].habitat || {};
		const est = k.mapBlock && Object.keys( table ).length
			? ( k.level >= MAX_LEVEL ? 'The shading is its water, as well as you know it.' : 'The shading is a guess at its water: it sharpens as you learn the fish.' ) : '';
		const dots = has ? 'Dots are where you caught it; the ring is your biggest.' : 'No catch positions recorded yet.';
		return `${ dots } ${ est }`;

	}

	_drawMap( canvas, k ) {

		const ctx = canvas.getContext( '2d' );
		if ( ! ctx ) return;
		const W = canvas.width;
		const { N, EXT, X0, Z0 } = MAP_GEOM;
		const ppm = N / EXT;
		const box = mapBox( k.catches, { extent: EXT, x0: X0, z0: Z0 } );
		const mm = this.game.minimap;
		ctx.fillStyle = '#0b2c48';
		ctx.fillRect( 0, 0, W, W );
		if ( mm && mm._bake && mm._bake.done ) ctx.drawImage( mm.canvas, ( box.x0 - X0 ) * ppm, ( box.z0 - Z0 ) * ppm, box.size * ppm, box.size * ppm, 0, 0, W, W );

		// the habitat estimate, blockier the less is known
		const f = FISH[ k.id ];
		if ( k.mapBlock > 0 && Object.keys( f.habitat || {} ).length ) {

			const key = `${ k.id }:${ Math.round( box.x0 ) }:${ Math.round( box.z0 ) }:${ Math.round( box.size ) }`;
			let grid = this._grid.get( key );
			if ( ! grid ) {

				grid = habitatGrid( k.id, box, GRID, ( x, z ) => this._habitatAt( x, z ) );
				this._grid.set( key, grid );

			}

			const shown = coarsen( grid, GRID, k.mapBlock );
			const off = document.createElement( 'canvas' );
			off.width = off.height = GRID;
			const octx = off.getContext( '2d' );
			const img = octx.createImageData( GRID, GRID );
			for ( let i = 0; i < GRID * GRID; i ++ ) {

				const v = shown[ i ];
				img.data[ i * 4 ] = 80; img.data[ i * 4 + 1 ] = 235; img.data[ i * 4 + 2 ] = 220; img.data[ i * 4 + 3 ] = Math.round( 170 * Math.min( 1, v * 1.2 ) );

			}

			octx.putImageData( img, 0, 0 );
			ctx.imageSmoothingEnabled = k.level >= MAX_LEVEL;
			ctx.drawImage( off, 0, 0, W, W );
			ctx.imageSmoothingEnabled = true;

		}

		// your catches
		let best = null;
		for ( const c of k.catches ) if ( Number.isFinite( c.x ) && ( ! best || c.kg > best.kg ) ) best = c;
		const at = ( c ) => [ ( c.x - box.x0 ) / box.size * W, ( c.z - box.z0 ) / box.size * W ];
		for ( const c of k.catches ) {

			if ( ! Number.isFinite( c.x ) ) continue;
			const [ x, y ] = at( c );
			ctx.beginPath(); ctx.arc( x, y, 4, 0, Math.PI * 2 );
			ctx.fillStyle = 'rgba(255,255,255,0.92)'; ctx.fill();
			ctx.lineWidth = 1.5; ctx.strokeStyle = 'rgba(8,20,30,0.8)'; ctx.stroke();

		}

		if ( best ) {

			const [ x, y ] = at( best );
			ctx.beginPath(); ctx.arc( x, y, 8, 0, Math.PI * 2 );
			ctx.lineWidth = 2.5; ctx.strokeStyle = '#f2c14e'; ctx.stroke();

		}

	}

	// the habitat weights at a point, or null on land (the map's estimate needs the terrain)
	_habitatAt( x, z ) {

		const t = this.game.app.terrainData;
		if ( ! t ) return null;
		const depth = - t.heightAt( x, z );
		return depth < 0.25 ? null : this.game.habitatAtPoint( x, z, depth );

	}

}

