using System;
using Newtonsoft.Json.Linq;
using Tidewater.Ocean;
using UnityEngine;
using UnityEngine.Rendering;
using V3 = Tidewater.Engine.Vector3;

// The Unity view of the humpback (Whale.js: the render object, load(), _createMaterial): loads the baked model (Resources/whale: humpback.json, humpback_mesh.bytes, the
// albedo and height maps), builds the three levels of detail and one material (Shaders/Whale), runs the brain and the rig (Whale) from the game camera and draws the
// level it chooses. The brain steps in beginCameraRendering of the game camera (once per frame, like Update would, so it also works when the Editor does not tick Update)
// while playing; in edit mode no time passes.
//   Mesh channels: position, normal (the model as the JS has it; the shader mirrors z), uv0 = skin uv (v flipped: Unity's textures start at the bottom), uv2 = ( rig.x, rig.y ),
//   uv3 = ( rig.z, rig.w ); the triangles are wound for the mirror.
namespace Tidewater.World.Marine
{
	[ExecuteAlways]
	public sealed class WhaleView : MonoBehaviour
	{
		public static WhaleView instance { get; private set; }

		public Whale whale { get; private set; }
		public WhaleBrain brain => whale != null ? whale.brain : null;
		// what the sound reads (SoundScape.whale): the brain's position, state and events
		public readonly Tidewater.Audio.WhaleBrain audio = new Tidewater.Audio.WhaleBrain();
		public string stats = "";
		public string lastDraw = "";
		public int forceLod = -1; // test hook: draw this level whatever the distance

		TerrainRenderer terrain;
		Tidewater.World.TerrainData dataSeen;
		OceanRenderer ocean;
		Material material;
		Mesh[] meshes;
		WhaleWater water = new WhaleWater();
		MeshRenderer[] renderers;
		Transform body;
		Texture2D albedo, height;
		int lastStepFrame = -1;
		static readonly Bounds BOUNDS = new Bounds( Vector3.zero, Vector3.one * 24f );

		static readonly int idPos = Shader.PropertyToID( "_WhalePos" ), idRot = Shader.PropertyToID( "_WhaleRot" ), idFlip = Shader.PropertyToID( "_WhaleFlip" ),
			idA = Shader.PropertyToID( "_WhaleA" ), idB = Shader.PropertyToID( "_WhaleB" ), idPecL = Shader.PropertyToID( "_WhalePecL" ), idPecR = Shader.PropertyToID( "_WhalePecR" ),
			idAlbedo = Shader.PropertyToID( "_WhaleAlbedo" ), idHeight = Shader.PropertyToID( "_WhaleHeight" );

		void OnEnable()
		{
			instance = this;
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
			if ( meshes != null ) foreach ( var m in meshes ) if ( m != null ) DestroyImmediate( m );
			meshes = null;
			if ( body != null ) DestroyImmediate( body.gameObject );
			body = null; renderers = null;
			if ( material != null ) DestroyImmediate( material );
			material = null;
			whale = null; dataSeen = null;
			water = new WhaleWater();
			WhaleWater.Clear();
		}

