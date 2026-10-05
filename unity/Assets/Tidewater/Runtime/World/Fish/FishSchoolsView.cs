using System;
using Tidewater.Fx;
using Tidewater.Ocean;
using UnityEngine;
using UnityEngine.Rendering;

// The Unity view of the swimming schools (Fish.js: FishSchools, the render object): runs the simulation (FishSchools) from the camera and draws the
// fish. The JS draws every fish with the instance's own record in one render object (ReefBatch: one indirect draw per model and level of detail); here
// the lists and records of the batch (FishBatch) go to GPU buffers once per camera and every non-empty (model, level) is one procedural instanced draw
// (Graphics.DrawMeshInstancedProcedural) with the swimming vertex stage of Shaders/Fish/TidewaterFishSwim.shader (FishFragment.hlsl, FISH_SWIM):
//   - the main list: the instances at one level of detail; the fade list: the instances in a level-of-detail cross-fade (two draws, the outgoing level
//     and the incoming one, dithered in complementary pixels; the last level fades out at the far culling distance);
//   - culled and levelled per camera in FishSchools.cull (distance, view frustum, size on screen); the state (heading, bank, wave phase) advances once
//     per simulation step;
//   - the simulation steps in beginCameraRendering of the game camera (once per frame, like Update would, so it also works when the Editor does not
//     tick Update) while playing; in edit mode it only (re)activates the groups around the camera, the fish keep their layout (Step() moves them).
// Not drawn: no shadows (the JS fish batch casts none), no motion vectors.
namespace Tidewater.World.Fish
{
	[ExecuteAlways]
	public sealed class FishSchoolsView : MonoBehaviour
	{
		public static FishSchoolsView instance { get; private set; }

		public FishSchools schools { get; private set; }
		public string stats = "";
		public int drawCalls, drawnInstances; // of the last camera drawn

		TerrainRenderer terrain;
		Tidewater.World.TerrainData dataSeen;
		Material material;
		Mesh[][] meshes; // per model: the four levels of detail (the rays and the turtle share the second)
		GraphicsBuffer instBuf, listBuf, fadeBuf;
		MaterialPropertyBlock block; // (native object: made in OnEnable, not in a field initializer)
		readonly Tidewater.Engine.Vector3 player = new Tidewater.Engine.Vector3();
		[NonSerialized] readonly Plane[] planes = new Plane[ 6 ];
		[NonSerialized] double[] simPlanes; // (made on first use: a serialized view comes back with empty arrays)
		int lastStepFrame = - 1;
		OceanRenderer ocean;
		readonly SprayEmitOptions splashOpts = new SprayEmitOptions { spread = 0.9f, life = 1.1f };

		static readonly int idInstances = Shader.PropertyToID( "_FishInstances" ), idList = Shader.PropertyToID( "_FishList" ), idFadeList = Shader.PropertyToID( "_FishFadeList" ), idDraw = Shader.PropertyToID( "_FishDraw" );

		void OnEnable()
		{
			instance = this;
			block = new MaterialPropertyBlock();
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			if ( instance == this ) instance = null;
			Release();
		}

		void Release()
		{
			if ( meshes != null ) foreach ( var set in meshes ) foreach ( var m in set ) if ( m != null ) DestroyImmediate( m );
			meshes = null;
			if ( material != null ) DestroyImmediate( material );
			material = null;
			instBuf?.Release(); listBuf?.Release(); fadeBuf?.Release();
			instBuf = listBuf = fadeBuf = null;
			schools = null; dataSeen = null;
		}

