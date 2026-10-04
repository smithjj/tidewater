using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.World.Boat;
using UnityEditor;
using UnityEngine;

// Compares the C# boat port with the JS original:
//   node unity/tools/dump-boat-hull.mjs unity/Temp/oracle/boat      (HullLines hydrostatics)
//   node unity/tools/dump-boat.mjs unity/Temp/oracle/boat           (every geometry the model adds, per bucket, and the model's numbers)
//   unity/tools/run-oracle.sh BoatOracle
namespace Tidewater.EditorTools
{
	public static class BoatOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/boat" ) );

		[MenuItem( "Tidewater/Compare boat with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		static double[] Arr( JToken t ) => t.Select( v => ( double ) v ).ToArray();

		// worst absolute / relative difference between two number lists
		static string Cmp( string name, double[] a, double[] b )
		{
			if ( a.Length != b.Length ) return $"{name}: LENGTH {a.Length} vs JS {b.Length}\n";
			double maxAbs = 0, maxRel = 0; int worst = 0;
			for ( int i = 0; i < a.Length; i ++ )
			{
				double e = Math.Abs( a[ i ] - b[ i ] );
				if ( e > maxAbs ) { maxAbs = e; worst = i; }
				maxRel = Math.Max( maxRel, e / Math.Max( 1e-9, Math.Abs( b[ i ] ) ) );
			}

			return $"{name}: n {a.Length}, max abs {maxAbs:E2}, max rel {maxRel:E2}" + ( maxAbs > 0 ? $" (worst #{worst}: {a[ worst ]:R} vs JS {b[ worst ]:R})" : "" ) + "\n";
		}

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			if ( File.Exists( dir + "/hull.json" ) ) sb.Append( CompareHull( dir + "/hull.json" ) );
			if ( File.Exists( dir + "/boat.json" ) ) sb.Append( CompareAdds( dir + "/boat.json" ) );
			return sb.ToString();
		}

		// checksums of one prepared geometry, the same as dump-boat.mjs
		static double[] Sums( Tidewater.Engine.BufferGeometry g, out int nv, out int ni, out double indexHash )
		{
			nv = g.attributes[ "position" ].count; ni = g.index != null ? g.index.count : 0;
			var r = new System.Collections.Generic.List<double>();
			foreach ( var name in new[] { "position", "normal", "uv", "color", "aux" } )
			{
				double sum = 0, abs = 0;
				foreach ( float v in g.attributes[ name ].array ) { sum += v; abs += Math.Abs( v ); }
				r.Add( sum ); r.Add( abs );
			}

			indexHash = 0;
			if ( g.index != null ) for ( int i = 0; i < g.index.count; i ++ ) indexHash += ( double ) g.index.array[ i ] * ( ( i % 7 ) + 1 );
			return r.ToArray();
		}

		// every geometry the model adds to its GeoKit, in order, against the JS (the first `count` of them)
		static string CompareAdds( string path )
		{
			var j = JObject.Parse( File.ReadAllText( path ) );
			var js = ( JArray ) j[ "adds" ];
			var cs = new System.Collections.Generic.List<(string bucket, int nv, int ni, double[] sums, double ih)>();
			GeoKit.onAdd = ( bucket, g ) => { var s = Sums( g, out int nv, out int ni, out double ih ); cs.Add( ( bucket, nv, ni, s, ih ) ); };
			BoatModel model;
			try { model = new BoatModel(); }
			finally { GeoKit.onAdd = null; }

			var sb = new StringBuilder( $"== GeoKit adds (C# {cs.Count} so far, JS {js.Count}) ==\n" );
			int bad = 0; double worst = 0;
			for ( int i = 0; i < cs.Count && i < js.Count; i ++ )
			{
				var e = js[ i ]; var c = cs[ i ];
				string why = null;
				if ( ( string ) e[ "bucket" ] != c.bucket ) why = $"bucket {c.bucket} vs {e[ "bucket" ]}";
				else if ( ( int ) e[ "nv" ] != c.nv || ( int ) e[ "ni" ] != c.ni ) why = $"counts {c.nv}/{c.ni} vs {e[ "nv" ]}/{e[ "ni" ]}";
				else if ( Math.Abs( ( double ) e[ "indexHash" ] - c.ih ) > 0 ) why = $"index hash {c.ih} vs {e[ "indexHash" ]}";
				else
				{
					string[] names = { "position", "normal", "uv", "color", "aux" };
					for ( int k = 0; k < 5 && why == null; k ++ )
					{
						for ( int q = 0; q < 2; q ++ )
						{
							double a = c.sums[ k * 2 + q ], b = ( double ) e[ names[ k ] ][ q ];
							double rel = Math.Abs( a - b ) / Math.Max( 1e-6, Math.Abs( b ) );
							worst = Math.Max( worst, rel );
							if ( rel > 1e-5 && Math.Abs( a - b ) > 1e-4 ) { why = $"{names[ k ]}[{q}] {a:R} vs JS {b:R}"; break; }
						}
					}
				}

				if ( why != null ) { bad ++; sb.AppendLine( $"  add #{i} ({c.bucket}): {why}" ); }
			}

			sb.AppendLine( $"compared {Math.Min( cs.Count, js.Count )} adds: {bad} differ, worst relative checksum difference {worst:E2}" );

			// the merged buckets and the animated parts
			var merged = ( JObject ) j[ "merged" ];
			var mine = new System.Collections.Generic.Dictionary<string, Tidewater.Engine.BufferGeometry>( model.staticGeometry )
				{ [ "wheel" ] = model.wheelGeometry, [ "throttle" ] = model.throttleGeometry, [ "radar" ] = model.radarGeometry, [ "propeller" ] = model.propGeometry, [ "rudder" ] = model.rudderGeometry };
			foreach ( var kv in merged )
			{
				if ( ! mine.TryGetValue( kv.Key, out var g ) ) { sb.AppendLine( $"  merged {kv.Key}: missing in C#" ); continue; }
				var s2 = Sums( g, out int nv, out int ni, out double ih );
				string why = null;
				if ( ( int ) kv.Value[ "nv" ] != nv || ( int ) kv.Value[ "ni" ] != ni ) why = $"counts {nv}/{ni} vs {kv.Value[ "nv" ]}/{kv.Value[ "ni" ]}";
				else if ( ( double ) kv.Value[ "indexHash" ] != ih ) why = $"index hash {ih} vs {kv.Value[ "indexHash" ]}";
				else
				{
					string[] names = { "position", "normal", "uv", "color", "aux" };
					for ( int k = 0; k < 5 && why == null; k ++ )
						for ( int q = 0; q < 2; q ++ )
						{
							double a = s2[ k * 2 + q ], b = ( double ) kv.Value[ names[ k ] ][ q ];
							if ( Math.Abs( a - b ) / Math.Max( 1e-6, Math.Abs( b ) ) > 1e-5 && Math.Abs( a - b ) > 1e-4 ) { why = $"{names[ k ]}[{q}] {a:R} vs JS {b:R}"; break; }
						}
				}

				sb.AppendLine( $"  merged {kv.Key}: {nv} vertices, {ni / 3} triangles: " + ( why ?? "match" ) );
			}

			sb.Append( CompareModel( ( JObject ) j[ "model" ], model ) );
			return sb.ToString();
		}

		static string CompareModel( JObject m, BoatModel b )
		{
			var sb = new StringBuilder( "== BoatModel numbers ==\n" );
			var d = ( JObject ) m[ "dimensions" ]; var dm = b.dimensions;
			sb.Append( Cmp( "dimensions", new[] { dm.length, dm.beam, dm.draft, dm.freeboard, dm.freeboardBow, dm.deckHeight, dm.hullDepth, dm.waterlineLength, dm.waterlineBeam, dm.houseRoofHeight },
				new[] { "length", "beam", "draft", "freeboard", "freeboardBow", "deckHeight", "hullDepth", "waterlineLength", "waterlineBeam", "houseRoofHeight" }.Select( k => ( double ) d[ k ] ).ToArray() ) );
			var h = ( JObject ) m[ "hydro" ]; var hb = b.hydro;
			sb.Append( Cmp( "hydro", new[] { hb.waterplaneArea, hb.canoeVolume, hb.keelVolume, hb.displacedVolume, hb.suggestedMass, hb.massWithKeel, hb.centerOfFlotationZ, hb.waterlineStart, hb.waterlineEnd, hb.seawaterDensity, hb.metacentricRadius },
				new[] { "waterplaneArea", "canoeVolume", "keelVolume", "displacedVolume", "suggestedMass", "massWithKeel", "centerOfFlotationZ", "waterlineStart", "waterlineEnd", "seawaterDensity", "metacentricRadius" }.Select( k => ( double ) h[ k ] ).ToArray() ) );
			sb.Append( Cmp( "cob / com / inertia", new[] { hb.centerOfBuoyancy.x, hb.centerOfBuoyancy.y, hb.centerOfBuoyancy.z, hb.centerOfMass.x, hb.centerOfMass.y, hb.centerOfMass.z, hb.inertia.x, hb.inertia.y, hb.inertia.z },
				Arr( h[ "centerOfBuoyancy" ] ).Concat( Arr( h[ "centerOfMass" ] ) ).Concat( Arr( h[ "inertia" ] ) ).ToArray() ) );
			Func<Tidewater.Engine.Vector3, double[]> v3 = v => new[] { v.x, v.y, v.z };
			sb.Append( Cmp( "helmEye / helmPoint / boardPoint", v3( b.helmEye ).Concat( new[] { b.helmPointX, b.helmPointZ } ).Concat( v3( b.boardPoint ) ).ToArray(),
				Arr( m[ "helmEye" ] ).Concat( Arr( m[ "helmPoint" ] ) ).Concat( Arr( m[ "boardPoint" ] ) ).ToArray() ) );
			sb.Append( Cmp( "exitPoints", b.exitPoints.SelectMany( v3 ).ToArray(), ( ( JArray ) m[ "exitPoints" ] ).SelectMany( Arr ).ToArray() ) );
			sb.Append( Cmp( "bowSprayPoints", b.bowSprayPoints.SelectMany( v3 ).ToArray(), ( ( JArray ) m[ "bowSprayPoints" ] ).SelectMany( Arr ).ToArray() ) );
			sb.Append( Cmp( "hullSamples", b.hullSamples.SelectMany( q => new[] { q.position.x, q.position.y, q.position.z, q.area, q.depth, q.bottomY } ).ToArray(), ( ( JArray ) m[ "hullSamples" ] ).SelectMany( Arr ).ToArray() ) );
			sb.Append( Cmp( "colliders", b.colliders.SelectMany( c => v3( c.center ).Concat( v3( c.half ) ).Concat( new[] { c.walkable ? 1.0 : 0, c.solid ? 1.0 : 0 } ) ).ToArray(),
				( ( JArray ) m[ "colliders" ] ).SelectMany( c => Arr( c[ "center" ] ).Concat( Arr( c[ "half" ] ) ).Concat( new[] { ( bool ) c[ "walkable" ] ? 1.0 : 0, ( bool ) c[ "solid" ] ? 1.0 : 0 } ) ).ToArray() ) );
			sb.AppendLine( "colliders tags: " + string.Join( ",", b.colliders.Select( c => c.tag ) ) + ( string.Join( ",", b.colliders.Select( c => c.tag ) ) == string.Join( ",", ( ( JArray ) m[ "colliders" ] ).Select( c => ( string ) c[ "tag" ] ) ) ? " (match)" : " DIFFER" ) );
			sb.AppendLine( $"triangles: C# {b.triangleCount} vs JS {( double ) m[ "triangles" ]}" );
			var pv = ( JObject ) m[ "pivots" ];
			sb.Append( Cmp( "pivots", v3( b.wheelPos ).Concat( v3( b.throttlePos ) ).Concat( v3( b.radarPos ) ).Concat( v3( b.propPos ) ).Concat( v3( b.rudderPos ) ).ToArray(),
				Arr( pv[ "wheel" ] ).Concat( Arr( pv[ "throttle" ] ) ).Concat( Arr( pv[ "radar" ] ) ).Concat( Arr( pv[ "prop" ] ) ).Concat( Arr( pv[ "rudder" ] ) ).ToArray() ) );
			var q0 = b.wheelPivotRotation;
			sb.Append( Cmp( "wheel pivot rotation", new[] { q0.x, q0.y, q0.z, q0.w }, Arr( pv[ "wheelQ" ] ) ) );
			return sb.ToString();
		}

		static string CompareHull( string path )
		{
			var j = JObject.Parse( File.ReadAllText( path ) );
			var sb = new StringBuilder( "== HullLines ==\n" );
			var L = new HullLines();
			sb.Append( Cmp( "wlStart,wlEnd,area,volume,cofZ", new[] { L.wlStart, L.wlEnd, L.waterplaneArea, L.canoeVolume, L.centerOfFlotationZ },
				new[] { ( double ) j[ "wlStart" ], ( double ) j[ "wlEnd" ], ( double ) j[ "waterplaneArea" ], ( double ) j[ "canoeVolume" ], ( double ) j[ "cofZ" ] } ) );
			sb.Append( Cmp( "cob", new[] { L.centerOfBuoyancy.x, L.centerOfBuoyancy.y, L.centerOfBuoyancy.z }, Arr( j[ "cob" ] ) ) );
			var st = j[ "stem" ];
			sb.Append( Cmp( "stem", new[] { L.stem.k, L.stem.R, L.stem.yF, L.stem.zW, L.stem.yc, L.stem.zc, L.stem.yT },
				new[] { ( double ) st[ "k" ], ( double ) st[ "R" ], ( double ) st[ "yF" ], ( double ) st[ "zW" ], ( double ) st[ "yc" ], ( double ) st[ "zc" ], ( double ) st[ "yT" ] } ) );
			sb.Append( Cmp( "stationParams(60)", L.stationParams( 60 ).ToArray(), Arr( j[ "stationParams" ] ) ) );
			sb.Append( Cmp( "section(0.5, 1)", L.sectionPoints( 0.5, 1 ), Arr( j[ "section05" ] ) ) );
			sb.Append( Cmp( "section(1, 2)", L.sectionPoints( 1, 2 ), Arr( j[ "section1" ] ) ) );
			sb.Append( Cmp( "halfBeamAt", new[] { -3, -2, -1, 0, 1, 2, 3, 4 }.Select( z => L.halfBeamAt( z ) ).ToArray(), Arr( j[ "beam" ] ) ) );
			sb.Append( Cmp( "draftAt", new[] { -3.5, -2, 0, 2, 3.5 }.Select( z => L.draftAt( z ) ).ToArray(), Arr( j[ "draft" ] ) ) );
			sb.Append( Cmp( "bottomAt", new[] { L.bottomAt( 0.3, 0 ), L.bottomAt( 0.7, 1.5 ), L.bottomAt( 0, -3 ) }, Arr( j[ "bottomAt" ] ) ) );
			sb.Append( Cmp( "hullXAt", new[] { L.hullXAt( 0, 0.5 ), L.hullXAt( -2, 0.2 ), L.hullXAt( 3, 0.8 ) }, Arr( j[ "hullXAt" ] ) ) );
			var s = L.buildHullSamples( 8 );
			var js = ( JArray ) j[ "samples" ];
			sb.AppendLine( $"hull samples: C# {s.Count}, JS {js.Count}" );
			var flat = s.SelectMany( q => new[] { q.position.x, q.position.y, q.position.z, q.area, q.depth, q.bottomY } ).ToArray();
			var jflat = js.SelectMany( q => new[] { ( double ) q[ "p" ][ 0 ], ( double ) q[ "p" ][ 1 ], ( double ) q[ "p" ][ 2 ], ( double ) q[ "area" ], ( double ) q[ "depth" ], ( double ) q[ "bottomY" ] } ).ToArray();
			sb.Append( Cmp( "hull samples", flat, jflat ) );
			return sb.ToString();
		}
	}
}
