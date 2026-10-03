using UnityEngine;

// Port of src/world/WorldLayout.js: shared world layout. Coordinates in meters, y up, sea level y = 0.
// The open ocean lies to the south (+z); the island to the north (-z).
// Sun rises in the east (+x) and sets in the west (-x).
//
// The three.js world is right-handed and Unity's is left-handed; the port keeps the three.js
// coordinates (x east, y up, z south) in all simulation code and mirrors only at the render boundary.
namespace Tidewater.World
{
	public static class WorldLayout
	{
		public const double TerrainSize = 2048; // heightmap domain, centered at origin
		public const int TerrainRes = 2048;

		// Central sandy beach inside the bay, shoreline near z ≈ -42 at x = 0.
		public const double BeachXMin = - 150, BeachXMax = 170;

		public static class Pier
		{
			public const double x = 55;
			public const double zStart = - 64; // on dry sand
			public const double zEnd = 40; // end of pier (~4 m depth)
			public const double deckHeight = 2.3; // deck surface above sea level
			public const double width = 2.6;
			public const double headWidth = 14; // T-shaped platform at the end
			public const double headDepth = 7;
		}

		// Where the boat is moored: east side of the pier head, bow pointing south.
		public static readonly Vector3 BoatDockPosition = new Vector3( 64.5f, 0, 36.5f );
		public const double BoatDockHeading = 0;

		// A second boat (Pelagic 30) moored off the west side of the pier head.
		public static readonly Vector3 PelagicMooringPosition = new Vector3( 43.5f, 0, 38 );
		public const double PelagicMooringHeading = - 0.35;

		public static class Village
		{
			public const double x = 40, z = - 118, radius = 95;
		}

		public static class Reef
		{
			public const double x = - 78, z = 58, radius = 58;
		}

		// kept clear of rocks, plants and debris
		public static readonly Vector3 SpawnPosition = new Vector3( 18, 0, - 60 );
		public const double SpawnYaw = System.Math.PI;
		// where the player starts: on the boardwalk up from the pier foot, looking down it toward the pier
		public static readonly Vector3 StartPosition = new Vector3( 53.6f, 0, - 77 );
		public const double StartYaw = System.Math.PI;

		// Incoming swell direction (unit, travel direction)
		public static readonly Vector2 SwellDir = new Vector2( - 0.12f, - 1 ).normalized;
		// the same in doubles (the shore field is computed with them)
		public static readonly double SwellDirX = - 0.12 / System.Math.Sqrt( 0.12 * 0.12 + 1 ), SwellDirZ = - 1 / System.Math.Sqrt( 0.12 * 0.12 + 1 );
	}
}
