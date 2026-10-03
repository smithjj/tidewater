using System;
using System.IO;
using System.Text;
using Tidewater.Ocean;
using UnityEditor;
using UnityEngine;

// Compares the C# WaterQuery with the JS original (unity/tools/dump-water-query.mjs):
//   node unity/tools/dump-water-query.mjs unity/Temp/oracle/query
//   unity/tools/run-oracle.sh WaterQueryOracle
// Same 255 deep-water points, the FFT advanced 120 frames of 1/60 s, then one query dispatch.
namespace Tidewater.EditorTools
{
	public static class WaterQueryOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/query" ) );

		[MenuItem( "Tidewater/Compare water query with JS oracle" )]
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
			var inputs = ReadFile( dir + "/query_inputs.f32" );
			var js = ReadFile( dir + "/query_results.f32" );
			var fftShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/OceanFFT.compute" );
			var qShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/WaterQuery.compute" );
			using ( var fft = new OceanFFT( fftShader ) )
			using ( var q = new WaterQuery( qShader, fft, null ) )
			{
				for ( int f = 0; f < 120; f ++ ) fft.Update( 1f / 60f );
				int n = 255;
				q.Allocate( "pts", n );
				for ( int i = 0; i < n; i ++ ) q.SetPoint( 1 + i, inputs[ ( 1 + i ) * 4 ], inputs[ ( 1 + i ) * 4 + 1 ] );
				q.SetCamera( inputs[ 0 ], inputs[ 1 ] );
				q.Update();
				q.Flush();
				sb.AppendLine( "valid " + q.cpuValid );

				double sumSq = 0, errSq = 0, maxErr = 0, maxN = 0; int worst = 0;
				for ( int i = 1; i <= n; i ++ )
				{
					var r = q.Get( i );
					double a = r.height, b = js[ i * 4 ];
					sumSq += b * b; double e = a - b; errSq += e * e;
					if ( Math.Abs( e ) > maxErr ) { maxErr = Math.Abs( e ); worst = i; }
					maxN = Math.Max( maxN, Math.Max( Math.Abs( r.nx - js[ i * 4 + 1 ] ), Math.Abs( r.nz - js[ i * 4 + 2 ] ) ) );
				}

				sb.AppendLine( $"height: rms {Math.Sqrt( sumSq / n ):F4}, error rms {Math.Sqrt( errSq / n ):E2}, max {maxErr:E2} at slot {worst} (cs {q.Get( worst ).height:F5} js {js[ worst * 4 ]:F5})" );
				sb.AppendLine( $"normal xz: max abs diff {maxN:E2}" );
				sb.AppendLine( $"floor: cs {q.Get( 1 ).floor} js {js[ 7 ]} (no terrain: -500)" );
			}

			return sb.ToString();
		}
	}
}
