using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Tidewater.Player;
using UnityEngine;

// Port of src/game/Guide.js, the first-play guide:
//  - an intro (3 cards) the first time the game starts: the goal, the fishing controls, getting around and where Joe and Marta are (live direction and distance; their markers pulse
//    on the minimap). Enter / Space / click / right arrow: next, left arrow back, Esc: skip. Replay from F1.
//  - one-time tips the first time something happens (rod out, first nibble, fish on, first catch, full cooler, night, next to the boat, at Joe's, at Marta's), in a card above the minimap.
// The seen state is saved under 'tidewater.guide' in the same JSON the browser keeps ({ "intro": true, "rodOut": true, ... }; FileSaveStore writes it beside the game save).
// The cards and tips keep the JS wording; the HTML is the markup UIKit.Flow reads. Drawn in IMGUI: the card, the dots and the buttons follow the CSS (.gm-guide*, .gm-coach*); the
// letter spacing of the eyebrows and the glass blur are not ported (see UIKit).
namespace Tidewater.Game
{
	public sealed class Guide
	{
		const string KEY = "tidewater.guide";

		// the intro
		static readonly string[] EYEBROW = { "Welcome to Tidewater", "Fishing", "Getting around" };
		static readonly string[] TITLE = { "Fish the island, sell your catch", "Cast, strike, reel", "Joe and Marta" };
		public const int CARDS = 3;

		static readonly string[] WELCOME =
		{
			"Catch fish from the <b>beach</b>, the <b>pier</b> or your <b>boat</b>. Different fish bite in the shallows, around the pier, over the reef and out in deep water, and they change with the time of day.",
			"The day runs on its own (<kbd bind=\"pauseTime\">T</kbd> pauses it): fish feed at dawn and dusk, tarpon bite at night, and the sea gets up and lies down on its own. Joe and Marta keep island hours.",
			"Sell your catch to <b>Joe</b> at the fish stand by the pier, then spend the money on upgrades from <b>Marta</b> at the chandlery by the boathouse: stronger line, a faster reel, a bigger hold, a fish finder and lights for fishing at night.",
		};

		// (keys, text) rows; "{stick}" is the movement cluster
		static readonly string[][] FISHING =
		{
			new[] { "<kbd bind=\"rod\">R</kbd>", "Take out the rod (by the water or on the boat)" },
			new[] { "Hold <kbd bind=\"rodUse\">LMB</kbd>", "Wind up, release to cast. Hold longer to cast farther" },
			new[] { "<kbd bind=\"rodUse\">LMB</kbd>", "Strike when the bobber is <b>pulled under</b> (dips are only nibbles)" },
			new[] { "Hold <kbd bind=\"rodUse\">LMB</kbd>", "Reel in. <b>Let go when the tension turns red</b>, or the line snaps" },
			new[] { "<kbd bind=\"rodIn\">RMB</kbd>", "Reel an empty line back in" },
			new[] { "<kbd bind=\"cooler\">I</kbd>", "Your cooler and fish log" },
		};

		static readonly string[][] AROUND =
		{
			new[] { "{stick}", "Move, mouse to look, <kbd>Shift</kbd> to run" },
			new[] { "<kbd bind=\"interact\">E</kbd>", "Board the boat, take the helm, talk to Joe and Marta" },
			new[] { "<kbd bind=\"controls\">F1</kbd>", "All controls, and this guide again" },
		};

		const string AROUND_NOTE = "Both are marked on the map in the lower right. Marta also sells a <b>trap licence</b> and lobster pots: set them from the working boat and haul them for lobster.";

