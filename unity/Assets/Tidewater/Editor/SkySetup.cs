using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// Sets up the scene's sky volume profile (Assets/Settings/SkyandFogSettingsProfile.asset) for the day-night sky (Runtime/Sky/DayNight.cs): the sky lights the
// world dynamically, DayNight adds the stars, and the picture is tone mapped like the JS (ACES). Safe to run again.
//   Tidewater > Set up sky profile
namespace Tidewater.EditorTools
{
	public static class SkySetup
	{
		const string ProfilePath = "Assets/Settings/SkyandFogSettingsProfile.asset";

		[MenuItem( "Tidewater/Set up sky profile" )]
		public static string Apply()
		{
			var p = AssetDatabase.LoadAssetAtPath<VolumeProfile>( ProfilePath );
			if ( p == null ) return "no profile at " + ProfilePath;
			var log = new System.Text.StringBuilder();

			if ( p.TryGet( out VisualEnvironment ve ) )
			{
				// the sky lights the world and the reflections as it is now (the clock moves it); "Static" would keep the baked environment
				ve.skyAmbientMode.Override( SkyAmbientMode.Dynamic );
				log.Append( "ambient dynamic; " );
			}

			if ( p.TryGet( out PhysicallyBasedSky pbs ) )
			{
				// the stars and their strength come from DayNight (a volume of its own above this one); none here
				pbs.spaceEmissionTexture.overrideState = false;
				pbs.spaceEmissionMultiplier.overrideState = false;
				log.Append( "stars left to DayNight; " );
			}

			// the clouds: HDRP's volumetric clouds, lit by the sun and moon (PORTING.md: "Clouds")
			// (cloudType 2 is HDRP's volumetric clouds: CloudType only names the cloud layer)
			if ( p.TryGet( out VisualEnvironment ve2 ) ) ve2.cloudType.Override( 2 );
			if ( ! p.TryGet( out VolumetricClouds vc ) ) vc = p.Add<VolumetricClouds>( false );
			vc.active = true;
			vc.enable.Override( true );
			log.Append( "clouds on; " );

			// tone mapping: PostFX.js maps the exposed scene through ACES filmic (the exposure itself is DayNight's: the JS eye)
			if ( ! p.TryGet( out Tonemapping tm ) ) tm = p.Add<Tonemapping>( false );
			tm.active = true;
			tm.mode.Override( TonemappingMode.ACES );
			log.Append( "ACES; " );

			EditorUtility.SetDirty( p );
			AssetDatabase.SaveAssets();
			return log.ToString();
		}
	}
}
