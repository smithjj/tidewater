using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Port of src/core/Input.js (+ the pad half of Gamepad.js): keyboard / mouse / gamepad input. The game reads *actions* (`act`, `actHit`,
// `axis`, `move`, `consumeLook`) which resolve through the binding table (Bindings.cs), so any action can live on any input and the pad works
// everywhere without the gameplay code knowing a pad exists. The state is the JS one: sets of held / pressed codes, mouse deltas in pixels
// (y down), wheel notches (+1 = scroll toward the user, as a browser's deltaY), a pad that is polled once a frame. Poll() reads Unity's devices
// into it; tools can drive the same state with Hold / Release / Tap / AddLook, so a headless run can press keys.
// Differences: the pointer lock is Cursor.lockState (a click captures it, Esc releases it in the Editor); the pad is Unity's Gamepad
// (the standard mapping the JS reads), the right stick turns the camera at a rate as in the JS.
namespace Tidewater.Core
{
	public sealed class GameInput
	{
		// a stick at full deflection turns this many pixels' worth of camera per second (mouse deltas are pixels, so this keeps the pad and the mouse in the same units)
		const double PAD_LOOK_PX = 420;
		const double AXIS_HELD = 0.25; // an axis action counts as "held" past this
		// A stick turns the camera at a *rate*, so a long frame must not turn into a jump: the time step is capped, and with it the step per frame.
		// A mouse delta is a distance already, but the odd enormous event does arrive from the driver: one of those is a snap that has nothing to do with the hand on the mouse.
		const double PAD_LOOK_MAX_DT = 1.0 / 30, PAD_LOOK_MAX_STEP = 24, MOUSE_MAX_STEP = 200;

		public readonly Bindings bindings = new Bindings();
		public readonly HashSet<string> keys = new HashSet<string>(), pressed = new HashSet<string>();
		readonly HashSet<string> held = new HashSet<string>();             // keys a tool holds (merged into `keys`)
		readonly HashSet<string> mousePressed = new HashSet<string>(), mouseReleased = new HashSet<string>();
		public readonly Engine.Vector2 look = new Engine.Vector2(), padLook = new Engine.Vector2(); // mouse deltas; the pad adds to padLook
		public double wheel;
		public bool mouseDown, rightDown, middleDown;
		public bool locked, enabled = true, capturing, menuMode;
		public Device device = Device.kb;
		double dt = 1.0 / 60;

		// ---- the pad (Gamepad.js)
		public bool padConnected;
		public string padLayout = "xbox";
		readonly Dictionary<string, double> padAxes = new Dictionary<string, double> { { "LSX", 0 }, { "LSY", 0 }, { "RSX", 0 }, { "RSY", 0 } };
		readonly HashSet<string> padDown = new HashSet<string>(), padPressed = new HashSet<string>(), padReleased = new HashSet<string>(), padPrev = new HashSet<string>();
		static readonly string[] PAD_BUTTONS = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "L3", "R3", "DUp", "DDown", "DLeft", "DRight" };

		static readonly Dictionary<string, Key> keyCache = new Dictionary<string, Key>();

		// KeyboardEvent.code -> Unity Key
		static bool KeyFor( string code, out Key key )
		{
			if ( keyCache.TryGetValue( code, out key ) ) return key != Key.None;
			key = Key.None;
			string name = code;
			if ( code.Length == 4 && code.StartsWith( "Key" ) ) name = code.Substring( 3 );
			else if ( code.StartsWith( "Arrow" ) ) name = code.Substring( 5 ) + "Arrow";
			else if ( code == "ShiftLeft" ) name = "LeftShift";
			else if ( code == "ShiftRight" ) name = "RightShift";
			else if ( code == "ControlLeft" ) name = "LeftCtrl";
			else if ( code == "ControlRight" ) name = "RightCtrl";
			else if ( code == "AltLeft" ) name = "LeftAlt";
			else if ( code == "AltRight" ) name = "RightAlt";
			if ( ! Enum.TryParse( name, out key ) ) key = Key.None;
			keyCache[ code ] = key;
			return key != Key.None;
		}

		static bool PadDownNow( UnityEngine.InputSystem.Gamepad p, string name )
		{
			switch ( name )
			{
				case "A": return p.buttonSouth.isPressed;
				case "B": return p.buttonEast.isPressed;
				case "X": return p.buttonWest.isPressed;
				case "Y": return p.buttonNorth.isPressed;
				case "LB": return p.leftShoulder.isPressed;
				case "RB": return p.rightShoulder.isPressed;
				case "LT": return p.leftTrigger.ReadValue() > 0.5f;
				case "RT": return p.rightTrigger.ReadValue() > 0.5f;
				case "View": return p.selectButton.isPressed;
				case "Menu": return p.startButton.isPressed;
				case "L3": return p.leftStickButton.isPressed;
				case "R3": return p.rightStickButton.isPressed;
				case "DUp": return p.dpad.up.isPressed;
				case "DDown": return p.dpad.down.isPressed;
				case "DLeft": return p.dpad.left.isPressed;
				case "DRight": return p.dpad.right.isPressed;
			}
			return false;
		}

		// scaled deadzone: below the zone nothing, above it the value grows from 0 so a stick does not jump
		public static double applyDeadzone( double v, double dz )
		{
			double mag = Math.Abs( v );
			if ( double.IsNaN( v ) || dz <= 0 || mag <= dz ) return 0;
			return Math.Sign( v ) * Math.Min( 1, ( mag - dz ) / ( 1 - dz ) );
		}

		// ---- tools: the same state, driven by hand
		public void Hold( string code ) { if ( held.Add( code ) && keys.Add( code ) ) pressed.Add( code ); }
		public void Release( string code ) { held.Remove( code ); keys.Remove( code ); }
		public void Tap( string code ) { pressed.Add( code ); }
		public void AddLook( double x, double y ) { look.x += x; look.y += y; }

		// once a frame, before anything reads input
		public void Poll( double dt = 1.0 / 60 )
		{
			this.dt = dt;
			locked = Cursor.lockState == CursorLockMode.Locked;
			var kb = Keyboard.current; var mouse = Mouse.current;
			// keys: everything the table binds (the codes the game reads), plus what a tool holds
			keys.Clear();
			foreach ( var c in held ) keys.Add( c );
			if ( kb != null )
			{
				foreach ( var a in Bindings.ACTIONS )
					foreach ( var e in a.kb )
					{
						if ( ! KeyFor( e.v, out var key ) ) continue;
						var k = kb[ key ];
						if ( k.isPressed ) keys.Add( e.v );
						if ( k.wasPressedThisFrame ) { pressed.Add( e.v ); useKeyboard(); }
					}
			}

			if ( mouse != null )
			{
				SetMouse( 0, mouse.leftButton.isPressed || fakeL ); SetMouse( 2, mouse.rightButton.isPressed || fakeR ); SetMouse( 1, mouse.middleButton.isPressed );
				if ( locked || mouseDown || rightDown )
				{
					var d = mouse.delta.ReadValue();
					double dx = Math.Max( - MOUSE_MAX_STEP, Math.Min( MOUSE_MAX_STEP, d.x ) );
					double dy = Math.Max( - MOUSE_MAX_STEP, Math.Min( MOUSE_MAX_STEP, - d.y ) ); // pixels, y down
					look.x += dx; look.y += dy;
					if ( dx != 0 || dy != 0 ) useKeyboard();
				}
				float sy = mouse.scroll.ReadValue().y;
				if ( Mathf.Abs( sy ) > 0.01f ) wheel += - Math.Sign( sy );
			}

			PollPad();
		}

		// scripted presses (GameDebug): 0 = left, 2 = right
		bool fakeL, fakeR;
		public void Press( int button, bool down ) { if ( button == 0 ) fakeL = down; else if ( button == 2 ) fakeR = down; SetMouse( button, down ); }

		void SetMouse( int button, bool down )
		{
			bool was = button == 0 ? mouseDown : button == 2 ? rightDown : middleDown;
			if ( button == 0 ) mouseDown = down; else if ( button == 2 ) rightDown = down; else middleDown = down;
			string name = button == 0 ? "LMB" : button == 2 ? "RMB" : "MMB";
			if ( down && ! was ) { mousePressed.Add( name ); useKeyboard(); }
			else if ( ! down && was ) mouseReleased.Add( name );
		}

		void PollPad()
		{
			var p = UnityEngine.InputSystem.Gamepad.current;
			if ( p == null || ! bindings.opts.padEnabled )
			{
				if ( padConnected ) { padConnected = false; padDown.Clear(); padPressed.Clear(); padReleased.Clear(); padPrev.Clear(); foreach ( var k in new List<string>( padAxes.Keys ) ) padAxes[ k ] = 0; }
				return;
			}

			if ( ! padConnected ) { padConnected = true; var id = ( p.description.product ?? "" ).ToLowerInvariant(); padLayout = id.Contains( "dualsense" ) || id.Contains( "dualshock" ) || id.Contains( "playstation" ) ? "ps" : "xbox"; }
			double dz = bindings.opts.deadzone;
			var ls = p.leftStick.ReadValue(); var rs = p.rightStick.ReadValue();
			// the standard mapping reports a stick pushed up as -1 (the table's signs are written for it)
			padAxes[ "LSX" ] = applyDeadzone( ls.x, dz ); padAxes[ "LSY" ] = applyDeadzone( - ls.y, dz );
			padAxes[ "RSX" ] = applyDeadzone( rs.x, dz ); padAxes[ "RSY" ] = applyDeadzone( - rs.y, dz );

			padDown.Clear(); padPressed.Clear(); padReleased.Clear();
			foreach ( var name in PAD_BUTTONS )
			{
				if ( PadDownNow( p, name ) ) { padDown.Add( name ); if ( ! padPrev.Contains( name ) ) padPressed.Add( name ); }
				else if ( padPrev.Contains( name ) ) padReleased.Add( name );
			}
			padPrev.Clear(); foreach ( var n in padDown ) padPrev.Add( n );

			bool active = padDown.Count > 0;
			foreach ( var v in padAxes.Values ) if ( Math.Abs( v ) > 0 ) active = true;
			if ( active ) device = Device.pad;

			// right stick: the same units as the mouse, per second (capped, see above)
			if ( ! menuMode )
			{
				double sens = PAD_LOOK_PX * bindings.opts.lookSensitivity, flip = bindings.opts.invertY ? -1 : 1, step = Math.Min( dt, PAD_LOOK_MAX_DT ), cap = PAD_LOOK_MAX_STEP;
				padLook.x += Math.Max( - cap, Math.Min( cap, padAxes[ "RSX" ] * sens * step ) );
				padLook.y += Math.Max( - cap, Math.Min( cap, padAxes[ "RSY" ] * sens * flip * step ) );
			}
		}

		void useKeyboard() { device = Device.kb; }

		// ---- pointer lock
		public void RequestLock() { if ( ! locked ) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; } }
		public void ReleaseLock() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; locked = false; }

		// ---- the raw code API
		public bool down( string code ) => enabled && keys.Contains( code );
		public bool hit( string code ) => enabled && pressed.Contains( code );

		// mouse deltas + right-stick motion this frame (the player calls it consumeLook)
		public Engine.Vector2 consumeLook()
		{
			var l = new Engine.Vector2( look.x + padLook.x, look.y + padLook.y );
			look.set( 0, 0 ); padLook.set( 0, 0 );
			return l;
		}

		public double consumeWheel() { double w = wheel; wheel = 0; return w; }

		// ---- the action API

		// An action is live unless input is off, a binding is being captured, or a panel owns the input and this action is one that
		// would walk, look or flip a switch behind it (see the table's `menu`).
		bool allowed( string id )
		{
			if ( ! enabled || capturing ) return false;
			if ( menuMode && bindings.menuPolicy( id ) == "block" ) return false;
			return true;
		}

		bool mouseHeld( string v ) => v == "LMB" ? mouseDown : v == "RMB" ? rightDown : v == "MMB" ? middleDown : false;

		public bool act( string id )
		{
			if ( ! allowed( id ) ) return false;
			string kind = bindings.kind( id );
			if ( kind == "axis" ) return Math.Abs( axis( id ) ) > AXIS_HELD;
			if ( kind == "look" ) return false;
			foreach ( var e in bindings.list( id, Device.kb ) ) if ( keys.Contains( e.v ) ) return true;
			foreach ( var e in bindings.list( id, Device.mouse ) ) if ( mouseHeld( e.v ) ) return true;
			foreach ( var e in bindings.list( id, Device.pad ) ) if ( padDown.Contains( e.v ) ) return true;
			return false;
		}

		public bool actHit( string id )
		{
			if ( ! allowed( id ) ) return false;
			foreach ( var e in bindings.list( id, Device.kb ) ) if ( pressed.Contains( e.v ) ) return true;
			foreach ( var e in bindings.list( id, Device.mouse ) ) if ( mousePressed.Contains( e.v ) ) return true;
			foreach ( var e in bindings.list( id, Device.pad ) ) if ( padPressed.Contains( e.v ) ) return true;
			return false;
		}

		public bool actReleased( string id )
		{
			if ( ! allowed( id ) ) return false;
			foreach ( var e in bindings.list( id, Device.mouse ) ) if ( mouseReleased.Contains( e.v ) ) return true;
			foreach ( var e in bindings.list( id, Device.pad ) ) if ( padReleased.Contains( e.v ) ) return true;
			return false;
		}

		double padAxis( string name )
		{
			if ( padAxes.TryGetValue( name, out var v ) ) return v;
			return padDown.Contains( name ) ? 1 : 0;
		}

		// -1..1: the stick (or trigger) value, or ±1 from the bound keys. Signed entries add up, so `forward` is W(+1) + S(-1) and the left stick.
		public double axis( string id )
		{
			if ( ! allowed( id ) ) return 0;
			double v = 0;
			foreach ( var e in bindings.list( id, Device.kb ) ) if ( keys.Contains( e.v ) ) v += e.sign;
			foreach ( var e in bindings.list( id, Device.mouse ) ) if ( mouseHeld( e.v ) ) v += e.sign;
			foreach ( var e in bindings.list( id, Device.pad ) ) v += padAxis( e.v ) * e.sign;
			return Math.Max( -1, Math.Min( 1, v ) );
		}

		// x = strafe (right +), y = forward
		public Engine.Vector2 move( Engine.Vector2 o ) { o.x = axis( "strafe" ); o.y = axis( "forward" ); return o; }

		// the glyph for the active device ('E', 'A', 'RT', 'LMB')
		public string label( string id ) => bindings.label( id, device, padLayout );

		public void endFrame()
		{
			pressed.Clear(); mousePressed.Clear(); mouseReleased.Clear(); padPressed.Clear(); padReleased.Clear();
		}
	}
}
