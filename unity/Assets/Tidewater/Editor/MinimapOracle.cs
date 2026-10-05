using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the Minimap (Runtime/Game/Minimap.cs) with src/game/Minimap.js (unity/tools/dump-minimap.mjs -> Temp/oracle/minimap), which ran for real against a stub DOM:
//   1. the bake: the 640 x 640 RGBA the JS hands to putImageData (colours, hill shading, coastline), byte for byte;
//   2. the canvas calls after it (the village pads and the pier: the rectangles and their styles);
//   3. a scripted session of 1,100 frames (walking, swimming, sailing, aboard, the large map opened and closed, the camera looking straight down, frame
//      times from 8 ms to 250 ms, the fish finder, traps set, an anchor down, markers highlighted): each frame the transform of the image, the arrow in the
//      middle, every marker's place, edge and highlight, the N, the fish rings and the schools they mark.
//   node unity/tools/dump-minimap.mjs unity/Temp/oracle/minimap && unity/tools/run-oracle-bg.sh MinimapOracle minimap
// `negative`: the C# side is made wrong (a pixel of the image, the camera 1 mm out), which must be reported as a mismatch.
namespace Tidewater.EditorTools
{
	public static class MinimapOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/minimap" ) );

		[MenuItem( "Tidewater/Compare minimap with JS oracle" )]
		static void Menu() => UnityEngine.Debug.Log( Compare() );

		sealed class Tally
		{
			public int compared, bad; public double maxAbs; public readonly List<string> first = new List<string>();
			public void Num( string at, double a, double b, double tol = 1e-9 )
			{
				compared ++;
				double e = Math.Abs( a - b );
				if ( double.IsNaN( a ) && double.IsNaN( b ) ) return;
				if ( e > maxAbs || double.IsNaN( e ) ) maxAbs = double.IsNaN( e ) ? double.PositiveInfinity : e;
				if ( ! ( e <= tol * ( 1 + Math.Abs( b ) ) ) ) { bad ++; if ( first.Count < 4 ) first.Add( $"{at}: {a:R} vs JS {b:R}" ); }
			}
			public void Eq( string at, object a, object b )
			{
				compared ++;
				if ( ! Equals( a, b ) ) { bad ++; if ( first.Count < 4 ) first.Add( $"{at}: {a} vs JS {b}" ); }
			}
			public string Line( string name ) => $"  {name}: {compared} compared, {bad} differ (max abs {maxAbs:E2})" + ( bad > 0 ? "  <-- MISMATCH" : "" ) + "\n" + string.Concat( first.Select( f => "      " + f + "\n" ) );
		}

		static readonly Regex NUM = new Regex( @"-?\d+(?:\.\d+)?(?:e[+-]?\d+)?", RegexOptions.IgnoreCase );
		static double[] Nums( string s ) => NUM.Matches( s ).Cast<Match>().Select( m => double.Parse( m.Value, System.Globalization.CultureInfo.InvariantCulture ) ).ToArray();

