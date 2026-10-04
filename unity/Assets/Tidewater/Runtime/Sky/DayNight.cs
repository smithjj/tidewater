using Tidewater.Core;
using Tidewater.Util;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The day-night sky: puts the sun, the moon and the sky's night side where src/App.js updateSun() / applyAtmosphereReadback() put them for the world
// clock's hour. HDRP draws the sky (PhysicallyBasedSky), the sun and moon discs and the clouds, and lights the world; this component feeds them.
//
//   Sun  (the scene's directional light): the real sun, rotated to sunDirectionFromTime( hour ) turned by the azimuth setting. Its intensity stays
//        at the full 11 scene units because the sky's scattering reads it: the JS likewise scatters with the full sun even below the horizon, and
//        only fades the key light (HDRP's planet shadow does that fade here).
//   Moon (a second directional light, made here): the JS moon, which hangs above the horizon whenever the sun is down. It lights the world with
//        (0.6, 0.7, 1) * 0.12 * night, and is the key light (it casts the shadows) when the sun is below -0.07.
//   G.sunDir / G.sunColor / G.night: the key light the water, boats, village and spray shaders read, in sim space and lux (OceanRenderer.PublishSun).
//   Stars and the faint moonlit sky: the space emission of the physically based sky (a cubemap baked from Sky.js skyStars by Tidewater > Bake star
//   cubemap), scaled by the night through a volume of our own that sits above the scene's sky volume and overrides that one number.
//
// Scene units to lux: the sun is 11 scene units in the JS and 130000 lux in HDRP's defaults, so one unit is 130000 / 11 lux. The scene's exposure is
// fixed to match the JS exposure setting (see ExposureEV).
namespace Tidewater.Sky
{
	[ExecuteAlways]
	[DisallowMultipleComponent]
	public sealed class DayNight : MonoBehaviour
	{
		public const double LUX_PER_UNIT = 130000.0 / SkyMath.SUN_ILLUMINANCE;

		public static DayNight instance;

		public double hour = 16.2;      // App.settings.timeOfDay: PlayerHost copies the game clock into it and calls Apply every frame
		public double sunAzimuth = 0;   // App.settings.sunAzimuth (degrees): turns the sun's daily path about the vertical
		public Light sun;
		public Light moon;

		public SkyState state { get; private set; } = new SkyState();

		Cubemap stars; // built from Resources/sky/Stars.bytes (the Editor bakes it: Tidewater > Bake star cubemap)

		Volume driver;
		VolumeProfile driverProfile;
		PhysicallyBasedSky driverSky;
		Exposure driverExposure;
		VolumetricClouds driverClouds;
		float cloudCover = -1, cloudCoverApplied = -1, cloudWind = -1;
		const float COVER_EASE = 8f; // seconds
		UnityEngine.Rendering.HighDefinition.RenderingLayerMask sunLayers; bool sunLayersKept;
		double curveNight = -1;
		HDAdditionalLightData sunData, moonData;
		readonly Tidewater.Engine.Vector3 keyCol = new Tidewater.Engine.Vector3();

		// the EV100 that gives the JS exposure setting (App.settings.exposure, 0.55): display = scene units * exposure, and HDRP's is
		// nits / ( 1.2 * 2^EV ) with a scene unit worth LUX_PER_UNIT nits
		public static double ExposureEV( double exposure = 0.55 ) => System.Math.Log( LUX_PER_UNIT / ( 1.2 * exposure ), 2 );

		// HDRP's exposure meter reports the scene's EV100 = log2( luminance[nits] * 100 / 12.5 ); one scene unit is LUX_PER_UNIT nits
		static double SceneEV( double avgUnits ) => System.Math.Log( avgUnits * LUX_PER_UNIT * 8.0, 2 );
		static double AvgUnits( double sceneEV ) => System.Math.Pow( 2, sceneEV ) / ( LUX_PER_UNIT * 8.0 );
		public const double EV_MIN = 0, EV_MAX = 22; // the domain of the exposure curve (HDRP: Limit Min / Max)

