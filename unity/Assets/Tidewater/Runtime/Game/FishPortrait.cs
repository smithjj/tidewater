using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using Tidewater.World.Fish;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Color = UnityEngine.Color;
using Matrix4 = Tidewater.Engine.Matrix4;
using Quaternion = Tidewater.Engine.Quaternion;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/game/FishPortrait.js (the live half): the studio portrait of the fish just caught, the big picture on the catch card. The real game model (the fish props'
// mesh and skin, Tidewater/Fish's surface) side-on and horizontal, nose left as in a field guide, in a neutral photo studio: a key light from the upper front left, a grey
// sweep and one softbox whose highlight slides along the flank. The card animation (slide in, tail flick, settle, slow turn) is the JS's.
//
// How it is drawn here: the studio is a private scene far above the island (layer Studio.LAYER, so the world's per-camera hooks skip its camera). One HDRP camera renders
// the fish into an HDR texture, twice the card's size on each axis, with the fish shader's studio pass (Tidewater/FishStudio: the JS STUDIO_LIGHTING in HLSL, so no HDRP
// light, sky, fog or post touches it; the camera's fixed exposure makes one scene unit 1.0). A resolve pass (FishPortraitPost.shader: ACES at the JS exposure on each
// sample, a 2 x 2 box, the coverage as alpha) writes into an sRGB target writes the picture the card draws, Texture.
//
// Not ported: the thumbnails (the JS draws the species thumbnails of the guide with the same machinery; the guide is not in the Unity port yet).
namespace Tidewater.Game
{
	public sealed class FishPortrait
	{
		// the studio (sim axes; the fish is parked far from the island)
		static readonly Vector3 STUDIO = new Vector3( 0, 6000, 0 );
		// model frame (nose +z, back +y, left flank +x) -> nose toward -x (left on screen), back up, left flank toward +z (the camera)
		static readonly Matrix4 PORTRAIT = new Matrix4().makeBasis( new Vector3( 0, 0, 1 ), new Vector3( 0, 1, 0 ), new Vector3( - 1, 0, 0 ) );
		const int SS = 2;        // supersampling per axis (the silhouette is the whole picture: no aliased edges)
		const float VFOV = 16f;  // degrees: a long lens, little perspective distortion
		const float EXPOSURE = 1.15f;
		const int MAX_WIDTH = 1600;

		static readonly Matrix4 _m = new Matrix4(), _f = new Matrix4();
		static readonly Quaternion _q = new Quaternion(), _q2 = new Quaternion();
		static readonly Vector3 _p = new Vector3(), _s = new Vector3(), _a = new Vector3();
		static readonly int idA = Shader.PropertyToID( "_FishA" ), idB = Shader.PropertyToID( "_FishB" ), idC = Shader.PropertyToID( "_FishC" );
		static readonly int idKey = Shader.PropertyToID( "_StudioKey" ), idKeyColor = Shader.PropertyToID( "_StudioKeyColor" ), idEnv = Shader.PropertyToID( "_StudioEnv" );

		sealed class Slot
		{
			public GameObject go; public MeshRenderer mr; public MaterialPropertyBlock mpb = new MaterialPropertyBlock(); public double pattern;
		}

		readonly Transform root;
		readonly Camera cam;
		readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
		readonly Dictionary<string, UnityEngine.Mesh> meshes = new Dictionary<string, UnityEngine.Mesh>();
		UnityEngine.Material material, post;
		RenderTexture hdr, picture;
		GameObject volumeGo;
		VolumeProfile volumeProfile;

		// the studio light (FishPortrait.js this.light): sim space
		readonly Vector3 keyDir = new Vector3( - 0.45, 0.78, 0.55 ).normalize();
		readonly Vector3 keyColor = new Vector3( 3.1, 3.0, 2.85 );
		const float ENV = 1.0f;
		double sweep = 0.4;

		string species; double kg, t;
		public bool Live => species != null;
		// the picture: straight alpha, an sRGB target (`width` x `height`; null until the first frame)
		public Texture Texture => picture;

		public FishPortrait( Transform parent )
		{
			// a leftover from a domain reload (the objects are not saved): drop it
			var old = parent != null ? parent.Find( "FishPortrait" ) : null;
			if ( old != null ) UnityEngine.Object.DestroyImmediate( old.gameObject );
			root = new GameObject( "FishPortrait" ) { hideFlags = HideFlags.DontSave, layer = Studio.LAYER }.transform;
			root.SetParent( parent, false );

			var camGo = new GameObject( "Studio camera" ) { hideFlags = HideFlags.DontSave, layer = Studio.LAYER };
			camGo.transform.SetParent( root, false );
			cam = camGo.AddComponent<Camera>();
			cam.enabled = false; // rendered by hand, once a frame, while the card is up
			cam.cullingMask = 1 << Studio.LAYER;
			cam.clearFlags = CameraClearFlags.SolidColor;
			cam.backgroundColor = new Color( 0, 0, 0, 0 );
			cam.fieldOfView = VFOV;
			cam.allowMSAA = false; cam.allowHDR = true; cam.allowDynamicResolution = false;
			cam.nearClipPlane = 0.05f; cam.farClipPlane = 200f;
			var hd = camGo.AddComponent<HDAdditionalCameraData>();
			hd.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
			hd.backgroundColorHDR = new Color( 0, 0, 0, 0 );
			hd.volumeLayerMask = 1 << Studio.LAYER; // only the studio's own volume
			hd.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
			hd.stopNaNs = false;
			hd.customRenderingSettings = true;
			var fs = hd.renderingPathCustomFrameSettings;
			var mask = hd.renderingPathCustomFrameSettingsOverrideMask;
			// nothing of the world's frame is wanted: only the forward-only studio pass of the fish
			foreach ( var f in new[] { FrameSettingsField.Postprocess, FrameSettingsField.Volumetrics, FrameSettingsField.SSR, FrameSettingsField.SSAO, FrameSettingsField.ContactShadows,
				FrameSettingsField.ShadowMaps, FrameSettingsField.Decals, FrameSettingsField.Water, FrameSettingsField.VolumetricClouds, FrameSettingsField.Transmission } )
			{
				fs.SetEnabled( f, false );
				mask.mask[ ( uint ) f ] = true;
			}
			hd.renderingPathCustomFrameSettings = fs;
			hd.renderingPathCustomFrameSettingsOverrideMask = mask;

			// the studio's volume: a fixed exposure that makes one scene unit 1.0 (HDRP exposes by 1 / ( 1.2 * 2^EV )), no sky, no fog, no clouds
			volumeGo = new GameObject( "Studio volume" ) { hideFlags = HideFlags.DontSave, layer = Studio.LAYER };
			volumeGo.transform.SetParent( root, false );
			var vol = volumeGo.AddComponent<Volume>();
			vol.isGlobal = true; vol.priority = 1000;
			volumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
			volumeProfile.hideFlags = HideFlags.DontSave;
			var exp = volumeProfile.Add<Exposure>( false );
			exp.active = true;
			exp.mode.Override( ExposureMode.Fixed );
			exp.fixedExposure.Override( ( float ) - Math.Log( 1.2, 2 ) );
			var env = volumeProfile.Add<VisualEnvironment>( false );
			env.active = true;
			env.skyType.Override( 0 );
			var fog = volumeProfile.Add<Fog>( false );
			fog.active = true;
			fog.enabled.Override( false );
			var clouds = volumeProfile.Add<VolumetricClouds>( false );
			clouds.active = true;
			clouds.enable.Override( false );
			vol.sharedProfile = volumeProfile;
		}

		public bool Alive => root != null;

		public void Dispose()
		{
			Hide();
			foreach ( var m in meshes.Values ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			meshes.Clear();
			slots.Clear();
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
			if ( post != null ) UnityEngine.Object.DestroyImmediate( post );
			if ( hdr != null ) { hdr.Release(); UnityEngine.Object.DestroyImmediate( hdr ); }
			if ( picture != null ) { picture.Release(); UnityEngine.Object.DestroyImmediate( picture ); }
			if ( volumeProfile != null ) UnityEngine.Object.DestroyImmediate( volumeProfile );
			if ( root != null ) UnityEngine.Object.DestroyImmediate( root.gameObject );
		}

		// start the card's animation for a fish
		public void Show( string species, double kg )
		{
			this.species = species; this.kg = kg; t = 0;
		}

		public void Hide()
		{
			species = null;
			foreach ( var s in slots.Values ) if ( s.go != null ) s.go.SetActive( false );
		}

		Slot SlotFor( string id )
		{
			if ( slots.TryGetValue( id, out var s ) ) return s;
			var f = FishTable.Get( id );
			// most species are a fish body from SPECIES; a trap catch (the lobster) has its own model
			string kind = f.kind ?? "whole", model = kind == "whole" ? f.model : null;
			if ( material == null )
			{
				FishPropsView.PublishSkin();
				material = new UnityEngine.Material( Shader.Find( "Tidewater/FishStudio" ) ) { name = "portrait-fish", hideFlags = HideFlags.HideAndDontSave };
			}

			string key = kind + ":" + ( model ?? "" );
			if ( ! meshes.TryGetValue( key, out var mesh ) ) meshes[ key ] = mesh = FishMesh.Build( FishProps.geometryOf( kind, model, 0 ), "portrait-" + key );
			var go = new GameObject( "portrait-" + id ) { hideFlags = HideFlags.DontSave, layer = Studio.LAYER };
			go.transform.SetParent( root, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = material;
			mr.shadowCastingMode = ShadowCastingMode.Off;
			mr.receiveShadows = false;
			go.SetActive( false );
			s = new Slot { go = go, mr = mr, pattern = kind == "whole" ? FishSpecies.SPECIES[ model ].pattern : 0 };
			slots[ id ] = s;
			return s;
		}

		struct Pose { public double x, y, yaw, pitch, roll, curl, jaw; }

		// card animation: the fish slides in from the left with a tail flick and settles, then turns slowly while the softbox highlight sweeps along its flank
		static Pose LiveTransform( double t )
		{
			Func<double, double> ease = x => 1 - Math.Pow( 1 - Math.Min( 1, Math.Max( 0, x ) ), 3 );
			double inT = ease( t / 0.7 );
			// overshoot on arrival (a spring)
			double settle = Math.Exp( - Math.Max( 0, t - 0.55 ) * 4 ) * Math.Sin( Math.Max( 0, t - 0.55 ) * 9 );
			return new Pose
			{
				x = - ( 1 - inT ) * 1.6 + settle * 0.05,
				y = ( 1 - inT ) * 0.25,
				curl = Math.Sin( t * 13 ) * 0.9 * Math.Exp( - t * 1.6 ) * ( t < 0.1 ? t * 10 : 1 ),
				yaw = - ( 1 - inT ) * 0.9 + Math.Sin( t * 0.55 ) * 0.2 * inT,
				roll = ( 1 - inT ) * 0.5 + Math.Sin( t * 0.4 + 1 ) * 0.05,
				pitch = ( 1 - inT ) * - 0.3 + Math.Sin( t * 0.7 ) * 0.03,
				jaw = 0.12 + 0.12 * Math.Max( 0, Math.Sin( t * 5 ) ) * Math.Exp( - t * 0.5 ),
			};
		}

		// the fish into the studio (FishPortrait.js _place + _write): the pose, then the model frame turned by PORTRAIT; returns its length (m)
		double Place( Slot s, string id, double kg, Pose o )
		{
			double L = CatchDisplay.fishLength( id, kg );
			_q.setFromAxisAngle( _a.set( 0, 1, 0 ), o.yaw );
			_q2.setFromAxisAngle( _a.set( 0, 0, 1 ), o.pitch );
			_q.multiply( _q2 );
			_q2.setFromAxisAngle( _a.set( 1, 0, 0 ), o.roll );
			_q.multiply( _q2 );
			_f.makeRotationFromQuaternion( _q ).setPosition( STUDIO.x + o.x * L, STUDIO.y + o.y * L, STUDIO.z );
			_m.multiplyMatrices( _f, PORTRAIT );
			_m.decompose( _p, _q, _s );
			s.go.SetActive( true );
			// the same conversion as FishPropsView.Make
			s.go.transform.SetPositionAndRotation( Sim.ToUnity( _p.x, _p.y, _p.z ), new UnityEngine.Quaternion( ( float ) - _q.x, ( float ) - _q.y, ( float ) _q.z, ( float ) _q.w ) );
			s.go.transform.localScale = UnityEngine.Vector3.one * ( float ) L;
			s.mpb.SetVector( idA, new Vector4( ( float ) ( s.pattern + 0.37 * 0.9 ), ( float ) o.curl, 0f, ( float ) o.jaw ) ); // pattern + seed, curl, sag, jaw
			s.mpb.SetVector( idB, new Vector4( 0.08f, 1f, 0f, 0f ) );  // cloudy eye, wet, dried, blood
			s.mpb.SetVector( idC, new Vector4( ( float ) L, 1f, 0f, 0f ) );
			s.mr.SetPropertyBlock( s.mpb );
			return L;
		}

		// frame the fish: its body length fills `fill` of the width (FishPortrait.js _frame)
		void Frame( double L, int w, int h, double fill )
		{
			cam.aspect = ( float ) w / h;
			double tanH = Math.Tan( VFOV * Math.PI / 360 ) * cam.aspect;
			double dist = ( L * 1.08 / fill ) / 2 / tanH;
			cam.nearClipPlane = ( float ) Math.Max( 0.02, dist - L * 2 );
			cam.farClipPlane = ( float ) ( dist + L * 4 );
			// a touch from above: the back and the flank both read
			var pos = Sim.ToUnity( STUDIO.x, STUDIO.y + dist * 0.06, STUDIO.z + dist );
			cam.transform.position = pos;
			cam.transform.rotation = UnityEngine.Quaternion.LookRotation( Sim.ToUnity( STUDIO.x, STUDIO.y, STUDIO.z ) - pos, UnityEngine.Vector3.up );
		}

		// once a frame while the card is up: `width` x `height` is the stage in pixels. Returns false when there is nothing to show (no fish, or a failure)
		public bool Update( double dt, int width, int height )
		{
			if ( species == null || width < 2 || height < 2 ) return false;
			float aspect = ( float ) height / width;
			width = Mathf.Min( width, MAX_WIDTH );
			height = Mathf.Max( 2, Mathf.RoundToInt( width * aspect ) );
			EnsureTargets( width, height );

			t += dt;
			var slot = SlotFor( species );
			var pose = LiveTransform( t );
			double L = Place( slot, species, kg, pose );
			sweep = - 1.1 + ( ( t * 0.32 ) % 2.6 );
			Frame( L, hdr.width, hdr.height, 0.78 );

			material.SetVector( idKey, new Vector4( ( float ) keyDir.x, ( float ) keyDir.y, ( float ) keyDir.z, 0 ) );
			material.SetVector( idKeyColor, new Vector4( ( float ) keyColor.x, ( float ) keyColor.y, ( float ) keyColor.z, 0 ) );
			material.SetVector( idEnv, new Vector4( ( float ) sweep, ENV, 0, 0 ) );
			cam.targetTexture = hdr;
			cam.Render();
			slot.go.SetActive( false ); // the world's cameras must not see it
			cam.targetTexture = null;

			post.SetFloat( "_SS", SS );
			post.SetFloat( "_Exposure", EXPOSURE );
			post.SetVector( "_DstSize", new Vector4( picture.width, picture.height, 0, 0 ) );
			Graphics.Blit( hdr, picture, post, 0 );
			return true;
		}

		void EnsureTargets( int w, int h )
		{
			if ( post == null ) post = new UnityEngine.Material( Shader.Find( "Hidden/Tidewater/FishPortraitPost" ) ) { hideFlags = HideFlags.HideAndDontSave };
			if ( picture != null && picture.width == w && picture.height == h && hdr != null ) return;
			if ( hdr != null ) { hdr.Release(); UnityEngine.Object.DestroyImmediate( hdr ); }
			if ( picture != null ) { picture.Release(); UnityEngine.Object.DestroyImmediate( picture ); }
			hdr = new RenderTexture( w * SS, h * SS, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear ) { name = "portrait-hdr", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, antiAliasing = 1 };
			hdr.Create();
			picture = new RenderTexture( w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB ) { name = "portrait", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, antiAliasing = 1 };
			picture.Create();
		}
	}
}
