// The controller's menu layer: walking the settings panel, the control sheet, the guide and the HUD
// panels with a d-pad, and driving whatever is focused.
//
// The interface already works by real DOM focus (every control row has one natively focusable element,
// the tab strip moves focus between tabs, the sliders and selects answer arrow keys, the HUD panels are
// real buttons), so this does not reimplement navigation: it moves focus and dispatches the same
// navigation events a keyboard would produce. Those events carry `__pad`, which core/Input.js ignores,
// so a synthetic Space cannot make the player jump.
//
// While any panel owns the input, `input.menuMode` is set: the actions that would walk, look or flip a
// switch behind the interface go dead (see the binding table's `menu` policy), and the ones the panels
// are closed with stay live.
const DPAD = { up: 'DUp', down: 'DDown', left: 'DLeft', right: 'DRight' };

export class PadUI {

	constructor( app ) {

		this.app = app;
		this.index = - 1;
		this.mode = null; // which surface owns the input right now
		this._list = [];
		this._edges = {};

	}

	// called every frame from App._frame, right after the pad is polled and before anything reads input
	update() {

		const app = this.app, inp = app.input;
		const ui = app.ui && app.ui.ui;
		if ( ! ui ) return;

		const hud = app.game && app.game.hud;
		const guide = app.game && app.game.guide;
		// which surface owns the pad: the overlays first (they are modal), then the settings panel, then
		// the HUD panels. The catch card is deliberately not in here: a keyboard player dismisses it by
		// pressing the cast button or Escape, which on a pad is RT or B, through the same action path.
		const mode = ui._start ? 'start'
			: guide && guide.open ? 'guide'
			: ui.helpOpen ? 'help'
			: ui.panelOpen ? 'panel'
			: hud && ( hud.invOpen || hud.standOpen || hud.guideOpen ) ? 'hud'
			: null;

		// the world goes quiet while a panel owns the input (a keyboard player gets this too: T used to
		// pause the day with the panel open, and a pad's d-pad would flip switches behind the interface)
		inp.menuMode = mode !== null;

		if ( this.mode !== mode ) {

			this.index = - 1;
			this._list = [];
			this.mode = mode;

		}

		if ( mode === null ) return;
		// a keyboard player is never moved around by a resting stick: the pad only steps in when it is
		// the device in hand (the start overlay is the exception: it is waiting for the first press)
		if ( inp.device !== 'pad' && mode !== 'start' ) return;

		const pad = inp.pad;
		const edge = ( key, on ) => {

			const was = !! this._edges[ key ];
			this._edges[ key ] = !! on;
			return !! on && ! was;

		};

		// the d-pad and the left stick both walk; a held direction steps once, not like key auto-repeat
		const up = edge( 'up', pad.isPressed( DPAD.up ) || pad.axes.LSY < - 0.7 );
		const down = edge( 'down', pad.isPressed( DPAD.down ) || pad.axes.LSY > 0.7 );
		const left = edge( 'left', pad.isPressed( DPAD.left ) || pad.axes.LSX < - 0.7 );
		const right = edge( 'right', pad.isPressed( DPAD.right ) || pad.axes.LSX > 0.7 );
		const confirm = edge( 'confirm', pad.isPressed( 'A' ) );
		const back = edge( 'back', pad.isPressed( 'B' ) );
		const tabL = edge( 'tabL', pad.isPressed( 'LB' ) );
		const tabR = edge( 'tabR', pad.isPressed( 'RB' ) );

		if ( mode === 'start' ) {

			if ( confirm || back ) dispatchKey( window, 'Enter' ); // the overlay wants a real gesture
			return;

		}

		if ( mode === 'guide' ) {

			if ( right || confirm ) dispatchKey( window, 'ArrowRight' );
			else if ( left ) dispatchKey( window, 'ArrowLeft' );
			else if ( back ) dispatchKey( window, 'Escape' );
			return;

		}

		if ( mode === 'hud' ) this._rescanHud( hud );
		else this._rescan( ui );

		if ( up ) this._walk( - 1 );
		if ( down ) this._walk( 1 );
		if ( tabL ) this._tab( ui, - 1 );
		if ( tabR ) this._tab( ui, 1 );
		if ( left ) this._nudge( 'ArrowLeft' );
		if ( right ) this._nudge( 'ArrowRight' );
		if ( confirm ) this._confirm();
		if ( back ) dispatchKey( window, 'Escape' );

	}

	// ---- the lists

