// The trap line: lobstering. Pots go over the side from the working boat, soak on the clock, and
// come back aboard with whatever crawled in (see the rules at the top, which are pure functions so
// the tests can drive them without a GPU).
//
//  - A set is one pot in the water: `state.sets` is the source of truth, this class draws it.
//  - The pots are the modelled trap (assets/lobster_trap_decimated.glb): a set pot on the seabed, the
//    stack on the working boat's deck, and the one that comes up on the hauler are the same geometry.
//  - The same file also holds the trap's buoy with its line and marker: the floating marker is taken
//    from it, split off by material family (the rope down to the pot stays procedural, because it is
//    scaled to the depth here).
//  - Each pot has its own buoy line, riding the same water readback the boats use.
import { Group, Mesh, Vector3, Color } from '../engine/index.js';
import { box, rod, cylinder, sphere, prepare, mergePrepared, mat4 } from '../world/boat/GeoKit.js';
import { TRAP, TRAPS } from '../world/boat/DeckGear.js';
import { loadStaticModel } from '../world/StaticGLB.js';
import { createPropMaterial } from './GameMaterials.js';
import { rollWeight, pickSpecies } from './Bites.js';
import { TRAP_LIMIT } from './Gear.js';

const BASE = ( import.meta.env && import.meta.env.BASE_URL ) || '/';
const MODEL_URL = BASE + 'models/props/lobster_trap_decimated.glb'; // the pot (and, unused, its own buoy)
const BUOY_URL = BASE + 'models/props/lobster_trap_buoy.glb'; // the marker float: its own export
const POT_LENGTH = 0.98; // m, the modelled pot scaled to a working size (it exports at ~1.5 m)
const BUOY_WIDTH = 0.34; // m across the float
// m of the marker that sits below the surface. The float is ~0.65 m tall, so a third of it in the water
// is what gives it a waterline: at a few centimetres it skims and reads as hovering above the sea.
const BUOY_SINK = 0.22;

// The decimated file is the trap *and* its buoy (231 k triangles for the pair): the pot is taken from it
// by material family, and the marker comes from the buoy's own export. The loader hands each material
// name to the skip predicate, which is how the halves are told apart.
const TRAP_MATS = /^(WOOD|TWINE|CORD|IRON) \|/;
const SKIP_MATS = ( n ) => /^SETTING \|/.test( n ); // the scene's backdrop (a pegged plank)

// Two materials in these exports carry no colour factor at all while every other material has one, and
// both fall back to the same grey — so they are painted here, the way each is named. (A re-export with
// the factors on would let the models carry them and this block could go.)
const FLOAT = /^FLOAT \|/; // worn red and yellow marine enamel: a red body with a yellow top
const FLOAT_STYLE = { body: 0xb0392a, band: 0xe3b93c, bandFrom: 0.55 };
const PALE_WOOD = /^WOOD \| Salt-silvered/; // salt-silvered oak: pale weathered grey-brown, not white
const PALE_WOOD_STYLE = { body: 0xa89a86 };

// a pot needs a few hours to fish; a full soak fills it up
export const SOAK_MIN = 1.5; // game hours before anything worthwhile is aboard
export const MAX_KEEP = 3; // most animals in one pot

// game hours a set has been soaking
export function soakHours( set, { hour, day } ) {

	return Math.max( 0, ( day * 24 + hour ) - ( set.day * 24 + set.clock ) );

}

// What is in the pot when it comes up: [ { species, kg } ] (empty on barren ground, or too soon).
// Lobsters want a bit of depth and hard ground; anything else that wanders in is whatever lives
// there, taken from the same habitat table the rod fishes.
export function haulYield( { soak, depth, habitat, hour, rng = Math.random } ) {

	if ( ! ( soak >= SOAK_MIN ) ) return [];
	const want = Math.min( MAX_KEEP, soak / 3.5 );
	let n = Math.floor( want );
	if ( rng() < want - n ) n ++;
	const out = [];
	for ( let i = 0; i < n; i ++ ) {

		const lobsterChance = depth < 2 ? 0.15 : depth > 35 ? 0.3 : 0.7;
		const species = rng() < lobsterChance ? 'lobster' : pickSpecies( habitat, hour, rng );
		if ( ! species ) continue; // nothing down there: the pot comes up light
		out.push( { species, kg: rollWeight( species, rng ) } );

	}

	return out;

}

export class Traps {

