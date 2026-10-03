using UnityEngine;

// Port of src/ocean/FoamTexture.js createFoamTexture: the tileable procedural foam pattern (see
// Shaders/Ocean/FoamPattern.compute for the channels). RGBA16F, repeat, trilinear + anisotropic, mipmapped.
namespace Tidewater.Ocean
{
	public static class FoamTexture
	{
		public static RenderTexture Create( ComputeShader shader, int size = 1024 )
		{
			var tex = new RenderTexture( size, size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
			{
				name = "foamPattern", enableRandomWrite = true, useMipMap = true, autoGenerateMips = false,
				wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8,
			};
			tex.Create();
			int k = shader.FindKernel( "FoamPattern" );
			shader.SetTexture( k, "_FoamOut", tex );
			shader.SetInt( "_FoamSize", size );
			shader.Dispatch( k, size / 8, size / 8, 1 );
			tex.GenerateMips();
			return tex;
		}
	}
}
