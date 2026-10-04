using System;
using System.Collections.Generic;
using System.Linq;

// Port of src/core/Bindings.js (the default table and the reading half): every input the game reads is a named action, and each
// action carries the inputs bound to it on each device (keyboard codes, mouse buttons, pad buttons / axes). Entries are the same
// strings the JS uses: a KeyboardEvent.code ('KeyW', 'ShiftLeft'), a mouse button ('LMB', 'RMB', 'MMB') or a pad name ('A', 'RT',
// 'LSY'); `sign` flips an axis action's direction (W is +1 on `forward`, S is -1). GameInput resolves the codes to Unity's devices.
// Not ported yet: rebinding, storage and the options (they come with the settings panel); the table is data, so they can land later
// without the gameplay code changing.
namespace Tidewater.Core
{
	public struct Bind
	{
		public string v; public int sign;
		public Bind( string v, int sign = 1 ) { this.v = v; this.sign = sign; }
		public static implicit operator Bind( string v ) => new Bind( v );
	}

	public sealed class ActionDef
	{
		public string id, label, group, kind, menu, help;
		public Bind[] kb = new Bind[ 0 ], mouse = new Bind[ 0 ], pad = new Bind[ 0 ];
	}

	public enum Device { kb, mouse, pad }

	public sealed class Options
	{
		public bool padEnabled = true;
		public double deadzone = 0.15, lookSensitivity = 1, rumble = 0.7;
		public bool invertY = false;
		public bool flashlightOn = true; // the handheld torch: on by default (it is near-invisible in daylight)
	}

	public sealed class Bindings
	{
		static ActionDef A( string id, string label, string group, string kind, string menu, string help, Bind[] kb = null, Bind[] mouse = null, Bind[] pad = null )
			=> new ActionDef { id = id, label = label, group = group, kind = kind, menu = menu, help = help, kb = kb ?? new Bind[ 0 ], mouse = mouse ?? new Bind[ 0 ], pad = pad ?? new Bind[ 0 ] };
		static Bind[] B( params Bind[] b ) => b;

		public static readonly ActionDef[] ACTIONS =
		{
			// ---- movement. forward/strafe are axes: the stick, or the pair of keys, signed
			A( "forward", "Move forward / back", "Movement", "axis", "block", "Move<small>W A S D, or the left stick</small>",
				kb: B( new Bind( "KeyW", 1 ), new Bind( "KeyS", -1 ) ), pad: B( new Bind( "LSY", -1 ) ) ), // standard mapping: pushing the stick up is -1
			A( "strafe", "Move left / right", "Movement", "axis", "block", "",
				kb: B( new Bind( "KeyD", 1 ), new Bind( "KeyA", -1 ) ), pad: B( new Bind( "LSX", 1 ) ) ),
			A( "look", "Look around", "Movement", "look", "block", "Look around<small>Click to capture the mouse</small>",
				mouse: B( "Mouse" ), pad: B( new Bind( "RSX", 1 ), new Bind( "RSY", 1 ) ) ),
			A( "sprint", "Sprint / boost", "Movement", "button", "block", "Sprint, boat boost", kb: B( "ShiftLeft", "ShiftRight" ), pad: B( "LT" ) ),
			A( "ascend", "Jump / swim up", "Movement", "button", "block", "Jump, swim up, fly up", kb: B( "Space" ), pad: B( "X" ) ),
			// KeyQ is in here for the free camera, which has always used Q to fly down (it is otherwise unused)
			A( "descend", "Crouch / dive", "Movement", "button", "block", "Crouch, dive, fly down", kb: B( "KeyC", "ControlLeft", "KeyQ" ), pad: B( "Y" ) ),

			// ---- fishing
			A( "rod", "Take out the rod", "Fishing", "button", "allow", "Fishing rod<small>Take out / put away</small>", kb: B( "KeyR" ), pad: B( "LB" ) ),
			A( "rodUse", "Cast / strike / reel", "Fishing", "button", "allow", "Cast, strike, reel<small>Hold to wind up / reel. At the helm: set and haul pots</small>", mouse: B( "LMB" ), pad: B( "RT" ) ),
			A( "rodIn", "Reel in an empty line", "Fishing", "button", "allow", "Reel in an empty line", mouse: B( "RMB" ), pad: B( "RB" ) ),

			// ---- interact
			A( "interact", "Interact", "Interact", "button", "allow", "Interact<small>Board, helm, step ashore, trade, traps on deck</small>", kb: B( "KeyE" ), pad: B( "A" ) ),
			A( "cooler", "Cooler and fish log", "Interact", "button", "allow", "Cooler and fish log", kb: B( "KeyI", "Tab" ), pad: B( "R3" ) ),
			A( "anchor", "Anchor", "Interact", "button", "block", "Anchor<small>Drop or weigh, aboard a boat</small>", kb: B( "KeyX" ) ),
			A( "boatCamera", "Boat camera", "Interact", "button", "block", "Boat camera<small>1st / 3rd person</small>", kb: B( "KeyV" ), pad: B( "L3" ) ),

			// ---- interface
			A( "codex", "Fish guide", "Interface", "button", "allow", "Fish guide<small>What you have learned about each fish</small>", kb: B( "KeyJ" ) ),
			A( "settings", "Settings panel", "Interface", "button", "allow", "Settings panel", kb: B( "KeyH" ), pad: B( "Menu" ) ),
			A( "controls", "All controls", "Interface", "button", "allow", "This sheet", kb: B( "F1" ), pad: B( "View" ) ),
			A( "map", "Large map", "Interface", "button", "block", "Large map<small>Open / close, north is up</small>", kb: B( "KeyN" ) ),
			A( "photo", "Photo mode", "Interface", "button", "block", "Photo mode<small>Hides all interface</small>", kb: B( "KeyP" ), pad: B( "DRight" ) ),
			A( "cancel", "Back / close", "Interface", "button", "allow", "Close, release the mouse", kb: B( "Escape" ), pad: B( "B" ) ),
			A( "pauseTime", "Run or pause the day", "Interface", "button", "block", "Run or pause the day", kb: B( "KeyT" ), pad: B( "DUp" ) ),
			A( "flashlight", "Flashlight", "Interface", "button", "block", "Flashlight", kb: B( "KeyL" ), pad: B( "DDown" ) ),
			A( "mute", "Mute", "Interface", "button", "block", "Mute", kb: B( "KeyM" ), pad: B( "DLeft" ) ),
			A( "freeCam", "Free camera", "Interface", "button", "block", "Free camera<small>Developer camera</small>", kb: B( "KeyF" ) ),
		};

