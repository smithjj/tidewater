using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;

// Sets up Resources/characters (Joe and Marta, Rocketbox avatars, converted by unity/tools/characters-to-fbx.py): the texture import types, an HDRP Lit material per
// avatar material (body, head, and the hair / lash cut-out `opacity` where there is one) with the maps the converter wrote, and the model's import settings:
// the seven clips as separate takes (the idle and talk ones loop), no Unity-made materials. Run it again after the files change: Tidewater / Set up characters.
namespace Tidewater.EditorTools
{
	public static class CharacterImport
	{
		const string DIR = "Assets/Tidewater/Resources/characters/";
		static readonly string[] NAMES = { "joe", "marta" };
		static readonly string[] LOOPS = { "idle_neutral_01", "idle_breathe_01", "idle_look_around_01", "gestic_talk_neutral_01", "gestic_talk_relaxed_01" };

		[MenuItem( "Tidewater/Set up characters" )]
		public static string Run()
		{
			var log = new System.Text.StringBuilder();
			AssetDatabase.StartAssetEditing();
			try
			{
				foreach ( var n in NAMES )
					foreach ( var f in System.IO.Directory.GetFiles( DIR, n + "_*.png" ) )
					{
						string p = f.Replace( '\\', '/' );
						var t = ( TextureImporter ) AssetImporter.GetAtPath( p );
						if ( t == null ) continue;
						if ( p.EndsWith( "_normal.png" ) ) t.textureType = TextureImporterType.NormalMap;
						else { t.textureType = TextureImporterType.Default; t.sRGBTexture = p.EndsWith( "_color.png" ); }
						t.alphaSource = p.EndsWith( "_mask.png" ) || p.EndsWith( "opacity_color.png" ) ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
						t.alphaIsTransparency = p.EndsWith( "opacity_color.png" );
						t.maxTextureSize = 1024; t.mipmapEnabled = true; t.anisoLevel = 4;
						t.SaveAndReimport();
					}
			}
			finally { AssetDatabase.StopAssetEditing(); }
			AssetDatabase.Refresh();

			foreach ( var n in NAMES )
			{
				var imp = ( ModelImporter ) AssetImporter.GetAtPath( DIR + n + ".fbx" );
				if ( imp == null ) { log.AppendLine( n + ": no fbx" ); continue; }
				imp.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
				imp.importCameras = false; imp.importLights = false; imp.importBlendShapes = false;
				imp.animationType = ModelImporterAnimationType.Generic;
				imp.importAnimation = true;
				imp.meshCompression = ModelImporterMeshCompression.Off;
				imp.optimizeGameObjects = false;
				imp.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
				imp.SaveAndReimport();

				// one clip per take, named after the action; the idle and talk clips loop
				var clips = imp.defaultClipAnimations.Where( c => ! c.name.StartsWith( "Take" ) ).ToArray();
				foreach ( var c in clips )
				{
					string key = c.name.Contains( "|" ) ? c.name.Substring( c.name.LastIndexOf( '|' ) + 1 ) : c.name;
					c.name = key;
					c.loopTime = LOOPS.Contains( key );
					c.loopPose = false;
				}

				imp.clipAnimations = clips;
				// the materials: HDRP Lit from the converter's maps, matched by the avatar's material names
				var map = imp.GetExternalObjectMap();
				foreach ( var m in new[] { "body", "head", "opacity" } )
				{
					string tex = DIR + n + "_" + m + "_color.png";
					if ( AssetDatabase.LoadAssetAtPath<Texture2D>( tex ) == null ) continue;
					var mat = Material( n, m );
					var id = new AssetImporter.SourceAssetIdentifier( typeof( UnityEngine.Material ), m );
					imp.AddRemap( id, mat );
					log.AppendLine( n + ": " + m + " -> " + mat.name );
				}

				imp.SaveAndReimport();
				var take = imp.clipAnimations.Select( c => c.name + ( c.loopTime ? "*" : "" ) );
				log.AppendLine( n + ": clips " + string.Join( ", ", take ) );
			}

			AssetDatabase.SaveAssets();
			return log.ToString();
		}

		static Material Material( string n, string m )
		{
			string path = DIR + n + "_" + m + ".mat";
			var mat = AssetDatabase.LoadAssetAtPath<Material>( path );
			if ( mat == null ) { mat = new Material( Shader.Find( "HDRP/Lit" ) ); AssetDatabase.CreateAsset( mat, path ); }
			Texture2D T( string s ) => AssetDatabase.LoadAssetAtPath<Texture2D>( DIR + n + "_" + m + "_" + s + ".png" );
			bool cut = m == "opacity";
			mat.SetTexture( "_BaseColorMap", T( "color" ) );
			mat.SetColor( "_BaseColor", Color.white );
			if ( T( "normal" ) != null ) { mat.SetTexture( "_NormalMap", T( "normal" ) ); mat.SetFloat( "_NormalScale", 1f ); mat.EnableKeyword( "_NORMALMAP" ); }
			if ( T( "mask" ) != null ) { mat.SetTexture( "_MaskMap", T( "mask" ) ); mat.EnableKeyword( "_MASKMAP" ); }
			else { mat.SetFloat( "_Metallic", 0f ); mat.SetFloat( "_Smoothness", 0.4f ); }
			mat.SetFloat( "_AlphaCutoffEnable", cut ? 1f : 0f );
			mat.SetFloat( "_AlphaCutoff", 0.4f );
			mat.SetFloat( "_DoubleSidedEnable", cut ? 1f : 0f );
			mat.SetFloat( "_SpecularAAThreshold", 0.2f );
			HDShaderUtils.ResetMaterialKeywords( mat );
			EditorUtility.SetDirty( mat );
			return mat;
		}
	}
}
