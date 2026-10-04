using UnityEngine;

// Port of the frame globals `G` (src/engine/render/Frame.js) that the ported systems share. Values are in SIM space
// (see Util/Sim.cs). Systems read them from here and publish what their shaders need themselves.
namespace Tidewater.Core
{
	public static class G
	{
		public static float time;
		public static float dt = 1f / 60f;

		// 0 by day, 1 with the sun well below the horizon (App.js G.night): written by the day-night sky (Sky/DayNight.cs); without one,
		// by OceanRenderer from the sun light
		public static float night;

		// the key light (App.js G.sunDir / G.sunColor): the sun, or the moon once the sun is well below the horizon. Direction in SIM space,
		// colour as illuminance in lux. Written by DayNight when it is driving the sky (skyDriven); the water and the other shaders read them
		// through OceanRenderer.PublishSun
		public static bool skyDriven;
		public static Vector3 sunDir = new Vector3( 0.3f, 0.8f, 0.5f ).normalized;
		public static Vector3 sunColor = new Vector3( 130000, 130000, 130000 );

		public static float seaLevel = 0;
		// wind blowing toward (sim xz) and its speed at 10 m (m/s): written by the sea conditions (Conditions.WriteConditions)
		public static Vector2 windDir = new Vector2( 0.35f, 0.94f ).normalized;
		public static float windSpeed = 7;
		// cloud cover 0..1 (Conditions `cover`): written by the sea conditions, read by DayNight for the clouds
		public static float cover = 0.45f;

		// water volume: absorption / scattering coefficients (1 / m)
		public static Vector3 waterAbsorption = new Vector3( 0.42f, 0.075f, 0.035f );
		public static Vector3 waterScattering = new Vector3( 0.012f, 0.018f, 0.024f );
		// the water height at the camera (WaterQuery slot 0 once ported); frame.cameraWaterHeight
		public static float cameraWaterHeight = 0;
	}
}
