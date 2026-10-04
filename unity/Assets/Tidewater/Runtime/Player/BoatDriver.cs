using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.World;
using Tidewater.World.Boat;
using UnityEngine;
using UnityEngine.InputSystem;

// Hosts a BoatController on the lobster boat's BoatView until the player (Player.js) and the app loop are ported: builds the controller
// once the sea's water queries exist, feeds it the controls and ticks it. Keyboard (play mode): W / S throttle ahead / astern, A / D steer
// to port / starboard, Space throttle to neutral, Enter takes / leaves the helm. Tools drive it with SetControls / Tick.
namespace Tidewater.Player
{
	public sealed class BoatDriver : MonoBehaviour
	{
		public BoatController controller { get; private set; }
		public Colliders colliders = new Colliders();
		public bool keyboard = true;
		double throttleIn, steerIn;
		BoatView view;

		// the controller, once the sea (its queries) and the terrain exist
		public bool Ensure()
		{
			if ( controller != null ) return true;
			var ocean = OceanRenderer.instance;
			var terrain = FindAnyObjectByType<TerrainRenderer>();
			view = GetComponent<BoatView>();
			if ( ocean == null || ocean.query == null || terrain == null || terrain.data == null || view == null || view.model == null ) return false;
			controller = new BoatController( view.model.dynamics(), ocean.query, terrain.data.HeightAt, colliders, BoatDock.Lobster ) { view = view };
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
		}

		void Update()
		{
			if ( ! Application.isPlaying ) return;
			if ( Ensure() && keyboard && Keyboard.current != null )
			{
				var kb = Keyboard.current;
				if ( kb.enterKey.wasPressedThisFrame ) { controller.driven = ! controller.driven; if ( controller.driven ) controller.moored = false; }
				if ( kb.wKey.isPressed ) throttleIn = 1; else if ( kb.sKey.isPressed ) throttleIn = -1; else if ( kb.spaceKey.isPressed ) throttleIn = 0;
				steerIn = ( kb.aKey.isPressed ? 1 : 0 ) - ( kb.dKey.isPressed ? 1 : 0 );
			}

			Tick( Mathf.Min( Time.deltaTime, 0.1f ) );
		}
	}
}
