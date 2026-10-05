using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Tidewater.Game;
using UnityEngine;

// Port of src/core/Bindings.js: every input the game reads is a named action, and each action carries the inputs bound to it on each
// device (keyboard codes, mouse buttons, pad buttons / axes). Entries are the same strings the JS uses: a KeyboardEvent.code ('KeyW',
// 'ShiftLeft'), a mouse button ('LMB', 'RMB', 'MMB') or a pad name ('A', 'RT', 'LSY'); `sign` flips an axis action's direction (W is +1
// on `forward`, S is -1). GameInput resolves the codes to Unity's devices. ACTIONS is the table of defaults; the Bindings instance holds
// what the player has made of it (rebind, steal on conflict, clear, reset), the options (pad deadzone, look sensitivity, invert,
// rumble, the flashlight's last state) and the store they are saved in, in the JS's JSON ('tidewater.controls.v1', only what differs
// from the defaults). Checked against the real JS class by BindingsOracle.
namespace Tidewater.Core
{
	public struct Bind
	{
		public string v; public int sign;
		public Bind( string v, int sign = 1 ) { this.v = v; this.sign = sign; }
		public static implicit operator Bind( string v ) => new Bind( v );
		public bool Same( Bind o ) => v == o.v && sign == o.sign;
	}

	public sealed class ActionDef
	{
		public string id, label, group, kind, menu, help;
		public bool noRebind; // the look stick is not a button: it has a sensitivity, not a binding
		public Bind[] kb = new Bind[ 0 ], mouse = new Bind[ 0 ], pad = new Bind[ 0 ];
	}

	public enum Device { kb, mouse, pad }

	public sealed class Options
	{
		public bool padEnabled = true;
		public double deadzone = 0.15, lookSensitivity = 1, rumble = 0.7;
		public bool invertY = false;
		public bool flashlightOn = true; // the handheld torch: on by default (it is near-invisible in daylight)

		// the object stays the same (the input reads it by reference): reset and load write into it
		public void CopyFrom( Options o ) { padEnabled = o.padEnabled; deadzone = o.deadzone; lookSensitivity = o.lookSensitivity; rumble = o.rumble; invertY = o.invertY; flashlightOn = o.flashlightOn; }
	}

	public sealed class Bindings
	{
		static ActionDef A( string id, string label, string group, string kind, string menu, string help, Bind[] kb = null, Bind[] mouse = null, Bind[] pad = null, bool noRebind = false )
			=> new ActionDef { id = id, label = label, group = group, kind = kind, menu = menu, help = help, kb = kb ?? new Bind[ 0 ], mouse = mouse ?? new Bind[ 0 ], pad = pad ?? new Bind[ 0 ], noRebind = noRebind };

		public const string STORE_KEY = "tidewater.controls.v1";

		public static readonly string[] PAD_BUTTONS = { "A", "B", "X", "Y", "LB", "RB", "LT", "RT", "View", "Menu", "L3", "R3", "DUp", "DDown", "DLeft", "DRight" };
		public static readonly string[] PAD_AXES = { "LSX", "LSY", "RSX", "RSY", "LT", "RT" };
		public static readonly string[] MOUSE_BUTTONS = { "LMB", "RMB", "MMB" };

		// the groups the Controls tab and the help sheet are built from, in order
		public static readonly string[] GROUPS = { "Movement", "Fishing", "Interact", "Interface" };
		public static readonly Device[] DEVICES = { Device.kb, Device.mouse, Device.pad };
		static Bind[] B( params Bind[] b ) => b;

