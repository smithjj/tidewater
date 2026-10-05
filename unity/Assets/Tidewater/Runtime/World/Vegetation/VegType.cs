using System;
using System.Collections.Generic;
using UnityEngine;

// Port of src/world/vegetation/InstanceLOD.js: the instance records of one vegetation type with a grid for the near queries, and its levels: near (the instances
// within range + margin of the camera, refilled when the camera has moved a few metres, optionally sorted front to back) and far (all of them, static; or without the
// ones certainly near; or front to back in 6 m buckets for the impostors). The exact near / far split is made per pixel in the shader (the draw's _VegLod window).
// Instead of copying the records into the level's buffers (JS), the records are one static GPU buffer ( 3 float4: iPos, iDat, ext ) and a level is the list of the
// record numbers it draws.
namespace Tidewater.World.Vegetation
{
	public sealed class VegInstances
	{
		public readonly int count;
		public readonly float[] px, py, pz, qr2;
		public readonly float[] rec; // 12 floats per record: iPos ( x y z s ), iDat ( la l H seed ), ext ( yaw |sy| 0 0 )
		readonly float cellSize;
		readonly Dictionary<int, int[]> cells = new Dictionary<int, int[]>();

		public VegInstances( List<VegRec> records, float cellSize = 32 )
		{
			int n = records.Count;
			count = n;
			px = new float[ n ]; py = new float[ n ]; pz = new float[ n ]; qr2 = new float[ n ];
			rec = new float[ n * 12 ];
			for ( int i = 0; i < n; i ++ )
			{
				var r = records[ i ];
				px[ i ] = ( float ) r.x; py[ i ] = ( float ) r.y; pz[ i ] = ( float ) r.z;
				float s = ( float ) r.s;
				// (the JS matrix: scale ( s, s * |sy || 1|, s ); sy is undefined / 0 for the types without)
				float sy = r.sy == 0 ? 1f : ( float ) Math.Abs( r.sy );
				int o = i * 12;
				rec[ o ] = px[ i ]; rec[ o + 1 ] = py[ i ]; rec[ o + 2 ] = pz[ i ]; rec[ o + 3 ] = s;
				rec[ o + 4 ] = ( float ) r.la; rec[ o + 5 ] = ( float ) r.l; rec[ o + 6 ] = r.H != 0 ? ( float ) r.H : 1f; rec[ o + 7 ] = ( float ) r.seed;
				rec[ o + 8 ] = ( float ) r.yaw; rec[ o + 9 ] = sy;
				qr2[ i ] = r.qr != 0 ? ( float ) ( r.qr * r.qr ) : 0f;
			}

			this.cellSize = cellSize;
			var tmp = new Dictionary<int, List<int>>();
			for ( int i = 0; i < n; i ++ )
			{
				int k = Key( ( int ) Math.Floor( px[ i ] / cellSize ), ( int ) Math.Floor( pz[ i ] / cellSize ) );
				if ( ! tmp.TryGetValue( k, out var a ) ) tmp[ k ] = a = new List<int>();
				a.Add( i );
			}

			foreach ( var kv in tmp ) cells[ kv.Key ] = kv.Value.ToArray();
		}

		static int Key( int i, int j ) { return ( i + 4096 ) * 8192 + ( j + 4096 ); }

		// indices of the instances with a horizontal distance < radius (or their own query radius), written to out; returns the count
		public int queryNear( float x, float z, float radius, int[] outIds )
		{
			float cs = cellSize;
			int i0 = ( int ) Math.Floor( ( x - radius ) / cs ), i1 = ( int ) Math.Floor( ( x + radius ) / cs );
			int j0 = ( int ) Math.Floor( ( z - radius ) / cs ), j1 = ( int ) Math.Floor( ( z + radius ) / cs );
			float r2 = radius * radius;
			int c = 0;
			for ( int j = j0; j <= j1; j ++ )
				for ( int i = i0; i <= i1; i ++ )
				{
					if ( ! cells.TryGetValue( Key( i, j ), out var a ) ) continue;
					for ( int k = 0; k < a.Length; k ++ )
					{
						int id = a[ k ];
						float dx = px[ id ] - x, dz = pz[ id ] - z;
						float q = qr2[ id ] != 0 ? qr2[ id ] : r2;
						if ( dx * dx + dz * dz < q && c < outIds.Length ) outIds[ c ++ ] = id;
					}
				}

			return c;
		}
	}

