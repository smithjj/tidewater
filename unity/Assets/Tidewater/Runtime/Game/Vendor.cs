using System;
using Tidewater.Engine;

// Port of the logic of src/game/Vendor.js: a trader the player talks to (E within `radius`). Hours, range, and the idle motion of the stand-in figure
// (breathing, a slow weight shift, turning toward the player when near); StallsView draws it. With the Rocketbox character loaded (CharacterModel) the
// character takes the figure's place, as in the JS: an idle clip, a wave when the player comes up, the talk gestures while their panel is open.
//   new Vendor( name, kind, position, yaw, radius, hours, greeting, idle )
namespace Tidewater.Game
{
	public sealed class Vendor
	{
		public string name, kind, greeting, idle; // kind: 'buyer' (fish stand) | 'shop' (upgrades)
		public double[] hours;                    // [ open, close ] on the 24 h clock, or null for always open
		public double radius;
		public Vector3 position;
		public double yaw;
		public bool talking, shutTold;
		// the stand-in's pose this frame (read by VendorView): turn toward the player, sway, roll, breathing
		public double figureYaw, figureRoll, figureScaleY = 1;
				// the skinned character that replaces the stand-in once it has loaded (null: the stand-in shows), and the clips it plays
		public CharacterModel character;
		public string clipIdle = "idle_neutral_01", clipTalk = "gestic_talk_relaxed_01", clipGreet = "wave_01";
		bool near;

		// Vendor.loadCharacter: pose it right away (far from the player the animation holds, and the rest pose is a T-pose), and hand the one-shot clips (the wave)
		// back to the idle / talk loop
		public void SetCharacter( CharacterModel model )
		{
			character = model;
			if ( model == null ) return;
			model.play( clipIdle, 0.01, true, 1, new System.Random().NextDouble() * model.clipDuration( clipIdle ) );
			model.update( 0 );
			model.onClipEnd = _ => model.play( talking ? clipTalk : clipIdle, 0.5 );
		}
		double t, yawOff;

		public Vendor( string name, string kind, Vector3 position, double yaw, double radius, double[] hours, string greeting, string idle = "" )
		{
			this.name = name; this.kind = kind; this.position = position.clone(); this.yaw = yaw; this.radius = radius; this.hours = hours; this.greeting = greeting; this.idle = idle;
		}

		// "Joe" of "Joe · Fish buyer"
		public string shortName => name.Split( new[] { " ·" }, StringSplitOptions.None )[ 0 ];

		public bool inRange( Vector3 p ) => JS.Hypot( p.x - position.x, p.z - position.z ) < radius && Math.Abs( p.y - position.y ) < 2.5;

		// island hours: open from `hours[ 0 ]` up to (not including) `hours[ 1 ]`, wrapping midnight
		public bool openAt( double hour )
		{
			if ( hours == null ) return true;
			double open = hours[ 0 ], close = hours[ 1 ];
			return close >= open ? hour >= open && hour < close : hour >= open || hour < close;
		}

		// idle motion of the stand-in figure (Vendor.update)
		public void update( double dt, Vector3 lookAt = null )
		{
			t += dt;
			double want = 0;
			if ( lookAt != null )
			{
				double dx = lookAt.x - position.x, dz = lookAt.z - position.z;
				if ( dx * dx + dz * dz < 64 )
				{
					want = Math.Atan2( dx, dz ) - yaw;
					want = Math.Atan2( Math.Sin( want ), Math.Cos( want ) );
					want = Math.Max( - 1.1, Math.Min( 1.1, want ) );
				}
			}

			yawOff += ( want - yawOff ) * ( 1 - Math.Exp( - dt * 2.5 ) );
			if ( character != null )
			{
				var c = character;
				double d2 = lookAt != null ? ( lookAt.x - position.x ) * ( lookAt.x - position.x ) + ( lookAt.z - position.z ) * ( lookAt.z - position.z ) : double.PositiveInfinity;
				// animate only when someone can see it up close (the pose holds otherwise)
				if ( d2 > 3600 ) { c.hold(); return; }
				bool nearNow = d2 < ( radius + 3 ) * ( radius + 3 );
				if ( nearNow && ! near && clipGreet != null && ! talking ) c.play( clipGreet, 0.35, false );
				near = nearNow;
				if ( c.current != clipGreet )
				{
					string want2 = talking ? clipTalk : clipIdle;
					if ( c.current != want2 ) c.play( want2, 0.6 );
				}

				figureYaw = yawOff; figureRoll = 0; figureScaleY = 1;
				c.update( dt );
				return;
			}

			figureYaw = yawOff + Math.Sin( t * 0.37 ) * 0.05;
			figureRoll = Math.Sin( t * 0.23 ) * 0.02;
			figureScaleY = 1 + Math.Sin( t * 1.6 ) * 0.006;
		}
	}
}
