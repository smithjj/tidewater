using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;
using Vector3 = Tidewater.Engine.Vector3;

// Compares the C# swimming schools (FishSchools.cs) with the JS original (unity/tools/dump-schools.mjs -> Temp/oracle/schools): the layout after
// construction (groups, homes, every fish's placement, size, pattern, slot) and, for four scenarios (quiet; the swimmer inside the bait ball;
// someone wading; the swimmer at the pier piles), the state after N updates and culls: positions, velocities, headings, bank, bend, wave
// phase, the groups, the lists of the batch and the instance records.
namespace Tidewater.EditorTools
{
	public static class SchoolsOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/schools" ) );

		[MenuItem( "Tidewater/Compare schools with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare( DefaultDir ) );

		static double[] D( JToken t ) => t.Select( x => ( double ) x ).ToArray();

		// max |got - want| over the array, and where
		static string Diff( string name, float[] got, double[] want, StringBuilder sb, ref double worst )
		{
			if ( got.Length != want.Length ) { sb.AppendLine( $"  {name}: length {got.Length} (want {want.Length})" ); worst = double.PositiveInfinity; return null; }
			double max = 0; int at = -1;
			for ( int i = 0; i < want.Length; i ++ )
			{
				double d = Math.Abs( ( double ) got[ i ] - want[ i ] );
				if ( d > max || double.IsNaN( d ) ) { max = d; at = i; }
			}

			worst = Math.Max( worst, max );
			return $"{name} {max:E1}" + ( max > 0 ? $"@{at}" : "" );
		}

		static string Diff( string name, byte[] got, double[] want, StringBuilder sb, ref double worst ) => Diff( name, got.Select( b => ( float ) b ).ToArray(), want, sb, ref worst );
		static string Diff( string name, int[] got, double[] want, StringBuilder sb, ref double worst ) => Diff( name, got.Select( b => ( float ) b ).ToArray(), want, sb, ref worst );

		static double V3( Vector3 v, JToken w ) => Math.Max( Math.Abs( v.x - ( double ) w[ 0 ] ), Math.Max( Math.Abs( v.y - ( double ) w[ 1 ] ), Math.Abs( v.z - ( double ) w[ 2 ] ) ) );

		// the groups: the worst difference of any field, and the first group that differs
		static double Groups( List<FishGroup> got, JToken want, StringBuilder sb, string label )
		{
			double worst = 0; int bad = -1;
			if ( got.Count != want.Count() ) { sb.AppendLine( $"  {label}: {got.Count} groups (want {want.Count()})" ); return double.PositiveInfinity; }
			for ( int i = 0; i < got.Count; i ++ )
			{
				var g = got[ i ]; var w = want[ i ];
				double d = 0;
				if ( g.sp.name != ( string ) w[ "name" ] || g.count != ( int ) w[ "count" ] || g.offset != ( int ) w[ "offset" ] ) d = double.PositiveInfinity;
				var z = w[ "zone" ];
				d = Math.Max( d, Math.Max( Math.Abs( g.zone.x - ( double ) z[ "x" ] ), Math.Max( Math.Abs( g.zone.z - ( double ) z[ "z" ] ), Math.Abs( g.zone.r - ( double ) z[ "r" ] ) ) ) );
				d = Math.Max( d, Math.Max( Math.Abs( g.zone.band[ 0 ] - ( double ) z[ "band" ][ 0 ] ), Math.Abs( g.zone.band[ 1 ] - ( double ) z[ "band" ][ 1 ] ) ) );
				if ( ( g.zone.path != null ? g.zone.path.Count : 0 ) != ( int ) z[ "path" ] || ( g.zone.piles != null ? g.zone.piles.Count : 0 ) != ( int ) z[ "piles" ] || g.zone.sand != ( bool ) z[ "sand" ] ) d = double.PositiveInfinity;
				d = Math.Max( d, V3( g.home, w[ "home" ] ) ); d = Math.Max( d, V3( g.goal, w[ "goal" ] ) ); d = Math.Max( d, V3( g.center, w[ "center" ] ) ); d = Math.Max( d, V3( g.heading, w[ "heading" ] ) );
				d = Math.Max( d, Math.Abs( g.timer - ( double ) w[ "timer" ] ) ); d = Math.Max( d, Math.Abs( g.alarm - ( double ) w[ "alarm" ] ) ); d = Math.Max( d, Math.Abs( g.spin - ( double ) w[ "spin" ] ) );
				if ( g.active != ( bool ) w[ "active" ] ) d = double.PositiveInfinity;
				d = Math.Max( d, Math.Abs( g.radius - ( double ) w[ "radius" ] ) ); d = Math.Max( d, Math.Abs( g.ball - ( double ) w[ "ball" ] ) ); d = Math.Max( d, Math.Abs( g.rest - ( double ) w[ "rest" ] ) );
				d = Math.Max( d, Math.Abs( g.breath - ( double ) w[ "breath" ] ) ); d = Math.Max( d, Math.Abs( g.jumpTimer - ( double ) w[ "jumpTimer" ] ) );
				if ( g.pathIndex != ( int ) w[ "pathIndex" ] || g.pathDir != ( int ) w[ "pathDir" ] ) d = double.PositiveInfinity;
				if ( d > worst ) { worst = d; bad = i; }
			}

			sb.AppendLine( $"  {label}: {got.Count} groups, worst field diff {worst:E2}" + ( worst > 0 ? $" (group {bad}: {got[ bad ].sp.name})" : "" ) );
			return worst;
		}

		static double Hypot2( double a, double b ) => Math.Sqrt( a * a + b * b );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var J = JObject.Parse( File.ReadAllText( dir + "/schools.json" ) );
			int seed = ( int ) J[ "seed" ];
			var terrain = new TerrainData( seed );
			var shore = ShoreField.Compute( terrain, 512, WorldLayout.SwellDirX, WorldLayout.SwellDirZ );
			Func<FishSchools> make = () => new FishSchools( terrain, new Vector3( WorldLayout.Reef.x, 0, WorldLayout.Reef.z ), WorldLayout.Reef.radius + 10, shoreField: shore );
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var f0 = make();
			sb.AppendLine( $"built in {sw.ElapsedMilliseconds} ms" );

			// ---- layout
			var L = J[ "layout" ];
			sb.AppendLine( $"layout: {f0.fishCount} fish (want {( int ) L[ "count" ]}), models {string.Join( ",", f0.models )} (want {string.Join( ",", L[ "models" ].Select( x => ( string ) x ) )})" );
			var dp = ( JArray ) L[ "dropPath" ];
			double dd = f0.dropPath.Count == dp.Count ? 0 : double.PositiveInfinity;
			for ( int i = 0; dd < double.PositiveInfinity && i < dp.Count; i ++ ) dd = Math.Max( dd, Math.Max( Math.Abs( f0.dropPath[ i ][ 0 ] - ( double ) dp[ i ][ 0 ] ), Math.Abs( f0.dropPath[ i ][ 1 ] - ( double ) dp[ i ][ 1 ] ) ) );
			sb.AppendLine( $"  drop-off path: {f0.dropPath.Count} points (want {dp.Count}), diff {dd:E2}" );
			Groups( f0.groups, L[ "groups" ], sb, "groups" );
			var F = L[ "fish" ];
			double w0 = 0; var parts = new List<string>();
			parts.Add( Diff( "pos", f0.pos, D( F[ "pos" ] ), sb, ref w0 ) ); parts.Add( Diff( "vel", f0.vel, D( F[ "vel" ] ), sb, ref w0 ) ); parts.Add( Diff( "head", f0.head, D( F[ "head" ] ), sb, ref w0 ) );
			parts.Add( Diff( "size", f0.size, D( F[ "size" ] ), sb, ref w0 ) ); parts.Add( Diff( "phase", f0.phase, D( F[ "phase" ] ), sb, ref w0 ) ); parts.Add( Diff( "speedMul", f0.speedMul, D( F[ "speedMul" ] ), sb, ref w0 ) );
			parts.Add( Diff( "seed", f0.seed, D( F[ "seed" ] ), sb, ref w0 ) ); parts.Add( Diff( "kind", f0.kind, D( F[ "kind" ] ), sb, ref w0 ) ); parts.Add( Diff( "pattern", f0.pattern, D( F[ "pattern" ] ), sb, ref w0 ) );
			parts.Add( Diff( "slot", f0.slot, D( F[ "slot" ] ), sb, ref w0 ) ); parts.Add( Diff( "floorC", f0.floorC, D( F[ "floorC" ] ), sb, ref w0 ) );
			sb.AppendLine( "  fish: " + string.Join( ", ", parts ) );

			// ---- scenarios
			foreach ( var S in J[ "scenarios" ] )
			{
				string name = ( string ) S[ "name" ]; int steps = ( int ) S[ "steps" ];
				var f = make();
				var C = S[ "camera" ];
				var planes = D( C[ "planes" ] ); var cp = D( C[ "pos" ] ); double pxScale = ( double ) C[ "pxScale" ];
				var P = S[ "player" ];
				int air = 0;
				for ( int s = 0; s < steps; s ++ )
				{
					Vector3 pl = null;
					if ( name == "bait" ) { var c = f.baitGroups[ 0 ].center; pl = new Vector3( c.x + 4, c.y, c.z ); }
					else if ( P.Type != JTokenType.Null ) pl = new Vector3( ( double ) P[ 0 ], ( double ) P[ 1 ], ( double ) P[ 2 ] );
					f.update( 1.0 / 60, pl );
					f.cull( cp[ 0 ], cp[ 1 ], cp[ 2 ], planes, pxScale );
					for ( int i = 0; i < f.jump.Length; i ++ ) if ( f.jump[ i ] > 0.5f ) { air ++; break; }
				}

				int act = f.groups.Count( g => g.active ); double moved = 0;
				for ( int i = 0; i < f.groups.Count; i ++ ) moved = Math.Max( moved, Hypot2( f.groups[ i ].center.x - f0.groups[ i ].home.x, f.groups[ i ].center.z - f0.groups[ i ].home.z ) );
				sb.AppendLine( $"scenario {name} ({steps} steps, time {f.time:0.000} (want {( double ) S[ "time" ]:0.000}); {act} groups active (want {( int ) S[ "active" ]}), the farthest group centre moved {moved:0.0} m (JS {( double ) S[ "moved" ]:0.0}); steps with a mullet rising or leaping {air} (JS {( int ) S[ "airSteps" ]})):" );
				var SF = S[ "fish" ];
				double w = 0; var ps = new List<string>();
				ps.Add( Diff( "pos", f.pos, D( SF[ "pos" ] ), sb, ref w ) ); ps.Add( Diff( "vel", f.vel, D( SF[ "vel" ] ), sb, ref w ) ); ps.Add( Diff( "head", f.head, D( SF[ "head" ] ), sb, ref w ) );
				ps.Add( Diff( "panic", f.panic, D( SF[ "panic" ] ), sb, ref w ) ); ps.Add( Diff( "jump", f.jump, D( SF[ "jump" ] ), sb, ref w ) ); ps.Add( Diff( "roll", f.roll, D( SF[ "roll" ] ), sb, ref w ) );
				ps.Add( Diff( "bend", f.bend, D( SF[ "bend" ] ), sb, ref w ) ); ps.Add( Diff( "phase", f.phase, D( SF[ "phase" ] ), sb, ref w ) ); ps.Add( Diff( "floorC", f.floorC, D( SF[ "floorC" ] ), sb, ref w ) );
				ps.Add( Diff( "shallowC", f.shallowC, D( SF[ "shallowC" ] ), sb, ref w ) );
				sb.AppendLine( "  fish: " + string.Join( ", ", ps ) );
				Groups( f.groups, S[ "groups" ], sb, "groups" );

				var B = S[ "batch" ]; var b = f.batch;
				int listBad = 0;
				var wl = D( B[ "list" ] ); var wf = D( B[ "fadeList" ] );
				if ( b.visibleInstances != ( int ) B[ "visible" ] || b.fadeInstances != ( int ) B[ "fade" ] ) listBad = int.MaxValue;
				else
				{
					for ( int i = 0; i < wl.Length; i ++ ) if ( b.list[ i ] != ( uint ) wl[ i ] ) listBad ++;
					for ( int i = 0; i < wf.Length; i ++ ) if ( b.fadeList[ i ] != ( uint ) wf[ i ] ) listBad ++;
				}

				sb.AppendLine( $"  batch: {b.visibleInstances} drawn + {b.fadeInstances} fading (want {( int ) B[ "visible" ]} + {( int ) B[ "fade" ]}), list mismatches {listBad}" );
				var ids = D( B[ "ids" ] ); var data = ( JArray ) B[ "data" ];
				double rd = 0; int rdAt = -1, rdId = -1;
				for ( int k = 0; k < ids.Length; k ++ )
					for ( int e = 0; e < 16; e ++ )
					{
						double d = Math.Abs( ( double ) b.data[ ( int ) ids[ k ] * 16 + e ] - ( double ) data[ k ][ e ] );
						if ( d > rd ) { rd = d; rdAt = e; rdId = ( int ) ids[ k ]; }
					}

				sb.AppendLine( $"  records: {ids.Length} compared, worst diff {rd:E2}" + ( rd > 0 ? $" (field {rdAt} of fish {rdId})" : "" ) );
			}

			return sb.ToString();
		}
	}
}