		static readonly Dictionary<string, string> KB_GLYPHS = new Dictionary<string, string>
		{
			{ "Space", "Space" }, { "Escape", "Esc" }, { "Tab", "Tab" }, { "Enter", "Enter" }, { "Backspace", "Backspace" }, { "Delete", "Del" },
			{ "ShiftLeft", "Shift" }, { "ShiftRight", "Shift" }, { "ControlLeft", "Ctrl" }, { "ControlRight", "Ctrl" }, { "AltLeft", "Alt" }, { "AltRight", "Alt" },
			{ "ArrowUp", "↑" }, { "ArrowDown", "↓" }, { "ArrowLeft", "←" }, { "ArrowRight", "→" },
			{ "Slash", "/" }, { "Backslash", "\\" }, { "Semicolon", ";" }, { "Quote", "'" }, { "Comma", "," }, { "Period", "." }, { "Backquote", "`" },
			{ "Minus", "-" }, { "Equal", "=" }, { "BracketLeft", "[" }, { "BracketRight", "]" }, { "CapsLock", "Caps" }, { "NumpadEnter", "Enter" },
		};

		static readonly Dictionary<string, string> XBOX_GLYPHS = new Dictionary<string, string>
		{
			{ "DUp", "↑" }, { "DDown", "↓" }, { "DLeft", "←" }, { "DRight", "→" }, { "LSX", "LS" }, { "LSY", "LS" }, { "RSX", "RS" }, { "RSY", "RS" },
		};

		static readonly Dictionary<string, string> PS_GLYPHS = new Dictionary<string, string>
		{
			{ "A", "✕" }, { "B", "○" }, { "X", "□" }, { "Y", "△" }, { "LB", "L1" }, { "RB", "R1" }, { "LT", "L2" }, { "RT", "R2" },
			{ "View", "Share" }, { "Menu", "Options" },
			{ "DUp", "↑" }, { "DDown", "↓" }, { "DLeft", "←" }, { "DRight", "→" }, { "LSX", "LS" }, { "LSY", "LS" }, { "RSX", "RS" }, { "RSY", "RS" },
		};

		public static string keyGlyph( string code )
		{
			if ( KB_GLYPHS.TryGetValue( code, out var g ) ) return g;
			if ( code.Length == 4 && code.StartsWith( "Key" ) ) return code.Substring( 3 );
			if ( code.StartsWith( "Digit" ) ) return code.Substring( 5 );
			if ( code.StartsWith( "Numpad" ) ) return "Num " + code.Substring( 6 );
			return code;
		}

		public static string padGlyph( string v, string layout = "xbox" )
			=> ( layout == "ps" ? PS_GLYPHS : XBOX_GLYPHS ).TryGetValue( v, out var g ) ? g : v;

		public readonly Options opts = new Options();
		readonly Dictionary<string, ActionDef> byId = ACTIONS.ToDictionary( a => a.id );

		public Bind[] list( string id, Device device )
		{
			if ( ! byId.TryGetValue( id, out var a ) ) return new Bind[ 0 ];
			return device == Device.kb ? a.kb : device == Device.mouse ? a.mouse : a.pad;
		}

		public string kind( string id ) => byId.TryGetValue( id, out var a ) ? a.kind : "button";

		// 'block' | 'allow' while a panel owns the input
		public string menuPolicy( string id ) => byId.TryGetValue( id, out var a ) ? a.menu : "block";

		// 'E', 'A', 'RT', 'LMB', 'W / S' ... for the active device
		public string label( string id, Device device = Device.kb, string layout = "xbox" )
		{
			var dev = device;
			if ( dev == Device.mouse && list( id, Device.mouse ).Length == 0 ) dev = Device.kb;
			if ( dev == Device.kb && list( id, Device.kb ).Length == 0 ) dev = Device.mouse;
			if ( list( id, dev ).Length == 0 )
			{
				// nothing on this device: show whatever else the action has, so a row is never blank
				foreach ( Device other in Enum.GetValues( typeof( Device ) ) )
				{
					var l = list( id, other );
					if ( l.Length > 0 ) return glyphs( l, other, layout );
				}

				return "—";
			}

			return glyphs( list( id, dev ), dev, layout );
		}

		string glyphs( Bind[] l, Device device, string layout )
			=> string.Join( " / ", l.Select( e => device == Device.kb ? keyGlyph( e.v ) : device == Device.mouse ? e.v : padGlyph( e.v, layout ) ) );
	}
}
