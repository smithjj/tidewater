using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.Player;
using Tidewater.World;
using Tidewater.World.Boat;
using UnityEditor;
using UnityEngine;

// Compares the C# boat controller with the JS original, on a synthetic sea (polynomial waves) and sea floor:
//   node unity/tools/dump-boat-controller.mjs unity/Temp/oracle/boatctl
//   unity/tools/boat-controller-oracle.sh
namespace Tidewater.EditorTools
{
	public static class BoatControllerOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/boatctl" ) );

		static double tri( double p ) { double q = p - Math.Floor( p ); double k = q < 0.5 ? 2 * q : 2 - 2 * q; return k * k * ( 3 - 2 * k ); }
		static double waterAt( double x, double z, double t ) => 0.55 * ( tri( x * 0.11 + z * 0.07 - 0.45 * t ) - 0.5 ) * 2 + 0.22 * ( tri( - x * 0.2 + z * 0.17 - 0.7 * t ) - 0.5 ) * 2;
		static double groundAt( double x, double z ) => - 6 + 0.05 * ( 60 - z ) + 9.5 * ( 1 - Tidewater.Engine.MathUtils.smoothstep( JS.Hypot( x - 70, z - 95 ), 0, 25 ) );

		sealed class FakeQuery : IWaterQuery
		{
			public int count = 1;
			public float latency { get; set; } = 0.05f;
			public bool cpuValid { get; set; }
			public int version { get; set; }
			public double resultTime { get; set; }
			public float[] cpu { get; } = new float[ 1024 ];
			public float[] resultInputs { get; } = new float[ 1024 ];
			readonly float[] inputs = new float[ 1024 ];
			bool pending; int pendingF; float[] pendingRes; float[] pendingInp; double pendingT;

			public int Allocate( string name, int n ) { int s = count; count += n; return s; }
			public void SetPoint( int i, float x, float z ) { inputs[ i * 4 ] = x; inputs[ i * 4 + 1 ] = z; }

			public void Update( int f, double t )
			{
				if ( pending && f >= pendingF )
				{
					Array.Copy( pendingRes, cpu, cpu.Length ); Array.Copy( pendingInp, resultInputs, cpu.Length ); resultTime = pendingT; cpuValid = true; version ++; pending = false;
				}

				if ( ! pending && f % 2 == 0 )
				{
					var res = new float[ 1024 ]; double e = 0.35;
					for ( int i = 1; i < count; i ++ )
					{
						double x = inputs[ i * 4 ], z = inputs[ i * 4 + 1 ];
						double h = waterAt( x, z, t ), hx = waterAt( x + e, z, t ), hz = waterAt( x, z + e, t );
						var n = new Tidewater.Engine.Vector3( ( h - hx ) / e, 1, ( h - hz ) / e ).normalize();
						res[ i * 4 ] = ( float ) h; res[ i * 4 + 1 ] = ( float ) n.x; res[ i * 4 + 2 ] = ( float ) n.z; res[ i * 4 + 3 ] = ( float ) groundAt( x, z );
					}

					pending = true; pendingF = f + 2; pendingRes = res; pendingInp = ( float[] ) inputs.Clone(); pendingT = t;
				}
			}
		}

		static string Cmp( string name, double[] a, double[] b )
		{
			if ( a.Length != b.Length ) return $"  {name}: LENGTH {a.Length} vs JS {b.Length}\n";
			double maxAbs = 0; int worst = 0;
			for ( int i = 0; i < a.Length; i ++ ) { double e = Math.Abs( a[ i ] - b[ i ] ); if ( ! ( e <= maxAbs ) ) { maxAbs = e; worst = i; } }
			return $"  {name}: n {a.Length}, max abs {maxAbs:E2}" + ( maxAbs > 0 ? $" (#{worst}: {a[ worst ]:R} vs JS {b[ worst ]:R})" : "" ) + "\n";
		}

		static double[] Arr( JToken t ) => t.Select( v => ( double ) v ).ToArray();

