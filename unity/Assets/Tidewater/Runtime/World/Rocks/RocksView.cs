using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

// The Unity view of the scattered rocks (Rocks.js): four rock shapes x two levels of detail = eight procedural instanced draws per camera ( Shaders/Vegetation/TidewaterVegRock.shader ). The instances are re-bucketed into near /
// far meshes and frustum culled on the CPU every frame ( 714 rocks ); a rock crossing the LOD distance is drawn by both levels, dithered ( LODFade.js ). Placement: RockPlacement ( oracle-exact ), at the terrain's build.
namespace Tidewater.World.Rocks
{
	[ExecuteAlways]
	public sealed class RocksView : MonoBehaviour
	{
		const int NEAR_SUBDIV = 3, FAR_SUBDIV = 2; // 1280 / 320 triangles
		const float NEAR_DIST = 60, FAR_DIST = 700, BAND = 0.12f;

		public static RocksView instance;

		[StructLayout( LayoutKind.Sequential )]
		struct Vert { public Vector3 p, n; public Vector4 aVeg, aMat; public Vector2 uv, lobeA, lobeB; }

		sealed class Bucket
		{
			public int[] members; // indices into the rocks
			public Vector4[] rows; // the 4 float4 of every member ( matrix rows and lod, lod unset )
			public Mesh near, far;
			public int nearTris, farTris;
		}

		sealed class CamState
		{
			public readonly GraphicsBuffer[] nearBuf = new GraphicsBuffer[ 4 ], farBuf = new GraphicsBuffer[ 4 ];
			public readonly Vector4[][] nearArr = new Vector4[ 4 ][], farArr = new Vector4[ 4 ][];
			public readonly int[] nearCount = new int[ 4 ], farCount = new int[ 4 ];
		}

		List<RockInstance> built;
		List<RockInstance> rocks;
		readonly Bucket[] buckets = new Bucket[ 4 ];
		readonly Dictionary<CameraType, CamState> states = new Dictionary<CameraType, CamState>();
		readonly Plane[] planes = new Plane[ 6 ];
		MaterialPropertyBlock block;
		Material material;
		public string stats = "";

