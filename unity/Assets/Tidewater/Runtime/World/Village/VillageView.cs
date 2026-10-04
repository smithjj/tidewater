using System;
using System.Collections.Generic;
using Tidewater.Core;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

// The Unity view of the fishing village (Village.js, the mesh / material half): turns the geometry built by Village (sim coordinates,
// z south) into Unity meshes (z mirrored: Unity z = -sim z, winding reversed), with the village materials.
//   - one mesh for all the opaque geometry (wood, hard, roofMetal, thatch, stone: one submesh index range each, as the JS draw ranges),
//     the only shadow caster; fabric and nets are their own meshes; the sign and the pier lanterns swing about their pivots.
// Village is built by TerrainRenderer (it flattens the building pads in the height map before the terrain textures are made).
namespace Tidewater.World.Village
{
	[ExecuteAlways]
	public sealed class VillageView : MonoBehaviour
	{
		static readonly string[] KINDS = { "wood", "hard", "roofMetal", "thatch", "stone", "fabric", "net" };
		// the shader keyword of each kind (wood: none; net has its own shader)
		static readonly string[] KIND_KEYWORDS = { null, "_KIND_HARD", "_KIND_ROOF", "_KIND_THATCH", "_KIND_STONE", "_KIND_FABRIC", null };

		// the texture bake (Shaders/Village/VillageBake.compute)
		public ComputeShader bakeShader;

		Village village;
		readonly List<GameObject> built = new List<GameObject>();
		readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
		Transform signT;
		Tidewater.World.Fish.FishPropsView fish; // fish, lobsters, ice and banana leaves of the stall, racks and tables
		readonly List<Transform> lanternT = new List<Transform>();
		public string stats = "";

		void Reset() { FillDefaults(); }

		void FillDefaults()
		{
#if UNITY_EDITOR
			if ( bakeShader == null ) bakeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Village/VillageBake.compute" );
#endif
		}

		void OnEnable() { Refresh(); }

		// builds the meshes once the terrain has made the village (either component may be enabled first)
		public void Refresh()
		{
			if ( village != null ) return;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			if ( terrain != null && terrain.village != null ) Build( terrain.village );
		}
		void OnDisable() { Release(); }

		// the terrain made a new village (its pads changed the heights): drop the old meshes
		public void Rebuild() { Release(); Refresh(); }

		void Release()
		{
			foreach ( var g in built )
			{
				if ( g == null ) continue;
				var mf = g.GetComponent<MeshFilter>();
				if ( mf != null && mf.sharedMesh != null ) DestroyImmediate( mf.sharedMesh );
				DestroyImmediate( g );
			}

			built.Clear();
			if ( fish != null ) { fish.Release(); fish = null; }
			lanternT.Clear();
			signT = null;
			foreach ( var m in materials.Values ) if ( m != null ) DestroyImmediate( m );
			materials.Clear();
			village = null;
		}

		Material Mat( string key )
		{
			if ( materials.TryGetValue( key, out var m ) ) return m;
			int kind = Array.IndexOf( KINDS, key );
			m = new Material( Shader.Find( key == "net" ? "Tidewater/VillageNet" : "Tidewater/Village" ) ) { name = "village-" + key, hideFlags = HideFlags.HideAndDontSave };
			m.SetFloat( "_VillageKind", kind );
			if ( KIND_KEYWORDS[ kind ] != null ) m.EnableKeyword( KIND_KEYWORDS[ kind ] );
			// fabric is double sided (alpha 1: no alpha test); the nets blend, double sided, without depth write
			if ( key == "fabric" ) m.SetFloat( "_CullMode", 0f );
			materials[ key ] = m;
			return m;
		}