		static BoatModel cachedModel;
		public static string Compare( string dir = null, string only = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "boatctl.json" ) ) );
			var sb = new StringBuilder();
			var model = cachedModel ?? ( cachedModel = new BoatModel() );
			var colliders = new Colliders();
			colliders.addCylinder( 66.0, 35.6, 0.3, -3, 2.3, "pile" );
			colliders.addBox( new Tidewater.Engine.Vector3( 60, 0, 38 ), new Tidewater.Engine.Vector3( 1, 2, 3 ), 0.3, false, true, "box" );
			double dt = 1.0 / 60;
			string[] cols = { "t", "pos", "pos", "pos", "quat", "quat", "quat", "quat", "vel", "vel", "vel", "ang", "ang", "ang", "throttle", "steer", "rpm", "thrust", "wet", "speed", "tension", "hasWater" };

			foreach ( var prop in js.Properties() )
			{
				if ( only != null && prop.Name != only ) continue;
				var sc = prop.Value;
				sb.Append( $"[{prop.Name}]\n" );
				var d = sc[ "dock" ];
				var dockPos = Arr( d[ "position" ] );
				var dock = new BoatDock( new Tidewater.Engine.Vector3( dockPos[ 0 ], dockPos[ 1 ], dockPos[ 2 ] ), ( double ) d[ "heading" ] );
				var query = new FakeQuery();
				var c = new BoatController( model.dynamics(), query, groundAt, colliders, dock );

				var ctor = sc[ "ctor" ];
				sb.Append( Cmp( "mass/BG/pitchK", new[] { c.mass, c.BG, c.pitchStiffness, c.nHull, c.samples.Count, c.slot - c.slot + ( int ) ctor[ "slot" ] }, new[] { ( double ) ctor[ "mass" ], ( double ) ctor[ "BG" ], ( double ) ctor[ "pitchStiffness" ], ( double ) ctor[ "nHull" ], ( double ) ctor[ "n" ], ( double ) ctor[ "slot" ] } ) );
				sb.Append( Cmp( "com+inertia", new[] { c.com.x, c.com.y, c.com.z, c.inertia.x, c.inertia.y, c.inertia.z }, Arr( ctor[ "com" ] ).Concat( Arr( ctor[ "inertia" ] ) ).ToArray() ) );
				var smp = c.samples.SelectMany( s => new[] { s.p.x, s.p.y, s.p.z, s.area, s.bottom, s.reserve ? 1.0 : 0, s.reserve ? s.@ref : -1 } ).ToArray();
				sb.Append( Cmp( "samples", smp, ctor[ "samples" ].SelectMany( r => Arr( r ) ).ToArray() ) );
				sb.Append( Cmp( "bow+stern", new[] { c.bowWorld.x, c.bowWorld.y, c.bowWorld.z, c.sternWorld.x, c.sternWorld.y, c.sternWorld.z }, Arr( ctor[ "bow" ] ).Concat( Arr( ctor[ "stern" ] ) ).ToArray() ) );

				double throttle = 0, steer = 0;
				int frames = ( int ) JS.Round( ( double ) sc[ "seconds" ] / dt );
				var events = sc[ "events" ].Select( e => (e: e, f: ( int ) JS.Round( ( double ) e[ "t" ] / dt )) ).ToArray();
				int evi = 0;
				var rows = sc[ "rows" ].Select( r => Arr( r ) ).ToArray();
				int ri = 0;
				var worst = new double[ cols.Length ]; var firstBad = -1.0; double finalDiff = 0;
				for ( int f = 0; f < frames; f ++ )
				{
					double t = f * dt;
					while ( evi < events.Length && events[ evi ].f <= f )
					{
						var e = events[ evi ++ ].e;
						if ( e[ "driven" ] != null ) c.driven = ( bool ) e[ "driven" ];
						if ( e[ "moored" ] != null ) c.moored = ( bool ) e[ "moored" ];
						if ( e[ "throttle" ] != null ) throttle = ( double ) e[ "throttle" ];
						if ( e[ "steer" ] != null ) steer = ( double ) e[ "steer" ];
						if ( e[ "drop" ] != null ) c.dropAnchor( ( double ) e[ "drop" ] );
						if ( e[ "weigh" ] != null ) c.weighAnchor();
					}

					c.setInput( throttle, steer, dt );
					c.queueQueries();
					query.Update( f, t );
					c.update( dt );
					if ( f % 15 == 0 )
					{
						var cs = new[] { t, c.position.x, c.position.y, c.position.z, c.quaternion.x, c.quaternion.y, c.quaternion.z, c.quaternion.w, c.velocity.x, c.velocity.y, c.velocity.z,
							c.angular.x, c.angular.y, c.angular.z, c.throttle, c.steer, c.rpm, c.thrust, c.wetFraction, c.speed, c.anchor.tension, c.hasWater ? 1.0 : 0 };
						var jr = rows[ ri ++ ];
						double rowMax = 0;
						for ( int k = 0; k < cs.Length; k ++ ) { double e = Math.Abs( cs[ k ] - jr[ k ] ); worst[ k ] = Math.Max( worst[ k ], e ); if ( k < 14 ) rowMax = Math.Max( rowMax, e ); }
						if ( firstBad < 0 && rowMax > 1e-6 ) firstBad = t;
						finalDiff = rowMax;
					}
				}

				sb.Append( "  worst abs error over the run: " + string.Join( " ", Enumerable.Range( 0, cols.Length ).Select( k => cols[ k ] + "=" + worst[ k ].ToString( "E1" ) ).Distinct() ) + "\n" );
				sb.Append( $"  state error at the end {finalDiff:E2}; first row off by > 1e-6: " + ( firstBad < 0 ? "none" : firstBad.ToString( "F2" ) + " s" ) + "\n" );
			}

			return sb.ToString();
		}
	}
}
