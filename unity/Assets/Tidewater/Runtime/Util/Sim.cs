using UnityEngine;

// Coordinate boundary between the simulation and Unity.
//
// All ported simulation code keeps the three.js axes: x east, y up, z south (toward the open ocean), right
// handed. Unity is left handed, so the render side mirrors z: a sim point (x, y, z) is the Unity point
// (x, y, -z), i.e. Unity +z points north. Mirroring z (rather than swapping x and z) keeps east on +x and the
// sun's path unchanged. Shaders do the same flip when they turn a world position into sim coordinates.
namespace Tidewater.Util
{
	public static class Sim
	{
		public static Vector3 ToUnity( Vector3 p ) => new Vector3( p.x, p.y, - p.z );
		public static Vector3 ToUnity( double x, double y, double z ) => new Vector3( ( float ) x, ( float ) y, ( float ) - z );
		public static Vector3 FromUnity( Vector3 p ) => new Vector3( p.x, p.y, - p.z );
		public static Vector3 DirToUnity( Vector3 d ) => new Vector3( d.x, d.y, - d.z );
	}
}
