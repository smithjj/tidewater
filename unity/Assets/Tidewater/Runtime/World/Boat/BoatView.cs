using System.Collections.Generic;
using Tidewater.Engine;
using UnityEngine;
using Tidewater.Core;
using Vector3 = UnityEngine.Vector3;
using Quaternion = UnityEngine.Quaternion;

// The Unity view of the lobster boat (BoatModel.js, the mesh / material / animated-part half): builds the model data into child meshes
// with the boat materials. Boat frame in Unity: +Z forward, +Y up, +X starboard (the JS +X is port: meshes are mirrored in x, see
// UnityMesh). Material kinds: 0 hull, 1 gelcoat, 2 wood, 3 fittings, 4 glass, 5 glow, 6 trap.
namespace Tidewater.World.Boat
{
	[ExecuteAlways]
	public sealed class BoatView : MonoBehaviour
	{
		public BoatModel model { get; private set; }
		readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
		readonly List<GameObject> built = new List<GameObject>();
		public Transform wheelPivot, wheelMesh, throttleMesh, radarMesh, propMesh, rudderMesh;

		static readonly string[] BUCKET_KIND = { "hull", "gelcoat", "wood", "fittings", "glass", "glow", "trap" };

		void OnEnable() { if ( model == null ) Build(); }
		void OnDisable() { Release(); }

		public void Rebuild() { Release(); Build(); }

		void Release()
		{
			foreach ( var g in built ) if ( g != null ) { var mf = g.GetComponent<MeshFilter>(); if ( mf != null && mf.sharedMesh != null ) DestroyImmediate( mf.sharedMesh ); DestroyImmediate( g ); }
			built.Clear();
			foreach ( var m in materials.Values ) if ( m != null ) DestroyImmediate( m );
			materials.Clear();
			model = null;
		}

		Material MakeMaterial( string bucket )
		{
			int kind = System.Array.IndexOf( BUCKET_KIND, bucket );
			var shader = Shader.Find( kind == 4 ? "Tidewater/BoatGlass" : "Tidewater/Boat" );
			var m = new Material( shader ) { name = "boat-" + bucket, hideFlags = HideFlags.HideAndDontSave };
			m.SetFloat( "_BoatKind", kind );
			if ( kind == 6 ) { m.EnableKeyword( "_ALPHATEST_ON" ); m.SetFloat( "_CullMode", 0f ); m.SetFloat( "_AlphaCutoff", 0.5f ); }
			if ( kind == 3 )
			{
				var fp = model.parts.flagPivot;
				m.SetVector( "_FlagPivot", new Vector4( ( float ) fp.x, ( float ) fp.y, ( float ) fp.z, 0 ) );
				m.SetVector( "_FlagDir", new Vector4( 0, 0, -1, 0 ) );
				m.SetFloat( "_FlagWind", 0.5f );
			}

			return m;
		}

		Material Mat( string bucket )
		{
			if ( ! materials.TryGetValue( bucket, out var m ) ) materials[ bucket ] = m = MakeMaterial( bucket );
			return m;
		}

		GameObject AddMesh( string name, BufferGeometry geo, string bucket, Transform parent, Vector3 localPosition )
		{
			var go = new GameObject( name ) { hideFlags = HideFlags.DontSave };
			go.transform.SetParent( parent, false );
			go.transform.localPosition = localPosition;
			go.AddComponent<MeshFilter>().sharedMesh = UnityMesh.Create( geo, name );
			var mr = go.AddComponent<MeshRenderer>();
			mr.sharedMaterial = Mat( bucket );
			mr.shadowCastingMode = bucket == "glass" ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
			built.Add( go );
			return go;
		}

		static Vector3 Boat( Tidewater.Engine.Vector3 v ) => new Vector3( -( float ) v.x, ( float ) v.y, ( float ) v.z );

		public void Build()
		{
			model = new BoatModel();
			foreach ( var kv in model.staticGeometry ) AddMesh( "boat-" + kv.Key, kv.Value, kv.Key, transform, Vector3.zero );

			// helm wheel: pivot aligned with the shaft, wheel spins about its local Z
			var pivot = new GameObject( "boat-wheel-pivot" ) { hideFlags = HideFlags.DontSave };
			pivot.transform.SetParent( transform, false );
			pivot.transform.localPosition = Boat( model.wheelPos );
			var q = model.wheelPivotRotation;
			pivot.transform.localRotation = new Quaternion( ( float ) q.x, ( float ) -q.y, ( float ) -q.z, ( float ) q.w );
			built.Add( pivot ); wheelPivot = pivot.transform;
			wheelMesh = AddMesh( "boat-wheel", model.wheelGeometry, "wood", wheelPivot, Vector3.zero ).transform;
			throttleMesh = AddMesh( "boat-throttle", model.throttleGeometry, "fittings", transform, Boat( model.throttlePos ) ).transform;
			radarMesh = AddMesh( "boat-radar", model.radarGeometry, "fittings", transform, Boat( model.radarPos ) ).transform;
			propMesh = AddMesh( "boat-propeller", model.propGeometry, "fittings", transform, Boat( model.propPos ) ).transform;
			rudderMesh = AddMesh( "boat-rudder", model.rudderGeometry, "fittings", transform, Boat( model.rudderPos ) ).transform;
		}
	}
}