	// one draw of a type: a mesh with its material, the record list and the LOD window ( visible from, fade-out start, fade-out end )
	public sealed class VegLevel
	{
		public const int MIN_CAPACITY = 1100;
		public string name;
		public Mesh mesh;
		public Material material;
		public bool castShadow;
		public Vector3 lodRange;
		public int capacity, count;
		public uint[] ids;
		public GraphicsBuffer list;
		public bool dirty = true;
		public long trianglesPerInstance;

		public VegLevel( string name, Mesh mesh, Material material, bool castShadow, int capacity, Vector3 lodRange )
		{
			this.name = name; this.mesh = mesh; this.material = material; this.castShadow = castShadow; this.lodRange = lodRange;
			this.capacity = Math.Max( capacity, MIN_CAPACITY );
			ids = new uint[ this.capacity ];
			list = new GraphicsBuffer( GraphicsBuffer.Target.Structured, this.capacity, 4 );
			trianglesPerInstance = mesh.GetIndexCount( 0 ) / 3;
		}

		// the instances ( all, or the given ids ) as the level's list; matrices flag of the JS is gone (the records are static)
		public void fill( int[] src, int n )
		{
			n = Math.Min( n, capacity );
			for ( int k = 0; k < n; k ++ ) ids[ k ] = ( uint ) src[ k ];
			count = n;
			dirty = true;
		}

		public void fillAll( int total )
		{
			int n = Math.Min( total, capacity );
			for ( int k = 0; k < n; k ++ ) ids[ k ] = ( uint ) k;
			count = n;
			dirty = true;
		}

		// uploads the list when it changed
		public void upload()
		{
			if ( ! dirty ) return;
			dirty = false;
			if ( count > 0 ) list.SetData( ids, 0, 0, count );
		}

		public void release()
		{
			if ( list != null ) { list.Release(); list = null; }
		}
	}

	public sealed class VegTypeOptions
	{
		public VegMeshSpec near, far;
		public float nearRange = 100, margin = 14, refreshDistance = 6, farRefresh = 16;
		public Vector2 fade; // types without a far level: shrink out between fade.x and fade.y
		public bool farExcludeNear, sortNear, sortFar;
	}

	public sealed class VegMeshSpec
	{
		public string name; public Mesh mesh; public Material material; public bool castShadow; public Vector2 fade; // (fade: far levels)
	}

	public sealed class VegType
	{
		public readonly string name;
		public readonly bool sortNear, sortFar, farExcludeNear;
		public readonly float margin, refreshDistance, nearRange, queryRadius, farRefresh;
		public readonly VegInstances inst;
		public readonly List<VegLevel> levels = new List<VegLevel>();
		public VegLevel near, far;
		public GraphicsBuffer records;
		float lastX = float.PositiveInfinity, lastY = float.PositiveInfinity, lastZ = float.PositiveInfinity, farX = float.PositiveInfinity, farZ = float.PositiveInfinity;
		int[] nearIds, farIds;
		float[] sortKey;
		ushort[] bucket;
		int[] bstart;

		public VegType( string name, List<VegRec> records, VegTypeOptions o )
		{
			this.name = name;
			sortNear = o.sortNear;
			sortFar = o.sortFar && o.far != null && ! o.farExcludeNear;
			farRefresh = o.farRefresh;
			inst = new VegInstances( records );
			nearRange = o.nearRange; margin = o.margin; refreshDistance = o.refreshDistance;
			farExcludeNear = o.farExcludeNear;
			this.records = new GraphicsBuffer( GraphicsBuffer.Target.Structured, Math.Max( 1, inst.count ) * 3, 16 );
			if ( inst.count > 0 ) this.records.SetData( inst.rec );

			var nearWindow = o.far != null ? new Vector3( 0, nearRange, nearRange + 0.01f ) : new Vector3( 0, o.fade.x, o.fade.y );
			queryRadius = ( o.far != null ? nearRange : o.fade.y ) + margin;

			if ( o.near != null )
			{
				nearIds = new int[ Math.Max( 1, inst.count ) ];
				near = new VegLevel( o.near.name, o.near.mesh, o.near.material, o.near.castShadow, Math.Min( inst.count, 6000 ), nearWindow );
				levels.Add( near );
			}

			if ( o.far != null )
			{
				far = new VegLevel( o.far.name, o.far.mesh, o.far.material, o.far.castShadow, inst.count, new Vector3( nearRange, o.far.fade.x, o.far.fade.y ) );
				far.fillAll( inst.count );
				levels.Add( far );
				if ( sortFar || farExcludeNear ) farIds = new int[ Math.Max( 1, inst.count ) ];
			}

			foreach ( var l in levels )
			{
				l.material.SetBuffer( "_VegInst", this.records );
				l.material.SetBuffer( "_VegList", l.list );
			}
		}

