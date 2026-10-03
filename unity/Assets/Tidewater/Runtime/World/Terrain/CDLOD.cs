using System;
using UnityEngine;

// Port of src/core/CDLOD.js: continuous distance-dependent LOD (Strugar 2010) quadtree grid.
// A single G x G grid mesh is instanced for every selected node. Vertices near the outer edge of each LOD
// range geomorph toward the next coarser grid so there are never cracks or pops between levels.
//
// The geomorph itself runs in the vertex shader (Shaders/Terrain/TerrainFragment.hlsl, same arithmetic as the
// WGSL cdlodSnapped / cdlodMorph); this class does the CPU side: the quadtree selection against the camera
// and the frustum, the per-level morph constants and the grid mesh. Node data is (origin x, origin z, size,
// lod) in sim coordinates (see Util/Sim.cs); the selection runs in sim space and mirrors only the camera and
// the frustum boxes into Unity space.
namespace Tidewater.World.Terrain
{
	public sealed class CDLOD
	{
		public delegate void HeightBounds( double x0, double z0, double x1, double z1, out double min, out double max );

		public readonly int G; // quads per node side (even)
		public readonly double leafSize; // node size at LOD 0 (m)
		public readonly int levels;
		readonly double minY, maxY;
		readonly HeightBounds heightBounds; // optional
		readonly (double x, double z, double size)? fixedRoot; // optional fixed world bounds for the root grid

		public readonly double[] ranges;
		// per level: ( morph start, 1 / morph range, grid spacing, 0 )
		public readonly Vector4[] morph;

		public readonly Vector4[] nodeArray;
		public readonly int maxInstances;
		public int count;
		public readonly int[] lodCounts;

		public Mesh gridMesh;

		// camera (sim space) and frustum (Unity space) of the current update
		double camX, camY, camZ;
		readonly Plane[] planes = new Plane[ 6 ];
		double bMinX, bMinY, bMinZ, bMaxX, bMaxY, bMaxZ;

		public CDLOD( int gridSize = 64, double leafSize = 8, int levels = 12, double rangeFactor = 2.5, double morphStartRatio = 0.66,
			int maxInstances = 1500, double minY = - 20, double maxY = 20, HeightBounds heightBounds = null, (double, double, double)? center = null )
		{
			G = gridSize;
			this.leafSize = leafSize;
			this.levels = levels;
			this.minY = minY;
			this.maxY = maxY;
			this.heightBounds = heightBounds;
			if ( center.HasValue ) fixedRoot = ( center.Value.Item1, center.Value.Item2, center.Value.Item3 );
			this.maxInstances = maxInstances;

			ranges = new double[ levels ];
			morph = new Vector4[ levels ];
			double prev = 0;
			for ( int l = 0; l < levels; l ++ )
			{
				double r = leafSize * Math.Pow( 2, l ) * rangeFactor;
				ranges[ l ] = r;
				double start = prev + ( r - prev ) * morphStartRatio;
				double spacing = leafSize * Math.Pow( 2, l ) / gridSize;
				morph[ l ] = new Vector4( ( float ) start, ( float ) ( 1 / Math.Max( 1e-3, r - start ) ), ( float ) spacing, 0 );
				prev = r;
			}

			nodeArray = new Vector4[ maxInstances ];
			lodCounts = new int[ levels ];
			gridMesh = BuildGridMesh( gridSize );
		}

