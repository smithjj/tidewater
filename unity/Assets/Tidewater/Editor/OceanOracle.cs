using System;
using System.IO;
using System.Text;
using Tidewater.Ocean;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Compares the C# ocean FFT with the JS original (unity/tools/dump-ocean-fft.mjs, run headless on Dawn):
//   node unity/tools/dump-ocean-fft.mjs unity/Temp/oracle/fft 120
//   unity/tools/run-oracle.sh OceanOracle
// Both sides run update( 1 / 60 ) with frame.dt = 1 / 60 and are compared after frame 1 and after frame 120. The
// maps are float16 on both sides and the GPUs differ (trig / tanh / pow), so the comparison is statistical: error
// per cascade and channel relative to the RMS of the JS values.
namespace Tidewater.EditorTools
{
	public static class OceanOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/fft" ) );

		[MenuItem( "Tidewater/Compare ocean FFT with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		public static string Compare( string dir = null, int frames = 120 )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/OceanFFT.compute" );
			using ( var fft = new OceanFFT( shader ) )
			{
				for ( int f = 1; f <= frames; f ++ )
				{
					fft.Update( 1f / 60f );
					if ( f == 1 ) CompareTag( sb, fft, dir, "f1" );
				}

				CompareTag( sb, fft, dir, "final" );
			}

			return sb.ToString();
		}

		static float[] Read( RenderTexture rt )
		{
			var req = AsyncGPUReadback.Request( rt, 0, 0, rt.width, 0, rt.height, 0, rt.volumeDepth, TextureFormat.RGBAFloat );
			req.WaitForCompletion();
			int per = rt.width * rt.height * 4;
			var a = new float[ per * req.layerCount ];
			for ( int l = 0; l < req.layerCount; l ++ ) { var d = req.GetData<float>( l ); for ( int i = 0; i < per; i ++ ) a[ l * per + i ] = d[ i ]; }
			return a;
		}

		static float[] ReadFile( string path )
		{
			var bytes = File.ReadAllBytes( path );
			var a = new float[ bytes.Length / 4 ];
			Buffer.BlockCopy( bytes, 0, a, 0, bytes.Length );
			return a;
		}

		static void CompareTag( StringBuilder sb, OceanFFT fft, string dir, string tag )
		{
			sb.AppendLine( $"--- after {( tag == "f1" ? "frame 1" : "the last frame" )} ---" );
			foreach ( var (name, rt) in new[] { ( "disp", fft.displacementTexture ), ( "deriv", fft.derivativeTexture ) } )
			{
				var cs = Read( rt );
				var js = ReadFile( $"{dir}/fft_{name}_{tag}.f32" );
				if ( cs.Length != js.Length ) { sb.AppendLine( $"{name}: LENGTH MISMATCH {cs.Length} vs {js.Length}" ); continue; }
				int per = FFTLayer;
				for ( int c = 0; c < fft.cascades; c ++ )
				{
					var chans = new string[ 4 ];
					for ( int ch = 0; ch < 4; ch ++ )
					{
						double sumSq = 0, errSq = 0, maxErr = 0; int nan = 0;
						for ( int i = 0; i < per / 4; i ++ )
						{
							double a = cs[ c * per + i * 4 + ch ], b = js[ c * per + i * 4 + ch ];
							if ( double.IsNaN( a ) ) nan ++;
							sumSq += b * b; double e = a - b; errSq += e * e; maxErr = Math.Max( maxErr, Math.Abs( e ) );
						}

						double rms = Math.Sqrt( sumSq / ( per / 4 ) ), rmsErr = Math.Sqrt( errSq / ( per / 4 ) );
						chans[ ch ] = $"rms {rms:F4} err {rmsErr:E1} ({( rms > 0 ? 100 * rmsErr / rms : 0 ):F2}%) max {maxErr:E1}" + ( nan > 0 ? $" NaN {nan}" : "" );
					}

					sb.AppendLine( $"  {name} cascade {c} (L={fft.sizes[ c ]}):" );
					for ( int ch = 0; ch < 4; ch ++ ) sb.AppendLine( $"    ch{ch}: {chans[ ch ]}" );
				}
			}
		}

		const int FFTLayer = 256 * 256 * 4;
	}
}
