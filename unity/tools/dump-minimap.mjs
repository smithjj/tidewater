// Oracle for the C# minimap (Runtime/Game/Minimap.cs): src/game/Minimap.js, run for real against a stub DOM and a stub game, for comparison by
// unity/Assets/Tidewater/Editor/MinimapOracle.cs:
//   unity/tools/minimap-oracle.sh   (runs this, then the comparer)
// Writes bake.rgba (the 640 x 640 RGBA the bake hands to putImageData: the colours, hill shading and coastline, before the pads and the pier are drawn on
// the canvas), minimap.json (the 2D canvas calls that follow, and a scripted session: per frame the inputs and what the JS left on the elements after
// update(): the canvas transform, the markers' transforms and classes, the N, the fish rings, the arrow in the middle, the label).
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve( path.dirname( fileURLToPath( import.meta.url ) ), '../..' );
const out = process.argv[ 2 ] || '.';
fs.mkdirSync( out, { recursive: true } );

// ---- a stub DOM, as much of it as Minimap.js touches
const captured = { image: null, calls: [] };
class El {

	constructor( tag ) {

		this.tag = tag; this.style = {}; this.classes = new Set(); this._q = {}; this.clientWidth = 0; this.textContent = ''; this.firstChild = null;
		this.classList = { toggle: ( c, on ) => { if ( on === undefined ) on = ! this.classes.has( c ); if ( on ) this.classes.add( c ); else this.classes.delete( c ); return on; } };

	}

	set className( v ) { this.classes = new Set( String( v ).split( /\s+/ ).filter( Boolean ) ); }
	set innerHTML( v ) { this._html = v; this.firstChild = new El( 'child' ); }
	append() {}
	prepend() {}
	setAttribute() {}
	querySelector( sel ) {

		if ( ! this._q[ sel ] ) {

			const e = new El( sel );
			if ( sel === 'canvas' ) e.getContext = () => ctx;
			this._q[ sel ] = e;

		}

		return this._q[ sel ];

	}

}

const ctx = {
	fillStyle: '', strokeStyle: '', lineWidth: 1,
	createImageData: ( w, h ) => ( { width: w, height: h, data: new Uint8ClampedArray( w * h * 4 ) } ),
	putImageData: ( img ) => { captured.image = Buffer.from( img.data ); },
	fillRect( x, y, w, h ) { captured.calls.push( [ 'fillRect', this.fillStyle, x, y, w, h ] ); },
	strokeRect( x, y, w, h ) { captured.calls.push( [ 'strokeRect', this.strokeStyle, this.lineWidth, x, y, w, h ] ); },
};

globalThis.document = { createElement: ( t ) => new El( t ), head: { append() {} } };

const { Minimap, MAP_GEOM } = await import( root + '/src/game/Minimap.js' );
const { TerrainData } = await import( root + '/src/world/TerrainData.js' );
const { TRAP_LIMIT } = await import( root + '/src/game/Gear.js' );

const T = new TerrainData( 7 );
// the village's pads are only added when the village is built (World.js); a few stand-ins, one small enough for the 1.5 px minimum
const PADS = [ { x: 72, z: -80, radius: 6, height: 2.4 }, { x: 95, z: -92, radius: 4.5, height: 2.6 }, { x: 40, z: -110, radius: 1.2, height: 3 }, { x: 120, z: -70, radius: 8, height: 2.1 } ];
for ( const p of PADS ) T.pads.push( { ...p } );

// ---- the scripted session
function mulberry32( seed ) {

	let a = seed >>> 0;
	return () => {

		a = ( a + 0x6D2B79F5 ) >>> 0;
		let t = a;
		t = Math.imul( t ^ ( t >>> 15 ), t | 1 );
		t ^= t + Math.imul( t ^ ( t >>> 7 ), t | 61 );
		return ( ( t ^ ( t >>> 14 ) ) >>> 0 ) / 4294967296;

	};

}

const rnd = mulberry32( 20260411 );
const groups = [
	{ center: { x: 60, y: -8, z: 120 }, count: 40, radius: 6, sp: { mode: 'school', model: 'tuna', name: 'Tuna' } },
	{ center: { x: -40, y: -5, z: 80 }, count: 12, radius: 4, sp: { mode: 'school', model: 'grouper', name: 'Grouper' } },
	{ center: { x: 90, y: -3, z: 60 }, count: 3, radius: 3, sp: { mode: 'stingray', model: 'stingray', name: 'Stingray' } },
	{ center: { x: 70, y: -3, z: 70 }, count: 2, radius: 3, sp: { mode: 'escort', model: 'remora', name: 'Remora' } },
	{ center: { x: 0, y: -12, z: 200 }, count: 80, radius: 9, sp: { mode: 'school', model: 'mahi', name: 'Mahi' } },
	{ center: { x: 55, y: -6, z: 125 }, count: 20, radius: 5, sp: { mode: 'school', model: 'jack', name: 'Jack' } },
	{ center: { x: -120, y: -9, z: 150 }, count: 30, radius: 6, sp: { mode: 'school', model: 'mullet', name: 'Mullet' } },
];

