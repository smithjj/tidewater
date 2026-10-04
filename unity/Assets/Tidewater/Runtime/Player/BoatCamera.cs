using System;
using Tidewater.Engine;
using Tidewater.Util;
using UnityEngine;

// The two cameras of Player.updateBoat (src/player/Player.js): first person at the helm (the head half-stabilises against the boat's roll and
// pitch) and the third-person chase orbit. Everything is computed in sim coordinates with the engine's double math exactly as the JS does, and
// the resulting eye and view direction are handed to Unity through Sim (z mirrored). look is in pixels (x right, y down, as a browser reports
// it), wheel in notches.
namespace Tidewater.Player
{
	public sealed class BoatCamera
	{
		public bool firstPerson;         // camMode 'first' / 'third'; the game starts in 'third'
		public double orbitYaw, orbitPitch = 0.22, orbitDist = 13;
		public double helmYaw, helmPitch = - 0.05;
		readonly Engine.Vector3 camPos = new Engine.Vector3();
		bool camInit;

		static readonly Engine.Vector3 _yAxis = new Engine.Vector3( 0, 1, 0 );

		// Player.takeHelm
		public void takeHelm( BoatController b )
		{
			helmYaw = 0; helmPitch = - 0.05;
			orbitYaw = b.getYaw() + Math.PI;
			camInit = false;
		}

		public void toggle() { firstPerson = ! firstPerson; }

		// one frame; eye and view direction (sim axes) of the camera
		public void update( BoatController b, Engine.Vector3 helmEye, Engine.Vector2 look, double wheel, double dt, out Engine.Vector3 eye, out Engine.Vector3 forward, out Engine.Vector3 up )
		{
			eye = new Engine.Vector3(); forward = new Engine.Vector3(); up = new Engine.Vector3();
			if ( firstPerson )
			{
				helmYaw = MathUtils.clamp( helmYaw - look.x * 0.0022, - 2.2, 2.2 );
				helmPitch = MathUtils.clamp( helmPitch - look.y * 0.0022, - 1.2, 1.0 );
				b.toWorld( helmEye, eye );
				// head partially stabilises against roll and pitch (feels natural, less nausea)
				var yawOnly = new Engine.Quaternion().setFromAxisAngle( _yAxis, b.getYaw() + Math.PI );
				var boatUp = b.quaternion.clone().multiply( new Engine.Quaternion().setFromAxisAngle( _yAxis, Math.PI ) );
				var q = boatUp.slerp( yawOnly, 0.55 );
				q.multiply( new Engine.Quaternion().setFromEuler( new Euler( helmPitch, helmYaw, 0 ) ) );
				forward.set( 0, 0, - 1 ).applyQuaternion( q );
				up.set( 0, 1, 0 ).applyQuaternion( q );
				return;
			}

			orbitYaw -= look.x * 0.003;
			orbitPitch = MathUtils.clamp( orbitPitch + look.y * 0.003, - 0.05, 1.2 );
			orbitDist = MathUtils.clamp( orbitDist * ( 1 + wheel * 0.08 ), 6, 40 );
			// gently swing behind the boat when moving
			if ( b.speed > 2 && Math.Abs( look.x ) < 0.5 )
			{
				double behind = b.getYaw() + Math.PI;
				double d = behind - orbitYaw;
				d = Math.Atan2( Math.Sin( d ), Math.Cos( d ) );
				orbitYaw += d * ( 1 - Math.Exp( - dt * 0.8 ) );
			}

			var target = b.toWorld( new Engine.Vector3( 0, 1.4, 0 ), new Engine.Vector3() );
			var off = new Engine.Vector3(
				Math.Sin( orbitYaw ) * Math.Cos( orbitPitch ),
				Math.Sin( orbitPitch ),
				Math.Cos( orbitYaw ) * Math.Cos( orbitPitch ) ).multiplyScalar( orbitDist );
			var want = target.clone().add( off );
			want.y = Math.Max( want.y, b.sampleWaterAt( b.position ) + 0.7 ); // (Player.waterH, the water under the player)
			if ( ! camInit ) { camPos.copy( want ); camInit = true; }
			camPos.lerp( want, 1 - Math.Exp( - dt * 6 ) );
			eye.copy( camPos );
			forward.subVectors( target, camPos ).normalize();
			up.set( 0, 1, 0 );
		}

		public static void Apply( Transform cam, Engine.Vector3 eye, Engine.Vector3 forward, Engine.Vector3 up )
		{
			cam.position = Sim.ToUnity( ( float ) eye.x, ( float ) eye.y, ( float ) eye.z );
			var f = Sim.ToUnity( ( float ) forward.x, ( float ) forward.y, ( float ) forward.z );
			var u = Sim.ToUnity( ( float ) up.x, ( float ) up.y, ( float ) up.z );
			if ( f.sqrMagnitude > 1e-12f ) cam.rotation = UnityEngine.Quaternion.LookRotation( f, u );
		}
	}
}
