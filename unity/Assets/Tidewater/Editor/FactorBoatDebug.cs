using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.World.Boat;
using UnityEditor;
using UnityEngine;

// Editor tools for the glTF boats, "mini" (the mini fishing boat) and "pelagic" (the Pelagic 30); the lobster boat's are BoatDebug in
// SceneBuilder.cs: put the scene object in place, run the boat in the Editor (it does not tick when not playing), place the camera on it.
namespace Tidewater.EditorTools
{
	public static class FactorBoatDebug
	{
		static string ObjectName( string which ) => which == "pelagic" ? "Pelagic 30" : "Mini Fishing Boat";

		// the boat's object with its view and driver (SceneBuilder makes them with the island scene; this adds them to an open scene)
		public static string Ensure( string which )
		{
			var g = GameObject.Find( ObjectName( which ) ) ?? new GameObject( ObjectName( which ) );
			if ( which == "pelagic" )
			{
				if ( g.GetComponent<Pelagic30View>() == null ) g.AddComponent<Pelagic30View>();
				if ( g.GetComponent<Pelagic30Driver>() == null ) g.AddComponent<Pelagic30Driver>();
			}
			else
			{
				if ( g.GetComponent<MiniBoatView>() == null ) g.AddComponent<MiniBoatView>();
				if ( g.GetComponent<MiniBoatDriver>() == null ) g.AddComponent<MiniBoatDriver>();
			}

			return which + " boat object ready";
		}

		static (BoatController c, System.Action<double> tick, bool ready) Find( string which )
		{
			var g = GameObject.Find( ObjectName( which ) );
			if ( g == null ) return ( null, null, false );
			if ( which == "pelagic" ) { var d = g.GetComponent<Pelagic30Driver>(); return ( d?.controller, d != null ? d.Tick : null, d != null && d.Ensure() ); }
			var m = g.GetComponent<MiniBoatDriver>(); return ( m?.controller, m != null ? m.Tick : null, m != null && m.Ensure() );
		}

		// runs the boat for `seconds` of sim time: each frame the sea advances, the water queries are dispatched and read back at once, and the controller steps
		public static string Sim( string which, double seconds, double throttle, double steer, bool driven, double dt = 1.0 / 30 )
		{
			var ocean = Object.FindAnyObjectByType<OceanRenderer>();
			if ( ocean == null || ocean.query == null ) return "no ocean query yet (render a frame first)";
			var f = Find( which );
			if ( f.tick == null ) return "no " + which + " boat";
			if ( ! f.ready ) return "driver not ready";
			f = Find( which );
			var c = f.c;
			c.driven = driven; if ( driven ) c.moored = false;
			var q = ocean.query;
			for ( double t = 0; t < seconds; t += dt )
			{
				ocean.Advance( ( float ) dt, ( float ) dt );
				q.Update(); q.Flush();
				c.setInput( throttle, steer, dt );
				f.tick( dt );
			}

			return State( which );
		}

		public static string State( string which )
		{
			var c = Find( which ).c;
			if ( c == null ) return "no controller";
			var f = c.forward( new Tidewater.Engine.Vector3() );
			return $"{which} at sim ({c.position.x:F2}, {c.position.y:F3}, {c.position.z:F2}) speed {c.speed:F2} m/s heading {System.Math.Atan2( f.x, - f.z ) * 180 / System.Math.PI:F1} deg moored {c.moored} driven {c.driven} wetFraction {c.wetFraction:F2}";
		}

		// camera `dist` m from the boat along `bearingDeg` (0 = north of it), `eye` m above the water, looking `lookY` m above its origin
		public static string Look( string which, double dist, double bearingDeg, double eye, double lookY = 0.5 )
		{
			var g = GameObject.Find( ObjectName( which ) );
			if ( g == null ) return "no " + which + " boat";
			var c = Camera.main;
			var dir = Quaternion.Euler( 0, ( float ) bearingDeg, 0 ) * Vector3.forward;
			var target = g.transform.position + Vector3.up * ( float ) lookY;
			c.transform.position = new Vector3( g.transform.position.x, ( float ) eye, g.transform.position.z ) + dir * ( float ) dist;
			c.transform.rotation = Quaternion.LookRotation( ( target - c.transform.position ).normalized, Vector3.up );
			return "camera at " + c.transform.position;
		}
	}
}

