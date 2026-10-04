using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.World.Boat;
using UnityEngine;
using Vector3 = Tidewater.Engine.Vector3;

// Compares the C# Pelagic30 with the JS original (the physics adapter's numbers, the deck facade sampled on a grid, the axis-aligned colliders, the
// materials and the mask):
//   node unity/tools/dump-pelagic.mjs unity/Temp/oracle/pelagic
//   unity eval 'return Tidewater.EditorTools.Pelagic30Oracle.Compare();'
namespace Tidewater.EditorTools
{
	public static class Pelagic30Oracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/pelagic" ) );
		static double[] Arr( JToken t ) => t.Select( v => ( double ) v ).ToArray();
		static double[] V( Vector3 v ) => new[] { v.x, v.y, v.z };

		static string Cmp( string name, double[] a, double[] b )
		{
			if ( a.Length != b.Length ) return $"  {name}: LENGTH {a.Length} vs JS {b.Length}\n";
			double maxAbs = 0; int worst = 0;
			for ( int i = 0; i < a.Length; i ++ ) { double e = Math.Abs( a[ i ] - b[ i ] ); if ( ! ( e <= maxAbs ) ) { maxAbs = e; worst = i; } }
			return $"  {name}: n {a.Length}, max abs {maxAbs:E2}" + ( maxAbs > 0 ? $" (#{worst}: {a[ worst ]:R} vs JS {b[ worst ]:R})" : "" ) + "\n";
		}

		static double[] Flat( JToken t ) => t.SelectMany( r => r is JArray ? Arr( r ) : new[] { ( double ) r } ).ToArray();

		public static string Compare( string dir = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "pelagic.json" ) ) );
			var m = new Pelagic30();
			var sb = new StringBuilder();
			sb.Append( Cmp( "hullSamples", m.hullSamples.SelectMany( s => V( s.position ).Concat( new[] { s.area, s.bottomY } ) ).ToArray(), js[ "hullSamples" ].SelectMany( s => Arr( s[ "p" ] ).Concat( new[] { ( double ) s[ "area" ], ( double ) s[ "bottomY" ] } ) ).ToArray() ) );
			sb.Append( Cmp( "hydro", new[] { m.hydro.suggestedMass }.Concat( V( m.hydro.centerOfMass ) ).Concat( V( m.hydro.inertia ) ).ToArray(), new[] { ( double ) js[ "hydro" ][ "suggestedMass" ] }.Concat( Arr( js[ "hydro" ][ "centerOfMass" ] ) ).Concat( Arr( js[ "hydro" ][ "inertia" ] ) ).ToArray() ) );
			sb.Append( Cmp( "drive", new[] { m.rudder.z, m.maxThrust, m.pitchSpeed, m.reverseFactor, m.lateralY, m.hullLift, m.rudderLift, m.bowZ, m.sternZ }.Concat( V( m.propeller ) ).ToArray(),
				new[] { "rudderZ", "maxThrust", "pitchSpeed", "reverseFactor", "lateralY", "hullLift", "rudderLift", "bowZ", "sternZ" }.Select( k => ( double ) js[ k ] ).Concat( Arr( js[ "propeller" ] ) ).ToArray() ) );
			sb.Append( Cmp( "stations", m.stations.SelectMany( s => s ).ToArray(), Flat( js[ "stations" ] ) ) );
			sb.Append( Cmp( "contact+outline", m.contactPoints.Concat( m.outline ).SelectMany( V ).ToArray(), Flat( js[ "contactPoints" ] ).Concat( Flat( js[ "outline" ] ) ).ToArray() ) );
			sb.Append( Cmp( "helm / board / exit", V( m.helmEye ).Concat( V( m.helmPoint ) ).Concat( V( m.boardPoint ) ).Concat( m.exitPoints.SelectMany( V ) ).Concat( new[] { m.helmPointX, m.helmPointZ } ).ToArray(),
				Arr( js[ "helmEye" ] ).Concat( Arr( js[ "helmPoint" ] ) ).Concat( Arr( js[ "boardPoint" ] ) ).Concat( Flat( js[ "exitPoints" ] ) ).Concat( new[] { ( double ) js[ "helmPoint" ][ 0 ], ( double ) js[ "helmPoint" ][ 2 ] } ).ToArray() ) );
			sb.Append( Cmp( "colliders", m.colliders.SelectMany( c => V( c.center ).Concat( V( c.half ) ).Concat( new[] { c.walkable ? 1.0 : 0, c.solid ? 1.0 : 0 } ) ).ToArray(),
				js[ "colliders" ].SelectMany( c => Arr( c[ "center" ] ).Concat( Arr( c[ "half" ] ) ).Concat( new[] { ( bool ) c[ "walkable" ] ? 1.0 : 0, ( bool ) c[ "solid" ] ? 1.0 : 0 } ) ).ToArray() ) );
			var L = js[ "lines" ]; var d = m.deck;
			sb.Append( Cmp( "deck lines", new[] { d.deckY, d.shell, d.zAft, d.zFwd.Value }, new[] { ( double ) L[ "deckY" ], ( double ) L[ "shell" ], ( double ) L[ "zAft" ], ( double ) L[ "zFwd" ] } ) );
			var grid = L[ "facade" ].Select( r => Arr( r ) ).ToArray();
			sb.Append( Cmp( "facade tAtSheerZ / halfBreadth", grid.SelectMany( r => { double t = d.tAtSheerZ( r[ 0 ] ); return new[] { t, d.halfBreadth( t, r[ 1 ] ) }; } ).ToArray(), grid.SelectMany( r => new[] { r[ 2 ], r[ 3 ] } ).ToArray() ) );

			// the model: one mesh per glTF material, the welded mask
			var mats = m.materials;
			sb.Append( $"  materials: {mats.Count} ({mats.Sum( f => f.geometry.index.array.Length / 3 )} triangles, {mats.Count( f => f.glass )} glass), mask: {m.maskGeometry?.index.array.Length / 3} triangles on {m.maskGeometry?.getAttribute( "position" ).array.Length / 3} vertices\n" );
			var dyn = m.dynamics();
			sb.Append( $"  dynamics: hasLines {dyn.hasLines}, zAft {dyn.zAft}, zFwd {dyn.zFwd}, deckY {dyn.deckY}\n" );
			return sb.ToString();
		}
	}
}