		static readonly Dictionary<string, string> TIPS = new Dictionary<string, string>
		{
			{ "rodOut", "Hold the <b word=\"rodUse\">left mouse button</b> to wind up and release to cast. Try deeper water, around the pier or over the reef." },
			{ "nibble", "The bobber is dipping: something is <b>nibbling</b>. Wait until it is <b>pulled under</b>, then click to strike." },
			{ "fishOn", "<b word=\"rodUse\">Hold the left mouse button</b> to reel. When the tension needle nears the <b>red</b>, let go until it settles, then reel again." },
			{ "caught", "Into the cooler (<kbd bind=\"cooler\">I</kbd>). Sell your catch to <b>Joe</b> at the fish stand by the pier: he is on the map." },
			{ "full", "Your cooler is <b>full</b>. Sell to Joe, or buy a bigger hold from Marta at the chandlery." },
			{ "boat", "Your boat. <kbd bind=\"interact\">E</kbd> to board, <kbd bind=\"interact\">E</kbd> again at the wheel to drive (<kbd>W</kbd><kbd>S</kbd> throttle, <kbd>A</kbd><kbd>D</kbd> steer). Diesel is sold by Marta." },
			{ "joe", "<b>Joe</b> buys your fish. <kbd bind=\"interact\">E</kbd> to see what he will pay." },
			{ "marta", "<b>Marta</b> sells upgrades and diesel. <kbd bind=\"interact\">E</kbd> to see her stock." },
			{ "night", "After dark the <b>tarpon</b> and snapper feed, and the lamps come on. <b>Deck floodlights</b> from Marta let you fish from the boat at night." },
			{ "trap", "<b>Traps</b>: on deck <kbd bind=\"interact\">E</kbd> sets a pot from the working boat and hauls one back; at the helm it is <kbd bind=\"rodUse\">LMB</kbd>, at a crawl. They soak on the clock — a few hours fills them, and the map marks where you left them." },
		};

		// compass word for the direction from (x, z) to (tx, tz); north is -z
		public static string compassWord( double x, double z, double tx, double tz )
		{
			double a = Math.Atan2( tx - x, - ( tz - z ) ); // 0 north, clockwise
			string[] W = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
			return W[ ( ( ( int ) Math.Floor( a / ( Math.PI / 4 ) + 0.5 ) % 8 ) + 8 ) % 8 ]; // (Math.round: halves go up)
		}

		readonly GameHost game;
		readonly PlayerHost host;
		readonly MinimapView minimap;
		readonly ISaveStore store;
		readonly HashSet<string> seen = new HashSet<string>();

		public bool open { get; private set; }
		public int step { get; private set; }
		double wait;                      // seconds before the intro (the start overlay's 0.8 s); < 0: not at all
		double coachT, whereT;
		readonly List<string> queue = new List<string>();
		string current;                   // the tip on the card
		string afterCard;
		bool prevEquipped, prevFight;
		LastCatch prevCatch;
		float introFade, introRise, coachFade; // 0..1: the intro's opacity (420 ms) and rise (520 ms), the coach card's (360 ms)
		string lastTip;                        // the tip still fading out
		string joeWhere = "", martaWhere = "";

		public Guide( GameHost game, PlayerHost host, MinimapView minimap, ISaveStore store, bool intro )
		{
			this.game = game; this.host = host; this.minimap = minimap; this.store = store;
			Load();
			wait = intro && ! seen.Contains( "intro" ) ? 0.8 : - 1;
			prevCatch = game.state.lastCatch;
		}

		// ---- the seen state: { "intro": true, ... }

		static readonly Regex SEEN = new Regex( "\"([A-Za-z0-9_]+)\"\\s*:\\s*true", RegexOptions.Compiled );

		void Load()
		{
			try
			{
				var json = store?.GetItem( KEY );
				if ( json != null ) foreach ( Match m in SEEN.Matches( json ) ) seen.Add( m.Groups[ 1 ].Value );
			}
			catch ( Exception ) { /* unreadable: the guide just shows again */ }
		}

		void Save()
		{
			try
			{
				var sb = new StringBuilder( "{" );
				bool first = true;
				foreach ( var k in seen ) { if ( ! first ) sb.Append( ',' ); sb.Append( '"' ).Append( k ).Append( "\":true" ); first = false; }
				store?.SetItem( KEY, sb.Append( '}' ).ToString() );
			}
			catch ( Exception ) { /* storage blocked */ }
		}

		public bool Seen( string id ) => seen.Contains( id );

		// ---- intro

		public void Show( int i )
		{
			step = i;
			if ( minimap != null ) minimap.model.highlight( i == CARDS - 1 ? new[] { "joe", "marta" } : new string[ 0 ] );
			whereT = 0;
			if ( ! open )
			{
				if ( introFade <= 0 ) introRise = 0;
				open = true;
				game.GuideOpened( true );
			}
		}

