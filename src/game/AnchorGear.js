import { Group, Mesh, Vector3, Quaternion } from '../engine/index.js';
import { box, rod, cylinder, sphere, prepare, mergePrepared, mat4 } from '../world/boat/GeoKit.js';
import { createPropMaterial } from './GameMaterials.js';

// What an anchored boat shows: a small iron anchor lying on the seabed where it landed, the line from the
// bow chock down to it, and an orange buoy on the surface over the anchor (underwater the pale line and the
// iron turn the colour of the seabed and the hull hides the top of the line, so the buoy is what you can see,
// from the chase camera too). Dropping runs the anchor down from the chock; weighing runs it back up. The physics
// is the boat's own (BoatController.anchor); this only draws it.
//
//   const gear = new AnchorGear( { scene, terrain, query, boats } );   gear.update( boats, dt )   // every frame
//
// The meshes are made up front, hidden, for every boat given: the loading screen's precompile pass visits
// hidden meshes, so their shader pipelines are built there and not on the first frame an anchor goes down
// (a pipeline built mid-game leaves the mesh undrawn until it is ready).

const FALL = 0.9; // s to run the anchor down (or up)
const _c = new Vector3();
const _p = new Vector3();
const _d = new Vector3();
const _q = new Quaternion();
const _Y = new Vector3( 0, 1, 0 );

export class AnchorGear {

	constructor( { scene, terrain, query = null, boats = [] } ) {

		this.terrain = terrain;
		this.query = query; // the water height under each buoy rides the same read-back the boats use
		this.slot = - 1;
		if ( query ) {

			try { this.slot = query.allocate( 'anchors', 2 ); } catch ( e ) { /* out of slots: the buoys sit at sea level */ }

		}

		this.group = new Group();
		this.group.name = 'Anchors';
		scene.add( this.group );
		this.material = createPropMaterial( 'anchor' );
		this.anchorGeo = buildAnchor();
		this.lineGeo = prepare( cylinder( 0.035, 0.035, 1, 6 ), { color: 0xd9cba8, rough: 0.9 } ); // unit length (a stout line: it has to read from the chase camera), scaled to span
		this.buoyGeo = buildBuoy();
		this.views = new Map(); // boat -> { anchor, line, buoy, t, slot }
		for ( const b of boats ) if ( b ) this._view( b );

	}

	_view( boat ) {

		let v = this.views.get( boat );
		if ( ! v ) {

			v = { anchor: new Mesh( this.anchorGeo, this.material ), line: new Mesh( this.lineGeo, this.material ), buoy: new Mesh( this.buoyGeo, this.material ), t: 0, slot: this.slot >= 0 && this.views.size < 2 ? this.slot + this.views.size : - 1 };
			v.anchor.scale.setScalar( 1.7 ); // oversize (about 1.5 m): it has to read from the surface, against dark seagrass
			v.anchor.visible = v.line.visible = v.buoy.visible = false;
			this.group.add( v.anchor, v.line, v.buoy );
			this.views.set( boat, v );

		}

		return v;

	}

	update( boats, dt ) {

		for ( const boat of boats || [] ) {

			if ( ! boat || ! boat.anchor ) continue;
			const A = boat.anchor, v = this._view( boat );
			const target = A.down ? 1 : 0;
			const step = Math.min( dt, 0.1 ) / FALL;
			v.t += Math.max( - step, Math.min( step, target - v.t ) );
			const show = v.t > 0.001;
			v.anchor.visible = v.line.visible = show;
			// the buoy floats over the anchor once it has landed, until the line starts coming up
			v.buoy.visible = A.down && v.t >= 1;
			if ( v.buoy.visible ) {

				let y = 0;
				if ( v.slot >= 0 && this.query ) {

					this.query.setPoint( v.slot, A.x, A.z );
					const h = this.query.cpu[ v.slot * 4 ];
					if ( Number.isFinite( h ) ) y = h;

				}

				v.buoy.position.set( A.x, y - 0.06, A.z );
				v.buoy.rotation.z = Math.sin( A.x * 3.1 + A.z ) * 0.1;

			}

			if ( ! show ) continue;

			// from the chock down to where it lies on the bottom (eased: it gathers speed and settles)
			const e = v.t * v.t * ( 3 - 2 * v.t );
			const chock = boat.toWorld( boat.chock, _c );
			const bed = this.terrain.heightAt( A.x, A.z ) + 0.1;
			_p.set( chock.x + ( A.x - chock.x ) * e, chock.y + ( bed - chock.y ) * e, chock.z + ( A.z - chock.z ) * e );
			v.anchor.position.copy( _p );
			// upright on the way down, over onto its side on the bottom
			v.anchor.rotation.set( 0, A.x * 0.7 + A.z * 0.3, 1.25 * e * e );
			_d.subVectors( _p, chock );
			const len = Math.max( 0.05, _d.length() );
			v.line.position.copy( chock ).addScaledVector( _d, 0.5 );
			v.line.quaternion.copy( _q.setFromUnitVectors( _Y, _d.divideScalar( len ) ) );
			v.line.scale.set( 1, len, 1 );

		}

	}

}

// an orange float with a pale mast, about 0.5 m tall (the trap buoys' shape, larger: the anchor buoy is a marker
// you look for)
function buildBuoy() {

	const P = [];
	const add = ( g, o ) => P.push( prepare( g, o ) );
	add( sphere( 0.2, 10, 8 ), { color: 0xee5a1e, rough: 0.45, matrix: mat4( 0, 0, 0, 0, 0, 0, 1, 1.25, 1 ) } );
	add( box( 0.03, 0.34, 0.03 ), { color: 0xf1ebd8, rough: 0.5, matrix: mat4( 0, 0.3, 0 ) } );
	add( sphere( 0.045, 6, 5 ), { color: 0xee5a1e, rough: 0.5, matrix: mat4( 0, 0.5, 0 ) } );
	return mergePrepared( P );

}

// a stocked (Admiralty) anchor, about 0.9 m tall, standing on its crown: shank, a stock across the top and two
// arms with flukes
function buildAnchor() {

	const P = [];
	const iron = { color: 0x9aa1a6, rough: 0.45, metal: 0.4 }; // galvanised: pale, so it shows on dark ground
	const add = ( g, o = {} ) => P.push( prepare( g, { ...iron, ...o } ) );
	const V = ( x, y, z ) => new Vector3( x, y, z );
	add( rod( V( 0, - 0.38, 0 ), V( 0, 0.44, 0 ), 0.024, 6 ) );
	add( rod( V( 0, 0.36, - 0.22 ), V( 0, 0.36, 0.22 ), 0.016, 5 ) );
	add( box( 0.08, 0.035, 0.018 ), { matrix: mat4( 0, 0.47, 0 ) } ); // the ring the line is shackled to
	for ( const s of [ - 1, 1 ] ) {

		add( rod( V( 0, - 0.36, 0 ), V( s * 0.3, - 0.18, 0 ), 0.02, 6 ) );
		add( box( 0.13, 0.022, 0.1 ), { matrix: mat4( s * 0.32, - 0.17, 0, 0, 0, - s * 0.6 ) } );

	}

	return mergePrepared( P );

}
