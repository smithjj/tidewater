using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using Tidewater.World.Fish;
using UnityEngine;
using Matrix4 = Tidewater.Engine.Matrix4;
using Quaternion = Tidewater.Engine.Quaternion;
using Vector3 = Tidewater.Engine.Vector3;

// Port of src/game/CatchDisplay.js (the hanging half): the fish you just landed, hanging from the line by the gills and flapping, for a moment. The
// JS builds one props batch with a slot per catchable species parked far below the world; here a slot is one renderer with the fish's mesh and skin
// (the fish props' shader, Tidewater/Fish), built the first time the species is shown and switched off when it is hidden. The stall's fish on ice
// are FishPropsView's (the village), not this class's.
namespace Tidewater.Game
{
	public sealed class CatchDisplay
	{
		// model frame (nose +z, back +y, left flank +x) -> hanging from a hook through the gills: nose up, left flank toward +z, back toward +x
		// (FishProps' "gill" pose)
		static readonly Matrix4 GILL = new Matrix4().makeBasis( new Vector3( 0, 0, 1 ), new Vector3( 1, 0, 0 ), new Vector3( 0, 1, 0 ) );
		static readonly Matrix4 _m = new Matrix4(), _f = new Matrix4();
		static readonly Quaternion _q = new Quaternion();
		static readonly Vector3 _p = new Vector3(), _s = new Vector3(), _a = new Vector3();
		static readonly int idA = Shader.PropertyToID( "_FishA" ), idB = Shader.PropertyToID( "_FishB" ), idC = Shader.PropertyToID( "_FishC" );

		sealed class Slot
		{
			public GameObject go; public MeshRenderer mr; public MaterialPropertyBlock mpb = new MaterialPropertyBlock();
			public double pattern, seed; public double[] anchor;
		}

		readonly Transform root;
		readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
		readonly Dictionary<string, UnityEngine.Mesh> meshes = new Dictionary<string, UnityEngine.Mesh>();
		readonly System.Random random = new System.Random();
		UnityEngine.Material material;
		public string shown;
		double t;

		public CatchDisplay( Transform parent )
		{
			// a leftover from a domain reload (the objects are not saved): drop it
			var old = parent != null ? parent.Find( "CatchDisplay" ) : null;
			if ( old != null ) UnityEngine.Object.DestroyImmediate( old.gameObject );
			root = new GameObject( "CatchDisplay" ) { hideFlags = HideFlags.DontSave }.transform;
			root.SetParent( parent, false );
		}

		public bool Alive => root != null;

		public void Dispose()
		{
			foreach ( var m in meshes.Values ) if ( m != null ) UnityEngine.Object.DestroyImmediate( m );
			meshes.Clear();
			slots.Clear();
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
			if ( root != null ) UnityEngine.Object.DestroyImmediate( root.gameObject );
		}

		// body length (m) for a weight: the species' length-weight relation (FishTable.fishLengthCm), so the fish on the line is the size the catch
		// card reports
		public static double fishLength( string species, double kg ) => FishTable.fishLengthCm( species, kg ) / 100;

		Slot SlotFor( string species )
		{
			if ( slots.TryGetValue( species, out var s ) ) return s;
			var f = FishTable.Get( species );
			string kind = f.kind ?? "whole", model = kind == "whole" ? f.model : null;
			if ( material == null )
			{
				FishPropsView.PublishSkin();
				material = new UnityEngine.Material( Shader.Find( "Tidewater/Fish" ) ) { name = "catch-fish", hideFlags = HideFlags.HideAndDontSave };
			}

			string key = kind + ":" + ( model ?? "" );
			if ( ! meshes.TryGetValue( key, out var mesh ) ) meshes[ key ] = mesh = FishMesh.Build( FishProps.geometryOf( kind, model, 0 ), "catch-" + key );
			var go = new GameObject( "catch-" + species ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( root, false );
			go.AddComponent<MeshFilter>().sharedMesh = mesh;
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = material;
			mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			go.SetActive( false );
			s = new Slot
			{
				go = go, mr = mr, seed = random.NextDouble(),
				pattern = kind == "whole" ? FishSpecies.SPECIES[ model ].pattern : 0,
				anchor = kind == "whole" ? FishProps.gillAnchor( model ) : new double[] { 0, 0, 0 },
			};
			slots[ species ] = s;
			return s;
		}

		// hang `species` from `mouth` (world point: the end of the line), facing the camera
		public void show( string species, double kg, Vector3 mouth, double faceYaw, double dt )
		{
			if ( shown != species ) hide();
			var s = SlotFor( species );
			shown = species;
			t += dt;
			double L = fishLength( species, kg );
			// struggling: the body curls side to side, the jaw works, the whole fish swings on the line
			double flap = Math.Sin( t * 11 ) * 0.9 * Math.Exp( - t * 0.5 );
			double swing = Math.Sin( t * 2.6 ) * 0.25 * Math.Exp( - t * 0.4 );
			_q.setFromAxisAngle( _a.set( 0, 1, 0 ), faceYaw + swing );
			_f.makeRotationFromQuaternion( _q ).setPosition( mouth );
			place( s, _f, L, flap, 0, 0.4 + 0.4 * Math.Max( 0, Math.Sin( t * 7 ) ) );
		}

		public void hide()
		{
			if ( shown == null ) return;
			if ( slots.TryGetValue( shown, out var s ) && s.go != null ) s.go.SetActive( false );
			shown = null;
			t = 0;
		}

		// write the slot's transform and skin record (same math as FishProps.add with no bend on the anchor)
		void place( Slot s, Matrix4 frame, double L, double curl, double sag, double jaw )
		{
			_m.multiplyMatrices( frame, GILL );
			_m.decompose( _p, _q, _s );
			_p.sub( _a.set( s.anchor[ 0 ], s.anchor[ 1 ], s.anchor[ 2 ] ).multiplyScalar( L ).applyQuaternion( _q ) );
			s.go.SetActive( true );
			// the same conversion as FishPropsView.Make
			s.go.transform.SetPositionAndRotation( Sim.ToUnity( _p.x, _p.y, _p.z ), new UnityEngine.Quaternion( ( float ) - _q.x, ( float ) - _q.y, ( float ) _q.z, ( float ) _q.w ) );
			s.go.transform.localScale = UnityEngine.Vector3.one * ( float ) L;
			s.mpb.SetVector( idA, new UnityEngine.Vector4( ( float ) ( s.pattern + ( s.seed % 1 ) * 0.9 ), ( float ) curl, ( float ) sag, ( float ) jaw ) );
			s.mpb.SetVector( idB, new UnityEngine.Vector4( 0.1f, 1f, 0f, 0f ) ); // cloudy eye, wet, dried, blood
			s.mpb.SetVector( idC, new UnityEngine.Vector4( ( float ) L, 1f, 0f, 0f ) );
			s.mr.SetPropertyBlock( s.mpb );
		}
	}
}
