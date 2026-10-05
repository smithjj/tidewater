using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using UnityEngine;

// Compares the stall meshes Unity builds (StallKit.LoadMesh) with the JS kit builder's, vertex by vertex: the Unity mesh is mapped back to the JS frame (z
// mirrored, the winding un-flipped) and its 14 floats per vertex ( position, normal, uv, tint, layer, uv2 ) and its indices are summed with the weights
// dump-stalls.mjs uses; the counts, the layer histogram and the bounds are compared too. The tangent frame (a Unity-side construction) is checked against its
// definition: unit, orthogonal to the normal, along +u, the bitangent along -v. A perturbed vertex is the negative control (the sums must then differ).
//   node unity/tools/dump-stalls.mjs unity/Temp/oracle/stalls
//   unity/tools/ev.sh 'return Tidewater.EditorTools.StallKitOracle.Compare();'
namespace Tidewater.EditorTools
{
	public static class StallKitOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/stalls" ) );

		public static string Compare( string dir = null )
		{
			dir = dir ?? DefaultDir;
			var js = JObject.Parse( File.ReadAllText( Path.Combine( dir, "stalls.json" ) ) );
			var sb = new StringBuilder();
			bool ok = true;
			foreach ( var name in new[] { "stand", "chandlery" } )
			{
				var j = js[ name ];
				var mesh = StallKit.LoadMesh( name );
				if ( mesh == null ) { sb.AppendLine( name + ": NO MESH" ); ok = false; continue; }
				var pos = mesh.vertices; var nrm = mesh.normals; var uv = mesh.uv; var uv2 = mesh.uv2; var col = mesh.colors; var tan = mesh.tangents;
				var tri = mesh.triangles;
				int nv = pos.Length;
				double Sum( bool perturb )
				{
					double s = 0; int i = 0;
					for ( int v = 0; v < nv; v ++ )
					{
						var p = pos[ v ]; if ( perturb && v == nv / 2 ) p.x += 1e-4f;
						double[] f = { p.x, p.y, - p.z, nrm[ v ].x, nrm[ v ].y, - nrm[ v ].z, uv[ v ].x, uv[ v ].y, col[ v ].r, col[ v ].g, col[ v ].b, col[ v ].a, uv2[ v ].x, uv2[ v ].y };
						foreach ( var x in f ) { s += x * ( 1 + ( i % 7 ) * 0.125 ); i ++; }
					}

					return s;
				}

				double SumIdx()
				{
					double s = 0;
					for ( int i = 0; i < tri.Length; i += 3 )
					{
						// the file's order: ( a, b, c ); Unity's: ( a, c, b )
						s += tri[ i ] * ( 1 + ( i % 7 ) * 0.125 ) + tri[ i + 2 ] * ( 1 + ( ( i + 1 ) % 7 ) * 0.125 ) + tri[ i + 1 ] * ( 1 + ( ( i + 2 ) % 7 ) * 0.125 );
					}

					return s;
				}

				double a = Sum( false ), b = ( double ) j[ "sumVerts" ], ai = SumIdx(), bi = ( double ) j[ "sumIdx" ], neg = Sum( true );
				var bmin = j[ "min" ]; var bmax = j[ "max" ];
				var bb = mesh.bounds;
				double bd = Math.Max( Math.Max( Math.Abs( bb.min.x - ( double ) bmin[ 0 ] ), Math.Abs( bb.min.y - ( double ) bmin[ 1 ] ) ), Math.Max( Math.Abs( - bb.max.z - ( double ) bmin[ 2 ] ), Math.Max( Math.Abs( bb.max.x - ( double ) bmax[ 0 ] ), Math.Max( Math.Abs( bb.max.y - ( double ) bmax[ 1 ] ), Math.Abs( - bb.min.z - ( double ) bmax[ 2 ] ) ) ) ) );
				// the layer histogram
				var hist = new System.Collections.Generic.SortedDictionary<int, int>();
				for ( int v = 0; v < nv; v ++ ) { int l = Mathf.RoundToInt( col[ v ].a ); hist[ l ] = hist.TryGetValue( l, out var c ) ? c + 1 : 1; }
				bool histOk = hist.Count == ( ( JObject ) j[ "layers" ] ).Count;
				foreach ( var kv in hist ) if ( ( int ) j[ "layers" ][ kv.Key.ToString() ] != kv.Value ) histOk = false;

				// the tangent frame
				int bad = 0, badFrame = 0, checkedN = 0; var badKinds = new System.Collections.Generic.SortedDictionary<string, int>();
				var seen = new bool[ nv ];
				for ( int i = 0; i < tri.Length; i += 3 )
				{
					int p0 = tri[ i ], p1 = tri[ i + 1 ], p2 = tri[ i + 2 ];
					Vector3 e1 = pos[ p1 ] - pos[ p0 ], e2 = pos[ p2 ] - pos[ p0 ];
					Vector2 d1 = uv[ p1 ] - uv[ p0 ], d2 = uv[ p2 ] - uv[ p0 ];
					float det = d1.x * d2.y - d2.x * d1.y;
					if ( Mathf.Abs( det ) < 1e-8f || Mathf.RoundToInt( col[ p0 ].a ) < 0 ) continue;
					for ( int k = 0; k < 3; k ++ )
					{
						int v = tri[ i + k ];
						if ( seen[ v ] ) continue;
						seen[ v ] = true; checkedN ++;
						Vector3 t = tan[ v ], n = nrm[ v ].normalized;
						Vector3 dPdu = ( e1 * d2.y - e2 * d1.y ) / det, dPdv = ( e2 * d1.x - e1 * d2.x ) / det;
						Vector3 B = Vector3.Cross( n, t ) * tan[ v ].w;
						bool good = Mathf.Abs( t.magnitude - 1 ) < 1e-4f && Mathf.Abs( Vector3.Dot( t, n ) ) < 1e-3f && Vector3.Dot( t, dPdu ) > 0 && Vector3.Dot( B, - dPdv ) > 0;
						if ( ! good )
						{
							bad ++; if ( Mathf.Abs( t.magnitude - 1 ) >= 1e-4f || Mathf.Abs( Vector3.Dot( t, n ) ) >= 1e-3f ) badFrame ++;
							int l = Mathf.RoundToInt( col[ v ].a ); string why = ( Mathf.Abs( t.magnitude - 1 ) >= 1e-4f ? "len " : "" ) + ( Mathf.Abs( Vector3.Dot( t, n ) ) >= 1e-3f ? "ortho " : "" ) + ( Vector3.Dot( t, dPdu ) <= 0 ? "u " : "" ) + ( Vector3.Dot( B, - dPdv ) <= 0 ? "v " : "" );
							string k2 = l + ":" + why; badKinds[ k2 ] = badKinds.TryGetValue( k2, out var cc ) ? cc + 1 : 1;
						}
					}
				}

				bool pass = Math.Abs( a - b ) <= 1e-6 * Math.Abs( b ) && Math.Abs( ai - bi ) <= 1e-9 * Math.Abs( bi ) && nv == ( int ) j[ "vertices" ] && tri.Length == ( int ) j[ "indices" ] && bd < 1e-4 && histOk && badFrame == 0 && Math.Abs( neg - b ) > 1e-6;
				ok &= pass;
				sb.AppendLine( $"{name}: {nv} vertices (JS {( int ) j[ "vertices" ]}), {tri.Length / 3} triangles; vertex sum {a:R} vs JS {b:R} (diff {a - b:E2}), index sum diff {ai - bi:E2}, bounds diff {bd:E2}, layers {( histOk ? "match" : "DIFFER" )} ({hist.Count} kinds), tangent frames: {badFrame} not unit / not orthogonal, {bad} of {checkedN} not along +u / -v at their first triangle (seams of smooth props), negative control moves the sum by {neg - b:E2}  {( pass ? "OK" : "FAIL" )}" );
				foreach ( var kv in badKinds ) sb.AppendLine( $"    bad frames, layer:what  {kv.Key} x{kv.Value}" );
				UnityEngine.Object.DestroyImmediate( mesh );
			}

			sb.AppendLine( ok ? "stall kit: ALL OK" : "stall kit: FAILED" );
			return sb.ToString();
		}
	}
}
