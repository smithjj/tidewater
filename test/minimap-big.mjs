// The large map (N): opens and closes, and is north up (the minimap itself turns with the view), in plain
// node with stand-ins for the DOM and the camera.
//   node test/minimap-big.mjs
import { Minimap } from '../src/game/Minimap.js';
import { ACTIONS } from '../src/core/Bindings.js';

let fails = 0;
const ok = ( c, msg ) => {

	if ( ! c ) { fails ++; console.log( 'FAIL', msg ); } else console.log( 'ok  ', msg );

};

ok( ACTIONS.some( ( a ) => a.id === 'map' && a.kb.includes( 'KeyN' ) ), 'the map action is bound to N' );
const dup = ACTIONS.filter( ( a ) => a.id !== 'map' && ( a.kb || [] ).some( ( k ) => ( k.v || k ) === 'KeyN' ) );
ok( dup.length === 0, 'N is not used by anything else' );

const el = () => ( { style: {}, classList: { on: new Set(), toggle( c, v ) { v ? this.on.add( c ) : this.on.delete( c ); } }, textContent: '' } );
const mk = ( yaw ) => {

	const m = Object.create( Minimap.prototype );
	// camera looking along (sin yaw, -cos yaw): yaw 0 is north, a quarter turn is east
	const fx = Math.sin( yaw ), fz = - Math.cos( yaw );
	const mat = new Array( 16 ).fill( 0 );
	mat[ 8 ] = - fx; mat[ 10 ] = - fz;
	Object.assign( m, {
		el: el(), label: el(), me: el(), canvas: el(), north: el(), markers: [], fish: [], _fishPts: [], _fishT: 0, _hot: new Set(),
		big: false, radiusM: 110, _viewSize: 200, _ro: {}, _bake: { done: true },
		game: { state: { stats: { finder: false } }, app: { camera: { matrixWorld: { elements: mat }, position: { x: 10, z: 20 } }, player: { mode: 'walk' } } },
	} );
	return m;

};

{

	const m = mk( 0 );
	m.toggleBig( 'N' );
	ok( m.big && m.el.classList.on.has( 'is-big' ) && m.label.textContent.includes( 'N' ), 'toggling opens it with a hint of the key' );
	m.toggleBig( '', false );
	ok( ! m.big && ! m.el.classList.on.has( 'is-big' ) && m.label.textContent === '', 'closing it again clears it' );
	m.toggleBig( '', false );
	ok( ! m.big, 'closing a closed map does nothing' );

}

{

	// facing east: the small map turns (north no longer up), the large one keeps north up and turns the arrow
	const small = mk( Math.PI / 2 );
	small.update( 0.016 );
	const big = mk( Math.PI / 2 );
	big.toggleBig( 'N' );
	big.update( 0.016 );
	const rot = ( m ) => Number( /rotate\((-?[\d.e-]+)rad\)/.exec( m.canvas.style.transform )[ 1 ] );
	ok( Math.abs( rot( big ) - ( - Math.PI / 2 - Math.atan2( - 1, 0 ) ) ) < 1e-9, 'the large map is fixed north up (rotation of the map frame is constant)' );
	ok( Math.abs( rot( small ) - rot( big ) ) > 1, 'while the small one turns with the view' );
	ok( Math.abs( Number( /rotate\((-?[\d.e-]+)rad\)/.exec( big.me.style.transform )[ 1 ] ) - Math.PI / 2 ) < 1e-9, 'the arrow in the middle points east instead' );
	ok( small.me.style.transform === '', 'the small map\'s arrow stays pointing up' );
	big.update( 10 );
	ok( big.radiusM > 300, 'the large map shows a wider stretch of sea' );
	big.toggleBig( '', false );
	for ( let i = 0; i < 20; i ++ ) big.update( 1 );
	ok( Math.abs( big.radiusM - 110 ) < 1, 'and the radius eases back when it closes' );

}

console.log( fails ? `${ fails } FAILED` : 'all ok' );
process.exit( fails ? 1 : 0 );
