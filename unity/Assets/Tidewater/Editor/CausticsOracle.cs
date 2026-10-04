using System;
using System.IO;
using System.Text;
using Tidewater.Ocean;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Compares the C# caustics layers with the JS original (unity/tools/dump-caustics.mjs): both run the FFT 120 frames of 1/60 s, then
// render the fine and broad layers with the sun toward (0.45, 0.55, -0.7) (sim space). Pixel-for-pixel statistics of R and G.
//   node unity/tools/dump-caustics.mjs unity/Temp/oracle/caustics ; unity/tools/run-oracle.sh CausticsOracle
namespace Tidewater.EditorTools
{
	public static class CausticsOracle
	{
		static string DefaultDir => Path.GetFullPath( Path.Combine( Application.dataPath, "../Temp/oracle/caustics" ) );

		[MenuItem( "Tidewater/Compare caustics with JS oracle" )]
		static void Menu() => Debug.Log( Compare( DefaultDir ) );

		public static string Compare( string dir = null )
		{
			dir = string.IsNullOrEmpty( dir ) ? DefaultDir : dir;
			var sb = new StringBuilder();
			var fftShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/OceanFFT.compute" );
			var splat = Shader.Find( "Hidden/Tidewater/CausticsSplat" );
			var sun = new Vector3( 0.45f, 0.55f, -0.7f ).normalized;
			Shader.SetGlobalVector( "_TWSunDir", new Vector4( sun.x, sun.y, sun.z, 0 ) );
			using ( var fft = new OceanFFT( fftShader ) )
			{
				for ( int f = 0; f < 120; f ++ ) fft.Update( 1f / 60f );
				fft.SetGlobals();
				using ( var cau = new Caustics( fft, splat ) )
				{
					cau.Update();
					foreach ( var (name, layer) in new[] { ( "fine", cau.fine ), ( "broad", cau.broad ) } )
					{
						var req = AsyncGPUReadback.Request( layer.texture, 0, TextureFormat.RGBAFloat );
						req.WaitForCompletion();
						var cs = req.GetData<float>().ToArray();
						var bytes = File.ReadAllBytes( $"{dir}/caustics_{name}.f32" );
						var js = new float[ bytes.Length / 4 ];
						Buffer.BlockCopy( bytes, 0, js, 0, bytes.Length );
						if ( js.Length != cs.Length ) { sb.AppendLine( $"{name}: LENGTH MISMATCH {cs.Length} vs {js.Length}" ); continue; }
						for ( int ch = 0; ch < 2; ch ++ )
						{
							double sc = 0, sj = 0, sc2 = 0, sj2 = 0, err = 0, errFlip = 0; int n = js.Length / 4, res = layer.res;
							for ( int i = 0; i < n; i ++ )
							{
								double a = cs[ i * 4 + ch ], b = js[ i * 4 + ch ];
								sc += a; sj += b; sc2 += a * a; sj2 += b * b; err += Math.Abs( a - b );
								// the same image mirrored in y (a y-orientation difference would show here)
								int x = i % res, y = i / res;
								errFlip += Math.Abs( a - js[ ( ( res - 1 - y ) * res + x ) * 4 + ch ] );
							}

							sb.AppendLine( $"{name} {"RG"[ ch ]}: cs mean {sc / n:F4} std {Math.Sqrt( sc2 / n - ( sc / n ) * ( sc / n ) ):F4} | js mean {sj / n:F4} std {Math.Sqrt( sj2 / n - ( sj / n ) * ( sj / n ) ):F4} | mean abs diff {err / n:F4}, if mirrored in y {errFlip / n:F4}" );
						}
					}
				}
			}

			return sb.ToString();
		}
	}
}