		public void Next() { if ( step < CARDS - 1 ) Show( step + 1 ); else Close(); }

		public void Close()
		{
			if ( ! open ) return;
			open = false;
			if ( minimap != null ) minimap.model.highlight( new string[ 0 ] );
			seen.Add( "intro" );
			Save();
			game.GuideOpened( false );
		}

		// F1's "show the guide again"
		public void Replay()
		{
			seen.Clear();
			Save();
			queue.Clear();
			HideCoach();
			Show( 0 );
		}

		// ---- one-time tips

		public void Tip( string id )
		{
			if ( seen.Contains( id ) || queue.Contains( id ) || current == id ) return;
			queue.Add( id );
		}

		void HideCoach() { current = null; }

		// per frame, after the game (Game.update: this.guide.update( dt ))
		public void Update( double dt )
		{
			var g = game; var f = g.fishing; var p = host.player;
			if ( f == null ) return;
			float dtf = ( float ) dt;
			introFade = Mathf.Clamp01( introFade + ( open ? 1 : - 1 ) * dtf / 0.42f );
			if ( open ) introRise = Mathf.Min( 1, introRise + dtf / 0.52f );
			coachFade = Mathf.Clamp01( coachFade + ( current != null ? 1 : - 1 ) * dtf / 0.36f );
			if ( current != null ) lastTip = current;

			// the intro, a moment after the world is up
			if ( wait >= 0 )
			{
				wait -= dt;
				if ( wait < 0 ) Show( 0 );
			}

			if ( open )
			{
				// live direction and distance to Joe and Marta
				whereT -= dt;
				if ( whereT <= 0 && step == CARDS - 1 )
				{
					whereT = 0.25;
					double x = p.position.x, z = p.position.z;
					joeWhere = Where( x, z, Stalls.STAND );
					martaWhere = Where( x, z, Stalls.CHANDLERY );
				}

				return;
			}

			// triggers (edge detected; each tip shows once)
			var rod = f.rod; var b = f.bite;
			if ( rod.equipped && ! prevEquipped ) Tip( "rodOut" );
			if ( b != null && b.phase == "nibble" ) Tip( "nibble" );
			if ( f.fight != null && ! prevFight ) Tip( "fishOn" );
			var s = g.state;
			var lc = s.lastCatch;
			if ( lc != null && lc != prevCatch && lc.kept ) afterCard = "caught";
			if ( afterCard != null && ! f.catchOpen && f.landing == null )
			{
				Tip( afterCard );
				afterCard = null;
			}

			if ( s.holdKg >= s.stats.holdKg * 0.92 ) Tip( "full" );
			// after dark the island changes character: different fish, lamps lit
			double hour = g.clock.hour;
			if ( hour > 19.5 || hour < 4.5 ) Tip( "night" );
			// the trap line: the first time there is a pot to set or haul
			if ( g.trap != null && g.trap.Prompt( p ) != null ) Tip( "trap" );
			if ( p.mode == "walk" )
			{
				foreach ( var bt in g.OwnedBoats() )
					if ( Math.Sqrt( ( bt.position.x - p.position.x ) * ( bt.position.x - p.position.x ) + ( bt.position.z - p.position.z ) * ( bt.position.z - p.position.z ) ) < 9 ) { Tip( "boat" ); break; }
				foreach ( var v in g.vendors ) if ( v.inRange( p.position ) ) Tip( v.kind == "buyer" ? "joe" : "marta" );
			}

			prevEquipped = rod.equipped;
			prevFight = f.fight != null;
			prevCatch = lc;

			// the coach card: one tip at a time, ~7 s each (not over the catch card or a panel)
			bool busy = f.catchOpen || g.inventoryOpen || g.openVendor != null || g.fishGuideOpen;
			if ( current != null )
			{
				coachT -= dt;
				if ( coachT <= 0 || busy ) HideCoach();
			}
			else if ( queue.Count > 0 && ! busy )
			{
				var id = queue[ 0 ]; queue.RemoveAt( 0 );
				current = id;
				seen.Add( id );
				Save();
				if ( coachFade <= 0 ) lastTip = id;
				coachT = 7.5;
			}
		}

