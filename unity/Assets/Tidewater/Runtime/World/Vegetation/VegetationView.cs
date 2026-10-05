using System;
using System.Collections.Generic;
using Tidewater.Core;
using UnityEngine;
using UnityEngine.Rendering;

// The Unity view of the island vegetation (Vegetation.js): scatters the plants once the terrain and the village exist (VegScatter, oracle-exact), builds the types with
// their levels of detail and draws them with Graphics.DrawMeshInstancedProcedural for every Game / Scene camera (Shaders/Vegetation: the vertex stage poses each plant from
// its record, wind and gusts included). The near levels are refilled when the main camera has moved (VegType.update), at most two types per frame; _VegCam, _VegGust
// and _VegNear are the shaders' per-frame globals (VegNodes.js uCamPos, uGustOffset, uCanopyNear).
namespace Tidewater.World.Vegetation
{
	[ExecuteAlways]
	public sealed class VegetationView : MonoBehaviour
	{
		// the hand-over distances and fades (Vegetation.js)
		public const float PALM_NEAR = 120, TREE_NEAR = 65, SHRUB_NEAR = 45;
		static readonly Vector2 UNDER_FADE = new Vector2( 120, 140 ), BROAD_FADE = new Vector2( 85, 105 ), BANANA_FADE = new Vector2( 100, 120 ), CANOPY_FAR = new Vector2( 2600, 2800 );
		const float UNDER_FERN_FADE_1 = 58;
		// the plant kinds that ride on the seed (PlantGeometry.js UNDERSTORY / BROADLEAF)
		const double YOUNG = 1, BANANA = 2, FERN = 3, BANANA_B = 4, MONSTERA = 11, ELEPHANT = 12, HELICONIA = 13, STRELITZIA = 14;

		public static VegetationView instance;

		Tidewater.World.Village.Village built;
		readonly List<VegType> types = new List<VegType>();
		readonly List<Mesh> meshes = new List<Mesh>();
		readonly List<Material> materials = new List<Material>();
		MaterialPropertyBlock block;
		VegAtlases atlases;
		GrassField grass;
		Vector3 gust; // the integrated gust offset ( sim x, z )
		int lastFrame = -1, drawCalls, drawnInstances;
		public string stats = "";
		public VegRecords records;
		public IReadOnlyList<VegType> Types => types;

		void OnEnable() { instance = this; RenderPipelineManager.beginCameraRendering += OnBeginCamera; }
		void OnDisable() { RenderPipelineManager.beginCameraRendering -= OnBeginCamera; Release(); if ( instance == this ) instance = null; }

		public void Rebuild() { Release(); Refresh(); }

		void Release()
		{
			foreach ( var t in types ) t.release();
			types.Clear();
			foreach ( var m in meshes ) if ( m != null ) DestroyImmediate( m );
			meshes.Clear();
			if ( atlases != null ) { atlases.Dispose(); atlases = null; }
			if ( grass != null ) { grass.Dispose(); grass = null; }
			foreach ( var m in materials ) if ( m != null ) DestroyImmediate( m );
			materials.Clear();
			built = null;
		}

		Mesh Mesh( string name ) { var m = VegMesh.Load( name ); meshes.Add( m ); return m; }

		Material Mat( string shader, string name )
		{
			var m = new Material( Shader.Find( shader ) ) { name = name, hideFlags = HideFlags.HideAndDontSave };
			materials.Add( m );
			return m;
		}