	// Rows come from the toolkit: the visible controls of the active tab, plus the folder headings, so a
	// collapsed folder can still be opened (A on the heading) from the pad.
	_rescan( ui ) {

		const tab = ui.activeTab;
		if ( ! tab ) return;
		const list = [];
		for ( const c of tab.controls() ) {

			if ( ! c._isShown() ) continue;
			const el = focusTarget( c );
			if ( el ) list.push( el );

		}

		const page = tab.page || tab.el;
		if ( page ) for ( const head of page.querySelectorAll( '.tw-folder-head' ) ) if ( head.offsetParent !== null ) list.push( head );

		this._set( list );

	}

	_rescanHud( hud ) {

		const panel = hud.guideOpen ? hud.fishGuide.el : hud.invOpen ? hud.inv : hud.stand;
		if ( ! panel ) return;
		this._set( [ ...panel.querySelectorAll( 'button[data-fish], button[data-sell], button[data-all], button[data-buy], button[data-fuel], button[data-traps], button[data-release], button[data-close]' ) ].filter( ( b ) => ! b.disabled ) );

	}

	_set( list ) {

		if ( list.length === this._list.length && list.every( ( el, i ) => el === this._list[ i ] ) ) return;
		const focused = document.activeElement;
		this._list = list;
		const at = list.indexOf( focused );
		this.index = at; // keep the walker on whatever the player last touched

	}

	// ---- the verbs

	_walk( d ) {

		if ( ! this._list.length ) return;
		this.index = this.index < 0 ? ( d > 0 ? 0 : this._list.length - 1 ) : ( this.index + d + this._list.length ) % this._list.length;
		const el = this._list[ this.index ];
		if ( ! el ) return;
		el.focus( { preventScroll: true } );
		el.scrollIntoView( { block: 'nearest' } );
		this.app.input.pad.rumble( { strong: 0, weak: 0.12, ms: 30, cooldown: 40 } );

	}

	_tab( ui, d ) {

		const ids = [ ...ui.tabs.keys() ].filter( ( id ) => ! ui.tabs.get( id ).btn.hidden );
		const i = ids.indexOf( ui.activeTab && ui.activeTab.id );
		const next = ids[ ( i + d + ids.length ) % ids.length ];
		if ( ! next ) return;
		ui.selectTab( next );
		this.index = - 1;
		this._list = [];

	}

	// a slider, a select or the time dial answers an arrow key on the focused element
	_nudge( code ) {

		const el = document.activeElement;
		if ( el ) dispatchKey( el, code );

	}

	_confirm() {

		const el = document.activeElement;
		if ( ! el ) return;
		// a synthetic keydown does not press a real <button> in any browser: click it instead
		if ( el.tagName === 'BUTTON' || el.tagName === 'INPUT' || el.classList.contains( 'tw-folder-head' ) ) el.click();
		else if ( el.classList.contains( 'tw-ctl-binding' ) ) el.click(); // start capturing a binding
		else dispatchKey( el, 'ArrowDown' ); // a closed dropdown opens on ArrowDown

	}

}

// the element a control is driven through, by type (see the toolkit's DOM in ui/UI.js)
function focusTarget( c ) {

	switch ( c.type ) {

		case 'slider': return c.track;
		case 'toggle': return c.sw;
		case 'binding': return c.el;
		case 'select': return c.btns ? c.btns[ 0 ] : c.btn;
		case 'color': return c.input;
		case 'button': return c.btn;
		case 'presets': return c.chips ? ( c.chips[ typeof c.active === 'number' ? c.active : 0 ] || c.chips[ 0 ] ) : null;
		case 'time': return c.svg;
		default: return null; // info rows are labels

	}

}

// A navigation event as the interface would have received it from a keyboard. Both fields are set: some
// handlers read `key`, others `code`. `__pad` marks it as ours, and core/Input.js ignores those.
function dispatchKey( target, code ) {

	const el = target && target.dispatchEvent ? target : document.activeElement;
	if ( ! el ) return;
	try {

		const ev = new KeyboardEvent( 'keydown', { key: keyName( code ), code, bubbles: true, cancelable: true } );
		ev.__pad = true;
		el.dispatchEvent( ev );

	} catch ( e ) { /* an element that refuses events costs a menu press, nothing more */ }

}

function keyName( code ) {

	if ( code.startsWith( 'Arrow' ) ) return code;
	if ( code === 'Space' ) return ' ';
	return code;

}