		public static readonly ActionDef[] ACTIONS =
		{
			// ---- movement. forward/strafe are axes: the stick, or the pair of keys, signed
			A( "forward", "Move forward / back", "Movement", "axis", "block", "Move<small>W A S D, or the left stick</small>",
				kb: B( new Bind( "KeyW", 1 ), new Bind( "KeyS", -1 ) ), pad: B( new Bind( "LSY", -1 ) ) ), // standard mapping: pushing the stick up is -1
			A( "strafe", "Move left / right", "Movement", "axis", "block", "",
				kb: B( new Bind( "KeyD", 1 ), new Bind( "KeyA", -1 ) ), pad: B( new Bind( "LSX", 1 ) ) ),
			A( "look", "Look around", "Movement", "look", "block", "Look around<small>Click to capture the mouse</small>",
				mouse: B( "Mouse" ), pad: B( new Bind( "RSX", 1 ), new Bind( "RSY", 1 ) ), noRebind: true ),
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
		readonly Dictionary<string, List<Bind>[]> map = new Dictionary<string, List<Bind>[]>(); // id -> the lists for kb, mouse, pad (Device order)
		static readonly List<Bind> NONE = new List<Bind>();
		ISaveStore store;

		// bumped by every change, so interface rows can re-read themselves cheaply (see SettingsUI's binding rows)
		public int rev;

		public Bindings( ISaveStore store = null )
		{
			foreach ( var a in ACTIONS ) map[ a.id ] = new[] { new List<Bind>(), new List<Bind>(), new List<Bind>() };
			this.store = store;
			load();
		}

		// the store the controls are kept in (GameHost: the file, as the game save); loads what is there
		public bool Attach( ISaveStore s ) { store = s; return load(); }

		// ---- reading

		public List<Bind> list( string id, Device device ) => map.TryGetValue( id, out var m ) ? m[ ( int ) device ] : NONE;

		public ActionDef meta( string id ) => byId.TryGetValue( id, out var a ) ? a : null;

		public string kind( string id ) => byId.TryGetValue( id, out var a ) ? a.kind : "button";

		// 'block' | 'allow' while a panel owns the input
		public string menuPolicy( string id ) => byId.TryGetValue( id, out var a ) ? a.menu : "block";

		// is this action on its defaults?
		public bool isDefault( string id )
		{
			foreach ( var d in DEVICES )
			{
				var a = list( id, d ); var b = defaults( id, d );
				if ( a.Count != b.Count ) return false;
				for ( int i = 0; i < a.Count; i ++ ) if ( ! a[ i ].Same( b[ i ] ) ) return false;
			}

			return true;
		}

		// the default entries of an action on a device, normalized
		public List<Bind> defaults( string id, Device device )
		{
			var o = new List<Bind>();
			if ( ! byId.TryGetValue( id, out var a ) ) return o;
			foreach ( var e in device == Device.kb ? a.kb : device == Device.mouse ? a.mouse : a.pad ) o.Add( new Bind( e.v, e.sign == -1 ? -1 : 1 ) );
			return o;
		}

		// which action currently owns an input, or null
		public string ownerOf( Device device, string v )
		{
			foreach ( var a in ACTIONS ) foreach ( var e in list( a.id, device ) ) if ( e.v == v ) return a.id;
			return null;
		}

		// ---- writing

		static bool IsCode( string v ) => v != null && Regex.IsMatch( v, "^[A-Za-z][A-Za-z0-9]*$" );

		static bool ValidEntry( Device device, Bind e )
		{
			if ( e.v == null ) return false;
			if ( device == Device.kb ) return IsCode( e.v );
			if ( device == Device.mouse ) return Array.IndexOf( MOUSE_BUTTONS, e.v ) >= 0 || e.v == "Mouse";
			return Array.IndexOf( PAD_BUTTONS, e.v ) >= 0 || Array.IndexOf( PAD_AXES, e.v ) >= 0;
		}

		public bool set( string id, Device device, IEnumerable<Bind> entries )
		{
			if ( ! map.TryGetValue( id, out var m ) ) return false;
			var l = m[ ( int ) device ];
			var next = entries.Select( e => new Bind( e.v, e.sign == -1 ? -1 : 1 ) ).Where( e => ValidEntry( device, e ) ).ToList();
			l.Clear(); l.AddRange( next );
			save();
			rev ++;
			return true;
		}

		// bind one input to an action, taking it from any other action that had it. Returns the actions that lost an input, so the interface can say so.
		public List<string> add( string id, Device device, Bind e )
		{
			var lost = new List<string>();
			var one = new Bind( e.v, e.sign == -1 ? -1 : 1 );
			if ( ! map.ContainsKey( id ) || ! ValidEntry( device, one ) ) return lost;
			foreach ( var a in ACTIONS )
			{
				if ( a.id == id ) continue;
				var l = list( a.id, device );
				int at = l.FindIndex( x => x.v == one.v );
				if ( at >= 0 ) { l.RemoveAt( at ); lost.Add( a.id ); }
			}

			var mine = list( id, device );
			if ( ! mine.Any( x => x.v == one.v ) ) mine.Add( one );
			save();
			rev ++;
			return lost;
		}

		public bool remove( string id, Device device, string v )
		{
			if ( ! map.ContainsKey( id ) ) return false;
			var l = list( id, device );
			int at = l.FindIndex( x => x.v == v );
			if ( at < 0 ) return false;
			l.RemoveAt( at );
			save();
			rev ++;
			return true;
		}

		public bool clear( string id )
		{
			if ( ! map.TryGetValue( id, out var m ) ) return false;
			foreach ( var l in m ) l.Clear();
			save();
			rev ++;
			return true;
		}

		void SetDefaults( string id ) { var m = map[ id ]; foreach ( var d in DEVICES ) { m[ ( int ) d ].Clear(); m[ ( int ) d ].AddRange( defaults( id, d ) ); } }

		public bool reset( string id )
		{
			if ( ! map.ContainsKey( id ) ) return false;
			SetDefaults( id );
			save();
			rev ++;
			return true;
		}

		public void resetAll()
		{
			foreach ( var a in ACTIONS ) SetDefaults( a.id );
			opts.CopyFrom( new Options() );
			save();
			rev ++;
		}

		// ---- glyphs

		// 'E', 'A', 'RT', 'LMB', 'W / S' ... for the active device
		public string label( string id, Device device = Device.kb, string layout = "xbox" )
		{
			var dev = device;
			if ( dev == Device.mouse && list( id, Device.mouse ).Count == 0 ) dev = Device.kb;
			if ( dev == Device.kb && list( id, Device.kb ).Count == 0 ) dev = Device.mouse;
			if ( list( id, dev ).Count == 0 )
			{
				// nothing on this device: show whatever else the action has, so a row is never blank
				foreach ( var other in DEVICES )
				{
					var l = list( id, other );
					if ( l.Count > 0 ) return glyphs( l, other, layout );
				}

				return "—";
			}

			return glyphs( list( id, dev ), dev, layout );
		}

		public static string Glyph( Device device, string v, string layout = "xbox" ) => device == Device.kb ? keyGlyph( v ) : device == Device.mouse ? v : padGlyph( v, layout );

		string glyphs( List<Bind> l, Device device, string layout ) => string.Join( " / ", l.Select( e => Glyph( device, e.v, layout ) ) );

		// every binding of an action, per device, for the rebinding rows
		public string summary( string id, string layout = "xbox" )
		{
			var parts = new List<string>();
			foreach ( var dev in DEVICES )
			{
				var l = list( id, dev );
				if ( l.Count > 0 ) parts.Add( glyphs( l, dev, layout ) );
			}

			return parts.Count > 0 ? string.Join( "  ·  ", parts ) : "—";
		}

		// ---- storage

		static readonly string[] DEVICE_KEYS = { "kb", "mouse", "pad" };

		// a whole number is written without the point, as JSON.stringify does (1, not 1.0)
		static JToken Num( double d ) => d == Math.Floor( d ) && Math.Abs( d ) < 1e15 ? ( JToken ) ( long ) d : ( JToken ) d;

		public JObject toJSON()
		{
			var actions = new JObject();
			foreach ( var a in ACTIONS )
			{
				// only store what differs from the defaults: keeps the blob small and lets new defaults land
				var diff = new JObject();
				foreach ( var dev in DEVICES )
				{
					var own = list( a.id, dev ); var d = defaults( a.id, dev );
					bool same = own.Count == d.Count && own.Select( ( e, i ) => e.Same( d[ i ] ) ).All( x => x );
					if ( ! same ) diff[ DEVICE_KEYS[ ( int ) dev ] ] = new JArray( own.Select( e => new JObject { [ "v" ] = e.v, [ "sign" ] = e.sign } ) );
				}

				if ( diff.Count > 0 ) actions[ a.id ] = diff;
			}

			return new JObject
			{
				[ "v" ] = 1, [ "actions" ] = actions,
				[ "opts" ] = new JObject { [ "padEnabled" ] = opts.padEnabled, [ "deadzone" ] = Num( opts.deadzone ), [ "lookSensitivity" ] = Num( opts.lookSensitivity ), [ "invertY" ] = opts.invertY, [ "rumble" ] = Num( opts.rumble ), [ "flashlightOn" ] = opts.flashlightOn },
			};
		}

		public void save()
		{
			if ( store == null ) return;
			try { store.SetItem( STORE_KEY, toJSON().ToString( Newtonsoft.Json.Formatting.None ) ); }
			catch ( Exception ) { /* the disk is full or read-only: controls just do not persist */ }
		}

		// a stored entry (a string, or { v, sign }) into a Bind (v null: not an entry)
		public static Bind EntryOf( JToken e )
		{
			if ( e == null ) return new Bind( null );
			if ( e.Type == JTokenType.String ) return new Bind( ( string ) e );
			if ( e is JObject o )
			{
				var v = o[ "v" ];
				var sg = o[ "sign" ];
				return new Bind( v != null && v.Type == JTokenType.String ? ( string ) v : null, sg != null && ( sg.Type == JTokenType.Integer || sg.Type == JTokenType.Float ) && ( double ) sg == -1 ? -1 : 1 );
			}

			return new Bind( null );
		}

		public bool load()
		{
			opts.CopyFrom( new Options() );
			foreach ( var a in ACTIONS ) SetDefaults( a.id );
			if ( store == null ) return false;
			string raw;
			try { raw = store.GetItem( STORE_KEY ); }
			catch ( Exception ) { return false; }
			if ( string.IsNullOrEmpty( raw ) ) return false;
			JObject data;
			try { data = JToken.Parse( raw ) as JObject; }
			catch ( Exception ) { Debug.LogWarning( "controls: the saved bindings are not readable, using the defaults" ); return false; }
			if ( data == null ) return false;
			if ( data[ "opts" ] is JObject o )
			{
				var def = new Options();
				bool Num( string k, out double v ) { var t = o[ k ]; v = 0; if ( t == null || ( t.Type != JTokenType.Integer && t.Type != JTokenType.Float ) ) return false; v = ( double ) t; return true; }
				bool Bool( string k, out bool v ) { var t = o[ k ]; v = false; if ( t == null || t.Type != JTokenType.Boolean ) return false; v = ( bool ) t; return true; }
				if ( Bool( "padEnabled", out var b1 ) ) opts.padEnabled = b1;
				if ( Num( "deadzone", out var n1 ) ) opts.deadzone = n1;
				if ( Num( "lookSensitivity", out var n2 ) ) opts.lookSensitivity = n2;
				if ( Bool( "invertY", out var b2 ) ) opts.invertY = b2;
				if ( Num( "rumble", out var n3 ) ) opts.rumble = n3;
				if ( Bool( "flashlightOn", out var b3 ) ) opts.flashlightOn = b3;
			}

			if ( ! ( data[ "actions" ] is JObject acts ) ) return false;
			foreach ( var prop in acts.Properties() )
			{
				if ( ! byId.ContainsKey( prop.Name ) ) continue; // an action that no longer exists
				if ( ! ( prop.Value is JObject own ) ) continue;
				foreach ( var dev in DEVICES )
				{
					if ( ! ( own[ DEVICE_KEYS[ ( int ) dev ] ] is JArray arr ) ) continue;
					var l = list( prop.Name, dev );
					l.Clear();
					l.AddRange( arr.Select( EntryOf ).Where( e => ValidEntry( dev, e ) ) );
				}
			}

			Dedupe();
			rev ++;
			return true;
		}

		// one input drives one action: a stored file with a duplicate (hand-edited, or written by an older version) keeps the first in table order and drops the rest
		void Dedupe()
		{
			var seen = new HashSet<string>();
			foreach ( var a in ACTIONS ) foreach ( var dev in DEVICES )
			{
				var l = list( a.id, dev );
				for ( int i = l.Count - 1; i >= 0; i -- )
				{
					string key = DEVICE_KEYS[ ( int ) dev ] + ":" + l[ i ].v;
					if ( seen.Contains( key ) )
					{
						Debug.LogWarning( $"controls: \"{l[ i ].v}\" was bound twice; dropped from \"{a.id}\"" );
						l.RemoveAt( i );
					}
					else seen.Add( key );
				}
			}
		}
	}
}
