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

			// the underwater lighting of lit surfaces (after the opaque lighting) and the underwater composite (before the
			// post-processing): HDRP custom passes
			var passes = GameObject.Find( "Underwater Passes" ) ?? new GameObject( "Underwater Passes" );
			var vol = passes.GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>() ?? passes.AddComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			vol.isGlobal = true;
			vol.injectionPoint = UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.BeforePreRefraction;
			if ( vol.customPasses.Count == 0 ) vol.customPasses.Add( new UnderwaterLightingPass() );
			var comp = GameObject.Find( "Underwater Composite" ) ?? new GameObject( "Underwater Composite" );
			var cvol = comp.GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>() ?? comp.AddComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			cvol.isGlobal = true;
			cvol.injectionPoint = UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.BeforePostProcess;
			if ( cvol.customPasses.Count == 0 ) cvol.customPasses.Add( new UnderwaterCompositePass() );

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

namespace Tidewater.EditorTools
{
	public static class PassDebug
	{
		// the debug view runs after the post-processing, so its colours are not tonemapped
		public static string Debug( bool on )
		{
			UnderwaterLightingPass.debug = on;
			var v = GameObject.Find( "Underwater Passes" ).GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			if ( v != null ) v.injectionPoint = on ? UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.AfterPostProcess : UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.BeforePreRefraction;
			return "underwater debug " + on;
		}

		// the baked maps against the camera's own water query; the report is written to Temp/bakecheck.txt
		public static string BakeCheck()
		{
			var o = Object.FindAnyObjectByType<Tidewater.Ocean.OceanRenderer>();
			var ul = o.underwaterLighting;
			if ( ul == null ) return "no underwater lighting";
			var cam = Camera.main.transform.position;
			float sx = cam.x, sz = -cam.z;
			var q = o.query.Get( 0 );
			var origin = ul.Origin( 0 ); float texel = ul.Texel( 0 );
			var path = System.IO.Path.GetFullPath( System.IO.Path.Combine( Application.dataPath, "../Temp/bakecheck.txt" ) );
			System.IO.File.WriteAllText( path, "pending" );
			UnityEngine.Rendering.AsyncGPUReadback.Request( ul.WavesMap( 0 ), 0, UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, ra =>
			{
				var d = ra.GetData<ushort>();
				float H( int i ) => Mathf.HalfToFloat( d[ i ] );
				int tx = Mathf.Clamp( Mathf.FloorToInt( ( sx - origin.x ) / texel ), 0, 511 ), tz = Mathf.Clamp( Mathf.FloorToInt( ( sz - origin.y ) / texel ), 0, 511 );
				int i0 = ( tz * 512 + tx ) * 4;
				double sum = 0, sum2 = 0, mn = 1e9, mx = -1e9, foam = 0; int land = 0;
				for ( int i = 0; i < 512 * 512; i ++ )
				{
					float h = H( i * 4 );
					if ( h < -5 ) { land ++; continue; }
					sum += h; sum2 += h * h; mn = System.Math.Min( mn, h ); mx = System.Math.Max( mx, h ); foam += H( i * 4 + 3 );
				}
				int n = 512 * 512 - land;
				System.IO.File.WriteAllText( path, $"camera sim ({sx:F2},{sz:F2}) texel ({tx},{tz})\nbaked height-sea at camera {H( i0 ):F4}  slope ({H( i0 + 1 ):F4},{H( i0 + 2 ):F4}) foam {H( i0 + 3 ):F4}\nquery height at camera {q.height:F4} (sea level {Tidewater.Core.G.seaLevel:F3}) nx {q.nx:F3} nz {q.nz:F3} floor {q.floor:F2}\nnear map: water texels {n}, land {land}, mean {sum / System.Math.Max( n, 1 ):F4} rms {System.Math.Sqrt( sum2 / System.Math.Max( n, 1 ) ):F4} min {mn:F3} max {mx:F3} foam mean {foam / System.Math.Max( n, 1 ):F4}" );
			} );
			return "scheduled";
		}

		// adds the pass volumes to the open scene without rebuilding it (what Build does for them)
		public static string AddPasses()
		{
			var passes = GameObject.Find( "Underwater Passes" ) ?? new GameObject( "Underwater Passes" );
			var vol = passes.GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>() ?? passes.AddComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			vol.isGlobal = true;
			vol.injectionPoint = UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.BeforePreRefraction;
			if ( vol.customPasses.Count == 0 ) vol.customPasses.Add( new UnderwaterLightingPass() );
			var comp = GameObject.Find( "Underwater Composite" ) ?? new GameObject( "Underwater Composite" );
			var cvol = comp.GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>() ?? comp.AddComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			cvol.isGlobal = true;
			cvol.injectionPoint = UnityEngine.Rendering.HighDefinition.CustomPassInjectionPoint.BeforePostProcess;
			if ( cvol.customPasses.Count == 0 ) cvol.customPasses.Add( new UnderwaterCompositePass() );
			UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty( comp.scene );
			UnityEditor.SceneManagement.EditorSceneManager.SaveScene( comp.scene );
			return "passes added, scene saved";
		}

		// the composite pass on / off
		public static string Composite( bool on )
		{
			var g = GameObject.Find( "Underwater Composite" );
			if ( g != null ) g.GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>().enabled = on;
			return "underwater composite " + on;
		}

		public static string Underwater( bool on )
		{
			var v = GameObject.Find( "Underwater Passes" ).GetComponent<UnityEngine.Rendering.HighDefinition.CustomPassVolume>();
			if ( v != null ) v.enabled = on;
			return "underwater pass " + on;
		}
	}
}

