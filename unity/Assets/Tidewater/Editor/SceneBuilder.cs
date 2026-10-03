using Tidewater.Player;
using Tidewater.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds the island scene from the HDRP template's OutdoorsScene (camera, sun, sky and fog volume): adds the
// terrain, a placeholder sea (until the ocean is ported) and a debug fly camera.
//   Tidewater > Build island scene
namespace Tidewater.EditorTools
{
	public static class SceneBuilder
	{
		const string ScenePath = "Assets/Tidewater/Scenes/Island.unity";

		[MenuItem( "Tidewater/Build island scene" )]
		public static string Build()
		{
			if ( ! AssetDatabase.CopyAsset( "Assets/OutdoorsScene.unity", ScenePath ) && ! System.IO.File.Exists( ScenePath ) ) return "could not copy the template scene";
			var scene = EditorSceneManager.OpenScene( ScenePath, OpenSceneMode.Single );

			// terrain
			var t = GameObject.Find( "Terrain" ) ?? new GameObject( "Terrain" );
			if ( t.GetComponent<TerrainRenderer>() == null ) t.AddComponent<TerrainRenderer>();

			// placeholder sea: a flat plane at sea level
			var sea = GameObject.Find( "Sea (placeholder)" );
			if ( sea == null )
			{
				sea = GameObject.CreatePrimitive( PrimitiveType.Plane );
				sea.name = "Sea (placeholder)";
				Object.DestroyImmediate( sea.GetComponent<Collider>() );
				sea.transform.localScale = new Vector3( 400, 1, 400 ); // 4 km square
				var mat = new Material( Shader.Find( "HDRP/Lit" ) ) { name = "SeaPlaceholder" };
				mat.SetColor( "_BaseColor", new Color( 0.01f, 0.12f, 0.2f ) );
				mat.SetFloat( "_Smoothness", 0.92f );
				AssetDatabase.CreateAsset( mat, "Assets/Tidewater/Scenes/SeaPlaceholder.mat" );
				sea.GetComponent<MeshRenderer>().sharedMaterial = mat;
				sea.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			}

			// camera: off the beach, looking north at the island (Unity +z = north)
			var cam = Camera.main;
			cam.transform.SetPositionAndRotation( new Vector3( 60, 12, - 150 ), Quaternion.Euler( 3, 0, 0 ) );
			cam.farClipPlane = 6000;
			if ( cam.GetComponent<DebugFlyCamera>() == null ) cam.gameObject.AddComponent<DebugFlyCamera>();

			// sun: from the south-west, low in the afternoon
			var sun = GameObject.Find( "Sun" );
			if ( sun != null ) sun.transform.rotation = Quaternion.Euler( 32, 55, 0 );

			EditorSceneManager.MarkSceneDirty( scene );
			EditorSceneManager.SaveScene( scene );
			return "built " + ScenePath;
		}
	}
}

namespace Tidewater.EditorTools
{
	// Camera placement for screenshots / checks, in sim coordinates (x east, z south).
	public static class CameraPlacer
	{
		// eye = metres above the terrain (or above sea level when the ground is lower), yaw 0 = north, 90 = east, pitch > 0 looks down
		public static string Place( double simX, double simZ, double eye, double yawDeg, double pitchDeg )
		{
			var tr = Object.FindAnyObjectByType<TerrainRenderer>();
			double ground = tr != null && tr.data != null ? System.Math.Max( 0, tr.data.HeightAt( simX, simZ ) ) : 0;
			var cam = Camera.main;
			cam.transform.SetPositionAndRotation( Tidewater.Util.Sim.ToUnity( simX, ground + eye, simZ ), Quaternion.Euler( ( float ) pitchDeg, ( float ) yawDeg, 0 ) );
			return $"camera at sim ({simX}, {ground + eye:F1}, {simZ}), ground {ground:F2}";
		}

		public static string Sea( bool visible )
		{
			var sea = GameObject.Find( "Sea (placeholder)" );
			if ( sea != null ) sea.SetActive( visible );
			return "sea " + visible;
		}
	}
}
