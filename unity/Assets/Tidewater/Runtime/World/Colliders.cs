using System;
using System.Collections.Generic;
using Tidewater.Engine;

// Port of src/world/Colliders.js: lightweight collision world for the character controller and the boat.
// Boxes are oriented around Y only. Walkable boxes (decks, floors, stairs) act as ground. Sim space (three.js axes).
namespace Tidewater.World
{
	public sealed class ColliderBox
	{
		public Vector3 center, half;
		public double rotY, cos, sin, top, bottom, radius;
		public bool walkable, solid;
		public string tag;
	}

	public sealed class ColliderCylinder
	{
		public double x, z, radius, yMin, yMax;
		public string tag;
	}

	public sealed class Colliders
	{
		public readonly List<ColliderBox> boxes = new List<ColliderBox>();
		public readonly List<ColliderCylinder> cylinders = new List<ColliderCylinder>();

		// center: world center, half: half extents (x, y, z) in the box's local frame, rotY: yaw (radians)
		public ColliderBox addBox( Vector3 center, Vector3 half, double rotY = 0, bool walkable = false, bool solid = true, string tag = "" )
		{
			var b = new ColliderBox
			{
				center = center.clone(), half = half.clone(), rotY = rotY,
				cos = Math.Cos( rotY ), sin = Math.Sin( rotY ),
				walkable = walkable, solid = solid, tag = tag,
				top = center.y + half.y, bottom = center.y - half.y,
				radius = JS.Hypot( half.x, half.z ),
			};
			boxes.Add( b );
			return b;
		}

		public ColliderCylinder addCylinder( double x, double z, double radius, double yMin, double yMax, string tag = "" )
		{
			var c = new ColliderCylinder { x = x, z = z, radius = radius, yMin = yMin, yMax = yMax, tag = tag };
			cylinders.Add( c );
			return c;
		}

		static void toLocal( ColliderBox b, double x, double z, out double lx, out double lz )
		{
			double dx = x - b.center.x, dz = z - b.center.z;
			lx = dx * b.cos - dz * b.sin; lz = dx * b.sin + dz * b.cos;
		}

		static void toWorldDir( ColliderBox b, double lx, double lz, out double wx, out double wz )
		{
			wx = lx * b.cos + lz * b.sin; wz = - lx * b.sin + lz * b.cos;
		}

		// Highest walkable surface under (x, z) not higher than maxY.
		public double groundHeightAt( double x, double z, double maxY, double pad = 0 )
		{
			double best = double.NegativeInfinity;
			foreach ( var b in boxes )
			{
				if ( ! b.walkable || b.top > maxY ) continue;
				if ( Math.Abs( x - b.center.x ) > b.radius + pad + 0.01 || Math.Abs( z - b.center.z ) > b.radius + pad + 0.01 ) continue;
				toLocal( b, x, z, out var lx, out var lz );
				if ( Math.Abs( lx ) <= b.half.x + pad && Math.Abs( lz ) <= b.half.z + pad ) best = Math.Max( best, b.top );
			}

			return best;
		}

		// Push a vertical capsule (feet at pos.y) out of solid geometry. Returns true if collided.
		public bool resolveCapsule( Vector3 pos, double radius, double height, double stepHeight = 0.35 )
		{
			bool hit = false;
			foreach ( var b in boxes )
			{
				if ( ! b.solid ) continue;
				if ( pos.y + height < b.bottom || pos.y + stepHeight > b.top ) continue;
				if ( Math.Abs( pos.x - b.center.x ) > b.radius + radius || Math.Abs( pos.z - b.center.z ) > b.radius + radius ) continue;
				toLocal( b, pos.x, pos.z, out var lx, out var lz );
				double cx = Math.Max( - b.half.x, Math.Min( b.half.x, lx ) );
				double cz = Math.Max( - b.half.z, Math.Min( b.half.z, lz ) );
				double dx = lx - cx, dz = lz - cz;
				double d2 = dx * dx + dz * dz;
				if ( d2 >= radius * radius ) continue;
				double nx, nz, pen;
				if ( d2 > 1e-8 )
				{
					double d = Math.Sqrt( d2 );
					nx = dx / d; nz = dz / d; pen = radius - d;
				}
				else
				{
					// center inside box: push out along the smallest axis
					double px = b.half.x - Math.Abs( lx ), pz = b.half.z - Math.Abs( lz );
					if ( px < pz ) { nx = JS.Or( JS.Sign( lx ), 1 ); nz = 0; pen = px + radius; } else { nx = 0; nz = JS.Or( JS.Sign( lz ), 1 ); pen = pz + radius; }
				}

				toWorldDir( b, nx, nz, out var wx, out var wz );
				pos.x += wx * pen;
				pos.z += wz * pen;
				hit = true;
			}

			foreach ( var c in cylinders )
			{
				if ( pos.y + height < c.yMin || pos.y + stepHeight > c.yMax ) continue;
				double dx = pos.x - c.x, dz = pos.z - c.z;
				double r = c.radius + radius;
				double d2 = dx * dx + dz * dz;
				if ( d2 >= r * r ) continue;
				double d = JS.Or( Math.Sqrt( d2 ), 1e-4 );
				pos.x = c.x + dx / d * r;
				pos.z = c.z + dz / d * r;
				hit = true;
			}

			return hit;
		}

		// Segment/ray against boxes (XZ-plane rotated) for camera occlusion; returns distance or Infinity.
		public double raycast( Vector3 origin, Vector3 dir, double maxDist )
		{
			double best = maxDist;
			foreach ( var b in boxes )
			{
				if ( ! b.solid ) continue;
				double ox = origin.x - b.center.x, oz = origin.z - b.center.z, oy = origin.y - b.center.y;
				double lox = ox * b.cos - oz * b.sin, loz = ox * b.sin + oz * b.cos;
				double ldx = dir.x * b.cos - dir.z * b.sin, ldz = dir.x * b.sin + dir.z * b.cos;
				double tmin = 0, tmax = best;
				Func<double, double, double, bool> slab = ( o, d, h ) =>
				{
					if ( Math.Abs( d ) < 1e-8 ) return Math.Abs( o ) <= h;
					double t1 = ( - h - o ) / d, t2 = ( h - o ) / d;
					if ( t1 > t2 ) { double t = t1; t1 = t2; t2 = t; }
					tmin = Math.Max( tmin, t1 );
					tmax = Math.Min( tmax, t2 );
					return tmin <= tmax;
				};

				if ( slab( lox, ldx, b.half.x ) && slab( oy, dir.y, b.half.y ) && slab( loz, ldz, b.half.z ) ) best = Math.Min( best, tmin );
			}

			return best;
		}
	}
}