		public static string Compare( string dir = null, bool negative = false )
		{
			dir = dir ?? DefaultDir;
			var sb = new StringBuilder();
			var J = JObject.Parse( File.ReadAllText( Path.Combine( dir, "minimap.json" ) ) );
			var T = new TerrainData( 7 );
			foreach ( var p in ( JArray ) J[ "pads" ] ) T.pads.Add( new Pad { x = ( double ) p[ "x" ], z = ( double ) p[ "z" ], radius = ( double ) p[ "radius" ], height = ( double ) p[ "height" ] } );

			// 1 + 2: the bake and the canvas calls
			var map = new Minimap( ( int ) J[ "trapLimit" ] ) { keepPreCanvas = true };
			int steps = 0;
			while ( ! map.done ) { map.BakeStep( T ); steps ++; }
			sb.AppendLine( $"bake: {steps} steps (JS: done at frame {( int ) J[ "bakedAtFrame" ] + 1})" );
			var js = File.ReadAllBytes( Path.Combine( dir, "bake.rgba" ) );
			var cs = map.preCanvas;
			if ( negative ) cs[ 1000 * 4 + 2 ] ^= 0x40;
			int diff = 0, off1 = 0, offMore = 0, maxD = 0; var firstBad = new List<string>();
			for ( int i = 0; i < js.Length; i ++ )
			{
				int d = Math.Abs( cs[ i ] - js[ i ] );
				if ( d == 0 ) continue;
				diff ++; if ( d == 1 ) off1 ++; else offMore ++;
				maxD = Math.Max( maxD, d );
				if ( d > 1 && firstBad.Count < 4 ) firstBad.Add( $"pixel ({( i / 4 ) % Minimap.N}, {( i / 4 ) / Minimap.N}) channel {i % 4}: {cs[ i ]} vs JS {js[ i ]}" );
			}

			sb.AppendLine( $"  image: {js.Length} bytes, {js.Length - diff} identical, {off1} off by one (a last-bit difference in a height), {offMore} off by more (max {maxD})" + ( offMore > 0 || cs.Length != js.Length ? "  <-- MISMATCH" : "" ) );
			foreach ( var f in firstBad ) sb.AppendLine( "      " + f );

			var calls = new Tally(); var jc = ( JArray ) J[ "calls" ];
			calls.Eq( "number of calls", map.Calls.Count, jc.Count );
			for ( int i = 0; i < Math.Min( map.Calls.Count, jc.Count ); i ++ )
			{
				var c = map.Calls[ i ]; var j = ( JArray ) jc[ i ];
				calls.Eq( $"call {i} method", c.method, ( string ) j[ 0 ] );
				calls.Eq( $"call {i} style", c.style, ( string ) j[ 1 ] );
				double[] a = c.method == "fillRect" ? new[] { c.x, c.y, c.w, c.h } : new[] { c.lineWidth, c.x, c.y, c.w, c.h };
				for ( int k = 0; k < a.Length; k ++ ) calls.Num( $"call {i} arg {k}", a[ k ], ( double ) j[ 2 + k ] );
			}

			sb.Append( calls.Line( "canvas calls" ) );

			// 3: the session
			var run = new Minimap( ( int ) J[ "trapLimit" ] );
			run.MarkBaked();
			var groups = new List<FishGroup>();
			foreach ( var g in ( JArray ) J[ "groups" ] )
			{
				var sp = new Tidewater.World.Fish.Behaviour { name = ( string ) g[ "sp" ][ "name" ], model = ( string ) g[ "sp" ][ "model" ], mode = ( string ) g[ "sp" ][ "mode" ] };
				var fg = new FishGroup( sp, ( int ) g[ "count" ], 0, new FishZone { x = 0, z = 0 }, new Tidewater.Util.Mulberry32( 1 ) );
				fg.center = new Tidewater.Engine.Vector3( ( double ) g[ "center" ][ "x" ], ( double ) g[ "center" ][ "y" ], ( double ) g[ "center" ][ "z" ] );
				fg.radius = ( double ) g[ "radius" ];
				groups.Add( fg );
			}

			var frames = ( JArray ) J[ "frames" ]; var expect = ( JArray ) J[ "expect" ];
			var view = new Tally(); var markers = new Tally(); var extras = new Tally(); var rings = new Tally();
			for ( int f = 0; f < frames.Count; f ++ )
			{
				var fr = frames[ f ]; var ex = expect[ f ];
				var v = new Minimap.View
				{
					dt = ( double ) fr[ "dt" ], x = ( double ) fr[ "x" ] + ( negative ? 0.001 : 0 ), z = ( double ) fr[ "z" ], e8 = ( double ) fr[ "e8" ], e10 = ( double ) fr[ "e10" ],
					mode = ( string ) fr[ "mode" ], size = ( double ) fr[ "size" ], finder = ( bool ) fr[ "finder" ], groups = groups,
				};
				if ( fr[ "boat" ].Type != JTokenType.Null ) { v.boatX = ( double ) fr[ "boat" ][ "x" ]; v.boatZ = ( double ) fr[ "boat" ][ "z" ]; }
				if ( fr[ "anchor" ].Type != JTokenType.Null ) { v.anchorDown = ( bool ) fr[ "anchor" ][ "down" ]; v.anchorX = ( double ) fr[ "anchor" ][ "x" ]; v.anchorZ = ( double ) fr[ "anchor" ][ "z" ]; }
				// sets: an entry per slot, null where the JS array had a hole or a null
				var sets = new List<TrapSet>();
				foreach ( var s in ( JArray ) fr[ "sets" ] ) sets.Add( s.Type == JTokenType.Null || s.Type == JTokenType.Undefined ? null : new TrapSet { x = ( double ) s[ "x" ], z = ( double ) s[ "z" ] } );
				v.sets = sets;
				foreach ( var k in ( JArray ) fr[ "keys" ] ) { if ( ( string ) k == "map" ) run.toggleBig( "N" ); else run.toggleBig( "", false ); }
				if ( fr[ "hot" ].Type != JTokenType.Null ) run.highlight( ( ( JArray ) fr[ "hot" ] ).Select( t => ( string ) t ) );
				run.Update( v, T );

				string at = $"frame {f}";
				view.Num( at + " radiusM", run.radiusM, ( double ) ex[ "radiusM" ] );
				view.Eq( at + " big", run.big, ( bool ) ex[ "big" ] );
				view.Eq( at + " label", run.label, ( string ) ex[ "label" ] );
				if ( ex[ "canvas" ].Type != JTokenType.Null )
				{
					var n = Nums( ( string ) ex[ "canvas" ] ); // translate( R, R ) rotate( rot ) scale( s ) translate( ax, ay )
					view.Num( at + " canvas R", run.R, n[ 0 ] ); view.Num( at + " canvas R", run.R, n[ 1 ] ); view.Num( at + " canvas rotation", run.rot, n[ 2 ] );
					view.Num( at + " canvas scale", run.scale, n[ 3 ] ); view.Num( at + " canvas x", run.ax, n[ 4 ] ); view.Num( at + " canvas y", run.ay, n[ 5 ] );
				}
				else view.Eq( at + " not sized", run.sized, false );

				var me = ex[ "me" ].Type == JTokenType.Null ? null : ( string ) ex[ "me" ];
				if ( me != null ) { view.Eq( at + " arrow turns", run.meTurns, me != "" ); if ( me != "" ) view.Num( at + " arrow heading", run.meHeading, Nums( me )[ 0 ] ); }

				if ( ex[ "north" ].Type != JTokenType.Null ) { var n = Nums( ( string ) ex[ "north" ] ); extras.Num( at + " N x", run.north.x, n[ 0 ] ); extras.Num( at + " N y", run.north.y, n[ 1 ] ); }
				foreach ( var m in ( JArray ) ex[ "marks" ] )
				{
					var mk = run.markers.First( q => q.id == ( string ) m[ 0 ] );
					string id = at + " " + mk.id;
					if ( m[ 1 ].Type != JTokenType.Null ) markers.Eq( id + " shown", mk.visible, ( string ) m[ 1 ] == "" );
					if ( m[ 2 ].Type != JTokenType.Null ) { var n = Nums( ( string ) m[ 2 ] ); markers.Num( id + " x", mk.x, n[ 0 ] ); markers.Num( id + " y", mk.y, n[ 1 ] ); }
					else markers.Eq( id + " never placed", mk.placed, false );
					markers.Eq( id + " edge", mk.edge, ( bool ) m[ 3 ] ); markers.Eq( id + " hot", mk.hot, ( bool ) m[ 4 ] );
					if ( m[ 5 ].Type != JTokenType.Null ) markers.Num( id + " arrow", mk.arrow, Nums( ( string ) m[ 5 ] )[ 0 ] ); else markers.Eq( id + " no arrow yet", double.IsNaN( mk.arrow ), true );
				}

				var jf = ( JArray ) ex[ "fish" ];
				for ( int i = 0; i < 2; i ++ )
				{
					rings.Eq( $"{at} ring {i} on", run.fish[ i ].visible, ( bool ) jf[ i ][ 0 ] );
					if ( jf[ i ][ 1 ].Type != JTokenType.Null ) { var n = Nums( ( string ) jf[ i ][ 1 ] ); rings.Num( $"{at} ring {i} x", run.fish[ i ].x, n[ 0 ] ); rings.Num( $"{at} ring {i} y", run.fish[ i ].y, n[ 1 ] ); }
				}

				var pts = ( JArray ) ex[ "pts" ];
				rings.Eq( at + " schools marked", run.fishPts.Count, pts.Count );
				for ( int i = 0; i < Math.Min( pts.Count, run.fishPts.Count ); i ++ ) { rings.Num( $"{at} school {i} x", run.fishPts[ i ].x, ( double ) pts[ i ][ 0 ] ); rings.Num( $"{at} school {i} z", run.fishPts[ i ].z, ( double ) pts[ i ][ 1 ] ); }
			}

			sb.AppendLine( $"session: {frames.Count} frames" );
			sb.Append( view.Line( "window (radius, image transform, arrow, label)" ) ).Append( markers.Line( "markers (place, edge, highlight, arrow)" ) ).Append( extras.Line( "N on the rim" ) ).Append( rings.Line( "fish finder rings and the schools they mark" ) );
			return sb.ToString();
		}
	}
}