const frames = [];
let px = 49.9, pz = -70, yaw = 0.3; // sim: yaw 0 looks north (-z)
for ( let f = 0; f < 1100; f ++ ) {

	const dt = f % 97 === 5 ? 0.25 : 1 / 60 * ( 0.6 + rnd() * 0.9 );
	let mode = 'walk';
	if ( f >= 320 && f < 420 ) mode = 'swim';
	if ( f >= 420 && f < 760 ) mode = 'boat';
	if ( f >= 760 && f < 840 ) mode = 'deck';
	// wander: heading changes slowly, a turn now and then
	yaw += ( rnd() - 0.5 ) * 0.08 + ( f % 150 === 0 ? 1.4 : 0 );
	const sp = mode === 'walk' ? 0.08 : mode === 'swim' ? 0.05 : mode === 'boat' ? 0.4 : 0;
	if ( f < 300 ) { px += Math.sin( yaw ) * sp * 4; pz += - Math.cos( yaw ) * sp * 4; } else { px += Math.sin( yaw ) * sp * 2; pz += Math.max( 0, - Math.cos( yaw ) * sp * 2 + 0.25 ); }
	// the camera's forward (xz) as the matrix elements hold it: e[8], e[10] = the camera's local +z, so forward = -( e8, e10 )
	const pitch = ( f % 220 ) < 20 ? 1.5707963 : ( rnd() - 0.5 ) * 0.5; // now and then straight down: the forward on the ground vanishes
	const fx = Math.sin( yaw ) * Math.cos( pitch ), fz = - Math.cos( yaw ) * Math.cos( pitch );
	frames.push( {
		dt, x: px, z: pz, e8: - fx, e10: - fz, mode,
		size: f < 6 ? 0 : f < 500 ? 174 : f < 700 ? 118 : 546, // the view's size in css px (a ResizeObserver would keep it)
		keys: f === 500 ? [ 'map' ] : f === 640 ? [ 'cancel' ] : f === 900 ? [ 'map' ] : f === 905 ? [ 'map' ] : f === 1000 ? [ 'cancel' ] : [],
		finder: f >= 200 && f < 900,
		boat: f < 100 ? null : { x: 64.5 + Math.sin( f / 90 ) * 3, z: 36.5 },
		anchor: f >= 450 && f < 600 ? { down: true, x: px - 8, z: pz + 12 } : f >= 600 && f < 700 ? { down: false, x: 0, z: 0 } : null,
		sets: f < 150 ? [] : [ { x: 20 + f * 0.05, z: 90 }, null, { x: -50, z: 60 }, undefined, { x: 130, z: 140 }, { x: px + 300, z: pz + 300 } ].slice( 0, TRAP_LIMIT ),
		hot: f === 120 ? [ 'joe', 'marta' ] : f === 260 ? [] : f === 700 ? [ 'boat' ] : f === 800 ? [] : null,
	} );

}

const game = {
	app: {
		terrainData: T,
		camera: { matrixWorld: { elements: new Array( 16 ).fill( 0 ) }, position: { x: 0, y: 1.7, z: 0 } },
		player: { mode: 'walk', boat: null },
		boatCtl: null,
		reef: { fish: { groups } },
	},
	state: { stats: { finder: false }, sets: [] },
};

const map = new Minimap( new El( 'hud' ), game );
// the elements by id, to read back what update() left on them
const els = {
	canvas: map.canvas, me: map.me, north: map.north, label: map.label, el: map.el,
	marks: map.markers.map( ( m ) => [ m.id, m.el, m.arrow ] ), fish: map.fish,
};

const snap = () => ( {
	radiusM: map.radiusM, big: map.big, size: map._viewSize,
	canvas: els.canvas.__gm_transform ?? null, me: els.me.__gm_transform ?? null, north: els.north.__gm_transform ?? null, label: map.label.textContent,
	marks: els.marks.map( ( [ id, e, a ] ) => [ id, e.__gm_display ?? null, e.__gm_transform ?? null, e.classes.has( 'is-edge' ), e.classes.has( 'is-hot' ), a.__gm_transform ?? null ] ),
	fish: els.fish.map( ( e ) => [ e.classes.has( 'is-on' ), e.__gm_transform ?? null ] ),
	pts: map._fishPts.map( ( q ) => [ q.x, q.z ] ),
} );

const expect = [];
let baked = - 1;
for ( let f = 0; f < frames.length; f ++ ) {

	const fr = frames[ f ];
	map.view.clientWidth = fr.size;
	const E = game.app.camera.matrixWorld.elements;
	E[ 8 ] = fr.e8; E[ 10 ] = fr.e10;
	game.app.camera.position.x = fr.x; game.app.camera.position.z = fr.z;
	game.app.player.mode = fr.mode;
	game.app.player.boat = fr.anchor ? { anchor: fr.anchor } : null;
	game.app.boatCtl = fr.boat ? { position: { x: fr.boat.x, z: fr.boat.z } } : null;
	game.state.stats.finder = fr.finder;
	game.state.sets = fr.sets;
	for ( const k of fr.keys ) map.toggleBig( k === 'map' ? 'N' : '', k === 'map' ? undefined : false );
	if ( fr.hot ) map.highlight( fr.hot );
	map.update( fr.dt );
	if ( baked < 0 && map._bake.done ) baked = f;
	expect.push( snap() );

}

fs.writeFileSync( path.join( out, 'bake.rgba' ), captured.image );
fs.writeFileSync( path.join( out, 'minimap.json' ), JSON.stringify( { geom: MAP_GEOM, pads: PADS, bakedAtFrame: baked, calls: captured.calls, trapLimit: TRAP_LIMIT, groups, frames, expect } ) );
console.log( `bake: ${ captured.image.length } bytes at frame ${ baked }, ${ captured.calls.length } canvas calls; ${ frames.length } frames` );

