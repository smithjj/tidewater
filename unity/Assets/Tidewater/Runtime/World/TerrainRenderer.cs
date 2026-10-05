using System;
using Tidewater.Core;
using Tidewater.Util;
using Tidewater.World.Terrain;
using UnityEngine;
using UnityEngine.Rendering;

// Port of src/world/Terrain.js: the island terrain, a CDLOD mesh displaced by the heightmap with a procedural
// material. In Unity the mesh is drawn with Graphics.DrawMeshInstanced: one grid mesh, one instance per
// selected CDLOD node, the node data (origin xz, size, lod) in the per-instance property _TWNodeData.
// The material (Shaders/Terrain/TidewaterTerrain.shader) is HDRP deferred lit with the terrain surface of
// Terrain.js ported to HLSL (TerrainSurface.hlsl).
namespace Tidewater.World
{
	[ExecuteAlways]
	public sealed class TerrainRenderer : MonoBehaviour
	{
		// gridSize 40 / rangeFactor 2.0: 0.2 m vertices at the camera, ~80 quads per LOD range
		// (~9-25 % finer than 32 / 2.3 at every distance) for 0.1-0.2 M triangles.
		public int gridSize = 40;
		public float rangeFactor = 2.0f;
		public int seed = 7;
		public Material material;

		public TerrainData data { get; private set; }
		public Colliders colliders { get; private set; } // the world's colliders (App.colliders): the village registers its buildings, the player and boats collide with them
		public Tidewater.World.Village.Village village { get; private set; }
		public TerrainGPU gpu { get; private set; }
		public CDLOD lod { get; private set; }
		public System.Collections.Generic.List<Tidewater.World.Rocks.RockInstance> rocks { get; private set; } // the scattered rocks ( RockPlacement ); they collide
		public ShoreFieldData shoreField { get; private set; }

		const int Batch = 1023;
		readonly Matrix4x4[] identity = new Matrix4x4[ Batch ];
		readonly Vector4[] batchData = new Vector4[ Batch ];
		MaterialPropertyBlock block;
		static readonly int NodeData = Shader.PropertyToID( "_TWNodeData" );

		// travelling gust field offset (integrated so speed / direction changes never jump)
		Vector2 gustOffset;

		public string stats = "";

		void OnEnable()
		{
			Build();
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			Release();
		}

		void Build()
		{
			Release();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			data = new TerrainData( seed );
			double genMs = sw.Elapsed.TotalMilliseconds;
			// the village flattens its building pads into the height map: it is built before the shore field and the terrain textures
			colliders = new Colliders();
			village = new Tidewater.World.Village.Village( data, colliders );
			double villageMs = sw.Elapsed.TotalMilliseconds - genMs;
			rocks = Tidewater.World.Rocks.RockPlacement.Place( data, village );
			Tidewater.World.Rocks.RockPlacement.AddColliders( rocks, colliders );
			foreach ( var vv in FindObjectsByType<Tidewater.World.Village.VillageView>( FindObjectsSortMode.None ) ) vv.Rebuild();
			foreach ( var vg in FindObjectsByType<Tidewater.World.Vegetation.VegetationView>( FindObjectsSortMode.None ) ) vg.Rebuild();
			foreach ( var rv in FindObjectsByType<Tidewater.World.Rocks.RocksView>( FindObjectsSortMode.None ) ) rv.Rebuild();
			// the wave travel-time field the shore waves follow (App.js: res 512, the world's swell direction)
			shoreField = ShoreField.Compute( data, 512, WorldLayout.SwellDirX, WorldLayout.SwellDirZ );
			gpu = new TerrainGPU( data, shoreField );
			gpu.SetGlobals();
			double half = data.size / 2;
			lod = new CDLOD( gridSize, 8, 9, rangeFactor, 0.66, 1500, - 20, 20,
				( double x0, double z0, double x1, double z1, out double min, out double max ) => { var b = data.BoundsFor( x0, z0, x1, z1 ); min = b.min; max = b.max; },
				( - half, - half, data.size ) );
			if ( material == null ) material = new Material( Shader.Find( "Tidewater/Terrain" ) ) { name = "Terrain" };
			material.enableInstancing = true; // DrawMeshInstanced needs it
			for ( int i = 0; i < Batch; i ++ ) identity[ i ] = Matrix4x4.identity;
			block = new MaterialPropertyBlock();
			Shader.SetGlobalVectorArray( "_TWLodMorph", PadMorph( lod.morph ) );
			stats = $"terrain: generate {genMs:F0} ms, village {villageMs:F0} ms, textures {gpu.bakeMs:F0} ms, total {sw.Elapsed.TotalMilliseconds:F0} ms";
		}

		static Vector4[] PadMorph( Vector4[] m )
		{
			var a = new Vector4[ 16 ];
			Array.Copy( m, a, m.Length );
			return a;
		}

		void Release()
		{
			if ( gpu != null ) gpu.Destroy();
			gpu = null; data = null; colliders = null; village = null; lod = null; shoreField = null; rocks = null;
		}

		// travelling gust field offset (the same integration as Vegetation's vegGustOffset)
		void Update()
		{
			float dt = Application.isPlaying ? Time.deltaTime : 0;
			float speed = 0.7f * G.windSpeed + 1.5f;
			gustOffset += G.windDir * speed * dt;
		}

		// The CDLOD selection depends on the camera, so every camera selects and draws its own nodes (the Game view,
		// the Scene view, probes ...) just before it renders.
		void OnBeginCamera( ScriptableRenderContext context, Camera cam )
		{
			if ( lod == null || ! isActiveAndEnabled ) return;
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			if ( Tidewater.Util.Studio.Is( cam ) ) return; // the fish portrait's studio

			// Morph toward the view camera in every pass: the shadow passes render the same surface
			// (with the pass camera they would morph toward the light camera instead).
			var cp = cam.transform.position;
			Shader.SetGlobalVector( "_TWViewPos", new Vector4( cp.x, cp.y, - cp.z, 0 ) );
			Shader.SetGlobalVector( "_TWWind", new Vector4( G.windDir.x, G.windDir.y, G.windSpeed, 0 ) );
			Shader.SetGlobalVector( "_TWGust", new Vector4( gustOffset.x, gustOffset.y, 0, 0 ) );

			lod.Update( cam );
			for ( int start = 0; start < lod.count; start += Batch )
			{
				int n = Math.Min( Batch, lod.count - start );
				Array.Copy( lod.nodeArray, start, batchData, 0, n );
				block.SetVectorArray( NodeData, batchData );
				Graphics.DrawMeshInstanced( lod.gridMesh, 0, material, identity, n, block, ShadowCastingMode.On, true, gameObject.layer, cam );
			}
		}
	}
}