		static string Where( double x, double z, double[] t ) => $"{Math.Round( Math.Sqrt( ( t[ 0 ] - x ) * ( t[ 0 ] - x ) + ( t[ 1 ] - z ) * ( t[ 1 ] - z ) ) )} m {compassWord( x, z, t[ 0 ], t[ 1 ] )}";

		// ---- drawing (IMGUI)

		Texture2D vignette;
		UIKit.Labeller Label => a => game.Input?.label( a );
		bool Pad => game.Input != null && game.Input.device == Tidewater.Core.Device.pad;

		string Stick() => Pad ? $"<kbd>{Label( "forward" )}</kbd>" : "<kbd>W</kbd><kbd>A</kbd><kbd>S</kbd><kbd>D</kbd>";

		// the body of card i: draws it (or just measures it) in `width` from (x, y); returns its height
		float Body( int i, float x, float y, float width, bool draw, float a )
		{
			float u = UIKit.U;
			var ink = UIKit.INK2; var bold = UIKit.INK;
			float h = 0;
			float Para( string md, float fs, float line, Color c ) => UIKit.Flow( x, y + h, width, md, fs, line, c, bold, draw, Label, Pad, a );
			if ( i == 0 )
			{
				foreach ( var p in WELCOME ) { h += Para( p, 12.5f, 1.55f, ink ) + 12 * u; }
				return h;
			}

			var rows = i == 1 ? FISHING : AROUND;
			float keyW = 92 * u;
			foreach ( var r in rows )
			{
				string keys = r[ 0 ] == "{stick}" ? Stick() : r[ 0 ];
				float kh = UIKit.Flow( x, y + h, keyW, keys, 12.5f, 1.55f, ink, bold, false, Label, Pad, a );
				float th = UIKit.Flow( x + keyW + 12 * u, y + h, width - keyW - 12 * u, r[ 1 ], 12.5f, 1.55f, ink, bold, false, Label, Pad, a );
				if ( draw )
				{
					UIKit.Flow( x, y + h, keyW, keys, 12.5f, 1.55f, ink, bold, true, Label, Pad, a );
					UIKit.Flow( x + keyW + 12 * u, y + h, width - keyW - 12 * u, r[ 1 ], 12.5f, 1.55f, ink, bold, true, Label, Pad, a );
				}

				h += Mathf.Max( kh, th ) + 7 * u;
			}

			h += ( 12 - 7 ) * u;
			if ( i == 1 ) return h;

			// where Joe and Marta are
			h += 4 * u;
			for ( int k = 0; k < 2; k ++ )
			{
				bool joe = k == 0;
				float bh = 38 * u;
				if ( draw )
				{
					var br = new Rect( x, y + h, width, bh );
					UIKit.Rounded( br, new Color( UIKit.FILL.r, UIKit.FILL.g, UIKit.FILL.b, UIKit.FILL.a * a ), 10 * u );
					UIKit.Rounded( br, new Color( UIKit.LINE.r, UIKit.LINE.g, UIKit.LINE.b, UIKit.LINE.a * a ), 10 * u, 1 );
					var dot = new Rect( br.x + 12 * u, br.y + bh / 2 - 6 * u, 12 * u, 12 * u );
					UIKit.Rounded( dot, new Color( 1, 1, 1, 0.8f * a ), 7.5f * u );
					var dc = joe ? UIKit.SUN : UIKit.AQUA; dc.a = a;
					UIKit.Rounded( new Rect( dot.x + 1.5f * u, dot.y + 1.5f * u, 9 * u, 9 * u ), dc, 5 * u );
					string name = joe ? "Joe" : "Marta", rest = joe ? " · fish stand by the pier" : " · chandlery by the boathouse";
					var st = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.MiddleLeft );
					var sb = UIKit.Style( UIFonts.InterSemi, 12.5f, TextAnchor.MiddleLeft );
					float nw = sb.CalcSize( new GUIContent( name ) ).x;
					sb.normal.textColor = new Color( bold.r, bold.g, bold.b, a ); GUI.Label( new Rect( br.x + 36 * u, br.y, nw + 2, bh ), name, sb );
					st.normal.textColor = new Color( ink.r, ink.g, ink.b, ink.a * a ); GUI.Label( new Rect( br.x + 36 * u + nw, br.y, width, bh ), rest, st );
					var em = UIKit.Style( UIFonts.Mono, 11.5f, TextAnchor.MiddleRight );
					em.normal.textColor = new Color( ink.r, ink.g, ink.b, ink.a * a );
					GUI.Label( new Rect( br.x, br.y, width - 12 * u, bh ), joe ? joeWhere : martaWhere, em );
				}

				h += bh + 8 * u;
			}

