using UnityEditor;
using UnityEngine;

// Sets up Resources/whale (the humpback's baked model and skin, copied from public/models/whale; the mesh binary is humpback_mesh.bytes, a TextAsset, so it and the manifest
// load by name). The albedo map carries the roughness in its alpha: sRGB colour, alpha as data, compressed (BC7). The height map holds a 16 bit relief in R / G, so it is linear
// and not compressed (the mips average the two bytes, as the JS's did). Both: mips, clamp, trilinear, anisotropic, 4096 wide, not readable. Run it again after the files change:
// Tidewater / Set up whale.
namespace Tidewater.EditorTools
{
	public static class WhaleImport
	{
		const string DIR = "Assets/Tidewater/Resources/whale/";

		[MenuItem( "Tidewater/Set up whale" )]
		public static string Run()
		{
			int n = 0;
			foreach ( var name in new[] { "humpback_albedo.png", "humpback_height.png" } )
			{
				var t = AssetImporter.GetAtPath( DIR + name ) as TextureImporter;
				if ( t == null ) continue;
				bool color = name.Contains( "albedo" );
				t.textureType = TextureImporterType.Default;
				t.sRGBTexture = color;
				t.alphaSource = color ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
				t.alphaIsTransparency = false;
				t.isReadable = false;
				t.mipmapEnabled = true;
				t.streamingMipmaps = false;
				t.wrapMode = TextureWrapMode.Clamp;
				t.filterMode = FilterMode.Trilinear;
				t.anisoLevel = 8;
				t.maxTextureSize = 4096;
				t.npotScale = TextureImporterNPOTScale.None;
				var d = t.GetDefaultPlatformTextureSettings();
				d.overridden = false; d.maxTextureSize = 4096; d.format = TextureImporterFormat.Automatic;
				t.SetPlatformTextureSettings( d );
				t.textureCompression = color ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Uncompressed;
				t.SaveAndReimport();
				n ++;
			}

			AssetDatabase.Refresh();
			return "whale maps: " + n + " imported";
		}
	}
}
