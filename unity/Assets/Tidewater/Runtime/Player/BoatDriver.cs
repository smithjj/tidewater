using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.World;
using Tidewater.World.Boat;
using UnityEngine;
using UnityEngine.InputSystem;

// Hosts a BoatController on the lobster boat's BoatView until the player (Player.js) and the app loop are ported: builds the controller
// once the sea's water queries exist, feeds it the controls and ticks it, and while at the helm drives the main camera with BoatCamera.
// Keyboard (play mode), as the game's bindings: Enter takes / leaves the helm, W / S throttle ahead (Shift: full) / astern, A / D steer to
// port / starboard, V toggles the helm / chase camera, right mouse button + move looks, mouse wheel zooms the chase camera. Tools drive it
// with SetControls / Tick.
namespace Tidewater.Player
{
	public sealed class BoatDriver : MonoBehaviour
	{
		public BoatController controller { get; private set; }
		public Colliders colliders = new Colliders();
		public bool keyboard = true;
		public bool followCamera = true; // tools turn this off to place the camera themselves
		public Engine.Vector3 debugEye, debugTarget; // tools: a camera fixed in the boat frame (with followCamera off)
		public BoatCamera cam = new BoatCamera();
		public BoatSpray spray { get; private set; }
		public Tidewater.Ocean.WakeSim wake { get; private set; }
		double throttleIn, steerIn;
		BoatView view;
		Engine.Vector2 look = new Engine.Vector2();
		double wheel;
		bool wasDriven;

		// the controller, once the sea (its queries) and the terrain exist
		public bool Ensure()
		{
			if ( controller != null ) return true;
			var ocean = OceanRenderer.instance;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			view = GetComponent<BoatView>();
			if ( ocean == null || ocean.query == null || terrain == null || terrain.data == null || view == null || view.model == null ) return false;
			controller = new BoatController( view.model.dynamics(), ocean.query, terrain.data.HeightAt, colliders, BoatDock.Lobster ) { view = view };
			// the sea is not drawn inside the boat (its hull volume masks the surface)
			ocean.hullMask.Add( Tidewater.Engine.UnityMesh.Create( view.model.createHullVolumeGeometry(), "boat-hullmask" ), view.transform );
			if ( ocean.spray != null ) spray = new BoatSpray( controller, view.model, ocean.spray );
			if ( ocean.wakeShader != null ) wake = new Tidewater.Ocean.WakeSim( ocean.wakeShader, terrain.gpu, controller, view.model.lines, colliders );
			return true;
		}

		public void SetControls( double throttle, double steer, bool driven )
		{
			throttleIn = throttle; steerIn = steer;
			if ( Ensure() ) { controller.driven = driven; if ( driven ) controller.moored = false; }
		}

		// one frame: controls in, queries queued, bodies stepped, parts animated
		public void Tick( double dt )
		{
			if ( ! Ensure() ) return;
			controller.setInput( throttleIn, steerIn, dt );
			controller.queueQueries();
			controller.update( dt );
			view.Tick( dt );
			if ( spray != null ) spray.update( dt );
			if ( wake != null ) wake.Update( dt );
		}

		void Update()
		{
			if ( ! Application.isPlaying ) return;
			look.set( 0, 0 ); wheel = 0;
			if ( Ensure() && keyboard && Keyboard.current != null )
			{
				var kb = Keyboard.current; var mouse = Mouse.current;
				if ( kb.enterKey.wasPressedThisFrame ) { controller.driven = ! controller.driven; if ( controller.driven ) controller.moored = false; }
				if ( kb.vKey.wasPressedThisFrame ) cam.toggle();
				// Player.updateBoat: W 0.7, Shift+W 1.0, S -0.6, released = 0; A to port is positive steer
				double fwd = ( kb.wKey.isPressed ? 1 : 0 ) - ( kb.sKey.isPressed ? 1 : 0 );
				throttleIn = fwd >= 0 ? fwd * ( kb.leftShiftKey.isPressed ? 1 : 0.7 ) : fwd * 0.6;
				steerIn = ( kb.aKey.isPressed ? 1 : 0 ) - ( kb.dKey.isPressed ? 1 : 0 );
				if ( mouse != null )
				{
					if ( mouse.rightButton.isPressed ) { var d = mouse.delta.ReadValue(); look.set( d.x, - d.y ); } // pixels, y down
					wheel = - Mathf.Sign( mouse.scroll.ReadValue().y ) * ( Mathf.Abs( mouse.scroll.ReadValue().y ) > 0.01f ? 1 : 0 );
				}
			}

			Tick( Mathf.Min( Time.deltaTime, 0.1f ) );
		}

		void OnDestroy()
		{
			if ( wake != null ) wake.Dispose();
			var ocean = OceanRenderer.instance;
			if ( ocean != null ) ocean.hullMask.Remove( transform );
		}

		// the camera follows the boat once it has moved this frame
		void LateUpdate()
		{
			if ( ! Application.isPlaying || controller == null ) return;
			var main = Camera.main;
			if ( main == null ) return;
			if ( controller.driven != wasDriven )
			{
				wasDriven = controller.driven;
				var fly = main.GetComponent<DebugFlyCamera>();
				if ( fly != null ) fly.enabled = ! wasDriven;
				if ( wasDriven ) cam.takeHelm( controller );
			}

			if ( debugEye != null && debugTarget != null )
			{
				var e = controller.toWorld( debugEye, new Engine.Vector3() ); var t = controller.toWorld( debugTarget, new Engine.Vector3() );
				BoatCamera.Apply( main.transform, e, t.sub( e ).normalize(), new Engine.Vector3( 0, 1, 0 ) );
				return;
			}

			if ( ! controller.driven || ! followCamera ) return;
			cam.update( controller, view.model.helmEye, look, wheel, Mathf.Min( Time.deltaTime, 0.1f ), out var eye, out var fwdV, out var up );
			BoatCamera.Apply( main.transform, eye, fwdV, up );
		}
	}
}
