using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Engine;
using Tidewater.Util;
using Tidewater.World;
using Tidewater.World.Village;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;
using Vector3 = Tidewater.Engine.Vector3;

// Compares the C# village port with the JS original (unity/tools/dump-village.mjs -> Temp/oracle/village): the merged
// meshes (counts, attributes, index ranges), the swinging parts, colliders, lights, footprints, foundation checks,
// buildings, pads and the terrain heights after the pads were flattened.
namespace Tidewater.EditorTools
{
	public static class VillageOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/village" ) );

		[MenuItem( "Tidewater/Compare village with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare( DefaultDir ) );

		static float[] ReadF( string p ) { var b = File.ReadAllBytes( p ); var a = new float[ b.Length / 4 ]; Buffer.BlockCopy( b, 0, a, 0, b.Length ); return a; }
		static uint[] ReadU( string p ) { var b = File.ReadAllBytes( p ); var a = new uint[ b.Length / 4 ]; Buffer.BlockCopy( b, 0, a, 0, b.Length ); return a; }

		static void CmpArr( StringBuilder sb, string name, float[] got, string path )
		{
			var want = ReadF( path );
			if ( want.Length != got.Length ) { sb.AppendLine( $"  {name}: LENGTH {got.Length} != {want.Length}" ); return; }
			double max = 0; int at = -1;
			for ( int i = 0; i < want.Length; i ++ )
			{
				double d = Math.Abs( ( double ) got[ i ] - want[ i ] );
				if ( d > max ) { max = d; at = i; }
			}

			sb.AppendLine( $"  {name}: n={want.Length} maxDiff={max:E2}" + ( max > 1e-4 ? $" at {at} (got {got[ at ]:R} want {want[ at ]:R})" : "" ) );
		}

		// the geometry of one mesh against <dir>/<buffer>.*
		static void CmpGeo( StringBuilder sb, string name, BuiltGeometry g, string dir, string buffer, JToken meta, int idxStart = 0, int idxCount = -1 )
		{
			sb.AppendLine( $"{name}: vertices {g.vertexCount} (want {meta[ "vertices" ]}), indices {g.index.Length} (want {meta[ "indices" ]})" );
			CmpArr( sb, "pos", g.position, $"{dir}/{buffer}.position.f32" );
			CmpArr( sb, "nrm", g.normal, $"{dir}/{buffer}.normal.f32" );
			CmpArr( sb, "uv", g.uv, $"{dir}/{buffer}.uv.f32" );
			CmpArr( sb, "tint", g.tint, $"{dir}/{buffer}.tint.f32" );
			CmpArr( sb, "vdata", g.vdata, $"{dir}/{buffer}.vdata.f32" );
			var wi = ReadU( $"{dir}/{buffer}.idx.u32" );
			int bad = 0; if ( wi.Length != g.index.Length ) bad = -1; else for ( int i = 0; i < wi.Length; i ++ ) if ( wi[ i ] != ( uint ) g.index[ i ] ) bad ++;
			sb.AppendLine( $"  idx: mismatches={bad}" );
		}

		static double V( JToken t, int i ) => ( double ) t[ i ];
		static double Dv( Vector3 v, JToken t ) => Math.Max( Math.Abs( v.x - V( t, 0 ) ), Math.Max( Math.Abs( v.y - V( t, 1 ) ), Math.Abs( v.z - V( t, 2 ) ) ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var J = JObject.Parse( File.ReadAllText( dir + "/summary.json" ) );
			var T = new TerrainData( ( int ) J[ "seed" ] );
			var C = new Colliders();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var v = new Tidewater.World.Village.Village( T, C );
			sb.AppendLine( $"C# village built in {sw.ElapsedMilliseconds} ms" );
			var M = J[ "meshes" ];

			// the shared opaque buffer
			CmpGeo( sb, "village_wood(shared)", v.shared, dir, "village_wood", M[ "village_wood" ] );
			sb.AppendLine( $"shadow triangles {v.shadowTriangles} (want {J[ "shadowTriangles" ]})" );
			foreach ( var r in v.ranges )
			{
				var m = M[ "village_" + r.key ];
				sb.AppendLine( $"range {r.key}: start {r.start} count {r.count} (want {m[ "drawStart" ]} / {m[ "drawCount" ]})" );
			}

			if ( v.fabric != null ) CmpGeo( sb, "village_fabric", v.fabric, dir, "village_fabric", M[ "village_fabric" ] );
			else sb.AppendLine( "village_fabric: MISSING" );
			if ( v.nets != null ) CmpGeo( sb, "village_nets", v.nets, dir, "village_nets", M[ "village_nets" ] );
			else sb.AppendLine( "village_nets: MISSING" );

			// swinging parts
			var sg = v.sign;
			sb.AppendLine( $"sign pivot diff={Dv( sg.pivot, J[ "sign" ][ "pivot" ] ):E2}" );
			foreach ( var kv in sg.geometry ) CmpGeo( sb, "village_sign_" + kv.Key, kv.Value, dir, "village_sign_" + kv.Key, M[ "village_sign_" + kv.Key ] );
			sb.AppendLine( $"lanterns {v.lanterns.Count} (want {J[ "lanterns" ].Count()})" );
			var JL = ( JArray ) J[ "lanterns" ];
			double ml = 0; int lbad = 0;
			for ( int i = 0; i < Math.Min( JL.Count, v.lanterns.Count ); i ++ )
			{
				var l = v.lanterns[ i ];
				ml = Math.Max( ml, Math.Max( Dv( l.pivot, JL[ i ][ "pivot" ] ), Dv( l.rest, JL[ i ][ "rest" ] ) ) );
				foreach ( var kv in l.part.geometry )
				{
					string n = $"lantern{i}_{( kv.Key == "wood" ? 0 : 1 )}_village_lantern_{kv.Key}";
					if ( M[ n ] == null ) { sb.AppendLine( $"{n}: EXTRA" ); lbad ++; continue; }
					var w = ReadF( $"{dir}/{n}.position.f32" );
					double md = w.Length == kv.Value.position.Length ? 0 : 1e9;
					for ( int k = 0; k < Math.Min( w.Length, kv.Value.position.Length ); k ++ ) md = Math.Max( md, Math.Abs( w[ k ] - kv.Value.position[ k ] ) );
					var wi = ReadU( $"{dir}/{n}.idx.u32" );
					if ( md > 1e-4 || wi.Length != kv.Value.index.Length ) { lbad ++; sb.AppendLine( $"{n}: pos diff {md:E2}, idx {kv.Value.index.Length} vs {wi.Length}" ); }
				}
			}

			sb.AppendLine( $"  lantern pivot/rest maxDiff={ml:E2}, part mismatches={lbad}" );

			// colliders
			var JC = JObject.Parse( File.ReadAllText( dir + "/colliders.json" ) );
			var wb = ( JArray ) JC[ "boxes" ]; var wc = ( JArray ) JC[ "cylinders" ];
			sb.AppendLine( $"boxes {C.boxes.Count} (want {wb.Count}), cylinders {C.cylinders.Count} (want {wc.Count})" );
			double mb = 0; int badTag = 0;
			for ( int i = 0; i < Math.Min( wb.Count, C.boxes.Count ); i ++ )
			{
				var g = C.boxes[ i ]; var w = wb[ i ];
				mb = Math.Max( mb, Math.Max( Dv( g.center, w[ "c" ] ), Math.Max( Dv( g.half, w[ "h" ] ), Math.Abs( g.rotY - ( double ) w[ "rotY" ] ) ) ) );
				if ( g.tag != ( string ) w[ "tag" ] || g.walkable != ( bool ) w[ "walkable" ] || g.solid != ( bool ) w[ "solid" ] ) { if ( badTag ++ < 3 ) sb.AppendLine( $"  box {i}: tag {g.tag} vs {w[ "tag" ]}" ); }
			}

			double mc = 0; int badCt = 0;
			for ( int i = 0; i < Math.Min( wc.Count, C.cylinders.Count ); i ++ )
			{
				var g = C.cylinders[ i ]; var w = wc[ i ];
				mc = Math.Max( mc, Math.Max( Math.Abs( g.x - ( double ) w[ "x" ] ), Math.Max( Math.Abs( g.z - ( double ) w[ "z" ] ), Math.Max( Math.Abs( g.radius - ( double ) w[ "r" ] ), Math.Max( Math.Abs( g.yMin - ( double ) w[ "y0" ] ), Math.Abs( g.yMax - ( double ) w[ "y1" ] ) ) ) ) ) );
				if ( g.tag != ( string ) w[ "tag" ] ) { if ( badCt ++ < 3 ) sb.AppendLine( $"  cyl {i}: tag {g.tag} vs {w[ "tag" ]}" ); }
			}

			sb.AppendLine( $"  box maxDiff={mb:E2} tag/flag mismatches={badTag}; cyl maxDiff={mc:E2} tag mismatches={badCt}" );

			// lights
			var wl = ( JArray ) J[ "lights" ];
			double mlt = 0; int badL = 0;
			for ( int i = 0; i < Math.Min( wl.Count, v.lights.Count ); i ++ )
			{
				mlt = Math.Max( mlt, Dv( v.lights[ i ].position, wl[ i ][ "p" ] ) );
				if ( v.lights[ i ].kind != ( string ) wl[ i ][ "kind" ] || v.lights[ i ].intensity != ( double ) wl[ i ][ "i" ] ) badL ++;
			}

			sb.AppendLine( $"lights {v.lights.Count} (want {wl.Count}) maxDiff={mlt:E2} mismatches={badL}" );

			// footprints, checks, buildings, pads
			var wf = ( JArray ) J[ "footprints" ];
			double mf = 0; int fk = 0;
			for ( int i = 0; i < Math.Min( wf.Count, v.footprints.Count ); i ++ )
			{
				var f = v.footprints[ i ];
				mf = Math.Max( mf, Math.Max( Math.Abs( f.x - ( double ) wf[ i ][ "x" ] ), Math.Max( Math.Abs( f.z - ( double ) wf[ i ][ "z" ] ), Math.Abs( f.r - ( double ) wf[ i ][ "r" ] ) ) ) );
				if ( f.kind != ( string ) wf[ i ][ "kind" ] ) fk ++;
			}

			sb.AppendLine( $"footprints {v.footprints.Count} (want {wf.Count}) maxDiff={mf:E2} kind mismatches={fk}" );
			var wk = ( JArray ) J[ "checks" ];
			double mk = 0;
			for ( int i = 0; i < Math.Min( wk.Count, v.foundationChecks.Count ); i ++ )
			{
				var c = v.foundationChecks[ i ];
				mk = Math.Max( mk, Math.Max( Math.Abs( c.x - ( double ) wk[ i ][ "x" ] ), Math.Max( Math.Abs( c.y - ( double ) wk[ i ][ "y" ] ), Math.Abs( c.z - ( double ) wk[ i ][ "z" ] ) ) ) );
			}

			sb.AppendLine( $"foundation checks {v.foundationChecks.Count} (want {wk.Count}) maxDiff={mk:E2}" );
			var wbd = ( JArray ) J[ "buildings" ];
			double mbd = 0; int nm = 0;
			for ( int i = 0; i < Math.Min( wbd.Count, v.buildings.Count ); i ++ )
			{
				var b = v.buildings[ i ];
				mbd = Math.Max( mbd, Math.Max( Math.Abs( b.floorY - ( double ) wbd[ i ][ "floorY" ] ), Math.Abs( b.roofTop - ( double ) wbd[ i ][ "roofTop" ] ) ) );
				if ( b.name != ( string ) wbd[ i ][ "name" ] ) nm ++;
			}

			sb.AppendLine( $"buildings {v.buildings.Count} (want {wbd.Count}) floorY/roofTop maxDiff={mbd:E2} name mismatches={nm}" );
			var wp = ( JArray ) J[ "pads" ];
			double mp = 0;
			for ( int i = 0; i < Math.Min( wp.Count, T.pads.Count ); i ++ )
			{
				var p = T.pads[ i ];
				mp = Math.Max( mp, Math.Max( Math.Abs( p.x - ( double ) wp[ i ][ "x" ] ), Math.Max( Math.Abs( p.z - ( double ) wp[ i ][ "z" ] ), Math.Max( Math.Abs( p.radius - ( double ) wp[ i ][ "radius" ] ), Math.Abs( p.height - ( double ) wp[ i ][ "height" ] ) ) ) ) );
			}

			sb.AppendLine( $"pads {T.pads.Count} (want {wp.Count}) maxDiff={mp:E2}" );
			var wh = ReadF( dir + "/heights.f32" );
			double mh = wh.Length == T.heights.Length ? 0 : 1e9;
			for ( int i = 0; i < Math.Min( wh.Length, T.heights.Length ); i ++ ) mh = Math.Max( mh, Math.Abs( wh[ i ] - T.heights[ i ] ) );
			sb.AppendLine( $"terrain heights after flatten: n={T.heights.Length} (want {wh.Length}) maxDiff={mh:E2}" );

			var P = J[ "path" ];
			sb.AppendLine( $"path length {v.path.length:R} (want {( double ) P[ "length" ]:R})" );
			return sb.ToString();
		}
	}
}