		// builds the schools once the terrain exists (the village flattens its pads into the height map first); again when the terrain is remade
		void Refresh()
		{
			if ( terrain == null ) terrain = FindAnyObjectByType<TerrainRenderer>();
			if ( terrain == null || terrain.data == null || terrain.shoreField == null ) return;
			if ( schools != null && terrain.data == dataSeen ) return;
			Release();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			dataSeen = terrain.data;
			var reef = new Tidewater.Engine.Vector3( WorldLayout.Reef.x, 0, WorldLayout.Reef.z );
			// no reef framework yet (Reef.js: floorHeightAt, the coral anchors): the seabed and the reef centre stand in
			schools = new FishSchools( terrain.data, reef, WorldLayout.Reef.radius + 10, shoreField: terrain.shoreField );
			schools.sprayEmit = Splash;
			FishPropsView.PublishSkin();
			material = new Material( Shader.Find( "Tidewater/FishSwim" ) ) { name = "fish-swim", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
			meshes = new Mesh[ schools.models.Count ][];
			int tris = 0, verts = 0;
			for ( int m = 0; m < schools.models.Count; m ++ )
			{
				var geos = FishSchools.modelGeometries( schools.models[ m ] );
				meshes[ m ] = new Mesh[ 4 ];
				for ( int l = 0; l < 4; l ++ )
				{
					int shared = Array.IndexOf( geos, geos[ l ] );
					meshes[ m ][ l ] = shared < l ? meshes[ m ][ shared ] : FishMesh.Build( geos[ l ], "fish-swim-" + schools.models[ m ] + "-" + l );
					if ( shared == l ) { tris += geos[ l ].index.array.Length / 3; verts += geos[ l ].getAttribute( "position" ).array.Length / 3; }
				}
			}

			int n = schools.fishCount;
			instBuf = new GraphicsBuffer( GraphicsBuffer.Target.Structured, n * 4, 16 );
			listBuf = new GraphicsBuffer( GraphicsBuffer.Target.Structured, n * 2, 4 );
			fadeBuf = new GraphicsBuffer( GraphicsBuffer.Target.Structured, n * 2, 4 );
			stats = $"schools: {n} fish in {schools.groups.Count} groups, {schools.models.Count} models ({verts} vertices, {tris} triangles) in {sw.ElapsedMilliseconds} ms";
		}

		// spray of a leaping mullet (Fish.js splash): the sim's space is the spray's
		void Splash( Tidewater.Engine.Vector3 p, Tidewater.Engine.Vector3 v, int count, double size )
		{
			if ( ocean == null ) ocean = FindAnyObjectByType<OceanRenderer>();
			var spray = ocean != null ? ocean.spray : null;
			if ( spray == null ) return;
			spray.Emit( new Vector3( ( float ) p.x, ( float ) p.y, ( float ) p.z ), new Vector3( ( float ) v.x, ( float ) v.y, ( float ) v.z ), count, ( float ) size, SprayKind.DROPLET, splashOpts );
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			if ( Tidewater.Util.Studio.Is( cam ) ) return; // the fish portrait's studio
			Refresh();
			if ( schools == null ) return;
			if ( cam.cameraType == CameraType.Game && lastStepFrame != Time.frameCount )
			{
				lastStepFrame = Time.frameCount;
				var p = cam.transform.position;
				player.set( p.x, p.y, - p.z );
				// edit mode: no time passes (the groups around the camera wake up; the fish stay where they are)
				schools.setWhale( Tidewater.World.Marine.WhaleView.instance?.whale );
				schools.update( Application.isPlaying ? Math.Min( Time.deltaTime, 0.1f ) : 0, player );
			}

			Draw( cam );
		}

		// advances the simulation by hand (the Editor does not tick Update when unfocused): steps of dt seconds, the swimmer at the camera
		public void Step( int steps, double dt = 1.0 / 60, Camera cam = null )
		{
			Refresh();
			if ( schools == null ) return;
			cam = cam ?? Camera.main;
			var p = cam.transform.position;
			player.set( p.x, p.y, - p.z );
			schools.setWhale( Tidewater.World.Marine.WhaleView.instance?.whale );
			for ( int i = 0; i < steps; i ++ ) schools.update( dt, player );
		}

		void Draw( Camera cam )
		{
			var s = schools;
			if ( ! s.anyActive ) return;
			if ( simPlanes == null || simPlanes.Length != 24 ) simPlanes = new double[ 24 ];
			GeometryUtility.CalculateFrustumPlanes( cam, planes );
			// the frustum in sim space (z mirrored)
			for ( int i = 0; i < 6; i ++ )
			{
				var nrm = planes[ i ].normal;
				simPlanes[ i * 4 ] = nrm.x; simPlanes[ i * 4 + 1 ] = nrm.y; simPlanes[ i * 4 + 2 ] = - nrm.z; simPlanes[ i * 4 + 3 ] = planes[ i ].distance;
			}

			var cp = cam.transform.position;
			// pixels per metre at 1 m: level of detail by the fish's size on screen
			double pxScale = cam.projectionMatrix[ 1, 1 ] * cam.pixelHeight * 0.5;
			s.cull( cp.x, cp.y, - cp.z, simPlanes, pxScale );

			var b = s.batch;
			instBuf.SetData( b.data );
			if ( b.visibleInstances > 0 ) listBuf.SetData( b.list, 0, 0, b.visibleInstances );
			if ( b.fadeInstances > 0 ) fadeBuf.SetData( b.fadeList, 0, 0, b.fadeInstances );
			material.SetBuffer( idInstances, instBuf );
			material.SetBuffer( idList, listBuf );
			material.SetBuffer( idFadeList, fadeBuf );
			var bounds = new Bounds( cp, Vector3.one * 400f );
			drawCalls = 0; drawnInstances = 0;
			int kinds = b.kinds;
			for ( int k = 0; k < kinds; k ++ )
			{
				var mesh = meshes[ k >> 2 ][ k & 3 ];
				if ( b.counts[ k ] > 0 )
				{
					block.SetVector( idDraw, new Vector4( b.baseArray[ k ], 0, 0, 0 ) );
					Graphics.DrawMeshInstancedProcedural( mesh, 0, material, bounds, b.counts[ k ], block, ShadowCastingMode.Off, true, gameObject.layer, cam );
					drawCalls ++; drawnInstances += b.counts[ k ];
				}

				if ( b.fadeCounts[ k ] > 0 )
				{
					block.SetVector( idDraw, new Vector4( b.fadeBaseArray[ k ], 1, 0, 0 ) );
					Graphics.DrawMeshInstancedProcedural( mesh, 0, material, bounds, b.fadeCounts[ k ], block, ShadowCastingMode.Off, true, gameObject.layer, cam );
					drawCalls ++; drawnInstances += b.fadeCounts[ k ];
				}
			}
		}
	}
}
