using UnityEngine;

// Port of the frame globals `G` (src/engine/render/Frame.js) that the ported systems share. Values are in SIM space
// (see Util/Sim.cs). Systems read them from here and publish what their shaders need themselves.
namespace Tidewater.Core
{
	public static class G
	{
		public static float time;
		public static float dt = 1f / 60f;

		public static float seaLevel = 0;
		// wind blowing toward (sim xz) and its speed at 10 m (m/s): written by the sea conditions (Conditions.WriteConditions)
		public static Vector2 windDir = new Vector2( 0.35f, 0.94f ).normalized;
		public static float windSpeed = 7;

		// water volume: absorption / scattering coefficients (1 / m)
		public static Vector3 waterAbsorption = new Vector3( 0.42f, 0.075f, 0.035f );
		public static Vector3 waterScattering = new Vector3( 0.012f, 0.018f, 0.024f );
		// the water height at the camera (WaterQuery slot 0 once ported); frame.cameraWaterHeight
		public static float cameraWaterHeight = 0;
	}
}
