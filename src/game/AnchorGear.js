import { Group, Mesh, Vector3, Quaternion } from '../engine/index.js';
import { box, rod, cylinder, prepare, mergePrepared, mat4 } from '../world/boat/GeoKit.js';
import { createPropMaterial } from './GameMaterials.js';

// What an anchored boat shows: a small iron anchor lying on the seabed where it landed, and the line from the
// bow chock down to it. Dropping runs the anchor down from the chock; weighing runs it back up. The physics
// is the boat's own (BoatController.anchor); this only draws it.
//
//   const gear = new AnchorGear( { scene, terrain } );   gear.update( boats, dt )   // every frame

const FALL = 0.9; // s to run the anchor down (or up)
const _c = new Vector3();
const _p = new Vector3();
const _d = new Vector3();
const _q = new Quaternion();
const _Y = new Vector3( 0, 1, 0 );

export class AnchorGear {

	constructor( { scene, terrain } ) {

		this.terrain = terrain;
		this.group = new Group();
		this.group.name = 'Anchors';
		scene.add( this.group );
		this.material = createPropMaterial( 'anchor' );
		this.anchorGeo = buildAnchor();
		this.lineGeo = prepare( cylinder( 0.02, 0.02, 1, 5 ), { color: 0xb8aa88, rough: 0.95 } ); // unit length, scaled to span
		this.views = new Map(); // boat -> { anchor, line, t }

	}

	_view( boat ) {

		let v = this.views.get( boat );
		if ( ! v ) {

			v = { anchor: new Mesh( this.anchorGeo, this.material ), line: new Mesh( this.lineGeo, this.material ), t: 0 };
			v.anchor.scale.setScalar( 1.3 ); // a little oversize: it has to read from the surface, against dark seagrass
			v.anchor.visible = v.line.visible = false;
			this.group.add( v.anchor, v.line );
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

// a stocked (Admiralty) anchor, about 0.9 m tall, standing on its crown: shank, a stock across the top and two
// arms with flukes
function buildAnchor() {

	const P = [];
	const iron = { color: 0x5b6165, rough: 0.5, metal: 0.55 };
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