		void OnEnable() { instance = this; RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
		void OnDisable() { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; Release(); if ( instance == this ) instance = null; }

		public void Rebuild() { Release(); Refresh(); }

		void Release()
		{
			foreach ( var st in states.Values ) { foreach ( var b in st.nearBuf ) b?.Release(); foreach ( var b in st.farBuf ) b?.Release(); }
			states.Clear();
			for ( int i = 0; i < 4; i ++ ) { if ( buckets[ i ] == null ) continue; if ( buckets[ i ].near != null ) DestroyImmediate( buckets[ i ].near ); if ( buckets[ i ].far != null ) DestroyImmediate( buckets[ i ].far ); buckets[ i ] = null; }
			if ( material != null ) { DestroyImmediate( material ); material = null; }
			built = null; rocks = null;
		}

		// the rock mesh as a Unity mesh: the vertices stay in the sim frame ( the vertex stage mirrors z ), the winding is reversed; uv0.x = the cavity ( ao ). The channels are the vegetation's, which every pass reads
		static Mesh MakeMesh( RockMesh r, string name )
		{
			int vc = r.vertexCount, ic = r.index.Length;
			var v = new Vert[ vc ];
			for ( int i = 0; i < vc; i ++ )
			{
				v[ i ].p = new Vector3( r.pos[ i * 3 ], r.pos[ i * 3 + 1 ], r.pos[ i * 3 + 2 ] );
				v[ i ].n = new Vector3( r.nor[ i * 3 ], r.nor[ i * 3 + 1 ], r.nor[ i * 3 + 2 ] );
				v[ i ].uv = new Vector2( r.ao[ i ], 0 );
			}

			var m = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
			m.SetVertexBufferParams( vc,
				new VertexAttributeDescriptor( VertexAttribute.Position, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Normal, VertexAttributeFormat.Float32, 3 ),
				new VertexAttributeDescriptor( VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.Color, VertexAttributeFormat.Float32, 4 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 2 ),
				new VertexAttributeDescriptor( VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 2 ) );
			m.SetVertexBufferData( v, 0, 0, vc );
			var s = new ushort[ ic ];
			for ( int i = 0; i + 2 < ic; i += 3 ) { s[ i ] = ( ushort ) r.index[ i ]; s[ i + 1 ] = ( ushort ) r.index[ i + 2 ]; s[ i + 2 ] = ( ushort ) r.index[ i + 1 ]; }
			m.SetIndexBufferParams( ic, IndexFormat.UInt16 );
			m.SetIndexBufferData( s, 0, 0, ic );
			m.subMeshCount = 1;
			m.SetSubMesh( 0, new SubMeshDescriptor( 0, ic, MeshTopology.Triangles ), MeshUpdateFlags.DontRecalculateBounds );
			m.bounds = new Bounds( Vector3.zero, Vector3.one * 100f );
			return m;
		}

		public void Refresh()
		{
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			if ( terrain == null || terrain.rocks == null ) return;
			if ( built == terrain.rocks ) return;
			Release();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			rocks = built = terrain.rocks;
			block = new MaterialPropertyBlock();
			material = new Material( Shader.Find( "Tidewater/VegRock" ) ) { name = "rocks", hideFlags = HideFlags.HideAndDontSave };
			for ( int s = 0; s < 4; s ++ )
			{
				var members = new List<int>();
				for ( int i = 0; i < rocks.Count; i ++ ) if ( rocks[ i ].style == s ) members.Add( i );
				var near = RockGeometry.Build( s, 17 + s * 101, NEAR_SUBDIV );
				var far = RockGeometry.Build( s, 17 + s * 101, FAR_SUBDIV );
				var b = new Bucket { members = members.ToArray(), near = MakeMesh( near, "rocks-" + RockGeometry.STYLES[ s ].name + "-near" ), far = MakeMesh( far, "rocks-" + RockGeometry.STYLES[ s ].name + "-far" ), nearTris = near.triangles, farTris = far.triangles };
				b.rows = new Vector4[ Math.Max( 1, members.Count ) * 4 ];
				for ( int k = 0; k < members.Count; k ++ )
				{
					var e = rocks[ members[ k ] ].matrix; // column major
					b.rows[ k * 4 ] = new Vector4( ( float ) e[ 0 ], ( float ) e[ 4 ], ( float ) e[ 8 ], ( float ) e[ 12 ] );
					b.rows[ k * 4 + 1 ] = new Vector4( ( float ) e[ 1 ], ( float ) e[ 5 ], ( float ) e[ 9 ], ( float ) e[ 13 ] );
					b.rows[ k * 4 + 2 ] = new Vector4( ( float ) e[ 2 ], ( float ) e[ 6 ], ( float ) e[ 10 ], ( float ) e[ 14 ] );
				}

				buckets[ s ] = b;
			}

			stats = $"rocks: {rocks.Count} ( {buckets[ 0 ].members.Length} boulders, {buckets[ 1 ].members.Length} blocks, {buckets[ 2 ].members.Length} slabs, {buckets[ 3 ].members.Length} spires ); build {sw.ElapsedMilliseconds} ms";
		}

		// the 0..1 smoothstep ramp across a distance band
		static float BandFade( float d, float start, float end )
		{
			float t = ( d - start ) / Mathf.Max( end - start, 1e-6f );
			return t <= 0 ? 0 : t >= 1 ? 1 : t * t * ( 3 - 2 * t );
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			if ( Tidewater.Util.Studio.Is( cam ) ) return;
			Refresh();
			if ( rocks == null ) return;
			if ( ! states.TryGetValue( cam.cameraType, out var st ) )
			{
				st = new CamState();
				for ( int s = 0; s < 4; s ++ )
				{
					int cap = Math.Max( 1, buckets[ s ].members.Length );
					st.nearBuf[ s ] = new GraphicsBuffer( GraphicsBuffer.Target.Structured, cap * 4, 16 );
					st.farBuf[ s ] = new GraphicsBuffer( GraphicsBuffer.Target.Structured, cap * 4, 16 );
					st.nearArr[ s ] = new Vector4[ cap * 4 ]; st.farArr[ s ] = new Vector4[ cap * 4 ];
				}

				states[ cam.cameraType ] = st;
			}

			GeometryUtility.CalculateFrustumPlanes( cam, planes );
			var cp = cam.transform.position;
			double cx = cp.x, cy = cp.y, cz = - cp.z; // the sim frame
			var bounds = new Bounds( cp, Vector3.one * 4000f );
			int drawn = 0;
			for ( int s = 0; s < 4; s ++ )
			{
				var b = buckets[ s ];
				int nn = 0, nf = 0;
				var na = st.nearArr[ s ]; var fa = st.farArr[ s ];
				for ( int k = 0; k < b.members.Length; k ++ )
				{
					var r = rocks[ b.members[ k ] ];
					var c = new Vector3( ( float ) r.x, ( float ) r.y, ( float ) - r.z );
					float rad = ( float ) r.radius;
					// keep shadow casters just outside the view a little longer
					if ( ! GeometryUtility.TestPlanesAABB( planes, new Bounds( c, Vector3.one * rad * 2 ) ) )
					{
						rad += 12;
						if ( ! GeometryUtility.TestPlanesAABB( planes, new Bounds( c, Vector3.one * rad * 2 ) ) ) continue;
					}

					float d = ( float ) Math.Sqrt( ( r.x - cx ) * ( r.x - cx ) + ( r.y - cy ) * ( r.y - cy ) + ( r.z - cz ) * ( r.z - cz ) );
					float dn = NEAR_DIST + ( float ) r.size * 6, df = FAR_DIST + ( float ) r.size * 60;
					float t = BandFade( d, dn * ( 1 - BAND / 2 ), dn * ( 1 + BAND / 2 ) );
					float outF = BandFade( d, df * ( 1 - BAND ), df );
					if ( outF >= 1 ) continue;
					if ( t < 1 ) { na[ nn * 4 ] = b.rows[ k * 4 ]; na[ nn * 4 + 1 ] = b.rows[ k * 4 + 1 ]; na[ nn * 4 + 2 ] = b.rows[ k * 4 + 2 ]; na[ nn * 4 + 3 ] = new Vector4( t, 1, 0, 0 ); nn ++; }
					if ( t > 0 ) { fa[ nf * 4 ] = b.rows[ k * 4 ]; fa[ nf * 4 + 1 ] = b.rows[ k * 4 + 1 ]; fa[ nf * 4 + 2 ] = b.rows[ k * 4 + 2 ]; fa[ nf * 4 + 3 ] = new Vector4( t * ( 1 - outF ), 0, 0, 0 ); nf ++; }
				}

				st.nearCount[ s ] = nn; st.farCount[ s ] = nf;
				if ( nn > 0 )
				{
					st.nearBuf[ s ].SetData( na, 0, 0, nn * 4 );
					block.SetBuffer( "_RockInst", st.nearBuf[ s ] );
					Graphics.DrawMeshInstancedProcedural( b.near, 0, material, bounds, nn, block, ShadowCastingMode.On, true, gameObject.layer, cam );
					drawn += nn;
				}

				if ( nf > 0 )
				{
					st.farBuf[ s ].SetData( fa, 0, 0, nf * 4 );
					block.SetBuffer( "_RockInst", st.farBuf[ s ] );
					Graphics.DrawMeshInstancedProcedural( b.far, 0, material, bounds, nf, block, ShadowCastingMode.On, true, gameObject.layer, cam );
					drawn += nf;
				}
			}

			if ( cam.cameraType == CameraType.Game ) visible = drawn;
		}

		int visible;
		public string Stats() => stats + $"; {visible} drawn";
	}
}
