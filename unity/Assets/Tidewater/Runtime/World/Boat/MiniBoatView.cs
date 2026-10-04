using System.Collections.Generic;
using Tidewater.Engine;
using UnityEngine;
using Color = UnityEngine.Color;
using Vector3 = UnityEngine.Vector3;


// The Unity view of the mini fishing boat (MiniFishingBoat.js, the mesh / material half): one mesh per glTF material of MiniBoatModel with the
// factor-based boat material (Tidewater/Boat, kind 7: colour, roughness, metalness, emissive, clear coat from the glTF factors, double-sided).
// Boat frame in Unity as the lobster boat: +Z forward, +Y up, +X starboard (UnityMesh mirrors x). It has nothing that moves with the controls
// (the controller's throttle / steering / propeller calls are no-ops, as in the JS).
namespace Tidewater.World.Boat
{
	[ExecuteAlways]
	public sealed class MiniBoatView : MonoBehaviour, Tidewater.Player.IBoatVisual
	{
		public BoatModel model { get; private set; }
		readonly List<GameObject> built = new List<GameObject>();
		readonly List<Material> materials = new List<Material>();

		public void SetPose( Tidewater.Engine.Vector3 position, Tidewater.Engine.Quaternion quaternion )
			=> transform.SetPositionAndRotation( Tidewater.Util.Sim.ToUnity( position.x, position.y, position.z ), Tidewater.Util.Sim.BoatToUnity( quaternion ) );
		public void SetSteering( double a ) { }
		public void SetThrottle( double t ) { }
		public void SetPropellerRPM( double rpm ) { }
		public void Tick( double dt ) { }

		void OnEnable() { if ( model == null ) Build(); }
		void OnDisable() { Release(); }
		public void Rebuild() { Release(); Build(); }

		void Release()
		{
			foreach ( var g in built ) if ( g != null ) { var mf = g.GetComponent<MeshFilter>(); if ( mf != null && mf.sharedMesh != null ) DestroyImmediate( mf.sharedMesh ); DestroyImmediate( g ); }
			built.Clear();
			foreach ( var m in materials ) if ( m != null ) DestroyImmediate( m );
			materials.Clear();
			model = null;
		}

		public void Build()
		{
			var mini = new MiniBoatModel();
			model = mini;
			var shader = Shader.Find( "Tidewater/Boat" );
			foreach ( var f in mini.materials )
			{
				var m = new Material( shader ) { name = "mini-" + f.name, hideFlags = HideFlags.HideAndDontSave };
				m.SetFloat( "_BoatKind", 7 );
				m.SetFloat( "_CullMode", 0f ); // the file is all double-sided
				m.SetColor( "_FacColor", new Color( ( float ) f.color[ 0 ], ( float ) f.color[ 1 ], ( float ) f.color[ 2 ], 1f ) );
				m.SetVector( "_FacPbr", new Vector4( ( float ) f.roughness, ( float ) f.metalness, ( float ) f.clearcoat, ( float ) f.clearcoatRoughness ) );
				m.SetColor( "_FacEmissive", new Color( ( float ) f.emissive[ 0 ], ( float ) f.emissive[ 1 ], ( float ) f.emissive[ 2 ], 0f ) );
				materials.Add( m );
				var go = new GameObject( "mini-" + f.name ) { hideFlags = HideFlags.DontSave };
				go.transform.SetParent( transform, false );
				go.AddComponent<MeshFilter>().sharedMesh = UnityMesh.Create( f.geometry, go.name );
				var mr = go.AddComponent<MeshRenderer>();
				mr.sharedMaterial = m;
				mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
				built.Add( go );
			}
		}
	}
}
