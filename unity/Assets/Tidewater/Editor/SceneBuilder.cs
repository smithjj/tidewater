using Tidewater.Ocean;
using Tidewater.Player;
using Tidewater.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds the island scene from the HDRP template's OutdoorsScene (camera, sun, sky and fog volume): adds the
// terrain, the ocean and a debug fly camera.
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

			// the sea (replaces the flat placeholder plane the scene had before the ocean was ported)
			var old = GameObject.Find( "Sea (placeholder)" );
			if ( old != null ) Object.DestroyImmediate( old );
			AssetDatabase.DeleteAsset( "Assets/Tidewater/Scenes/SeaPlaceholder.mat" );
			var sea = GameObject.Find( "Ocean" ) ?? new GameObject( "Ocean" );
			if ( sea.GetComponent<OceanRenderer>() == null ) sea.AddComponent<OceanRenderer>();

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

		// Fixed exposure (EV100) for repeatable screenshots, on an in-memory copy of the volume profile (the asset is not
		// touched); NaN restores the profile's own (automatic) exposure.
		static UnityEngine.Rendering.VolumeProfile original;
		public static string Exposure( float ev )
		{
			var v = Object.FindAnyObjectByType<UnityEngine.Rendering.Volume>();
			if ( v == null ) return "no volume";
			if ( original == null ) original = v.sharedProfile;
			if ( float.IsNaN( ev ) ) { v.profile = null; v.sharedProfile = original; return "automatic exposure"; }
			var p = Object.Instantiate( original );
			if ( p.TryGet( out UnityEngine.Rendering.HighDefinition.Exposure e ) )
			{
				e.mode.Override( UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed );
				e.fixedExposure.Override( ev );
			}

			v.sharedProfile = p;
			return "fixed exposure EV " + ev;
		}

		public static string Sea( bool visible )
		{
			var sea = GameObject.Find( "Ocean" );
			if ( sea != null ) sea.SetActive( visible );
			return "sea " + visible;
		}
	}
}

namespace Tidewater.EditorTools
{
	// Debug switches for isolating a renderer in screenshots.
	public static class OceanDebug
	{
		// run the sea forward (the Editor does not tick the ocean when not playing)
		public static string Advance( float seconds )
		{
			var o = Object.FindAnyObjectByType<OceanRenderer>();
			if ( o == null ) return "no ocean";
			o.Advance( seconds );
			return "advanced " + seconds + " s";
		}

		public static string State( float seaState )
		{
			var o = Object.FindAnyObjectByType<OceanRenderer>();
			if ( o != null ) o.seaState = seaState;
			return "sea state " + seaState;
		}

		public static string View( int v )
		{
			var o = Object.FindAnyObjectByType<OceanRenderer>();
			if ( o != null ) o.debugView = v;
			return "ocean debug view " + v;
		}
	}

	public static class Isolate
	{
		public static string Set( bool terrain, bool ocean )
		{
			var t = Object.FindAnyObjectByType<TerrainRenderer>();
			var o = Object.FindAnyObjectByType<OceanRenderer>();
			if ( t != null ) t.enabled = terrain;
			if ( o != null ) o.enabled = ocean;
			return $"terrain {terrain}, ocean {ocean}";
		}
	}
}
