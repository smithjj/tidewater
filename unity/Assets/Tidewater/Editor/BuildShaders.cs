using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// The game makes its materials in code from `Shader.Find( "Tidewater/..." )`. A player build only carries the shaders something refers to (a scene, a material asset, Resources), and
// nothing refers to these, so Shader.Find returns null there and the first Material( shader ) throws (the Editor finds every shader in the project, which hid it). Before each build this
// adds every shader under Assets/Tidewater/Shaders, and HDRP/Lit (the one the code asks for from the render pipeline), to Graphics > Always Included Shaders.
//   Tidewater > Include shaders in builds   does the same by hand
namespace Tidewater.EditorTools
{
	public sealed class BuildShaders : IPreprocessBuildWithReport
	{
		public int callbackOrder => 0;

		public void OnPreprocessBuild( BuildReport report ) => Include();

		[MenuItem( "Tidewater/Include shaders in builds" )]
		public static void IncludeMenu() => Debug.Log( Include() );

		public static string Include()
		{
			var wanted = new List<Shader>();
			foreach ( var guid in AssetDatabase.FindAssets( "t:Shader", new[] { "Assets/Tidewater/Shaders" } ) )
			{
				var s = AssetDatabase.LoadAssetAtPath<Shader>( AssetDatabase.GUIDToAssetPath( guid ) );
				if ( s != null ) wanted.Add( s );
			}

			var lit = Shader.Find( "HDRP/Lit" );
			if ( lit != null ) wanted.Add( lit );

			var gs = AssetDatabase.LoadAssetAtPath<Object>( "ProjectSettings/GraphicsSettings.asset" );
			var so = new SerializedObject( gs );
			var list = so.FindProperty( "m_AlwaysIncludedShaders" );
			var have = new HashSet<Object>();
			for ( int i = 0; i < list.arraySize; i ++ ) have.Add( list.GetArrayElementAtIndex( i ).objectReferenceValue );

			int added = 0;
			foreach ( var s in wanted.Where( s => ! have.Contains( s ) ) )
			{
				list.InsertArrayElementAtIndex( list.arraySize );
				list.GetArrayElementAtIndex( list.arraySize - 1 ).objectReferenceValue = s;
				added ++;
			}

			if ( added > 0 ) { so.ApplyModifiedPropertiesWithoutUndo(); AssetDatabase.SaveAssets(); }
			return $"Always Included Shaders: {added} added, {list.arraySize} in all";
		}
	}
}
