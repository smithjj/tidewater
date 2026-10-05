import * as THREE from '../engine/index.js';

// The wildlife visit: G goes to the eagle ray, the stingrays and the turtle in turn, and G again on to
// the next. The camera flies to a spot behind and beside the animal and stays with it as it swims
// (under water or at the surface); the mouse orbits it, the wheel zooms, F leaves it to fly freely from
// there, Esc ends the visit. The player stays where they were and the interface gives way to a caption
// (UI.setVisit). While it lasts the fish are calm (FishSchools.calm): the sim treats a camera under
// water as a diver and the animal would otherwise swim away from it.
// The Unity port has the same camera (Runtime/Game/WildlifeCam.cs); keep them together.

const MODELS = [ 'eagleRay', 'stingray', 'turtle' ];
// where each visit starts from: a little round to the left of straight behind the animal
const BEHIND = 0.5;
const NAMES = { eagleRay: 'Eagle ray', stingray: 'Southern stingray', turtle: 'Green turtle' };

export class WildlifeVisit {

	// schools: the FishSchools (or a function that returns them), terrain: the heights (heightAt),
	// waterHeight: () => the surface at the camera, toast: ( text ) => void
	constructor( { camera, input, schools, terrain, waterHeight = () => 0, toast = null } ) {

		this.camera = camera;
		this.input = input;
		this._schools = schools;
		this.terrain = terrain;
		this.waterHeight = waterHeight;
		this.toast = toast;
		this.active = false;
		this.caption = '';
		this.index = - 1; // the animal being visited
		this.last = - 1; // the one visited last, where G goes on from after a visit
		this.animals = [];
		this.schools = null;
		this.target = null;
		this.orbitYaw = BEHIND; // the camera's bearing round the animal, from straight behind it (growing swings it round to its left)
		this.orbitPitch = 0.22; // and its elevation
		this.zoom = 1;
		this.heading = 0;
		this.tp = new THREE.Vector3(); // the animal, smoothed

	}

	// the animals in a fixed order (eagle ray, stingrays, turtle), found again if the schools were rebuilt
	_list() {

		const s = typeof this._schools === 'function' ? this._schools() : this._schools;
		if ( s === this.schools && this.animals.length ) return;
		this.schools = s;
		this.animals = [];
		if ( ! s ) return;
		for ( const m of MODELS ) for ( const g of s.groups ) if ( g.sp && g.sp.model === m ) this.animals.push( g );

	}

	// once a frame, before the player is updated: G starts the visit or goes on to the next animal;
	// Esc and the free camera end it
	handle( freeCam ) {

		const inp = this.input;
		if ( this.active && ( freeCam || inp.actHit( 'cancel' ) ) ) {

			this.stop();
			return;

		}

		if ( freeCam || ! inp.actHit( 'wildlife' ) ) return;
		this._list();
		if ( ! this.animals.length ) {

			if ( this.toast ) this.toast( 'No wildlife about' );
			return;

		}

		this.visit( ( this.active ? this.index : this.last ) + 1 );

	}

	visit( i ) {

		const n = this.animals.length;
		this.index = this.last = ( ( i % n ) + n ) % n;
		const g = this.target = this.animals[ this.index ];
		const s = this.schools, k = g.offset * 3;
		s.calm = true;
		this.tp.set( s.pos[ k ], s.pos[ k + 1 ], s.pos[ k + 2 ] );
		this.heading = Math.atan2( s.vel[ k + 2 ], s.vel[ k ] );
		if ( s.vel[ k ] === 0 && s.vel[ k + 2 ] === 0 ) this.heading = Math.atan2( g.heading.z, g.heading.x );
		this.zoom = 1;
		this.orbitYaw = BEHIND;
		this.orbitPitch = 0.22;
		this.active = true;
		this._place( 1, 0 );

	}

	stop() {

		this.active = false;
		this.index = - 1;
		this.target = null;
		if ( this.schools ) this.schools.calm = false;

	}

	// while a visit is on: the camera follows the animal (instead of the player or the free camera)
	update( dt ) {

		if ( ! this.active || ! this.target ) return;
		const inp = this.input;
		const look = inp.consumeLook();
		// the view turns the way the mouse goes: the camera swings round the other way
		this.orbitYaw += look.x * 0.0035;
		this.orbitPitch = Math.max( - 0.5, Math.min( 1.2, this.orbitPitch + look.y * 0.0035 ) );
		this.zoom = Math.max( 0.5, Math.min( 4, this.zoom * Math.pow( 1.12, inp.consumeWheel() ) ) );
		this._place( 1 - Math.exp( - dt * 6 ), dt );

	}

	_place( follow, dt ) {

		const s = this.schools, k = this.target.offset * 3, tp = this.tp, cam = this.camera;
		const vx = s.vel[ k ], vz = s.vel[ k + 2 ], len = s.size[ this.target.offset ];
		tp.x += ( s.pos[ k ] - tp.x ) * follow;
		tp.y += ( s.pos[ k + 1 ] - tp.y ) * follow;
		tp.z += ( s.pos[ k + 2 ] - tp.z ) * follow;
		if ( vx * vx + vz * vz > 0.0025 ) {

			const want = Math.atan2( vz, vx ), d = Math.atan2( Math.sin( want - this.heading ), Math.cos( want - this.heading ) );
			this.heading += d * ( dt <= 0 ? 1 : 1 - Math.exp( - dt * 1.5 ) );

		}

		// behind the animal, turned by the orbit, at a distance that shows all of it
		const dist = Math.max( 2.4, len * 2.3 ) * this.zoom, a = this.heading + Math.PI + this.orbitYaw;
		const cp = Math.cos( this.orbitPitch );
		const cx = tp.x + Math.cos( a ) * cp * dist, cz = tp.z + Math.sin( a ) * cp * dist;
		const cy = Math.max( tp.y + Math.sin( this.orbitPitch ) * dist, this.terrain.heightAt( cx, cz ) + 0.3 );
		const f = follow >= 1 ? 1 : 1 - Math.exp( - dt * 5 );
		cam.position.x += ( cx - cam.position.x ) * f;
		cam.position.y += ( cy - cam.position.y ) * f;
		cam.position.z += ( cz - cam.position.z ) * f;
		if ( tp.distanceToSquared( cam.position ) > 1e-6 ) cam.lookAt( tp.x, tp.y, tp.z );

		const depth = Math.max( 0, this.waterHeight() - s.pos[ k + 1 ] );
		const name = NAMES[ this.target.sp.model ] || this.target.sp.model;
		this.caption = `${ name } · ${ len.toFixed( 1 ) } m · ${ depth < 0.3 ? 'at the surface' : depth.toFixed( 1 ) + ' m deep' }`;

	}

}
