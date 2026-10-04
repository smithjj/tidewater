using System;
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
	public sealed class BoatView : MonoBehaviour, Tidewater.Player.IBoatVisual
	{
		public BoatModel model { get; private set; }
		readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
		readonly List<GameObject> built = new List<GameObject>();
		public Transform wheelPivot, wheelMesh, throttleMesh, radarMesh, propMesh, rudderMesh;

		// ---- the controls and the pose (IBoatVisual: what BoatController drives). Rotations: the JS part rotations are about the part's
		// local axes in the right-handed boat frame; the Unity meshes are mirrored in x (Unity local x = -JS x), so a rotation about x keeps
		// its angle and about y or z flips it (S Ry(a) S = Ry(-a), S Rz(a) S = Rz(-a), S = diag(-1, 1, 1)).
		static UnityEngine.Quaternion RotX( double a ) => new UnityEngine.Quaternion( ( float ) Math.Sin( a / 2 ), 0, 0, ( float ) Math.Cos( a / 2 ) );
		static UnityEngine.Quaternion RotY( double a ) => new UnityEngine.Quaternion( 0, ( float ) Math.Sin( -a / 2 ), 0, ( float ) Math.Cos( a / 2 ) );
		static UnityEngine.Quaternion RotZ( double a ) => new UnityEngine.Quaternion( 0, 0, ( float ) Math.Sin( -a / 2 ), ( float ) Math.Cos( a / 2 ) );

		double rpmShown, flagWind = 0.5;
		public double propAngle, radarAngle;
		readonly Tidewater.Engine.Vector3 simPos = new Tidewater.Engine.Vector3(), lastPos = new Tidewater.Engine.Vector3(), vel = new Tidewater.Engine.Vector3(), flagDir = new Tidewater.Engine.Vector3( 0, 0, -1 );
		readonly Tidewater.Engine.Quaternion simQ = new Tidewater.Engine.Quaternion();
		bool hasLastPos;

		// pose of the boat in sim space (position, quaternion of the right-handed boat frame)
		public void SetPose( Tidewater.Engine.Vector3 position, Tidewater.Engine.Quaternion quaternion )
		{
			simPos.copy( position ); simQ.copy( quaternion );
			transform.SetPositionAndRotation( Tidewater.Util.Sim.ToUnity( position.x, position.y, position.z ), Tidewater.Util.Sim.BoatToUnity( quaternion ) );
		}

		// -1..1, positive turns the boat to port (left): rudder trailing edge to port, wheel turned counter-clockwise as seen from the helm
		public void SetSteering( double angle )
		{
			double a = MathUtils.clamp( angle, -1, 1 );
			if ( wheelMesh != null ) wheelMesh.localRotation = RotZ( - a * BoatModel.WHEEL_TURNS * Math.PI * 2 );
			if ( rudderMesh != null ) rudderMesh.localRotation = RotY( - a * RUDDER.maxAngle );
		}

		// -1 (full astern) .. 0 (neutral) .. 1 (full ahead); lever tips forward for ahead
		public void SetThrottle( double t )
		{
			if ( throttleMesh != null ) throttleMesh.localRotation = RotX( MathUtils.clamp( t, -1, 1 ) * BoatModel.THROTTLE_ANGLE );
		}

		// shaft RPM; positive = ahead (right-handed prop, clockwise from astern)
		public void SetPropellerRPM( double rpm ) { rpmShown = rpm; }

		// BoatModel.update( dt ): the propeller (its displayed speed saturates at ~5 rev/s so the blades don't strobe backwards), the radar,
		// and the ensign (apparent wind, true wind minus the boat's velocity, in the boat frame)
		public void Tick( double dt )
		{
			if ( ! ( dt > 0 ) || model == null ) return;
			double rps = rpmShown / 60;
			double shown = BoatModel.PROP_DISPLAY_RPS * Math.Tanh( rps / BoatModel.PROP_DISPLAY_RPS );
			propAngle = ( propAngle + shown * Math.PI * 2 * dt ) % ( Math.PI * 2 );
			if ( propMesh != null ) propMesh.localRotation = RotZ( propAngle );
			radarAngle = ( radarAngle + BoatModel.RADAR_RPM / 60 * Math.PI * 2 * dt ) % ( Math.PI * 2 );
			if ( radarMesh != null ) radarMesh.localRotation = RotY( radarAngle );

			var v = new Tidewater.Engine.Vector3();
			if ( hasLastPos )
			{
				v.subVectors( simPos, lastPos ).divideScalar( dt );
				if ( v.lengthSq() < 900 ) vel.lerp( v, 1 - Math.Exp( - dt * 4 ) ); // ignore teleports
			}

			lastPos.copy( simPos );
			hasLastPos = true;

			var wd = G.windDir; double ws = G.windSpeed;
			v.set( wd.x * ws, 0, wd.y * ws ).sub( vel ).applyQuaternion( new Tidewater.Engine.Quaternion().copy( simQ ).invert() );
			v.y = 0;
			double speed = v.length();
			if ( speed > 1e-3 )
			{
				v.divideScalar( speed );
				flagDir.lerp( v, 1 - Math.Exp( - dt * 3 ) );
				if ( flagDir.lengthSq() < 1e-4 ) flagDir.copy( v );
				flagDir.normalize();
			}

			flagWind += ( MathUtils.clamp( speed / 9, 0, 1 ) - flagWind ) * ( 1 - Math.Exp( - dt * 2 ) );
			if ( materials.TryGetValue( "fittings", out var fm ) )
			{
				fm.SetVector( "_FlagDir", new Vector4( ( float ) flagDir.x, ( float ) flagDir.y, ( float ) flagDir.z, 0 ) );
				fm.SetFloat( "_FlagWind", ( float ) flagWind );
			}
		}

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