	constructor( { scene, terrain, query, state, toast = null, boat = null } ) {

		this.scene = scene;
		this.boat = boat; // the working boat: its deck carries the stack
		this.stack = [];
		this.terrain = terrain;
		this.query = query;
		this.state = state;
		this.toast = toast;

		this.group = new Group();
		this.group.name = 'Traps';
		scene.add( this.group );

		this.material = createPropMaterial( 'trap' );
		// the procedural pot and float are what is there until the model lands (and the fallback if it
		// cannot be fetched at all); the modelled ones replace them in place
		this.pot = buildPot();
		this.float = buildBuoy();
		this.rope = buildRope();

		this.views = new Map(); // set id -> { pot, float, rope, slot, yaw }
		this.slot = - 1;
		if ( query ) {

			try { this.slot = query.allocate( 'traps', TRAP_LIMIT ); }
			catch ( e ) { console.warn( 'Traps: no water query slots, buoys will sit at sea level', e ); }

		}

		// the modelled pot, and the place the haul animation happens
		this.holder = new Group();
		this.holder.name = 'Traps:haul';
		this.holder.visible = false;
		this.group.add( this.holder );
		this._anim = null;
		this._off = state.onChange( () => this.sync() );
		this.loadModels();
		this._buildStack(); // the procedural pots stand in until the model lands
		this.sync();

	}

	dispose() {

		if ( this._off ) this._off();

	}

	// ---- the world

	// one pot + buoy + line per set; called whenever the trap line changes
	sync() {

		const sets = this.state.sets;
		const want = new Set();
		for ( const s of sets ) want.add( s.id );
		for ( const [ id, v ] of this.views ) {

			if ( want.has( id ) ) continue;
			this.group.remove( v.pot, v.float, v.rope );
			this.views.delete( id );

		}

		let i = 0;
		for ( const s of sets ) {

			const index = i ++;
			if ( this.views.has( s.id ) ) continue;
			const v = {
				// one readback slot per pot, by position in the gear (they wrap at the limit)
				slot: this.slot >= 0 ? this.slot + ( index % TRAP_LIMIT ) : - 1,
				pot: new Mesh( this.pot, this.material ),
				float: new Mesh( this.float, this.material ),
				rope: new Mesh( this.rope, this.material ),
				yaw: ( hash( s.id ) % 997 ) / 997 * Math.PI * 2,
			};
			v.pot.castShadow = true;
			v.pot.position.set( s.x, this.terrain.heightAt( s.x, s.z ) + 0.05, s.z );
			v.pot.rotation.y = v.yaw;
			this.group.add( v.pot, v.float, v.rope );
			this.views.set( s.id, v );

		}

	}

	update( dt ) {

		this.sync(); // cheap when nothing changed; keeps up with traps set or hauled by other means

		for ( const s of this.state.sets ) {

			const v = this.views.get( s.id );
			if ( ! v ) continue;
			const bed = this.terrain.heightAt( s.x, s.z );
			let y = 0;
			if ( v.slot >= 0 && this.query ) {

				this.query.setPoint( v.slot, s.x, s.z );
				const h = this.query.cpu[ v.slot * 4 ];
				if ( Number.isFinite( h ) ) y = h;

			}

			v.float.position.set( s.x, y - BUOY_SINK, s.z );
			v.float.rotation.z = Math.sin( ( s.id + v.yaw ) * 3.1 ) * 0.12;
			const len = Math.max( 0.15, y - bed );
			v.rope.position.set( s.x, ( bed + y ) * 0.5, s.z );
			v.rope.scale.set( 1, len, 1 );

		}

		if ( this._anim ) this._step( dt );

	}

	// ---- the haul: the modelled pot comes up on the hauler and lands on the aft deck

	// The modelled trap and its buoy, in one file (6.7 MB). Loaded at once: the pots on the working
	// boat's deck are part of the furniture, so there is no "first time you need it" any more.
	loadModels() {

		if ( this._loading ) return;
		this._loading = true;
		Promise.all( [
			loadStaticModel( MODEL_URL, { skip: SKIP_MATS, name: 'trap' } ),
			loadStaticModel( BUOY_URL, { skip: SKIP_MATS, name: 'buoy' } ),
		] ).then( ( [ t, b ] ) => {

			const pot = bake( t, TRAP_MATS, POT_LENGTH, true, { paint: { match: PALE_WOOD, ...PALE_WOOD_STYLE } } );
			// the buoy exports with the marker stick below the float (its yellow tip points down), so it
			// is stood the right way up: the float rides at the surface with the stick above it
			const buoy = bake( b, /./, BUOY_WIDTH, false, { flip: true, paint: { match: FLOAT, ...FLOAT_STYLE } } );
			if ( pot ) this.pot = pot;
			if ( buoy ) this.float = buoy;
			// everything already in the world (set pots, their markers) and the deck stack take it up
			for ( const v of this.views.values() ) {

				v.pot.geometry = this.pot;
				v.float.geometry = this.float;

			}

			this._buildStack();

		} ).catch( ( e ) => console.warn( 'Traps: the modelled pot failed to load, using the procedural one', e ) );

	}

