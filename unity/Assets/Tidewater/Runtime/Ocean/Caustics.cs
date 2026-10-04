using System;
using UnityEngine;
using UnityEngine.Rendering;

// Port of src/ocean/Caustics.js: caustics by rasterized photon splatting. Two layers are rendered each frame into RGBA16F tiles
// (Shaders/Ocean/CausticsSplat.shader), each with two focal planes (R = shallow, G = deep) blended by the real depth at lookup
// (Caustics.hlsl):
//   fine:  the finest FFT cascade, 512^2, planes at 1.2 and 4 m (ripples < ~11 cm filtered out: they defocus immediately)
//   broad: the next cascade, 256^2, planes at 3 and 9 m; a different tile size -> no visible repetition
// Published: _TWCausticsFine, _TWCausticsBroad, _TWCausticsParams = ( strength, fine tile, broad tile, 0 ).
namespace Tidewater.Ocean
{
	public sealed class CausticLayer : IDisposable
	{
		public readonly OceanFFT fft;
		public readonly int cascade, res, grid;
		public readonly double tile, margin;
		readonly double[] depths;
		readonly float slopeLevel;
		public RenderTexture texture;
		readonly Material[] mats;

		public CausticLayer( OceanFFT fft, Shader shader, int cascade, int res, int grid, double[] depths, string name, float slopeLevel, double margin )
		{
			this.fft = fft; this.cascade = cascade; this.res = res;
			tile = fft.sizes[ cascade ];
			this.margin = margin;
			this.grid = ( int ) Math.Ceiling( grid * ( 1 + 2 * margin ) );
			this.depths = depths;
			this.slopeLevel = slopeLevel;
			// the floor is mostly seen at grazing angles: filter along the view (anisotropic sampler at lookup), stay sharp across it
			texture = new RenderTexture( res, res, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
				{ name = name, useMipMap = true, autoGenerateMips = false, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 8 };
			texture.Create();
			mats = new Material[ depths.Length ];
			for ( int k = 0; k < depths.Length; k ++ )
			{
				mats[ k ] = new Material( shader ) { hideFlags = HideFlags.HideAndDontSave };
				mats[ k ].SetVector( "_CParams", new Vector4( this.grid, ( float ) margin, ( float ) tile, cascade ) );
				mats[ k ].SetVector( "_CParams2", new Vector4( slopeLevel, ( float ) depths[ k ], k, 0 ) );
			}
		}

		public void Render( CommandBuffer cmd )
		{
			cmd.SetRenderTarget( texture, 0 );
			cmd.ClearRenderTarget( false, true, new Color( 0, 0, 0, 0 ) );
			int verts = grid * grid * 6;
			foreach ( var m in mats ) cmd.DrawProcedural( Matrix4x4.identity, m, 0, MeshTopology.Triangles, verts );
		}

		public void Dispose()
		{
			if ( texture != null ) { texture.Release(); UnityEngine.Object.DestroyImmediate( texture ); }
			foreach ( var m in mats ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			texture = null;
		}
	}

	public sealed class Caustics : IDisposable
	{
		public float strength = 0.75f;
		public readonly CausticLayer fine, broad;
		readonly CommandBuffer cmd = new CommandBuffer { name = "Caustics" };

		public Caustics( OceanFFT fft, Shader splat )
		{
			int fineC = fft.cascades - 1;
			fine = new CausticLayer( fft, splat, fineC, 512, 256, new[] { 1.2, 4.0 }, "causticsFine", 1f, 0.35 );
			broad = new CausticLayer( fft, splat, fineC - 1, 256, 128, new[] { 3.0, 9.0 }, "causticsBroad", 0.5f, 0.35 );
		}

		// after the FFT update (and with the sun direction global _TWSunDir set)
		public void Update()
		{
			cmd.Clear();
			fine.Render( cmd );
			broad.Render( cmd );
			Graphics.ExecuteCommandBuffer( cmd );
			// box-filtered mips (the JS builds them in compute)
			fine.texture.GenerateMips();
			broad.texture.GenerateMips();
		}

		public void SetGlobals()
		{
			Shader.SetGlobalTexture( "_TWCausticsFine", fine.texture );
			Shader.SetGlobalTexture( "_TWCausticsBroad", broad.texture );
			Shader.SetGlobalVector( "_TWCausticsParams", new Vector4( strength, ( float ) fine.tile, ( float ) broad.tile, 0 ) );
		}

		public void Dispose()
		{
			fine.Dispose(); broad.Dispose(); cmd.Release();
		}
	}
}
