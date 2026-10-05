using UnityEngine;

// The private photo studio of the catch card's fish portrait (Game/FishPortrait.cs) sits far above the island on its own layer. Its camera is a game camera
// like the player's, so the per-camera hooks of the world (ocean, terrain, lights, fish) skip it; the player's cameras skip the studio layer.
namespace Tidewater.Util
{
	public static class Studio
	{
		public const int LAYER = 30;
		public static bool Is( Camera cam ) => cam != null && cam.gameObject.layer == LAYER;
	}
}