			h += ( 12 - 8 ) * u;
			h += UIKit.Flow( x, y + h, width, AROUND_NOTE, 11.5f, 1.55f, UIKit.INK3, new Color( UIKit.INK.r, UIKit.INK.g, UIKit.INK.b, 1 ), draw, Label, Pad, a );
			return h;
		}

		// the intro card, the dots and the buttons, the coach card
		public void OnGUI()
		{
			float u = UIKit.U, w = Screen.width, hgt = Screen.height;
			float op = UIScale.Ease( introFade );
			if ( op > 0.001f ) DrawIntro( u, w, hgt, op );
			DrawCoach( u, w, hgt );
		}

		void DrawIntro( float u, float w, float hgt, float op )
		{
			var ev = Event.current;
			if ( vignette == null )
			{
				// radial-gradient( 70% 70% at 50% 50%, rgba( 4, 10, 16, 0.2 ), rgba( 4, 10, 16, 0.55 ) )
				vignette = new Texture2D( 64, 36, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
				for ( int py = 0; py < 36; py ++ ) for ( int px = 0; px < 64; px ++ )
				{
					float vx = ( ( px + 0.5f ) / 64 - 0.5f ) / 0.7f, vy = ( ( py + 0.5f ) / 36 - 0.5f ) / 0.7f, r = Mathf.Clamp01( Mathf.Sqrt( vx * vx + vy * vy ) );
					vignette.SetPixel( px, py, new Color( 4 / 255f, 10 / 255f, 16 / 255f, Mathf.Lerp( 0.2f, 0.55f, r ) ) );
				}
				vignette.Apply();
			}

			var old = GUI.color; GUI.color = new Color( 1, 1, 1, op );
			GUI.DrawTexture( new Rect( 0, 0, w, hgt ), vignette, ScaleMode.StretchToFill, true );
			GUI.color = old;

			// the card: min( 480u, 100vw - 2 edge ), padding 24 / 24 / 16
			float cw = Mathf.Min( 480 * u, w - 2 * UIScale.Edge ), pad = 24 * u, inner = cw - 2 * pad;
			float bodyH = Body( step, 0, 0, inner, false, 1 );
			float eyebrowH = 15 * u, titleH = 24 * u * 1.2f, footH = 28 * u;
			float ch = pad + eyebrowH + 8 * u + titleH + 12 * u + bodyH + 16 * u + footH + 16 * u;
			float rise = ( 1 - UIScale.Ease( introRise ) ) * 10 * u;
			var card = new Rect( ( w - cw ) / 2, ( hgt - ch ) / 2 + rise, cw, ch );
			UIKit.Glass( card, 16 * u, op );

			float x = card.x + pad, y = card.y + pad;
			var eb = UIKit.Style( UIFonts.InterSemi, 10.5f, TextAnchor.UpperLeft );
			eb.normal.textColor = new Color( UIKit.SUN.r, UIKit.SUN.g, UIKit.SUN.b, op );
			GUI.Label( new Rect( x, y, inner, eyebrowH ), EYEBROW[ step ].ToUpperInvariant(), eb );
			y += eyebrowH + 8 * u;
			var t = UIKit.Style( UIFonts.InterSemi, 24f, TextAnchor.UpperLeft );
			t.normal.textColor = new Color( UIKit.INK.r, UIKit.INK.g, UIKit.INK.b, op );
			GUI.Label( new Rect( x, y, inner, titleH + 4 ), TITLE[ step ], t );
			y += titleH + 12 * u;
			Body( step, x, y, inner, true, op );
			y += bodyH + 16 * u;

			// the foot: the dots, the hint, Skip and the next button
			float dx = x;
			for ( int i = 0; i < CARDS; i ++ )
			{
				bool on = i == step;
				float dw = on ? 18 * u : 6 * u;
				UIKit.Rounded( new Rect( dx, y + footH / 2 - 3 * u, dw, 6 * u ), on ? new Color( UIKit.SUN.r, UIKit.SUN.g, UIKit.SUN.b, op ) : new Color( UIKit.FILL2.r, UIKit.FILL2.g, UIKit.FILL2.b, UIKit.FILL2.a * op ), 3 * u );
				dx += dw + 6 * u;
			}

			string nextText = step == CARDS - 1 ? "Let's fish" : "Next";
			float bh = footH, nw = UIKit.ButtonWidth( nextText ), sw = UIKit.ButtonWidth( "Skip" );
			var nextR = new Rect( card.xMax - pad - nw, y, nw, bh );
			var skipR = new Rect( nextR.x - 8 * u - sw, y, sw, bh );
			string esc = Label( "cancel" ) ?? "Esc";
			var hs = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.MiddleRight );
			hs.normal.textColor = new Color( UIKit.INK3.r, UIKit.INK3.g, UIKit.INK3.b, UIKit.INK3.a * op );
			float hintW = hs.CalcSize( new GUIContent( " to skip" ) ).x, kw = UIKit.KbdSize( esc ).width;
			float hx = skipR.x - 16 * u - hintW - kw;
			UIKit.Kbd( new Rect( hx, y + bh / 2 - 10 * u, kw, 20 * u ), esc, op );
			GUI.Label( new Rect( hx + kw, y, hintW + 2, bh ), " to skip", hs );

			if ( ! open ) return; // fading out: no input
			bool skip = UIKit.Button( skipR, "Skip", true, true, op ), next = UIKit.Button( nextR, nextText, false, true, op );
			if ( skip ) { Close(); return; }
			if ( next ) { Next(); return; }
			// a click anywhere else is "next"; the keys are the guide's own while it is up
			if ( ev.type == EventType.MouseDown && ev.button == 0 && ! skipR.Contains( ev.mousePosition ) && ! nextR.Contains( ev.mousePosition ) ) { ev.Use(); Next(); return; }
			if ( ev.type == EventType.KeyDown )
			{
				var k = ev.keyCode;
				if ( k == KeyCode.Escape ) { ev.Use(); Close(); }
				else if ( k == KeyCode.Return || k == KeyCode.KeypadEnter || k == KeyCode.Space || k == KeyCode.RightArrow ) { ev.Use(); Next(); }
				else if ( k == KeyCode.LeftArrow ) { ev.Use(); Show( Mathf.Max( 0, step - 1 ) ); }
			}
		}