	// The working boat's deck carries the gear: four pots in the boat's own frame (world/boat/DeckGear.js
	// has the layout). Not baked into the hull, so the modelled trap can stand in for the procedural one.
	_buildStack() {

		if ( ! this.boat || ! this.boat.group ) return;
		for ( const mesh of this.stack ) mesh.removeFromParent();
		this.stack = [];
		for ( const [ x, level, z, yaw ] of TRAPS ) {

			const mesh = new Mesh( this.pot, this.material );
			mesh.position.set( x, this.boat.lines.deckY + 0.03 + level * ( TRAP.H + 0.035 ), z );
			mesh.rotation.y = yaw;
			mesh.castShadow = true;
			mesh.receiveShadow = true;
			this.boat.group.add( mesh );
			this.stack.push( mesh );

		}

	}

	_swapAnimMesh() {

		this.holder.clear();
		this.holder.add( new Mesh( this.pot, this.material ) );

	}

	// bring a pot up beside the stern and swing it onto the stack (the catch is the caller's job)
	haulVisual( boat ) {

		this._swapAnimMesh();
		this.holder.visible = true;
		this._anim = { t: 0, dur: 3.6, boat };

	}

	_step( dt ) {

		const a = this._anim;
		a.t += dt;
		const u = Math.min( 1, a.t / a.dur );
		// up out of the water under the davit, then aft onto the deck stack
		const up = smooth( 0, 0.42, u ), over = smooth( 0.42, 0.78, u ), away = smooth( 0.82, 1, u );
		const x = lerp( - 1.05, - 0.68, over );
		const y = lerp( - 1.25, 1.2, up ) + lerp( 0, - 0.82, over ) - away * 0.35;
		const z = lerp( - 0.8, - 3.2, over );
		const b = a.boat;
		if ( b && b.toWorld ) {

			_hero.set( x, y, z );
			b.toWorld( _hero, this.holder.position );
			this.holder.rotation.set( 0, ( b.getYaw ? b.getYaw() : 0 ) + 0.35 * Math.sin( u * 7 ) * ( 1 - u ), 0.12 * Math.sin( u * 5 ) * ( 1 - u ) );

		}

		if ( u >= 1 ) {

			this.holder.visible = false;
			this._anim = null;

		}

	}

}

const _hero = new Vector3();

// ---- the meshes

// One half of the loaded model (the trap, or its buoy), welded into a single geometry: the colours of
// the source materials become vertex colours the way the procedural gear does it (world/boat/DeckGear),
// so a pot stays one draw call. The result is centred on x/z with its base at y = 0 and scaled to
// `target` across its longest horizontal axis; the trap is turned so its length runs fore-aft.
export function bake( model, wanted, target, alongZ, { flip = false, paint = null } = {} ) { // exported for the loader check in test/

	const mats = ( model.info && model.info.materials ) || [];
	const prefix = model.root.name + '-';
	const parts = [];
	for ( const mesh of model.root.children ) {

		const name = mesh.name.startsWith( prefix ) ? mesh.name.slice( prefix.length ) : mesh.name;
		if ( ! wanted.test( name ) ) continue;
		const gltf = mats.find( ( m ) => m.name === name );
		const pbr = ( gltf && gltf.pbrMetallicRoughness ) || {};
		const c = pbr.baseColorFactor || [ 0.8, 0.8, 0.8 ];
		const part = prepare( mesh.geometry, {
			color: new Color().setRGB( c[ 0 ], c[ 1 ], c[ 2 ] ).getHex(),
			rough: pbr.roughnessFactor ?? 0.85,
			metal: pbr.metallicFactor ?? 0,
		} );
		if ( flip ) part.rotateX( Math.PI ); // 180° about x: a marker that exports stood on its head
		if ( paint && paint.match.test( name ) ) paintPart( part, paint );
		parts.push( part );

	}

	if ( ! parts.length ) return null;
	const geo = mergePrepared( parts );
	const size = geo.boundingBox.getSize( new Vector3() );
	const s = target / Math.max( size.x, size.z, 1e-3 );
	geo.scale( s, s, s );
	if ( alongZ && size.x > size.z ) geo.rotateY( Math.PI / 2 );
	geo.computeBoundingBox();
	const b = geo.boundingBox;
	geo.translate( - ( b.min.x + b.max.x ) / 2, - b.min.y, - ( b.min.z + b.max.z ) / 2 );
	return geo;

}