		// how far (3D) past its refresh distance the camera has moved (> 0: needs a refill)
		public float overdue( Vector3 cam )
		{
			if ( near == null ) return -1;
			float dx = cam.x - lastX, dy = cam.y - lastY, dz = cam.z - lastZ;
			float d2 = dx * dx + dy * dy + dz * dz;
			float d = float.IsInfinity( d2 ) ? 1e9f : ( float ) Math.Sqrt( d2 ) - refreshDistance;
			if ( ! sortFar ) return d;
			float fd = ( float ) Math.Sqrt( ( cam.x - farX ) * ( cam.x - farX ) + ( cam.z - farZ ) * ( cam.z - farZ ) );
			return Math.Max( d, float.IsInfinity( fd ) || float.IsNaN( fd ) ? 1e9f : fd - farRefresh );
		}

		// far level front to back: counting sort into 6 m distance buckets
		void SortFar( Vector3 cam )
		{
			farX = cam.x; farZ = cam.z;
			int count = inst.count;
			const int B = 6, NB = 1024;
			if ( bucket == null ) { bucket = new ushort[ count ]; bstart = new int[ NB + 1 ]; }
			Array.Clear( bstart, 0, bstart.Length );
			for ( int i = 0; i < count; i ++ )
			{
				float dx = inst.px[ i ] - cam.x, dz = inst.pz[ i ] - cam.z;
				int b = Math.Min( NB - 1, ( int ) Math.Floor( Math.Sqrt( dx * dx + dz * dz ) / B ) );
				bucket[ i ] = ( ushort ) b;
				bstart[ b + 1 ] ++;
			}

			for ( int b = 0; b < NB; b ++ ) bstart[ b + 1 ] += bstart[ b ];
			for ( int i = 0; i < count; i ++ ) farIds[ bstart[ bucket[ i ] ] ++ ] = i;
			far.fill( farIds, count );
		}

		// returns true when the near buffers were rebuilt
		public bool update( Vector3 cam, bool force = false )
		{
			if ( near == null ) return false;
			if ( ! force && overdue( cam ) < 0 ) return false;
			if ( sortFar && ! ( Math.Sqrt( ( cam.x - farX ) * ( cam.x - farX ) + ( cam.z - farZ ) * ( cam.z - farZ ) ) < farRefresh ) ) SortFar( cam );
			lastX = cam.x; lastY = cam.y; lastZ = cam.z;
			// the vertical distance only makes instances further away, so a horizontal query is conservative
			int n = inst.queryNear( cam.x, cam.z, queryRadius, nearIds );
			if ( sortNear )
			{
				// front to back: alpha-tested foliage can still be rejected by the early depth test
				if ( sortKey == null ) sortKey = new float[ nearIds.Length ];
				for ( int k = 0; k < n; k ++ )
				{
					int i = nearIds[ k ];
					float dx = inst.px[ i ] - cam.x, dz = inst.pz[ i ] - cam.z;
					sortKey[ k ] = dx * dx + dz * dz;
				}

				Array.Sort( sortKey, nearIds, 0, n );
			}

			near.fill( nearIds, n );

			if ( farExcludeNear )
			{
				// drops only the instances that stay inside the near range (3D, like the shader test) until the camera has moved refreshDistance and triggered the next refill
				float rMin = Math.Max( 0, nearRange - refreshDistance - 2 );
				float r2 = rMin * rMin;
				int c = 0;
				for ( int i = 0; i < inst.count; i ++ )
				{
					float dx = inst.px[ i ] - cam.x, dy = inst.py[ i ] - cam.y, dz = inst.pz[ i ] - cam.z;
					if ( dx * dx + dy * dy + dz * dz >= r2 ) farIds[ c ++ ] = i;
				}

				far.fill( farIds, c );
			}

			return true;
		}

		public void release()
		{
			foreach ( var l in levels ) l.release();
			if ( records != null ) { records.Release(); records = null; }
		}
	}
}