		// builds the plants once the terrain has made the village
		public void Refresh()
		{
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			if ( terrain == null || terrain.village == null || terrain.data == null ) return;
			if ( built == terrain.village ) return;
			Release();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var site = VegSites.FromVillage( terrain.data, terrain.village );
			var recs = VegScatter.Scatter( site, 99 );
			records = recs;
			double scatterMs = sw.Elapsed.TotalMilliseconds;
			block = new MaterialPropertyBlock();

			Func<VegRec, double, double, bool, VegRec> kind2 = ( r, k, qr, keepSeed ) => new VegRec { x = r.x, y = r.y, z = r.z, s = r.s, sy = r.sy, yaw = r.yaw, la = r.la, l = r.l, H = r.H, seed = keepSeed ? r.seed : k + r.seed * 0.999, qr = qr };
			Func<VegRec, double, double, VegRec> kind = ( r, k, qr ) => kind2( r, k, qr, false );
			VegMeshSpec Spec( string name, string meshName, bool shadow ) => new VegMeshSpec { name = name, mesh = Mesh( meshName ), material = Mat( "Tidewater/VegPlant", "veg-" + name ), castShadow = shadow };

			var palmFar = Spec( "palm-far", "palmFar", false ); palmFar.fade = CANOPY_FAR;
			types.Add( new VegType( "palms", recs.palms, new VegTypeOptions
			{
				nearRange = PALM_NEAR, margin = 10, refreshDistance = 6, farExcludeNear = true, sortNear = true,
				near = Spec( "palm", "palmNear", true ), far = palmFar,
			} ) );

			// understory: young palms and ferns in one mesh; the plant kind rides on the seed
			var underRecs = new List<VegRec>();
			foreach ( var r in recs.youngPalms ) underRecs.Add( kind( r, YOUNG, UNDER_FADE.y + 10 ) );
			foreach ( var r in recs.ferns ) underRecs.Add( kind( r, FERN, UNDER_FERN_FADE_1 + 8 ) );
			// banana clumps (two variants; their heights are in the mesh: H ~ 0)
			var bananaRecs = new List<VegRec>();
			foreach ( var r in recs.bananas ) { var b = kind( r, r.seed < 0.5 ? BANANA : BANANA_B, BANANA_FADE.y + 10 ); b.H = 0.02; bananaRecs.Add( b ); }
			types.Add( new VegType( "bananas", bananaRecs, new VegTypeOptions { fade = BANANA_FADE, margin = 10, sortNear = true, near = Spec( "banana", "bananas", true ) } ) );
			types.Add( new VegType( "understory", underRecs, new VegTypeOptions { fade = UNDER_FADE, margin = 10, sortNear = true, near = Spec( "understory", "understory", false ) } ) );

			// broadleaf understory: monstera (own mesh), elephant ear + heliconia + bird of paradise (one mesh, the kind on the seed)
			var monsteraRecs = new List<VegRec>();
			foreach ( var r in recs.monsteras ) monsteraRecs.Add( kind( r, MONSTERA, BROAD_FADE.y + 10 ) );
			types.Add( new VegType( "monsteras", monsteraRecs, new VegTypeOptions { fade = BROAD_FADE, margin = 10, sortNear = true, near = Spec( "monstera", "monstera", true ) } ) );
			var broadRecs = new List<VegRec>();
			foreach ( var r in recs.elephantEars ) broadRecs.Add( kind( r, ELEPHANT, BROAD_FADE.y + 10 ) );
			foreach ( var r in recs.heliconias ) broadRecs.Add( kind( r, HELICONIA, BROAD_FADE.y + 10 ) );
			foreach ( var r in recs.strelitzias ) broadRecs.Add( kind( r, STRELITZIA, BROAD_FADE.y + 10 ) );
			types.Add( new VegType( "broadleaf", broadRecs, new VegTypeOptions { fade = BROAD_FADE, margin = 10, sortNear = true, near = Spec( "broadleaf", "broadleaf", true ) } ) );

			// canopy: trees + shrubs ( shrubs flagged by a negative vertical scale in iDat.y ); near geometry within TREE_NEAR / SHRUB_NEAR, octahedral impostors beyond. The leaf
			// and impostor atlases are baked now
			atlases = new VegAtlases( VegAtlases.LoadInfo() );
			atlases.Bake();
			atlases.SetGlobals();
			var canopyRecs = new List<VegRec>();
			foreach ( var r in recs.trees ) canopyRecs.Add( kind2( r, 0, TREE_NEAR + 10, true ) );
			foreach ( var r in recs.shrubs ) canopyRecs.Add( kind2( r, 0, SHRUB_NEAR + 10, true ) );
			var canopyNear = new VegMeshSpec { name = "canopy", mesh = Mesh( "canopy" ), material = Mat( "Tidewater/VegCanopy", "veg-canopy" ), castShadow = true };
			canopyNear.material.SetTexture( "_VegLeafAtlas", atlases.leaf );
			var impMat = Mat( "Tidewater/VegImpostor", "veg-impostor" );
			impMat.SetTexture( "_VegImpA", atlases.impA ); impMat.SetTexture( "_VegImpB", atlases.impB );
			var canopyFar = new VegMeshSpec { name = "canopy-far", mesh = atlases.quad, material = impMat, castShadow = false, fade = CANOPY_FAR };
			types.Add( new VegType( "canopy", canopyRecs, new VegTypeOptions { nearRange = TREE_NEAR, margin = 10, sortNear = true, sortFar = true, farRefresh = 16, near = canopyNear, far = canopyFar } ) );

			// the ground flora: meadow / dune grass, sea oats and creeper, from the density mask
			var mask = VegScatter.BuildGrassMask( site, out int mres );
			grass = new GrassField( terrain.data, mask, mres );

			built = terrain.village;
			stats = $"vegetation: {recs.palms.Count} palms, {recs.trees.Count} trees, {recs.shrubs.Count} shrubs, {recs.bananas.Count} bananas, {underRecs.Count} understory, {monsteraRecs.Count + broadRecs.Count} broadleaf; scatter {scatterMs:F0} ms, grass {grass.buildMs:F0} ms, total {sw.ElapsedMilliseconds} ms";
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			if ( Tidewater.Util.Studio.Is( cam ) ) return;
			Refresh();
			if ( types.Count == 0 ) return;
			if ( cam.cameraType == CameraType.Game && lastFrame != Time.frameCount )
			{
				lastFrame = Time.frameCount;
				Step( cam.transform.position, Application.isPlaying ? Math.Min( Time.deltaTime, 0.1f ) : 0f );
			}

			Draw( cam );
		}

