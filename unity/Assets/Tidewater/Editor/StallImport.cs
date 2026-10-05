using UnityEditor;
using UnityEngine;

// Sets up Resources/stalls/maps (the Poly Haven surfaces and prop textures of public/models/props, copied as they are, and the sign atlas): every map imported as
// a plain compressed texture (high-quality compression: BC7; the normal maps too, the shader decodes them as the JS does, OpenGL convention), sRGB for the albedo maps and the signs only,
// mips, repeat wrap (the signs clamp), not readable (StallKit copies them into texture arrays on the GPU). Run it again after the files change: Tidewater / Set up stalls.
namespace Tidewater.EditorTools
{
	public static class StallImport
	{
		const string DIR = "Assets/Tidewater/Resources/stalls/maps/";

		[MenuItem( "Tidewater/Set up stalls" )]
		public static string Run()
		{
			int n = 0;
			AssetDatabase.StartAssetEditing();
			try
			{
				foreach ( var f in System.IO.Directory.GetFiles( DIR ) )
				{
					string p = f.Replace( '\\', '/' );
					if ( p.EndsWith( ".meta" ) ) continue;
					var t = AssetImporter.GetAtPath( p ) as TextureImporter;
					if ( t == null ) continue;
					bool signs = p.EndsWith( "signs.png" );
					t.textureType = TextureImporterType.Default;
					t.sRGBTexture = signs || p.EndsWith( "_a.jpg" );
					t.alphaSource = signs ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
					t.alphaIsTransparency = false;
					t.isReadable = false;
					t.mipmapEnabled = true;
					t.streamingMipmaps = false;
					t.wrapMode = signs ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
					t.filterMode = FilterMode.Trilinear;
					t.anisoLevel = 8;
					t.maxTextureSize = 2048;
					t.npotScale = TextureImporterNPOTScale.None;
					// (a first version of this importer forced BC7 on the default platform, which is not allowed: put it back to automatic)
					var d = t.GetDefaultPlatformTextureSettings();
					d.format = TextureImporterFormat.Automatic; d.overridden = false; d.maxTextureSize = 2048;
					t.SetPlatformTextureSettings( d );
					t.textureCompression = TextureImporterCompression.CompressedHQ; // BC7 on desktop
					t.SaveAndReimport();
					n ++;
				}
			}
			finally { AssetDatabase.StopAssetEditing(); }
			AssetDatabase.Refresh();
			return "stall maps: " + n + " imported";
		}
	}
}
