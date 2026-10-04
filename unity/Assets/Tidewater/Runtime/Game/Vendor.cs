using System;
using Tidewater.Engine;

// Port of the logic of src/game/Vendor.js: a trader the player talks to (E within `radius`). Hours, range, and the idle motion of the stand-in figure
// (breathing, a slow weight shift, turning toward the player when near); VendorView draws it. The Rocketbox character (skinned, animated) of the JS
// is not ported: the stand-in figure stays.
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
			figureYaw = yawOff + Math.Sin( t * 0.37 ) * 0.05;
			figureRoll = Math.Sin( t * 0.23 ) * 0.02;
			figureScaleY = 1 + Math.Sin( t * 1.6 ) * 0.006;
		}
	}
}