		// the per-frame work of Vegetation.update: the gust field's offset, the globals and the refills of the near levels
		public void Step( Vector3 camWorld, float dt )
		{
			var p = new Vector3( camWorld.x, camWorld.y, - camWorld.z ); // the sim's frame
			var wd = G.windDir;
			float speed = 0.7f * G.windSpeed + 1.5f;
			gust.x += wd.x * speed * dt;
			gust.z += wd.y * speed * dt;
			Shader.SetGlobalVector( "_VegCam", new Vector4( p.x, p.y, p.z, 0 ) );
			Shader.SetGlobalVector( "_VegGust", new Vector4( gust.x, gust.z, 0, 0 ) );
			Shader.SetGlobalVector( "_VegNear", new Vector4( TREE_NEAR, SHRUB_NEAR, 0, 0 ) );

			// near-level refills happen only after the camera moved a few metres; at most two types per frame (most overdue first) so fast flights never stack up refills.
			// Teleports (anything far past the margin) refill everything at once.
			VegType a = null, b = null; float da = 0, db = 0;
			foreach ( var t in types )
			{
				float d = t.overdue( p );
				if ( d > t.margin - t.refreshDistance ) t.update( p, true );
				else if ( d > da ) { b = a; db = da; a = t; da = d; }
				else if ( d > db ) { b = t; db = d; }
			}

			if ( a != null ) a.update( p, true );
			if ( b != null ) b.update( p, true );
		}

		void Draw( Camera cam )
		{
			grass?.Draw( cam, gameObject.layer );
			var bounds = new Bounds( cam.transform.position, Vector3.one * 6000f );
			drawCalls = 0; drawnInstances = 0;
			foreach ( var t in types )
				foreach ( var l in t.levels )
				{
					l.upload();
					if ( l.count == 0 ) continue;
					block.SetVector( "_VegLod", new Vector4( l.lodRange.x, l.lodRange.y, l.lodRange.z, 0 ) );
					Graphics.DrawMeshInstancedProcedural( l.mesh, 0, l.material, bounds, l.count, block, l.castShadow ? ShadowCastingMode.On : ShadowCastingMode.Off, true, gameObject.layer, cam );
					drawCalls ++; drawnInstances += l.count;
				}
		}

		public string Stats()
		{
			var sb = new System.Text.StringBuilder( stats + "\n" );
			foreach ( var t in types )
				foreach ( var l in t.levels ) sb.AppendLine( $"  {t.name}/{l.name}: {l.count} of {t.inst.count} ( {l.count * l.trianglesPerInstance} triangles )" );
			if ( grass != null ) sb.AppendLine( "  " + grass.Info( CameraType.Game ) );
			sb.AppendLine( $"  {drawCalls} draws, {drawnInstances} instances" );
			return sb.ToString();
		}
	}
}
