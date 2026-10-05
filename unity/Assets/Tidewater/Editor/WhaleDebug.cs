using Tidewater.World.Marine;
using UnityEngine;

// Editor helpers for looking at the humpback in Edit mode (the Editor does not tick Update): move the whale along, put the camera next to it.
//   Tidewater.EditorTools.WhaleDebug.Run( 120 )                         // advance the whale 120 s (steps of 1/30 s, in chunks: an eval must stay under 5 s)
//   Tidewater.EditorTools.WhaleDebug.Look( 20, 90, 5 )                  // camera 20 m from the whale, 90 deg round from its heading, 5 deg down, looking at it
//   Tidewater.EditorTools.WhaleDebug.Until( "surface" )                // run until the sequencer is in that state
namespace Tidewater.EditorTools
{
	public static class WhaleDebug
	{
		public static string Info()
		{
			var v = WhaleView.instance; if ( v == null ) return "no WhaleView";
			if ( v.brain == null ) return "whale not built yet: " + v.stats;
			var b = v.brain;
			return $"{v.stats}\nwhale at sim ({b.position.x:F1}, {b.position.y:F1}, {b.position.z:F1}) yaw {b.yaw * 57.2958:F0} deg state {b.state} u {b.u:F0}/{b.length:F0} water {b.water:F2} blow {b.blow:F2} fluke {b.flukeUp:F2} breaches {b.breaches} lod {v.whale.lod}\ndraw: {v.lastDraw}";
		}

		public static string Run( double seconds, double dt = 1.0 / 30 )
		{
			var v = WhaleView.instance; if ( v == null ) return "no WhaleView";
			v.Step( ( int ) ( seconds / dt ), dt );
			return Info();
		}

		// the whale's sequencer until it is in `state` (at most `max` seconds)
		public static string Until( string state, double max = 400 )
		{
			var v = WhaleView.instance; if ( v == null || v.brain == null ) return "no whale";
			for ( double t = 0; t < max && v.brain.state != state; t += 1.0 / 30 ) v.Step( 1, 1.0 / 30 );
			return Info();
		}

		// run until the blow is at its strongest (the first `delay` seconds after that are stepped too)
		public static string UntilBlow( double delay = 0, double max = 600 )
		{
			var v = WhaleView.instance; if ( v == null || v.brain == null ) return "no whale";
			bool seen = false; double after = 0;
			for ( double t = 0; t < max; t += 1.0 / 30 )
			{
				v.Step( 1, 1.0 / 30 );
				if ( ! seen && v.brain.blow > 0.9 ) seen = true;
				if ( seen && ( after += 1.0 / 30 ) > delay ) break;
			}
			return Info();
		}

		// run the whale and the sea (so the spray lives) together for `seconds`
		public static string WithSea( double seconds )
		{
			var v = WhaleView.instance; var o = Tidewater.Ocean.OceanRenderer.instance; if ( v == null || v.brain == null || o == null ) return "no whale or sea";
			for ( double t = 0; t < seconds; t += 1.0 / 30 ) { v.Step( 1, 1.0 / 30 ); o.Advance( 1f / 30f ); }
			return Info();
		}

		// the camera `dist` m from the whale's middle at `yawDeg` round from its heading (0 = in front, 90 = its left, 180 = behind) and `pitchDeg` down, looking at the middle
		public static string Look( double dist, double yawDeg, double pitchDeg, double aimY = 0.5 )
		{
			var v = WhaleView.instance; if ( v == null || v.brain == null ) return "no whale";
			var b = v.brain; var cam = Camera.main;
			double a = b.yaw + yawDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
			double cx = b.position.x + System.Math.Sin( a ) * System.Math.Cos( p ) * dist, cz = b.position.z + System.Math.Cos( a ) * System.Math.Cos( p ) * dist, cy = b.position.y + 0.5 + System.Math.Sin( p ) * dist;
			cam.transform.position = Tidewater.Util.Sim.ToUnity( cx, cy, cz );
			cam.transform.LookAt( Tidewater.Util.Sim.ToUnity( b.position.x, b.position.y + aimY, b.position.z ) );
			return Info();
		}
	}
}