namespace Tidewater.EditorTools
{
	// Editor test bursts of spray (the Editor does not tick the ocean when not playing: Advance steps it)
	public static class SprayDebug
	{
		// one of each kind above sim position (x, y, z), then run the sea forward `after` seconds
		public static string Burst( double x, double y, double z, double after )
		{
			var o = Tidewater.Ocean.OceanRenderer.instance;
			if ( o == null || o.spray == null ) return "no spray";
			var sp = o.spray;
			var p = new Vector3( ( float ) x, ( float ) y, ( float ) z );
			var up = new Vector3( 0, 5, 0 );
			sp.Emit( p + new Vector3( -3, 0, 0 ), up, 600, 0.012f, Tidewater.Fx.SprayKind.DROPLET, new Tidewater.Fx.SprayEmitOptions { spread = 2.5f, jitter = 0.4f } );
			sp.Emit( p + new Vector3( -1, 0, 0 ), up, 120, 0.03f, Tidewater.Fx.SprayKind.LIGAMENT, new Tidewater.Fx.SprayEmitOptions { spread = 2.0f, jitter = 0.3f } );
			sp.Emit( p + new Vector3( 1, 0, 0 ), up * 0.6f, 60, 0.35f, Tidewater.Fx.SprayKind.SPRAY, new Tidewater.Fx.SprayEmitOptions { spread = 1.5f, jitter = 0.5f } );
			sp.Emit( p + new Vector3( 3, 0, 0 ), up * 0.2f, 40, 0.8f, Tidewater.Fx.SprayKind.MIST, new Tidewater.Fx.SprayEmitOptions { spread = 0.8f, jitter = 0.8f } );
			sp.Emit( p + new Vector3( 5, 0, 0 ), new Vector3( 0, 3, 0 ), 30, 0.5f, Tidewater.Fx.SprayKind.SHEET, new Tidewater.Fx.SprayEmitOptions { spread = 1.2f, jitter = 0.3f } );
			o.Advance( ( float ) after );
			return "burst at " + p;
		}
	}
}

namespace Tidewater.EditorTools
{
	public static class BreakersDebug
	{
		// Frames the biggest crest whose lip is in the air (b between lo and hi): the camera sits seaward of it, `dist` m out and
		// `side` m along the crest, `eye` m above the sea, looking at the lip root.
		public static string Frame( double lo, double hi, double dist, double side, double eye )
		{
			var o = Tidewater.Ocean.OceanRenderer.instance;
			if ( o == null || o.breakers == null ) return "no breakers";
			var data = new Vector4[ o.breakers.NS * 6 ];
			o.breakers.crestBuffer.GetData( data );
			int bestI = -1, bestS = 0; float bestH = 0;
			for ( int i = 0; i < o.breakers.NS; i ++ ) for ( int s = 0; s < 2; s ++ )
			{
				var c0 = data[ i * 6 + s * 3 ]; var c1 = data[ i * 6 + s * 3 + 1 ]; var c2 = data[ i * 6 + s * 3 + 2 ];
				if ( c2.w < 0.5 || c0.w < lo || c0.w > hi ) continue;
				if ( c1.w > bestH ) { bestH = c1.w; bestI = i; bestS = s; }
			}

			if ( bestI < 0 ) return "no crest with b in [" + lo + ", " + hi + "]";
			var r = data[ bestI * 6 + bestS * 3 ]; var rd = data[ bestI * 6 + bestS * 3 + 2 ];
			var root = new Vector3( r.x, r.y, r.z );
			var d3 = new Vector3( rd.x, 0, rd.y ); var tg = new Vector3( -rd.y, 0, rd.x );
			var cam = root - d3 * ( float ) dist + tg * ( float ) side; cam.y = root.y + ( float ) eye;
			var c = Camera.main;
			var look = Tidewater.Util.Sim.ToUnity( root.x, root.y, root.z );
			c.transform.position = Tidewater.Util.Sim.ToUnity( cam.x, cam.y, cam.z );
			c.transform.rotation = Quaternion.LookRotation( ( look - c.transform.position ).normalized, Vector3.up );
			return $"station {bestI} slot {bestS}: root ({root.x:F1},{root.y:F2},{root.z:F1}) b {r.w:F2} H {bestH:F2}, camera at ({cam.x:F1},{cam.y:F2},{cam.z:F1})";
		}

		// active crests: count, their breaking progress b range, and where the most advanced plunging one is
		public static string Stats()
		{
			var o = Tidewater.Ocean.OceanRenderer.instance;
			if ( o == null || o.breakers == null ) return "no breakers";
			var data = new Vector4[ o.breakers.NS * 6 ];
			o.breakers.crestBuffer.GetData( data );
			int active = 0, plunging = 0; float bMin = 9, bMax = -9; int bestI = -1; float bestB = -9;
			for ( int i = 0; i < o.breakers.NS; i ++ ) for ( int s = 0; s < 2; s ++ )
			{
				var c0 = data[ i * 6 + s * 3 ]; var c2 = data[ i * 6 + s * 3 + 2 ];
				if ( c2.w < 0.5 ) continue;
				active ++;
				bMin = Mathf.Min( bMin, c0.w ); bMax = Mathf.Max( bMax, c0.w );
				if ( c0.w > 0.35 && c0.w < 1.25 ) { plunging ++; if ( c0.w > bestB ) { bestB = c0.w; bestI = i; } }
			}
			string where = "";
			if ( bestI >= 0 ) { var r = data[ bestI * 6 ]; var r2 = data[ bestI * 6 + 1 ]; where = $" | most advanced: station {bestI} root ({r.x:F1},{r.y:F2},{r.z:F1}) b {r.w:F2} H {r2.w:F2}"; }
			return $"{o.breakers.NS} stations, {active} active crests, {plunging} with a lip (b .35..1.25), b {bMin:F2}..{bMax:F2}{where}";
		}
	}
}