		// grid geometry in [0,1]^2 on XZ (x -> i / G, z -> j / G), quads in column strips of STRIP: the next row
		// of a strip reuses vertices the GPU shaded a moment ago (a full grid row is longer than its
		// post-transform reuse window)
		static Mesh BuildGridMesh( int G )
		{
			var verts = new Vector3[ ( G + 1 ) * ( G + 1 ) ];
			int p = 0;
			for ( int j = 0; j <= G; j ++ ) for ( int i = 0; i <= G; i ++ ) verts[ p ++ ] = new Vector3( ( float ) i / G, 0, ( float ) j / G );

			const int STRIP = 8;
			var idx = new int[ G * G * 6 ];
			p = 0;
			for ( int i0 = 0; i0 < G; i0 += STRIP ) for ( int j = 0; j < G; j ++ )
			{
				for ( int i = i0; i < Math.Min( i0 + STRIP, G ); i ++ )
				{
					int a = j * ( G + 1 ) + i;
					int b = a + 1;
					int c = a + ( G + 1 );
					int d = c + 1;
					// alternate diagonal for better symmetry. The sim z axis is mirrored into Unity, which
					// reverses the winding: the JS order (a c b / b c d) is emitted as (a b c / b d c).
					if ( ( i + j ) % 2 == 0 )
					{
						idx[ p ++ ] = a; idx[ p ++ ] = b; idx[ p ++ ] = c;
						idx[ p ++ ] = b; idx[ p ++ ] = d; idx[ p ++ ] = c;
					}
					else
					{
						idx[ p ++ ] = a; idx[ p ++ ] = d; idx[ p ++ ] = c;
						idx[ p ++ ] = a; idx[ p ++ ] = b; idx[ p ++ ] = d;
					}
				}
			}

			var mesh = new Mesh { name = "CDLOD grid " + G, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
			mesh.vertices = verts;
			mesh.triangles = idx;
			// never culled: the vertex shader places every vertex (JS: a 1e7 m bounding sphere)
			mesh.bounds = new Bounds( Vector3.zero, new Vector3( 1e7f, 1e7f, 1e7f ) );
			mesh.UploadMeshData( true );
			return mesh;
		}

		// Select the nodes for `camera`. Nodes are written to nodeArray front to back (so early depth testing
		// rejects hidden fragments).
		public void Update( Camera camera )
		{
			GeometryUtility.CalculateFrustumPlanes( camera, planes );
			var cp = camera.transform.position;
			camX = cp.x; camY = cp.y; camZ = - cp.z;

			count = 0;
			Array.Clear( lodCounts, 0, lodCounts.Length );

			int top = levels - 1;
			double rootSize = leafSize * Math.Pow( 2, top );

			if ( fixedRoot.HasValue )
			{
				var f = fixedRoot.Value;
				int n = ( int ) Math.Ceiling( f.size / rootSize );
				for ( int j = 0; j < n; j ++ ) for ( int i = 0; i < n; i ++ )
					Select( f.x + i * rootSize, f.z + j * rootSize, rootSize, top );
			}
			else
			{
				int cx = ( int ) Math.Floor( camX / rootSize );
				int cz = ( int ) Math.Floor( camZ / rootSize );
				for ( int j = - 1; j <= 1; j ++ ) for ( int i = - 1; i <= 1; i ++ )
					Select( ( cx + i ) * rootSize, ( cz + j ) * rootSize, rootSize, top );
			}

			// front-to-back order
			int n2 = count;
			var keys = new float[ n2 ];
			var items = new Vector4[ n2 ];
			for ( int i = 0; i < n2; i ++ )
			{
				var a = nodeArray[ i ];
				double s = a.z;
				double dx = Math.Max( Math.Max( a.x - camX, 0 ), camX - a.x - s );
				double dz = Math.Max( Math.Max( a.y - camZ, 0 ), camZ - a.y - s );
				keys[ i ] = ( float ) ( dx * dx + dz * dz );
				items[ i ] = a;
			}

			Array.Sort( keys, items );
			for ( int i = 0; i < n2; i ++ ) nodeArray[ i ] = items[ i ];
		}

		void Bounds( double x, double z, double size )
		{
			if ( heightBounds != null )
			{
				heightBounds( x, z, x + size, z + size, out double a, out double b );
				bMinY = a; bMaxY = b;
			}
			else
			{
				bMinY = minY; bMaxY = maxY;
			}

			bMinX = x; bMinZ = z; bMaxX = x + size; bMaxZ = z + size;
		}

		bool IntersectsSphere( double r )
		{
			double dx = Math.Max( Math.Max( bMinX - camX, 0 ), camX - bMaxX );
			double dy = Math.Max( Math.Max( bMinY - camY, 0 ), camY - bMaxY );
			double dz = Math.Max( Math.Max( bMinZ - camZ, 0 ), camZ - bMaxZ );
			return dx * dx + dy * dy + dz * dz <= r * r;
		}

		// the current box (sim space) mirrored into Unity space, against the camera frustum
		bool IntersectsFrustum()
		{
			var min = new Vector3( ( float ) bMinX, ( float ) bMinY, ( float ) - bMaxZ );
			var max = new Vector3( ( float ) bMaxX, ( float ) bMaxY, ( float ) - bMinZ );
			var b = new UnityEngine.Bounds();
			b.SetMinMax( min, max );
			return GeometryUtility.TestPlanesAABB( planes, b );
		}

		void Add( double x, double z, double size, int lod )
		{
			if ( count >= maxInstances ) return;
			nodeArray[ count ++ ] = new Vector4( ( float ) x, ( float ) z, ( float ) size, lod );
			lodCounts[ lod ] ++;
		}

		bool Select( double x, double z, double size, int lod )
		{
			Bounds( x, z, size );
			if ( ! IntersectsSphere( ranges[ lod ] ) ) return false;
			if ( ! IntersectsFrustum() ) return true;

			if ( lod == 0 || ! IntersectsSphere( ranges[ lod - 1 ] ) )
			{
				Add( x, z, size, lod );
				return true;
			}

			double h = size * 0.5;
			for ( int c = 0; c < 4; c ++ )
			{
				double cx = x + ( c & 1 ) * h, cz = z + ( c >> 1 ) * h;
				if ( ! Select( cx, cz, h, lod - 1 ) )
				{
					// quadrant outside the finer range: draw it at this node's LOD
					Bounds( cx, cz, h );
					if ( IntersectsFrustum() ) Add( cx, cz, h, lod );
				}
			}

			return true;
		}
	}
}
