using System;
using Tidewater.Core;
using Tidewater.Engine;

// Port of src/player/FlyCamera.js: the free-fly debug camera. Mouse (or pad stick) to look, WASD + QE to move, Shift = fast; Space and E are
// "up" here (E has always flown up in this camera), C / Ctrl / Q are down.
namespace Tidewater.Player
{
	public sealed class FlyCamera
	{
		readonly SimCamera camera;
		readonly GameInput input;
		public double yaw, pitch, speed = 8;
		public bool enabled = true;
		public readonly Engine.Vector3 velocity = new Engine.Vector3();
		readonly Engine.Vector3 _fwd = new Engine.Vector3(), _right = new Engine.Vector3(), _move = new Engine.Vector3(), _up = new Engine.Vector3( 0, 1, 0 );
		readonly Engine.Vector2 _mv = new Engine.Vector2();
		readonly Euler _e = new Euler( 0, 0, 0, "YXZ" );

		public FlyCamera( SimCamera camera, GameInput input ) { this.camera = camera; this.input = input; }

		public void setPose( Engine.Vector3 position, double yaw, double pitch )
		{
			camera.position.copy( position );
			this.yaw = yaw;
			this.pitch = pitch;
			apply();
		}

		void apply() => camera.quaternion.setFromEuler( _e.set( pitch, yaw, 0 ) );

		public void update( double dt )
		{
			if ( ! enabled ) return;
			var inp = input;
			var look = inp.consumeLook();
			yaw -= look.x * 0.0022;
			pitch -= look.y * 0.0022;
			pitch = Math.Max( - 1.55, Math.Min( 1.55, pitch ) );
			apply();

			bool fast = inp.act( "sprint" );
			double sp = speed * ( fast ? 6 : 1 );
			camera.viewDir( _fwd );
			_right.crossVectors( _fwd, _up ).normalize();
			var mv = inp.move( _mv );
			double planar = Math.Min( 1, JS.Hypot( mv.x, mv.y ) );
			_move.set( 0, 0, 0 );
			if ( mv.y != 0 ) _move.addScaledVector( _fwd, mv.y );
			if ( mv.x != 0 ) _move.addScaledVector( _right, mv.x );
			if ( _move.lengthSq() > 0 ) _move.normalize().multiplyScalar( planar );
			// vertical: the walker's jump key and the interact key are both "up" here, the dive key is down
			double vert = ( inp.act( "ascend" ) || inp.act( "interact" ) ? 1 : 0 ) - ( inp.act( "descend" ) ? 1 : 0 );
			_move.y += vert;
			if ( _move.lengthSq() > 0 ) _move.normalize().multiplyScalar( Math.Max( planar, Math.Abs( vert ) ) );
			_move.multiplyScalar( sp );
			velocity.lerp( _move, 1 - Math.Exp( - dt * 8 ) );
			camera.position.addScaledVector( velocity, dt );
		}
	}
}