		// The JS eye as an HDRP exposure curve: scene EV100 (x) -> the EV100 to expose at (y), sampled from SkyMath.exposureTarget so the two stay one law:
		// the display is scene * exposure * m, m = exposureTarget( average luminance, night ), and HDRP's is nits / ( 1.2 * 2^EV )
		public static AnimationCurve ExposureCurve( double night, double exposure = 0.55 )
		{
			double ev0 = ExposureEV( exposure );
			// a key every 1/4 EV and at each kink of the law (the eye's reference, and where it reaches its floor and its ceiling), so the curve is the law
			var ts = new System.Collections.Generic.List<double>();
			for ( int i = 0; i <= 88; i ++ ) ts.Add( EV_MIN + ( EV_MAX - EV_MIN ) * i / 88 );
			double ceiling = Tidewater.Engine.MathUtils.lerp( SkyMath.AE_MAX, 2.0, night );
			foreach ( double ratio in new[] { 1.0, SkyMath.AE_MIN, System.Math.Pow( ceiling, 1.25 ) } ) ts.Add( SceneEV( SkyMath.AE_REF_LUM / ratio ) );
			ts.Sort();
			int n = ts.Count;
			var keys = new Keyframe[ n ];
			double[] t = ts.ToArray(), v = new double[ n ];
			for ( int i = 0; i < n; i ++ ) v[ i ] = ev0 - System.Math.Log( SkyMath.exposureTarget( AvgUnits( t[ i ] ), night ), 2 );

			// straight segments: each key's tangents are the slopes of the segments on either side
			for ( int i = 0; i < n; i ++ )
			{
				float inT = i > 0 ? ( float ) ( ( v[ i ] - v[ i - 1 ] ) / ( t[ i ] - t[ i - 1 ] ) ) : 0f;
				float outT = i < n - 1 ? ( float ) ( ( v[ i + 1 ] - v[ i ] ) / ( t[ i + 1 ] - t[ i ] ) ) : 0f;
				keys[ i ] = new Keyframe( ( float ) t[ i ], ( float ) v[ i ], inT, outT );
			}

			return new AnimationCurve( keys );
		}

		public static double SceneEVOf( double avgUnits ) => SceneEV( avgUnits );

		void OnEnable()
		{
			instance = this;
			Find();
			// (the volume of ours is destroyed with every script reload: make it again, and publish the key light, without moving the scene's lights)
			var s = SkyMath.At( hour, sunAzimuth, state );
			Publish( s );
			ApplyVolume( s );
		}

		void OnDisable()
		{
			if ( instance == this ) { instance = null; G.skyDriven = false; }
			if ( driver != null ) DestroyImmediate( driver.gameObject );
			if ( driverProfile != null ) DestroyImmediate( driverProfile );
			if ( stars != null ) DestroyImmediate( stars );
		}

		// the baked night sky as a cubemap: "TWSTARS1", n, six BC6H faces
		static Cubemap LoadStars()
		{
			var ta = Resources.Load<TextAsset>( "sky/Stars" );
			if ( ta == null ) return null;
			var data = ta.GetData<byte>();
			int n = System.BitConverter.ToInt32( data.ToArray(), 8 );
			int face = ( data.Length - 12 ) / 6;
			var cube = new Cubemap( n, TextureFormat.BC6H, false ) { name = "Stars", hideFlags = HideFlags.DontSave };
			for ( int f = 0; f < 6; f ++ ) cube.SetPixelData( data.GetSubArray( 12 + f * face, face ), 0, ( CubemapFace ) f );
			cube.Apply( false, true );
			Resources.UnloadAsset( ta );
			return cube;
		}

		void Find()
		{
			if ( sun == null ) sun = RenderSettings.sun != null ? RenderSettings.sun : FindAnyObjectByType<Light>();
			if ( sun != null ) sunData = sun.GetComponent<HDAdditionalLightData>();
			// the sun's disc: radius 0.004675 * 1.15 rad (SkyMath.SUN_ANGULAR_RADIUS), as a diameter in degrees
			if ( sunData != null ) sunData.angularDiameter = ( float ) ( 2 * SkyMath.SUN_ANGULAR_RADIUS * 180 / System.Math.PI );
			if ( moon == null )
			{
				var t = transform.Find( "Moon" );
				if ( t != null ) moon = t.GetComponent<Light>();
			}

			if ( moon != null ) moonData = moon.GetComponent<HDAdditionalLightData>();
		}

