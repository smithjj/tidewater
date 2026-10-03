using Tidewater.World.Terrain;
using Unity.Collections;
using UnityEngine;

// Port of src/world/TerrainGPU.js (the texture part): GPU-side terrain data shared by every shader that needs
// ground height: the terrain material, and later the ocean (depth attenuation / hiding under land), shore
// waves, particles and water queries.
//
// Textures (all over the terrain domain unless noted), published as global shader properties:
//   _TWHeightTex  R32F    exact heights, bilinear filtering done manually (terrainHeightAt)
//   _TWNormalTex  RGBA8   macro normal xz (encoded), rock mask, baked ambient occlusion (mipmapped)
//   _TWSplatTex   RGBA8   loose sand, worn ground / paths, gullies (land) or seagrass (seabed),
//                         seabed rubble / the eroded beach scarp face on land (mipmapped)
//   _TWDetailTex  RGBA8   512^2 tileable detail heights (rock, soil, sand, fbm), see DetailTexture.cs
// _TWTerrainParams = ( origin, size, res, 0 ).
//
// Not ported yet: the shore field texture and the heightfield sun shadow (shadowHeightTexture /
// sunShadowTexture / updateSunShadow); HDRP's cascaded shadows stand in for the latter for now.
namespace Tidewater.World
{
	public sealed class TerrainGPU
	{
		public readonly TerrainData terrain;
		public Texture2D heightTexture, normalTexture, splatTexture, detailTexture;
		public readonly double origin, size;
		public readonly int res;
		public readonly double bakeMs;

		public TerrainGPU( TerrainData terrain )
		{
			this.terrain = terrain;
			res = terrain.res;
			origin = terrain.origin;
			size = terrain.size;
			var sw = System.Diagnostics.Stopwatch.StartNew();

			// heights: R32F, loaded with manual bilinear filtering (no sampler / float filtering needed)
			heightTexture = new Texture2D( res, res, TextureFormat.RFloat, false, true ) { name = "terrainHeights", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
			heightTexture.SetPixelData( terrain.heights, 0 );
			heightTexture.Apply( false, true );

			var maps = TerrainBake.BakeTerrainMaps( terrain );
			// trilinear, clamp to edge, linear data (no colour space)
			normalTexture = DataTexture( maps.normal, res, "terrainNormalRockAO" );
			splatTexture = DataTexture( maps.splat, res, "terrainSplat" );

			// tileable detail: repeat wrapping, trilinear, anisotropy 4 (the JS aniso4Repeat sampler)
			detailTexture = DataTexture( DetailTexture.Get(), DetailTexture.S, "terrainDetail", TextureWrapMode.Repeat, 4 );

			bakeMs = sw.Elapsed.TotalMilliseconds;
		}

		static Texture2D DataTexture( byte[] data, int n, string label, TextureWrapMode wrap = TextureWrapMode.Clamp, int aniso = 1 )
		{
			var t = new Texture2D( n, n, TextureFormat.RGBA32, true, true ) { name = label, wrapMode = wrap, filterMode = FilterMode.Trilinear, anisoLevel = aniso };
			t.SetPixelData( data, 0 );
			t.Apply( true, true );
			return t;
		}

		// publish the textures and parameters to every shader
		public void SetGlobals()
		{
			Shader.SetGlobalTexture( "_TWHeightTex", heightTexture );
			Shader.SetGlobalTexture( "_TWNormalTex", normalTexture );
			Shader.SetGlobalTexture( "_TWSplatTex", splatTexture );
			Shader.SetGlobalTexture( "_TWDetailTex", detailTexture );
			Shader.SetGlobalVector( "_TWTerrainParams", new Vector4( ( float ) origin, ( float ) size, res, 0 ) );
		}

		public void Destroy()
		{
			Object.DestroyImmediate( heightTexture ); Object.DestroyImmediate( normalTexture );
			Object.DestroyImmediate( splatTexture ); Object.DestroyImmediate( detailTexture );
		}
	}
}