// Paint a part whose export lost its colour: a solid body with a band across its upper part.
function paintPart( geo, style ) {

	const pos = geo.attributes.position.array, col = geo.attributes.color.array;
	let ymin = Infinity, ymax = - Infinity;
	for ( let i = 1; i < pos.length; i += 3 ) { if ( pos[ i ] < ymin ) ymin = pos[ i ]; if ( pos[ i ] > ymax ) ymax = pos[ i ]; }
	const body = new Color( style.body );
	if ( style.band === undefined ) {

		for ( let i = 0; i < col.length; i += 3 ) { col[ i ] = body.r; col[ i + 1 ] = body.g; col[ i + 2 ] = body.b; }

	} else {

		const span = Math.max( 1e-6, ymax - ymin ), from = ymin + span * style.bandFrom;
		const band = new Color( style.band );
		for ( let i = 0; i < col.length; i += 3 ) {

			const c = pos[ i + 1 ] >= from ? band : body;
			col[ i ] = c.r; col[ i + 1 ] = c.g; col[ i + 2 ] = c.b;

		}

	}

	geo.attributes.color.needsUpdate = true;

}

// A wooden slat pot: 0.95 m long (z), 0.55 wide, runners and slats, net ends, a lath top.
function buildPot() {

	const P = [];
	const add = ( g, o ) => P.push( prepare( g, o ) );
	const WOOD = { color: 0x7d6242, rough: 0.86 };
	const OAK = { color: 0x5a4832, rough: 0.9 };
	const NET = { color: 0x2f3a2b, rough: 0.95 };
	const L = 0.95, W = 0.55, H = 0.4;
	for ( const s of [ - 1, 1 ] ) {

		add( box( W + 0.06, 0.05, 0.06 ), { ...OAK, matrix: mat4( 0, 0.03, s * L / 2 ) } );
		add( box( 0.06, 0.05, L + 0.06 ), { ...OAK, matrix: mat4( s * W / 2, 0.03, 0 ) } );
		for ( const e of [ - 1, 1 ] ) add( box( 0.05, H, 0.05 ), { ...WOOD, matrix: mat4( s * W / 2, H / 2, e * L / 2 ) } );

	}

	for ( let i = - 2; i <= 2; i ++ ) add( box( W + 0.04, 0.03, 0.075 ), { ...WOOD, matrix: mat4( 0, 0.06, i * 0.19 ) } );
	for ( let k = 0; k < 3; k ++ ) for ( const s of [ - 1, 1 ] ) add( box( 0.025, 0.075, L + 0.02 ), { ...WOOD, matrix: mat4( s * W / 2, 0.11 + k * 0.1, 0 ) } );
	for ( const e of [ - 1, 1 ] ) add( box( W + 0.02, H - 0.05, 0.02 ), { ...NET, matrix: mat4( 0, H / 2 + 0.01, e * ( L / 2 - 0.012 ) ) } );
	add( box( W + 0.03, 0.03, 0.17 ), { ...WOOD, matrix: mat4( 0, 0.43, 0 ) } );
	for ( const s of [ - 1, 1 ] ) add( box( W * 0.72, 0.03, 0.16 ), { ...WOOD, matrix: mat4( s * 0.1, 0.4, s * 0.2, 0.12 ) } );
	// the bridle over the top
	for ( const s of [ - 1, 1 ] ) add( rod( new Vector3( s * W / 2, 0.28, 0.1 ), new Vector3( 0, 0.5, 0 ), 0.012, 5 ), { color: 0xa8977a, rough: 0.9 } );
	return mergePrepared( P );

}

// egg float: orange, cream mast
function buildBuoy() {

	const P = [];
	const add = ( g, o ) => P.push( prepare( g, o ) );
	add( sphere( 0.15, 10, 7 ), { color: 0xd9622b, rough: 0.45, matrix: mat4( 0, 0, 0, 0, 0, 0, 1, 1.3, 1 ) } );
	add( box( 0.018, 0.24, 0.018 ), { color: 0xe8e2d0, rough: 0.5, matrix: mat4( 0, 0.2, 0 ) } );
	return mergePrepared( P );

}

// a unit-height rope, scaled per trap to reach from the pot to the buoy
function buildRope() {

	return prepare( cylinder( 0.012, 0.012, 1, 5 ), { color: 0x9c8f74, rough: 0.95 } );

}

function hash( n ) {

	return ( Math.imul( ( n | 0 ) ^ 0x9e3779b9, 2246822519 ) >>> 0 );

}

const lerp = ( a, b, t ) => a + ( b - a ) * t;
function smooth( e0, e1, x ) {

	const t = Math.min( 1, Math.max( 0, ( x - e0 ) / ( e1 - e0 ) ) );
	return t * t * ( 3 - 2 * t );

}