		// sim geometry -> Unity mesh (z mirrored, winding reversed); `ranges` gives the submeshes of the shared index buffer
		static Mesh MakeMesh( Tidewater.World.Village.BuiltGeometry g, string name, List<OpaqueRange> ranges = null )
		{
			int n = g.vertexCount;
			var v = new Vector3[ n ]; var nn = new Vector3[ n ]; var uv0 = new Vector2[ n ]; var col = new Color[ n ]; var uv1 = new Vector2[ n ]; var uv2 = new Vector2[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				v[ i ] = new Vector3( g.position[ i * 3 ], g.position[ i * 3 + 1 ], - g.position[ i * 3 + 2 ] );
				nn[ i ] = new Vector3( g.normal[ i * 3 ], g.normal[ i * 3 + 1 ], - g.normal[ i * 3 + 2 ] );
				uv0[ i ] = new Vector2( g.uv[ i * 2 ], g.uv[ i * 2 + 1 ] );
				col[ i ] = new Color( g.tint[ i * 3 ], g.tint[ i * 3 + 1 ], g.tint[ i * 3 + 2 ], 1f );
				uv1[ i ] = new Vector2( g.vdata[ i * 4 ], g.vdata[ i * 4 + 1 ] );
				uv2[ i ] = new Vector2( g.vdata[ i * 4 + 2 ], g.vdata[ i * 4 + 3 ] );
			}

			var idx = new int[ g.index.Length ];
			for ( int i = 0; i + 2 < idx.Length; i += 3 ) { idx[ i ] = g.index[ i ]; idx[ i + 1 ] = g.index[ i + 2 ]; idx[ i + 2 ] = g.index[ i + 1 ]; }

			var m = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
			m.SetVertices( v ); m.SetNormals( nn ); m.SetUVs( 0, uv0 ); m.SetColors( col ); m.SetUVs( 1, uv1 ); m.SetUVs( 2, uv2 );
			if ( ranges == null ) m.SetTriangles( idx, 0 );
			else
			{
				m.SetIndexBufferParams( idx.Length, IndexFormat.UInt32 );
				m.SetIndexBufferData( idx, 0, 0, idx.Length );
				m.subMeshCount = ranges.Count;
				for ( int i = 0; i < ranges.Count; i ++ ) m.SetSubMesh( i, new SubMeshDescriptor( ranges[ i ].start, ranges[ i ].count, MeshTopology.Triangles ), MeshUpdateFlags.DontRecalculateBounds );
			}

			m.RecalculateBounds();
			return m;
		}

		GameObject AddMesh( string name, Mesh mesh, Material[] mats, bool castShadows, Transform parent )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterials = mats;
			mr.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
			built.Add( go );
			return go;
		}

		void Build( Village v )
		{
			Release();
			village = v;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			FillDefaults();
			VillageTextures.Ensure( bakeShader );
			var mats = new Material[ v.ranges.Count ];
			for ( int i = 0; i < mats.Length; i ++ ) mats[ i ] = Mat( v.ranges[ i ].key );
			AddMesh( "village-opaque", MakeMesh( v.shared, "village-opaque", v.ranges ), mats, true, transform );
			if ( v.fabric != null ) AddMesh( "village-fabric", MakeMesh( v.fabric, "village-fabric" ), new[] { Mat( "fabric" ) }, false, transform );
			if ( v.nets != null ) AddMesh( "village-nets", MakeMesh( v.nets, "village-nets" ), new[] { Mat( "net" ) }, false, transform );

			// the swinging parts: geometry relative to the pivot, the pivot placed in the world
			if ( v.sign != null ) signT = AddSwing( "village-sign", v.sign, true );
			foreach ( var l in v.lanterns ) lanternT.Add( AddSwing( "village-lantern", l.part, false ) );
			if ( v.fishProps != null && v.fishProps.items.Count > 0 ) fish = new Tidewater.World.Fish.FishPropsView( transform, v.fishProps );
			stats = $"village: {v.shared.vertexCount} vertices, {v.shared.index.Length / 3} triangles, meshes + {( fish != null ? fish.count : 0 )} fish props + textures ({VillageTextures.bytes / 1048576.0:0} MB, bake {VillageTextures.bakeMs:0} ms) in {sw.ElapsedMilliseconds} ms";
		}

		Transform AddSwing( string name, SwingPart part, bool castShadows )
		{
			var root = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			root.transform.SetParent( transform, false );
			root.transform.localPosition = Tidewater.Util.Sim.ToUnity( part.pivot.x, part.pivot.y, part.pivot.z );
			built.Add( root );
			foreach ( var kv in part.geometry ) AddMesh( name + "-" + kv.Key, MakeMesh( kv.Value, name + "-" + kv.Key ), new[] { Mat( kv.Key ) }, castShadows, root.transform );
			return root.transform;
		}

		// sim rotation (quaternion q) -> Unity: the world mirrors z, so q' = ( -x, -y, z, w )
		static Quaternion ToUnity( Tidewater.Engine.Quaternion q ) => new Quaternion( ( float ) - q.x, ( float ) - q.y, ( float ) q.z, ( float ) q.w );
		static readonly Tidewater.Engine.Euler euler = new Tidewater.Engine.Euler();
		static readonly Tidewater.Engine.Quaternion quat = new Tidewater.Engine.Quaternion();

		void Update()
		{
			Refresh();
			if ( village == null ) return;

			// wind animation of the sign and the pier lanterns (Village.update)
			if ( ! Application.isPlaying ) return;
			double dt = Math.Min( Time.deltaTime, 0.1f );
			village.update( dt, G.windSpeed, G.windDir.x, G.windDir.y );
			if ( signT != null ) { euler.set( village.signRotX, village.signRotY, 0, "YXZ" ); signT.localRotation = ToUnity( quat.setFromEuler( euler ) ); }
			for ( int i = 0; i < lanternT.Count; i ++ )
			{
				var l = village.lanterns[ i ];
				euler.set( l.z, 0, - l.x, "XYZ" );
				lanternT[ i ].localRotation = ToUnity( quat.setFromEuler( euler ) );
			}
		}
	}
}
