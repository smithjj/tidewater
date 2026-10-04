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

		// The boat's pose. Its sim frame is right-handed (+Z forward, +X port); the Unity boat meshes are mirrored in x (local x = -x), and the
		// world mirrors z, so the Unity rotation is R_u = M R S with M = diag(1, 1, -1), S = diag(-1, 1, 1): M = Rz(180) * -1 and S = Rx(180) * -1
		// give R_u = Rz(180) R Rx(180), a proper rotation (a point p at sim position R p + t is at Unity R_u (S p) + M t).
		public static Quaternion BoatToUnity( Tidewater.Engine.Quaternion q )
		{
			var t = new Tidewater.Engine.Quaternion().multiplyQuaternions( new Tidewater.Engine.Quaternion( 0, 0, 1, 0 ), q );
			t.multiplyQuaternions( t, new Tidewater.Engine.Quaternion( 1, 0, 0, 0 ) );
			return new Quaternion( ( float ) t.x, ( float ) t.y, ( float ) t.z, ( float ) t.w );
		}
	}
}
