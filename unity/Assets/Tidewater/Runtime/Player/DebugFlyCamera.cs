using UnityEngine;
using UnityEngine.InputSystem;

// A throwaway free camera for looking around while systems are ported (not the game's FlyCamera, which is
// src/player/FlyCamera.js and comes with the player). WASD to move, Q / E down / up, hold the right mouse
// button to look, shift to go fast, mouse wheel changes the speed.
namespace Tidewater.Player
{
	public sealed class DebugFlyCamera : MonoBehaviour
	{
		public float speed = 30f;
		float yaw, pitch;

		void OnEnable()
		{
			var e = transform.eulerAngles;
			yaw = e.y; pitch = e.x > 180 ? e.x - 360 : e.x;
		}

		void Update()
		{
			var kb = Keyboard.current; var mouse = Mouse.current;
			if ( kb == null || mouse == null ) return;

			if ( mouse.rightButton.isPressed )
			{
				var d = mouse.delta.ReadValue();
				yaw += d.x * 0.12f;
				pitch = Mathf.Clamp( pitch - d.y * 0.12f, - 89f, 89f );
				transform.rotation = Quaternion.Euler( pitch, yaw, 0 );
			}

			speed = Mathf.Clamp( speed * Mathf.Pow( 1.1f, mouse.scroll.ReadValue().y / 120f ), 1f, 2000f );
			var move = Vector3.zero;
			if ( kb.wKey.isPressed ) move += transform.forward;
			if ( kb.sKey.isPressed ) move -= transform.forward;
			if ( kb.dKey.isPressed ) move += transform.right;
			if ( kb.aKey.isPressed ) move -= transform.right;
			if ( kb.eKey.isPressed ) move += Vector3.up;
			if ( kb.qKey.isPressed ) move -= Vector3.up;
			transform.position += move * ( speed * ( kb.leftShiftKey.isPressed ? 4f : 1f ) * Time.unscaledDeltaTime );
		}
	}
}