		// .gm-coach: bottom right above the minimap, 290u wide, fading and rising in
		void DrawCoach( float u, float w, float hgt )
		{
			float a = UIScale.Ease( coachFade );
			if ( a <= 0.001f ) return;
			string id = current ?? lastTip;
			if ( id == null ) return;
			float cw = Mathf.Min( 290 * u, w - 2 * UIScale.Edge ), padX = 16 * u, padY = 12 * u;
			float right = UIScale.Edge;
			// an open panel is not a case here: the coach steps aside while one is up (Update)
			float th = UIKit.Flow( 0, 0, cw - 2 * padX, TIPS[ id ], 11.5f, 1.5f, UIKit.INK, UIKit.INK, false, Label, Pad );
			float eyebrowH = 10.5f * u * 1.5f + 3 * u;
			float ch = padY * 2 + eyebrowH + th;
			float rise = ( 1 - a ) * 8 * u;
			float bottom = hgt - ( UIScale.Edge + 184 * u + 12 * u );
			var r = new Rect( w - right - cw, bottom - ch + rise, cw, ch );
			UIKit.Glass( r, 16 * u, a );
			var eb = UIKit.Style( UIFonts.InterSemi, 10.5f );
			eb.normal.textColor = new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, a );
			GUI.Label( new Rect( r.x + padX, r.y + padY, cw, 16 * u ), "TIP", eb );
			UIKit.Flow( r.x + padX, r.y + padY + eyebrowH, cw - 2 * padX, TIPS[ id ], 11.5f, 1.5f, UIKit.INK, UIKit.INK, true, Label, Pad, a );
		}

	}
}