		// builds the whale once the terrain and the sea's water query exist (the brain allocates a query slot); again when the terrain is remade
		void Refresh()
		{
			if ( terrain == null ) terrain = FindAnyObjectByType<TerrainRenderer>();
			if ( ocean == null ) ocean = OceanRenderer.instance;
			if ( terrain == null || terrain.data == null || ocean == null || ocean.query == null ) return;
			if ( whale != null && terrain.data == dataSeen ) return;
			Release();
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var manifestAsset = Resources.Load<TextAsset>( "whale/humpback" );
			var meshAsset = Resources.Load<TextAsset>( "whale/humpback_mesh" );
			albedo = Resources.Load<Texture2D>( "whale/humpback_albedo" );
			height = Resources.Load<Texture2D>( "whale/humpback_height" );
			if ( manifestAsset == null || meshAsset == null || albedo == null || height == null ) { stats = "whale: the model is missing from Resources/whale"; return; }
			dataSeen = terrain.data;
			var manifest = JObject.Parse( manifestAsset.text );
			var brain = new WhaleBrain( terrain.data, ocean.query );
			var w = new Whale( brain, manifest ) { spray = ocean.spray };
			var bin = meshAsset.GetData<byte>().ToArray();
			var levels = ( JArray ) manifest[ "levels" ];
			meshes = new Mesh[ levels.Count ];
			int verts = 0, tris = 0;
			for ( int i = 0; i < levels.Count; i ++ ) { meshes[ i ] = BuildLevel( bin, levels[ i ] ); verts += meshes[ i ].vertexCount; tris += ( int ) ( ( long ) levels[ i ][ "indices" ] / 3 ); }

			ReadSkin( bin, levels[ levels.Count - 1 ], out w.skinPos, out w.skinNrm );

			material = new Material( Shader.Find( "Tidewater/Whale" ) ) { name = "whale", hideFlags = HideFlags.HideAndDontSave };
			material.SetTexture( idAlbedo, albedo );
			material.SetTexture( idHeight, height );
			material.SetVector( idA, new Vector4( ( float ) w.zHead, ( float ) w.dz, ( float ) w.heightMin, ( float ) w.heightRange ) );
			material.SetVector( idPecL, new Vector4( ( float ) w.pecL.x, ( float ) w.pecL.y, ( float ) w.pecL.z, 0 ) );
			material.SetVector( idPecR, new Vector4( ( float ) w.pecR.x, ( float ) w.pecR.y, ( float ) w.pecR.z, 0 ) );
			// one object per level of detail (the vertex stage poses the mesh in world space itself; the object only has to put the culling bounds on the whale)
			body = new GameObject( "Whale body" ) { hideFlags = HideFlags.HideAndDontSave }.transform;
			body.SetParent( transform, false );
			renderers = new MeshRenderer[ meshes.Length ];
			for ( int i = 0; i < meshes.Length; i ++ )
			{
				var go = new GameObject( "lod" + i ) { hideFlags = HideFlags.HideAndDontSave };
				go.transform.SetParent( body, false );
				go.AddComponent<MeshFilter>().sharedMesh = meshes[ i ];
				var r = renderers[ i ] = go.AddComponent<MeshRenderer>();
				r.sharedMaterial = material;
				r.shadowCastingMode = ShadowCastingMode.On;
				r.receiveShadows = true;
				r.enabled = false;
			}

			whale = w;
			w.Update( 0, null );
			Upload();
			stats = $"whale: {verts} vertices, {tris} triangles in {levels.Count} levels, loaded in {sw.ElapsedMilliseconds} ms";
		}

		// a level's positions and normals as they are in the model (for the remoras' skin points)
		static void ReadSkin( byte[] bin, JToken lv, out float[] pos, out float[] nrm )
		{
			int n = ( int ) lv[ "vertices" ];
			pos = new float[ n * 3 ]; Buffer.BlockCopy( bin, ( int ) lv[ "position" ], pos, 0, n * 12 );
			var q = new short[ n * 3 ]; Buffer.BlockCopy( bin, ( int ) lv[ "normal" ], q, 0, n * 6 );
			nrm = new float[ n * 3 ];
			for ( int i = 0; i < n * 3; i ++ ) nrm[ i ] = Mathf.Max( q[ i ] / 32767f, -1f );
		}