		// the moon, once: a directional light under this object (kept in the scene so the Editor shows it). Editor tools call this when they build the scene.
		public Light EnsureMoon()
		{
			Find();
			if ( moon != null ) return moon;
			var go = new GameObject( "Moon" );
			go.transform.SetParent( transform, false );
			moon = go.AddComponent<Light>();
			moon.type = LightType.Directional;
			var hd = go.AddComponent<HDAdditionalLightData>();
			hd.SetIntensity( 0, LightUnit.Lux );
			moon.color = new Color( 0.6f, 0.7f, 1f );
			moon.shadows = LightShadows.None;
			hd.interactsWithSky = true;
			hd.angularDiameter = 0.55f; // Sky.js skyMoon: a disc of radius 0.0048 rad
			moonData = hd;
			return moon;
		}

		public void SetHour( double h ) { hour = ( ( h % 24 ) + 24 ) % 24; Apply(); }

		// everything that follows from `hour`
		public void Apply()
		{
			if ( sun == null || moon == null ) Find();
			if ( sun == null ) return;
			var s = SkyMath.At( hour, sunAzimuth, state );

			// the lights point against the direction toward them; sim space -> Unity world is (x, y, -z)
			sun.transform.rotation = LookingFrom( s.sun );
			Publish( s );

			// the sun: always full strength (the sky scatters with it); HDRP's planet shadow removes it from the world below the horizon
			SetLight( sun, sunData, ( float ) ( SkyMath.SUN_ILLUMINANCE * LUX_PER_UNIT ), s.sunIsKey );
			// once the moon is the key light the sun lights nothing (it would light undersides from below the planet), and still scatters in the sky
			if ( sunData != null )
			{
				if ( ! sunLayersKept ) { sunLayers = sunData.lightlayersMask; sunLayersKept = true; }
				sunData.lightlayersMask = s.sunIsKey ? sunLayers : ( UnityEngine.Rendering.HighDefinition.RenderingLayerMask ) 0;
			}
			if ( moon != null )
			{
				moon.transform.rotation = LookingFrom( s.moon );
				SetLight( moon, moonData, ( float ) ( 0.12 * s.night * LUX_PER_UNIT ), ! s.sunIsKey );
			}

			ApplyVolume( s );
		}

		// G.sunDir / G.sunColor / G.night: the key light as the shaders read it
		void Publish( SkyState s )
		{
			var keyU = s.key;
			G.skyDriven = true;
			G.night = ( float ) s.night;
			G.sunDir = new Vector3( ( float ) keyU.x, ( float ) keyU.y, ( float ) keyU.z );
			SkyMath.keyColor( s, keyCol );
			G.sunColor = new Vector3( ( float ) ( keyCol.x * LUX_PER_UNIT ), ( float ) ( keyCol.y * LUX_PER_UNIT ), ( float ) ( keyCol.z * LUX_PER_UNIT ) );
		}

		static Quaternion LookingFrom( Tidewater.Engine.Vector3 towards )
		{
			var u = Sim.ToUnity( towards.x, towards.y, towards.z );
			return Quaternion.LookRotation( - u, Vector3.up );
		}

		static void SetLight( Light l, HDAdditionalLightData hd, float lux, bool shadows )
		{
			if ( hd != null ) hd.SetIntensity( lux, LightUnit.Lux ); else l.intensity = lux;
			l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
			l.enabled = lux > 0.5f;
		}

