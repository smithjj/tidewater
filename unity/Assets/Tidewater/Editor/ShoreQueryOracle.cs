using System;
using System.IO;
using System.Text;
using Tidewater.Ocean;
using Tidewater.World;
using UnityEditor;
using UnityEngine;
using TerrainData = Tidewater.World.TerrainData;

// Compares the C# shore waves + WaterQuery with the JS originals on the real island (unity/tools/dump-shore-query.mjs):
//   node unity/tools/dump-shore-query.mjs unity/Temp/oracle/shorequery
//   unity/tools/run-oracle.sh ShoreQueryOracle
// Same 255 points in the surf zone, the FFT and the shore clock advanced 120 frames of 1/60 s, then one query dispatch.
namespace Tidewater.EditorTools
{
	public static class ShoreQueryOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/shorequery" ) );

		[MenuItem( "Tidewater/Compare shore query with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		static float[] ReadFile( string path )
		{
			var bytes = File.ReadAllBytes( path );
			var a = new float[ bytes.Length / 4 ];
			Buffer.BlockCopy( bytes, 0, a, 0, bytes.Length );
			return a;
		}

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var inputs = ReadFile( dir + "/shorequery_inputs.f32" );
			var js = ReadFile( dir + "/shorequery_results.f32" );
			var jsOff = ReadFile( dir + "/shorequery_results_off.f32" );
			var fftShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/OceanFFT.compute" );
			var qShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/WaterQuery.compute" );

			var terrain = new TerrainData( 7 );
			var field = ShoreField.Compute( terrain, 512, WorldLayout.SwellDirX, WorldLayout.SwellDirZ );
			var gpu = new TerrainGPU( terrain, field );
			using ( var fft = new OceanFFT( fftShader ) )
			{
				var shore = new ShoreWaves( gpu, field );
				using ( var q = new WaterQuery( qShader, fft, gpu, shore ) { oracleHash = true } )
				{
					for ( int f = 0; f < 120; f ++ ) { fft.Update( 1f / 60f ); shore.Update( 1f / 60f ); }
					int n = 255;
					q.Allocate( "pts", n );
					for ( int i = 0; i < n; i ++ ) q.SetPoint( 1 + i, inputs[ ( 1 + i ) * 4 ], inputs[ ( 1 + i ) * 4 + 1 ] );
					q.SetCamera( inputs[ 0 ], inputs[ 1 ] );
					shore.enabled = 0;
					q.Update();
					q.Flush();
					{
						double e2 = 0, s2 = 0, mx = 0;
						for ( int i = 1; i <= n; i ++ ) { double a = q.Get( i ).height, b = jsOff[ i * 4 ]; s2 += b * b; e2 += ( a - b ) * ( a - b ); mx = Math.Max( mx, Math.Abs( a - b ) ); }
						sb.AppendLine( $"shore DISABLED (FFT only, shallow-water attenuation): rms {Math.Sqrt( s2 / n ):F4}, error rms {Math.Sqrt( e2 / n ):E2}, max {mx:E2}" );
					}

					shore.enabled = 1;
					q.Update();
					q.Flush();
					sb.AppendLine( $"valid {q.cpuValid}, shore phase {shore.phase:F6}" );

					double sumSq = 0, errSq = 0, maxErr = 0, maxFloor = 0; int worst = 0;
					for ( int i = 1; i <= n; i ++ )
					{
						var r = q.Get( i );
						double a = r.height, b = js[ i * 4 ];
						sumSq += b * b; double e = a - b; errSq += e * e;
						if ( Math.Abs( e ) > maxErr ) { maxErr = Math.Abs( e ); worst = i; }
						maxFloor = Math.Max( maxFloor, Math.Abs( r.floor - js[ i * 4 + 3 ] ) );
					}

					sb.AppendLine( $"height: rms {Math.Sqrt( sumSq / n ):F4}, error rms {Math.Sqrt( errSq / n ):E2}, max {maxErr:E2} at slot {worst} (cs {q.Get( worst ).height:F5} js {js[ worst * 4 ]:F5}, point {inputs[ worst * 4 ]:F2},{inputs[ worst * 4 + 1 ]:F2})" );
					sb.AppendLine( $"sea floor: max abs diff {maxFloor:E2}" );
					var rows = new System.Collections.Generic.List<(double err, int i)>();
					int bad = 0;
					for ( int i = 1; i <= n; i ++ ) { double e = Math.Abs( q.Get( i ).height - js[ i * 4 ] ); rows.Add( ( e, i ) ); if ( e > 0.01 ) bad ++; }
					rows.Sort( ( x, y ) => y.err.CompareTo( x.err ) );
					sb.AppendLine( $"points with error > 1 cm: {bad} of {n}" );
					for ( int k = 0; k < 12; k ++ )
					{
						int i = rows[ k ].i;
						sb.AppendLine( $"  slot {i,3} at ({inputs[ i * 4 ],7:F2},{inputs[ i * 4 + 1 ],7:F2}) floor {js[ i * 4 + 3 ],6:F2}: cs {q.Get( i ).height,8:F4} js {js[ i * 4 ],8:F4} err {rows[ k ].err:F4}" );
					}
				}

				shore.Destroy();
			}

			gpu.Destroy();
			return sb.ToString();
		}
	}
}