		// one level of detail: the baked arrays of the binary (the offsets of the manifest) to a Mesh
		static Mesh BuildLevel( byte[] bin, JToken lv )
		{
			int n = ( int ) lv[ "vertices" ], ni = ( int ) lv[ "indices" ];
			var pos = new float[ n * 3 ]; Buffer.BlockCopy( bin, ( int ) lv[ "position" ], pos, 0, n * 12 );
			var nrm = new short[ n * 3 ]; Buffer.BlockCopy( bin, ( int ) lv[ "normal" ], nrm, 0, n * 6 );
			var uv = new float[ n * 2 ]; Buffer.BlockCopy( bin, ( int ) lv[ "uv" ], uv, 0, n * 8 );
			var rig = new float[ n * 4 ]; Buffer.BlockCopy( bin, ( int ) lv[ "rig" ], rig, 0, n * 16 );
			var idx = new uint[ ni ]; Buffer.BlockCopy( bin, ( int ) lv[ "index" ], idx, 0, ni * 4 );
			var v = new Vector3[ n ]; var nn = new Vector3[ n ]; var t0 = new Vector2[ n ]; var t2 = new Vector2[ n ]; var t3 = new Vector2[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				v[ i ] = new Vector3( pos[ i * 3 ], pos[ i * 3 + 1 ], pos[ i * 3 + 2 ] );
				nn[ i ] = new Vector3( Mathf.Max( nrm[ i * 3 ] / 32767f, -1f ), Mathf.Max( nrm[ i * 3 + 1 ] / 32767f, -1f ), Mathf.Max( nrm[ i * 3 + 2 ] / 32767f, -1f ) );
				t0[ i ] = new Vector2( uv[ i * 2 ], 1f - uv[ i * 2 + 1 ] );
				t2[ i ] = new Vector2( rig[ i * 4 ], rig[ i * 4 + 1 ] );
				t3[ i ] = new Vector2( rig[ i * 4 + 2 ], rig[ i * 4 + 3 ] );
			}

			var tri = new int[ ni ];
			for ( int i = 0; i + 2 < ni; i += 3 ) { tri[ i ] = ( int ) idx[ i ]; tri[ i + 1 ] = ( int ) idx[ i + 2 ]; tri[ i + 2 ] = ( int ) idx[ i + 1 ]; } // wound for the mirror
			var m = new Mesh { name = "whale-" + ( string ) lv[ "name" ], indexFormat = IndexFormat.UInt32 };
			m.SetVertices( v ); m.SetNormals( nn ); m.SetUVs( 0, t0 ); m.SetUVs( 2, t2 ); m.SetUVs( 3, t3 );
			m.SetTriangles( tri, 0, false );
			m.bounds = BOUNDS; // the mesh is posed in world space by the vertex stage: the draw's matrix puts these bounds on the whale
			m.UploadMeshData( true );
			return m;
		}

		void Upload()
		{
			var w = whale; var b = w.brain;
			material.SetVectorArray( idPos, w.uPos );
			material.SetVectorArray( idRot, w.uRot );
			material.SetVectorArray( idFlip, w.uFlip );
			material.SetVector( idB, new Vector4( ( float ) b.water, ( float ) Math.Exp( - b.wetAge / 12 ), 0, 0 ) );
		}

		void OnBeginCamera( ScriptableRenderContext ctx, Camera cam )
		{
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;
			if ( Tidewater.Util.Studio.Is( cam ) ) return; // the fish portrait's studio
			Refresh();
			if ( whale == null ) return;
			var p = cam.transform.position;
			if ( cam.cameraType == CameraType.Game )
			{
				// the behaviour steps once a frame (edit mode: no time passes); the level of detail and the culling follow the camera every time it renders
				// (the Editor does not advance the frame count while it is unfocused, so a camera moved by hand needs them again)
				bool step = lastStepFrame != Time.frameCount && Application.isPlaying;
				lastStepFrame = Time.frameCount;
				double dt = step ? Math.Min( Time.deltaTime, 0.1f ) : 0;
				whale.Update( dt, new V3( p.x, p.y, - p.z ) );
				if ( step || ! Application.isPlaying ) water.Update( whale, dt, whale.spray );
				Upload();
				PublishAudio();
			}

			Show();
		}

		// the level of detail to draw, on the whale
		void Show()
		{
			int lod = forceLod >= 0 ? forceLod : whale.lod; // -1: culled
			var b = whale.brain;
			body.position = new Vector3( ( float ) b.position.x, ( float ) b.position.y, - ( float ) b.position.z );
			for ( int i = 0; i < renderers.Length; i ++ ) renderers[ i ].enabled = i == lod;
			lastDraw = $"frame {Time.frameCount} lod {lod} at {b.position.x:F1},{b.position.y:F1},{b.position.z:F1}";
		}

		void PublishAudio()
		{
			var b = whale.brain; var a = audio;
			a.x = b.position.x; a.y = b.position.y; a.z = b.position.z; a.state = b.state; a.water = b.water; a.yaw = b.yaw;
			a.blow = b.blow; a.flukeUp = b.flukeUp; a.breaches = b.breaches; a.splashes = b.splashes;
		}

		// advances the behaviour by hand (the Editor does not tick Update when unfocused): steps of dt seconds, the camera at the given sim position
		public void Step( int steps, double dt = 1.0 / 60 )
		{
			Refresh();
			if ( whale == null ) return;
			for ( int i = 0; i < steps; i ++ ) { whale.Update( dt, null ); water.Update( whale, dt, whale.spray ); }
			Upload();
			PublishAudio();
			Show();
		}
	}
}
