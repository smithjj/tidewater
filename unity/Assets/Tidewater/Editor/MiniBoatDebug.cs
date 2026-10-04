using Tidewater.Ocean;
using UnityEditor;
using UnityEngine;

// Editor tools for the mini fishing boat (the lobster boat's are BoatDebug in SceneBuilder.cs): put the scene object in place, run the boat in the
// Editor (it does not tick when not playing), place the camera on it.
namespace Tidewater.EditorTools
{
	public static class MiniBoatDebug
	{
		// the "Mini Fishing Boat" object with its view and driver (SceneBuilder makes it with the island scene; this adds it to an open scene)
		public static string Ensure()
		{
			var g = GameObject.Find( "Mini Fishing Boat" ) ?? new GameObject( "Mini Fishing Boat" );
			if ( g.GetComponent<Tidewater.World.Boat.MiniBoatView>() == null ) g.AddComponent<Tidewater.World.Boat.MiniBoatView>();
			if ( g.GetComponent<Tidewater.Player.MiniBoatDriver>() == null ) g.AddComponent<Tidewater.Player.MiniBoatDriver>();
			return "mini boat object ready";
		}

		// runs the boat for `seconds` of sim time: each frame the sea advances, the water queries are dispatched and read back at once, and the controller steps
		public static string Sim( double seconds, double throttle, double steer, bool driven, double dt = 1.0 / 30 )
		{
			var g = GameObject.Find( "Mini Fishing Boat" );
			var drv = g != null ? g.GetComponent<Tidewater.Player.MiniBoatDriver>() : null;
			var ocean = Object.FindAnyObjectByType<OceanRenderer>();
			if ( drv == null ) return "no mini boat";
			if ( ocean == null || ocean.query == null ) return "no ocean query yet (render a frame first)";
			if ( ! drv.Ensure() ) return "driver not ready";
			var c = drv.controller;
			c.driven = driven; if ( driven ) c.moored = false;
			var q = ocean.query;
			for ( double t = 0; t < seconds; t += dt )
			{
				ocean.Advance( ( float ) dt, ( float ) dt );
				q.Update(); q.Flush();
				c.setInput( throttle, steer, dt );
				drv.Tick( dt );
			}

			return State();
		}

		public static string State()
		{
			var g = GameObject.Find( "Mini Fishing Boat" );
			var c = g?.GetComponent<Tidewater.Player.MiniBoatDriver>()?.controller;
			if ( c == null ) return "no controller";
			double heave = c.position.y;
			var f = c.forward( new Tidewater.Engine.Vector3() );
			return $"mini at sim ({c.position.x:F2}, {c.position.y:F3}, {c.position.z:F2}) speed {c.speed:F2} m/s heading {System.Math.Atan2( f.x, - f.z ) * 180 / System.Math.PI:F1} deg moored {c.moored} driven {c.driven} wetFraction {c.wetFraction:F2}";
		}

		// camera `dist` m from the boat along `bearingDeg` (0 = north of it), `eye` m above the water, looking `lookY` m above its origin
		public static string Look( double dist, double bearingDeg, double eye, double lookY = 0.5 )
		{
			var g = GameObject.Find( "Mini Fishing Boat" );
			if ( g == null ) return "no mini boat";
			var c = Camera.main;
			var dir = Quaternion.Euler( 0, ( float ) bearingDeg, 0 ) * Vector3.forward;
			var target = g.transform.position + Vector3.up * ( float ) lookY;
			c.transform.position = new Vector3( g.transform.position.x, ( float ) eye, g.transform.position.z ) + dir * ( float ) dist;
			c.transform.rotation = Quaternion.LookRotation( ( target - c.transform.position ).normalized, Vector3.up );
			return "camera at " + c.transform.position;
		}
	}
}
