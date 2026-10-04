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

// Compares the C# pier port with the JS original (unity/tools/dump-pier.mjs -> Temp/oracle/pier):
// per-material batch vertex / index counts and attribute differences, colliders, lights, pier info.
namespace Tidewater.EditorTools
{
	public static class PierOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/pier" ) );

		[MenuItem( "Tidewater/Compare pier with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare( DefaultDir ) );

		static float[] ReadF( string p ) { var b = File.ReadAllBytes( p ); var a = new float[ b.Length / 4 ]; Buffer.BlockCopy( b, 0, a, 0, b.Length ); return a; }
		static uint[] ReadU( string p ) { var b = File.ReadAllBytes( p ); var a = new uint[ b.Length / 4 ]; Buffer.BlockCopy( b, 0, a, 0, b.Length ); return a; }

		static void CmpAttr( StringBuilder sb, string name, List<double> got, string path )
		{
			var want = ReadF( path );
			if ( want.Length != got.Count ) { sb.AppendLine( $"  {name}: LENGTH {got.Count} != {want.Length}" ); return; }
			double max = 0; int at = -1;
			for ( int i = 0; i < want.Length; i ++ )
			{
				double d = Math.Abs( ( float ) got[ i ] - want[ i ] );
				if ( d > max ) { max = d; at = i; }
			}

			sb.AppendLine( $"  {name}: n={want.Length} maxDiff={max:E2}" + ( max > 1e-4 ? $" at {at} (got {got[ at ]:R} want {want[ at ]:R})" : "" ) );
		}

		static void CmpBatch( StringBuilder sb, string name, Batch b, string dir, JToken meta )
		{
			sb.AppendLine( $"{name}: vertices {b.vcount} (want {meta[ "vertices" ]}), indices {b.idx.Count} (want {meta[ "indices" ]})" );
			CmpAttr( sb, "pos", b.pos, $"{dir}/{name}.pos.f32" );
			CmpAttr( sb, "nrm", b.nrm, $"{dir}/{name}.nrm.f32" );
			CmpAttr( sb, "uv", b.uv, $"{dir}/{name}.uv.f32" );
			CmpAttr( sb, "tint", b.tint, $"{dir}/{name}.tint.f32" );
			CmpAttr( sb, "data", b.data, $"{dir}/{name}.data.f32" );
			var wi = ReadU( $"{dir}/{name}.idx.u32" );
			int bad = 0; for ( int i = 0; i < wi.Length && i < b.idx.Count; i ++ ) if ( wi[ i ] != ( uint ) b.idx[ i ] ) bad ++;
			sb.AppendLine( $"  idx: mismatches={bad}" );
		}

		static double V( JToken t, int i ) => ( double ) t[ i ];
		static double Dv( Vector3 v, JToken t ) => Math.Max( Math.Abs( v.x - V( t, 0 ) ), Math.Max( Math.Abs( v.y - V( t, 1 ) ), Math.Abs( v.z - V( t, 2 ) ) ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var J = JObject.Parse( File.ReadAllText( dir + "/pier.json" ) );
			var T = new TerrainData( ( int ) J[ "seed" ] );
			var C = new Colliders();
			var rand = new Rand( new Mulberry32( 90210 ) );
			var B = new Builder(); var signB = new Builder(); var hungB = new List<Builder>();
			var inst = new InstancedProps( B );
			var lights = new List<LightSource>();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var info = Pier.buildPier( B, T, C, rand, lights, inst, signB, () => { var b = new Builder(); hungB.Add( b ); return b; } );
			var checks = new List<FoundationCheck>();
			var foot = info.stepFoot;
			var path = Boardwalk.buildBoardwalk( new BuildCtx { B = B, terrain = T, colliders = C, rand = rand, lights = lights, inst = inst, checks = checks },
				new[] { new[] { foot.x, foot.z + 0.05 }, new[] { 54.6, - 72.0 }, new[] { 52.4, - 82.0 }, new[] { 48.4, - 92.0 }, new[] { 44.8, - 100.5 }, new[] { 42.6, - 107.2 } },
				new BoardwalkOpts { width = 1.8, startY = foot.y + 0.24, lightEvery = 70 } );
			var JP = J[ "path" ]; var jd = ( JArray ) JP[ "deck" ];
			double md = 0; for ( int i = 0; i < Math.Min( jd.Count, path.deck.Length ); i ++ ) md = Math.Max( md, Math.Abs( path.deck[ i ] - ( double ) jd[ i ] ) );
			sb.AppendLine( $"boardwalk: length {path.length:R} (want {( double ) JP[ "length" ]:R}), samples {path.deck.Length} (want {jd.Count}), deck maxDiff={md:E2}; checks {checks.Count} (want {J[ "checks" ].Count()})" );
			sb.AppendLine( $"C# pier built in {sw.ElapsedMilliseconds} ms" );

			var meta = J[ "batches" ];
			var all = new List<KeyValuePair<string, Builder>> { new KeyValuePair<string, Builder>( "main", B ), new KeyValuePair<string, Builder>( "sign", signB ) };
			for ( int i = 0; i < hungB.Count; i ++ ) all.Add( new KeyValuePair<string, Builder>( "hung" + i, hungB[ i ] ) );
			int seen = 0;
			foreach ( var kv in all )
				foreach ( var bk in kv.Value.batches )
				{
					string n = kv.Key + "_" + bk.Key;
					if ( meta[ n ] == null ) { sb.AppendLine( $"{n}: EXTRA batch in C#" ); continue; }
					seen ++;
					CmpBatch( sb, n, bk.Value, dir, meta[ n ] );
				}

			sb.AppendLine( $"batches matched by name: {seen} of {( ( JObject ) meta ).Count}" );

			// colliders
			var wb = ( JArray ) J[ "boxes" ]; var wc = ( JArray ) J[ "cylinders" ];
			sb.AppendLine( $"boxes {C.boxes.Count} (want {wb.Count}), cylinders {C.cylinders.Count} (want {wc.Count})" );
			double mb = 0; int badTag = 0;
			for ( int i = 0; i < Math.Min( wb.Count, C.boxes.Count ); i ++ )
			{
				var g = C.boxes[ i ]; var w = wb[ i ];
				mb = Math.Max( mb, Math.Max( Dv( g.center, w[ "c" ] ), Math.Max( Dv( g.half, w[ "h" ] ), Math.Abs( g.rotY - ( double ) w[ "rotY" ] ) ) ) );
				if ( g.tag != ( string ) w[ "tag" ] || g.walkable != ( bool ) w[ "walkable" ] || g.solid != ( bool ) w[ "solid" ] ) badTag ++;
			}

			double mc = 0; int badCt = 0;
			for ( int i = 0; i < Math.Min( wc.Count, C.cylinders.Count ); i ++ )
			{
				var g = C.cylinders[ i ]; var w = wc[ i ];
				mc = Math.Max( mc, Math.Max( Math.Abs( g.x - ( double ) w[ "x" ] ), Math.Max( Math.Abs( g.z - ( double ) w[ "z" ] ), Math.Max( Math.Abs( g.radius - ( double ) w[ "r" ] ), Math.Max( Math.Abs( g.yMin - ( double ) w[ "y0" ] ), Math.Abs( g.yMax - ( double ) w[ "y1" ] ) ) ) ) ) );
				if ( g.tag != ( string ) w[ "tag" ] ) badCt ++;
			}

			sb.AppendLine( $"  box maxDiff={mb:E2} tag/flag mismatches={badTag}; cyl maxDiff={mc:E2} tag mismatches={badCt}" );

			// lights and info
			var wl = ( JArray ) J[ "lights" ];
			double ml = 0; int badL = 0;
			for ( int i = 0; i < Math.Min( wl.Count, lights.Count ); i ++ )
			{
				ml = Math.Max( ml, Dv( lights[ i ].position, wl[ i ][ "p" ] ) );
				if ( lights[ i ].kind != ( string ) wl[ i ][ "kind" ] || lights[ i ].intensity != ( double ) wl[ i ][ "i" ] ) badL ++;
			}

			sb.AppendLine( $"lights {lights.Count} (want {wl.Count}) maxDiff={ml:E2} mismatches={badL}" );
			var I = J[ "info" ];
			sb.AppendLine( $"stepFoot diff={Dv( info.stepFoot, I[ "stepFoot" ] ):E2} ladder diff={Dv( info.ladder, I[ "ladder" ] ):E2} signPivot diff={Dv( info.signPivot, I[ "signPivot" ] ):E2}" );
			sb.AppendLine( $"lamps {info.lamps.Count} (want {I[ "lamps" ].Count()}), bollards {info.bollards.Count} (want {I[ "bollards" ].Count()}), hung {info.hung.Count} (want {I[ "hung" ].Count()})" );
			double mh = 0;
			for ( int i = 0; i < Math.Min( info.hung.Count, I[ "hung" ].Count() ); i ++ ) mh = Math.Max( mh, Math.Max( Dv( info.hung[ i ].pivot, I[ "hung" ][ i ][ "pivot" ] ), Dv( info.hung[ i ].rest, I[ "hung" ][ i ][ "rest" ] ) ) );
			sb.AppendLine( $"  hung pivot/rest maxDiff={mh:E2}" );
			// the next random number: proves the whole call sequence consumed the same count
			sb.AppendLine( $"next rand {rand.next():R} (want {( double ) J[ "nextRand" ]:R}); instanced props {inst.count} (want {J[ "instCount" ]})" );
			return sb.ToString();
		}
	}
}
