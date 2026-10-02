import * as THREE from '../engine/index.js';

// Free-fly debug camera: drag (or pointer lock) to look, WASD + QE to move, Shift = fast.
export class FlyCamera {

	constructor( camera, dom, input ) {

		this.camera = camera;
		this.dom = dom;
		this.input = input;
		this.yaw = 0;
		this.pitch = 0;
		this.speed = 8;
		this.enabled = true;
		this.velocity = new THREE.Vector3();
		this._fwd = new THREE.Vector3();
		this._right = new THREE.Vector3();

	}

	setPose( position, yaw, pitch ) {

		this.camera.position.copy( position );
		this.yaw = yaw;
		this.pitch = pitch;
		this.apply();

	}

	apply() {

		this.camera.rotation.set( this.pitch, this.yaw, 0, 'YXZ' );

	}

	update( dt ) {

		if ( ! this.enabled ) return;
		const inp = this.input;
		const look = inp.consumeLook();
		this.yaw -= look.x * 0.0022;
		this.pitch -= look.y * 0.0022;
		this.pitch = Math.max( - 1.55, Math.min( 1.55, this.pitch ) );
		this.apply();

		const fast = inp.act( 'sprint' );
		const speed = this.speed * ( fast ? 6 : 1 );
		this.camera.getWorldDirection( this._fwd );
		this._right.crossVectors( this._fwd, this.camera.up ).normalize();
		const move = this._move || ( this._move = new THREE.Vector3() );
		const mv = inp.move( this._mv || ( this._mv = { x: 0, y: 0 } ) );
		const planar = Math.min( 1, Math.hypot( mv.x, mv.y ) );
		move.set( 0, 0, 0 );
		if ( mv.y ) move.addScaledVector( this._fwd, mv.y );
		if ( mv.x ) move.addScaledVector( this._right, mv.x );
		if ( move.lengthSq() > 0 ) move.normalize().multiplyScalar( planar );
		// vertical: the walker's jump key and the interact key are both "up" here (this camera is the
		// developer's, and E has always flown up in it), the dive key is down
		const vert = ( inp.act( 'ascend' ) || inp.act( 'interact' ) ? 1 : 0 ) - ( inp.act( 'descend' ) ? 1 : 0 );
		move.y += vert;
		if ( move.lengthSq() > 0 ) move.normalize().multiplyScalar( Math.max( planar, Math.abs( vert ) ) );
		move.multiplyScalar( speed );
		this.velocity.lerp( move, 1 - Math.exp( - dt * 8 ) );
		this.camera.position.addScaledVector( this.velocity, dt );

	}

}