		// the night's share of the sky: the stars and the moonlit sky, scaled the way Sky.js scales them (starIntensity = night, the faint stars
		// only once the sun is well down: `dark`)
		void ApplyVolume( SkyState s )
		{
			if ( driver == null ) MakeDriver();
			if ( driverSky == null ) return;
			double stars = s.night * SkyMath.starDark( s.sun.y );
			driverSky.spaceEmissionMultiplier.Override( ( float ) ( stars * LUX_PER_UNIT ) );

			// the clouds: the sea state's cover, and the wind that drifts them. The weather writes the cover in whole steps (Weather.js: a step drops the JS clouds'
			// temporal history), where HDRP's clouds would reshape in a frame, so the cover shown eases toward it (~8 s); in the Editor it snaps
			if ( driverClouds != null )
			{
				float target = G.cover;
				if ( cloudCover < 0 || ! Application.isPlaying ) cloudCover = target;
				else cloudCover += ( target - cloudCover ) * ( 1 - Mathf.Exp( - Time.deltaTime / COVER_EASE ) );
				if ( System.Math.Abs( cloudCover - cloudCoverApplied ) > 0.002f || System.Math.Abs( G.windSpeed - cloudWind ) > 0.05f )
				{
					cloudCoverApplied = cloudCover; cloudWind = G.windSpeed;
					driverClouds.shapeFactor.Override( 1.0f - 0.55f * cloudCover ); // HDRP: the lower the shape factor, the more sky is cloud
					driverClouds.densityMultiplier.Override( 0.25f + 0.3f * cloudCover );
					// the wind blows toward G.windDir (sim xz): Unity world is (x, -z); HDRP's orientation is degrees counter-clockwise from +x (not yet checked by eye)
					driverClouds.globalWindSpeed = new WindSpeedParameter( cloudWind, WindParameter.WindOverrideMode.Custom, true );
					driverClouds.orientation = new WindOrientationParameter( ( float ) ( System.Math.Atan2( - G.windDir.y, G.windDir.x ) * 180 / System.Math.PI + 360 ) % 360, WindParameter.WindOverrideMode.Custom, true );
				}
			}

			// the eye: its ceiling follows the night
			if ( driverExposure != null && System.Math.Abs( s.night - curveNight ) > 0.002 )
			{
				curveNight = s.night;
				driverExposure.curveMap.Override( ExposureCurve( s.night ) );
			}
		}

		void MakeDriver()
		{
			var go = new GameObject( "Sky Driver" ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( transform, false );
			driver = go.AddComponent<Volume>();
			driver.isGlobal = true;
			driver.priority = 100; // above the scene's sky volume (priority 0)
			driverProfile = ScriptableObject.CreateInstance<VolumeProfile>();
			driverProfile.hideFlags = HideFlags.DontSave;
			driverSky = driverProfile.Add<PhysicallyBasedSky>( false ); // (no parameter is overridden until we ask)
			driverSky.active = true;
			// the clouds: only the cover and the wind are ours, the rest is the scene profile's (SkySetup)
			driverClouds = driverProfile.Add<VolumetricClouds>( false );
			driverClouds.active = true;
			cloudCover = -1; cloudCoverApplied = -1; cloudWind = -1;
			if ( stars == null ) stars = LoadStars();
			if ( stars != null ) driverSky.spaceEmissionTexture.Override( stars );

			// PostFX.js auto exposure: metered on the centre-weighted log average of the scene, adapting at 1.6 / s toward more exposure and 1.1 / s toward less
			driverExposure = driverProfile.Add<Exposure>( false );
			driverExposure.active = true;
			driverExposure.mode.Override( ExposureMode.CurveMapping );
			driverExposure.meteringMode.Override( MeteringMode.CenterWeighted );
			driverExposure.luminanceSource.Override( LuminanceSource.ColorBuffer );
			driverExposure.limitMin.Override( ( float ) EV_MIN );
			driverExposure.limitMax.Override( ( float ) EV_MAX );
			driverExposure.compensation.Override( 0 );
			driverExposure.adaptationMode.Override( AdaptationMode.Progressive );
			driverExposure.adaptationSpeedDarkToLight.Override( 1.1f );
			driverExposure.adaptationSpeedLightToDark.Override( 1.6f );
			curveNight = -1;
			driver.sharedProfile = driverProfile;
		}
	}
}
