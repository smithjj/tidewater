using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Core;
using UnityEngine;
using UnityEngine.InputSystem;

// The settings rail and panel (src/ui/UI.js _buildPanel, Tab, togglePanel, setPhotoMode; ui.css .tw-panel, .tw-rail, .tw-tabs, .tw-panel-foot, .tw-photo-hint, .tw-tip, .tw-menu) as IMGUI.
// SettingsTabs fills it with the seven tabs; SettingsControls has the controls. Drawn in two passes: OnGUI (the rail, the panel, the photo hint), and OnGUIOverlay at the very end of
// the game's OnGUI (the open dropdown, the tooltip: above everything, as the JS z-order has them).
//
// Same IMGUI rules as the other panels: the Layout and the Repaint of a frame must agree (animation only in Tick), the panel does its own hit testing (Hot: IMGUI lets an event reach a
// control that a scroll view has clipped away), and the mouse is not captured while it is over the panel.
//
// Not ported: keyboard focus on the tab strip and the arrow keys (the pad's menu layer, PadUI, is not ported), the page's edge fade (the CSS mask), the tab strip's horizontal
// scrolling (the inactive tabs shrink to fit instead).
namespace Tidewater.Game
{
	public sealed class SettingsUI
	{
		public readonly GameHost game;
		public readonly List<STab> tabs = new List<STab>();
		public STab activeTab { get; private set; }
		public bool open { get; private set; }
		public bool photo { get; private set; }

		// shared with the controls
		public bool inputOk = true;       // the controls may react (off while a sheet covers the panel, or a fold is moving)
		public SItem active;              // the control being dragged
		public sealed class EditState { public SItem owner; public string text; public Action<string> commit; public bool focused, selected; }
		public sealed class MenuState { public SItem owner; public Vector2 anchorScreen, anchorSize; public string[] opts; public int sel; public Action<int> pick; public float born; }
		public EditState edit;
		public MenuState menu;
		public SBinding capture;
		public GameInput Input => game.Input;

		float panelT, railT, pageT = 1, idleT, hintT, photoPoke = -10, menuT, tipA, lastAct;
		Rect clip = BIG;
		readonly Stack<Rect> clips = new Stack<Rect>();
		static readonly Rect BIG = new Rect( -100000, -100000, 200000, 200000 );
		const string TAB_KEY = "tidewater.ui.tab";

		// the tooltip: what the pointer is over this frame, what has been over it long enough, and how it looks while it fades
		struct TipReq { public string text, sub; public Vector2 screen, size; public bool left; }
		TipReq? tipReq; TipReq tipShown; string tipKey; float tipSince;

		public SettingsUI( GameHost game ) { this.game = game; lastAct = Time.unscaledTime; }

		// ---- the tabs

		public STab AddTab( string id, string label, string icon )
		{
			var t = new STab( id, label, icon ) { ui = this };
			tabs.Add( t );
			string saved = null;
			try { saved = PlayerPrefs.GetString( TAB_KEY, null ); } catch ( Exception ) { }
			if ( activeTab == null || saved == id ) { if ( activeTab != null ) activeTab.act = 0; activeTab = t; t.act = 1; }
			return t;
		}

		public STab GetTab( string id ) => tabs.FirstOrDefault( t => t.id == id );

		public void SelectTab( string id, bool remember = true )
		{
			var t = GetTab( id );
			if ( t == null || t == activeTab ) return;
			CloseMenu(); EndEdit( true ); capture?.EndCapture();
			activeTab = t;
			pageT = 0;
			if ( remember ) try { PlayerPrefs.SetString( TAB_KEY, id ); } catch ( Exception ) { }
		}

		// ---- the commands (UI.command / togglePanel / setPhotoMode)

		public bool Command( string name )
		{
			if ( name == "settings" ) { if ( photo ) SetPhoto( false ); Toggle(); return true; }
			if ( name == "photo" ) { SetPhoto( ! photo ); return true; }
			return false;
		}

		public void Toggle( bool? force = null )
		{
			bool want = force ?? ! open;
			if ( want == open ) return;
			open = want;
			active = null; CloseMenu(); EndEdit( false ); capture?.EndCapture();
			if ( open && Input != null ) Input.ReleaseLock(); // (UI.releasePointerOnPanel)
			lastAct = Time.unscaledTime;
		}

		public void SetPhoto( bool on )
		{
			if ( on == photo ) return;
			photo = on;
			if ( on )
			{
				if ( game.controls != null ) game.controls.Toggle( false );
				active = null; CloseMenu(); EndEdit( false ); capture?.EndCapture();
				photoPoke = Time.unscaledTime;
			}
			else lastAct = Time.unscaledTime;
		}

		// Esc (the cancel action): a dropdown closes first
		public bool Cancel()
		{
			if ( menu != null ) { CloseMenu(); return true; }
			return false;
		}

		// ---- what the controls call

		public void PushClip( Rect local )
		{
			clips.Push( clip );
			var r = new Rect( GUIUtility.GUIToScreenPoint( local.position ), local.size );
			float x0 = Mathf.Max( clip.x, r.x ), y0 = Mathf.Max( clip.y, r.y ), x1 = Mathf.Min( clip.xMax, r.xMax ), y1 = Mathf.Min( clip.yMax, r.yMax );
			clip = new Rect( x0, y0, Mathf.Max( 0, x1 - x0 ), Mathf.Max( 0, y1 - y0 ) );
		}

		public void PopClip() { clip = clips.Count > 0 ? clips.Pop() : BIG; }

		// the pointer is over `r` (in the current GUI space), the panel is live, nothing is being dragged, no dropdown has the clicks, and the point is not clipped away
		public bool Hot( Rect r )
		{
			var ev = Event.current;
			if ( ev.type == EventType.Layout || ! inputOk || menu != null || active != null ) return false;
			if ( ! clip.Contains( GUIUtility.GUIToScreenPoint( ev.mousePosition ) ) ) return false;
			return r.Contains( ev.mousePosition );
		}

		public void Tip( string text, string sub, Rect r, bool left = false )
		{
			if ( Event.current.type != EventType.Repaint || string.IsNullOrEmpty( text ) ) return;
			tipReq = new TipReq { text = text, sub = sub, screen = GUIUtility.GUIToScreenPoint( r.position ), size = r.size, left = left };
		}

		// ---- the value editor (click a readout, type, Enter)

		public void BeginEdit( SItem owner, string text, Action<string> commit )
		{
			EndEdit( true );
			CloseMenu();
			edit = new EditState { owner = owner, text = text, commit = commit };
		}

		public void EndEdit( bool apply )
		{
			var e = edit;
			if ( e == null ) return;
			edit = null;
			if ( GUI.GetNameOfFocusedControl() == "tw-edit" ) GUI.FocusControl( null );
			if ( apply ) { try { e.commit?.Invoke( e.text ); } catch ( Exception ex ) { Debug.LogException( ex ); } }
		}

		static GUIStyle editStyle; static float editU;
		static GUIStyle EditStyle()
		{
			if ( editStyle != null && editU == UIKit.U && editStyle.font == UIFonts.Mono ) return editStyle;
			editU = UIKit.U;
			editStyle = new GUIStyle( GUI.skin.textField ) { font = UIFonts.Mono, fontSize = Mathf.RoundToInt( 11.5f * UIKit.U ), alignment = TextAnchor.MiddleRight, clipping = TextClipping.Clip, padding = new RectOffset(), margin = new RectOffset(), border = new RectOffset() };
			foreach ( var s in new[] { editStyle.normal, editStyle.hover, editStyle.active, editStyle.focused, editStyle.onNormal, editStyle.onHover, editStyle.onActive, editStyle.onFocused } ) { s.background = null; s.textColor = UIKit.INK; }
			return editStyle;
		}

		public void DrawEdit( Rect r, float a )
		{
			var e = edit; var ev = Event.current; float u = UIKit.U;
			if ( e == null ) return;
			if ( ev.type == EventType.Repaint )
			{
				UIKit.Rounded( r, new Color( 0, 0, 0, 0.3f * a ), 5 * u );
				UIKit.Rounded( r, UIKit.AQUA.WithAlpha( 0.55f * a ), 5 * u, 1 );
			}

			if ( ev.type == EventType.KeyDown && ( ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter ) ) { ev.Use(); EndEdit( true ); return; }
			if ( ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape ) { ev.Use(); EndEdit( false ); return; }
			if ( ev.type == EventType.MouseDown && ! r.Contains( ev.mousePosition ) ) { EndEdit( true ); return; }

			GUI.SetNextControlName( "tw-edit" );
			e.text = GUI.TextField( new Rect( r.x + 6 * u, r.y, r.width - 12 * u, r.height ), e.text ?? "", 24, EditStyle() );
			if ( ! e.focused ) { GUI.FocusControl( "tw-edit" ); e.focused = true; }
			else if ( ! e.selected && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() == "tw-edit" )
			{
				var te = GUIUtility.GetStateObject( typeof( TextEditor ), GUIUtility.keyboardControl ) as TextEditor;
				te?.SelectAll();
				e.selected = true;
			}
			else if ( e.selected && ev.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != "tw-edit" ) EndEdit( true ); // (focus left it: Tab, or a click elsewhere)
		}

		// ---- the dropdown

		public void OpenMenu( SItem owner, Rect anchor, string[] opts, int sel, Action<int> pick )
		{
			CloseMenu(); EndEdit( true );
			menu = new MenuState { owner = owner, anchorScreen = GUIUtility.GUIToScreenPoint( anchor.position ), anchorSize = anchor.size, opts = opts, sel = sel, pick = pick, born = Time.unscaledTime };
			menuT = 0;
		}

		public void CloseMenu() { menu = null; }

		// a setting that is saved to disk: saved once the slider has been still for a moment (and when the panel closes), not on every frame of a drag
		Action pendingSave; float pendingAt;
		public void SaveSoon( Action save ) { pendingSave = save; pendingAt = Time.unscaledTime; }
		void FlushSave() { var s = pendingSave; pendingSave = null; try { s?.Invoke(); } catch ( Exception e ) { Debug.LogException( e ); } }

		// ---- per frame (GameHost.Tick: animations step here, never in OnGUI)

		public void Tick( double dtd )
		{
			float dt = ( float ) dtd;
			bool blocked = Blocked;
			if ( pendingSave != null && ( ! open || ( active == null && Time.unscaledTime - pendingAt > 0.25f ) ) ) FlushSave();
			panelT = UIKit.Approach( panelT, open && ! photo ? 1 : 0, 0.42f, dt );
			railT = UIKit.Approach( railT, ! open && ! photo ? 1 : 0, open ? 0.14f : 0.24f, dt );
			pageT = UIKit.Approach( pageT, 1, 0.24f, dt );
			menuT = menu != null ? UIKit.Approach( menuT, 1, 0.2f, dt ) : 0;
			hintT = UIKit.Approach( hintT, photo && Time.unscaledTime - photoPoke < 2.4f ? 1 : 0, 0.7f, dt );
			foreach ( var t in tabs ) t.act = UIKit.Approach( t.act, t == activeTab ? 1 : 0, 0.42f, dt );

			// the HUD recedes after a few seconds without any input (the rail dims)
			var m = Mouse.current; var k = Keyboard.current;
			if ( ( m != null && m.delta.ReadValue().sqrMagnitude > 0 ) || ( k != null && k.anyKey.isPressed ) ) { lastAct = Time.unscaledTime; if ( photo ) photoPoke = Time.unscaledTime; }
			bool idle = Time.unscaledTime - lastAct > 5;
			idleT = UIKit.Approach( idleT, idle ? 1 : 0, 0.24f, dt );

			if ( open && ! photo ) foreach ( var t in tabs ) t.Tick( dt );

			// a binding row waiting for input: the pad has no events, so it is polled here
			if ( capture != null && capture.Ready && Input != null && Input.PollCapture( out var r ) ) capture.Assign( r );
			// typing a number or pressing the key to bind must not also move the boat
			if ( Input != null ) Input.capturing = edit != null || capture != null;

			inputOk = open && ! blocked;
			if ( ! open ) { active = null; }
		}

		// a sheet drawn over the panel owns the clicks
		bool Blocked => ( game.controls != null && game.controls.open ) || ( game.guide != null && game.guide.open );

		// ---- layout constants (CSS px, times U)

		static float PanelW => Mathf.Min( 340 * UIKit.U, Screen.width - 24 * UIKit.U );

		float[] TabWidths( float inner )
		{
			float u = UIKit.U, gap = 3 * u;
			int n = tabs.Count;
			var w = new float[ n ];
			var st = UIKit.Style( UIFonts.InterMedium, 12.5f );
			float sum = 0, free;
			for ( int i = 0; i < n; i++ )
			{
				float e = UIScale.Ease( tabs[ i ].act );
				float lw = Mathf.Min( 130 * u, st.CalcSize( new GUIContent( tabs[ i ].label ) ).x );
				float natural = 18 * u + 16 * u + 7 * u + lw;
				w[ i ] = Mathf.Lerp( 32 * u, Mathf.Max( 32 * u, natural ), e );
				sum += w[ i ];
			}

			free = inner - sum - gap * ( n - 1 );
			float wt = tabs.Sum( t => 1 - UIScale.Ease( t.act ) );
			if ( free > 0 && wt > 0.001f ) { for ( int i = 0; i < n; i++ ) w[ i ] += free * ( 1 - UIScale.Ease( tabs[ i ].act ) ) / wt; }
			else if ( free < 0 && wt > 0.001f ) { for ( int i = 0; i < n; i++ ) w[ i ] = Mathf.Max( 26 * u, w[ i ] + free * ( 1 - UIScale.Ease( tabs[ i ].act ) ) / wt ); }
			return w;
		}

		// ---- the frame

		public void OnGUI()
		{
			var ev = Event.current; float u = UIKit.U, w = Screen.width, h = Screen.height;
			clip = BIG; clips.Clear();
			if ( ev.type == EventType.Repaint ) tipReq = null;
			if ( ev.type == EventType.MouseMove || ev.type == EventType.MouseDown || ev.type == EventType.ScrollWheel || ev.type == EventType.KeyDown ) { lastAct = Time.unscaledTime; if ( photo ) photoPoke = Time.unscaledTime; }

			if ( photo ) { Toasts.panelInset = 0; DrawPhotoHint(); if ( Input != null ) Input.uiHover = false; return; }

			float pw = PanelW, pe = UIScale.Ease( Mathf.Clamp01( panelT ) );
			Toasts.panelInset = ( pw + 12 * UIKit.U ) * pe;
			var panel = new Rect( w - 12 * u - pw + 28 * u * ( 1 - pe ), 12 * u, pw, h - 24 * u );
			float pa = Mathf.Clamp01( UIScale.Ease( Mathf.Clamp01( panelT * 1.75f ) ) );
			float re = UIScale.Ease( Mathf.Clamp01( railT ) );
			var rail = new Rect( w - 12 * u - 48 * u + 14 * u * ( 1 - re ), ( h - RailH() ) / 2, 48 * u, RailH() );

			bool overPanel = panelT > 0.02f && open && panel.Contains( ev.mousePosition ), overRail = railT > 0.5f && rail.Contains( ev.mousePosition );
			if ( Input != null ) Input.uiHover = overPanel || overRail;

			if ( panelT > 0.001f ) DrawPanel( panel, pa );
			if ( railT > 0.001f ) DrawRail( rail, re, overRail );
		}

		float RailH() => ( 12 + 36 + 9 + ( tabs.Count * 36 + Mathf.Max( 0, tabs.Count - 1 ) * 2 ) + 9 + 36 + 4 * 2 ) * UIKit.U;

		static void Label( Rect r, string s, Font f, float px, Color c, TextAnchor al = TextAnchor.MiddleLeft, bool clipText = false )
		{
			var st = UIKit.Style( f, px, al );
			st.normal.textColor = c;
			st.clipping = clipText ? TextClipping.Clip : TextClipping.Overflow;
			GUI.Label( r, s, st );
			st.clipping = TextClipping.Overflow;
		}

		bool IconButton( Rect b, string icon, string tip, float a )
		{
			float u = UIKit.U; var ev = Event.current;
			bool hot = Hot( b );
			if ( ev.type == EventType.Repaint )
			{
				if ( hot ) UIKit.Rounded( b, UIKit.FILL2.WithAlpha( a ), 8 * u );
				IconKit.DrawAt( b.center.x, b.center.y, 18 * u, icon, ( hot ? UIKit.INK : UIKit.INK3 ).WithAlpha( a ) );
			}

			if ( hot ) Tip( tip, null, b );
			if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { ev.Use(); return true; }
			return false;
		}

		void DrawPanel( Rect p, float a )
		{
			var ev = Event.current; float u = UIKit.U;
			bool was = inputOk;
			if ( panelT < 1 ) inputOk = was && open; // (live as soon as it is open)
			// (the JS glass blurs what is behind it: a second layer of the fill keeps the minimap and the HUD under the panel from reading through)
			if ( ev.type == EventType.Repaint ) { UIKit.Rounded( p, new Color( UIKit.GLASS.r, UIKit.GLASS.g, UIKit.GLASS.b, 0.8f * a ), 16 * u ); UIKit.Glass( p, 16 * u, a ); }

			// header: the title and the three actions
			float hy = p.y + 12 * u;
			Label( new Rect( p.x + 16 * u, hy, 160 * u, 30 * u ), "Settings", UIFonts.InterSemi, 14, UIKit.INK.WithAlpha( a ) );
			float bx = p.xMax - 10 * u - 3 * 30 * u - 2 * 2 * u;
			if ( IconButton( new Rect( bx, hy, 30 * u, 30 * u ), "viewfinder", "Photo mode (" + KeyOf( "photo", "P" ) + ")", a ) ) SetPhoto( true );
			if ( IconButton( new Rect( bx + 32 * u, hy, 30 * u, 30 * u ), "help", "Controls (" + KeyOf( "controls", "F1" ) + ")", a ) ) game.controls?.Toggle();
			if ( IconButton( new Rect( bx + 64 * u, hy, 30 * u, 30 * u ), "chevrons-right", "Collapse (" + KeyOf( "settings", "H" ) + ")", a ) ) Toggle( false );

			// the tab strip: the open tab shows its name
			var bar = new Rect( p.x + 12 * u, hy + 30 * u + 8 * u, p.width - 24 * u, 40 * u );
			if ( ev.type == EventType.Repaint )
			{
				UIKit.Rounded( bar, new Color( 0, 0, 0, 0.22f * a ), 12 * u );
				UIKit.Rounded( bar, UIKit.LINE.WithAlpha( a ), 12 * u, 1 );
			}

			var tw = TabWidths( bar.width - 6 * u );
			float tx = bar.x + 3 * u;
			for ( int i = 0; i < tabs.Count; i++ )
			{
				var t = tabs[ i ]; float e = UIScale.Ease( t.act );
				var tr = new Rect( tx, bar.y + 3 * u, tw[ i ], 34 * u );
				tx += tw[ i ] + 3 * u;
				bool hot = Hot( tr ), on = t == activeTab;
				if ( ev.type == EventType.Repaint )
				{
					if ( e > 0.01f )
					{
						UIKit.Rounded( new Rect( tr.x - 3 * u, tr.y - 3 * u, tr.width + 6 * u, tr.height + 6 * u ), UIKit.AQUA.WithAlpha( 0.08f * e * a ), 12 * u );
						UIKit.VRampPill( tr, UIKit.AQUA.WithAlpha( 0.2f * e ), UIKit.AQUA.WithAlpha( 0.09f * e ), 9 * u, a );
						UIKit.Rounded( tr, UIKit.AQUA.WithAlpha( 0.38f * e * a ), 9 * u, 1 );
					}
					else if ( hot ) UIKit.Rounded( tr, new Color( 1, 1, 1, 0.04f * a ), 9 * u );

					var st = UIKit.Style( UIFonts.InterMedium, 12.5f, TextAnchor.MiddleLeft );
					float lw = Mathf.Min( 130 * u, st.CalcSize( new GUIContent( t.label ) ).x ) * e, gapw = 7 * u * e;
					float cw = 16 * u + gapw + lw, cx = tr.center.x - cw / 2;
					var ink = Color.Lerp( hot ? UIKit.INK : UIKit.INK3, UIKit.INK, e );
					IconKit.DrawAt( cx + 8 * u, tr.center.y, 16 * u, t.icon, Color.Lerp( ink, UIKit.AQUA, e ).WithAlpha( a ) );
					if ( e > 0.02f ) Label( new Rect( cx + 16 * u + gapw, tr.y, lw + 4, tr.height ), t.label, UIFonts.InterMedium, 12.5f, UIKit.INK.WithAlpha( e * a ), TextAnchor.MiddleLeft, true );
				}

				if ( hot && ! on ) Tip( t.label, null, tr );
				if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { SelectTab( t.id ); ev.Use(); }
			}

			// the footer
			float footH = ( 10 + 18 + 10 ) * u + 1;
			var foot = new Rect( p.x, p.yMax - footH, p.width, footH );
			if ( ev.type == EventType.Repaint )
			{
				UIKit.Rounded( new Rect( foot.x, foot.y, foot.width, 1 ), UIKit.LINE.WithAlpha( a ), 0 );
				float fx = foot.x + 16 * u, fy = foot.y + 1 + 10 * u;
				foreach ( var (key, text) in new[] { ( KeyOf( "settings", "H" ), "Hide" ), ( KeyOf( "controls", "F1" ), "Controls" ), ( KeyOf( "photo", "P" ), "Photo mode" ) } )
				{
					var kb = UIKit.KbdSize( key, 9.5f );
					var kr = new Rect( fx, fy, Mathf.Max( 18 * u, kb.width - 2 * u ), 18 * u );
					UIKit.Kbd( kr, key, a, 9.5f );
					var st = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.MiddleLeft );
					float tw2 = st.CalcSize( new GUIContent( text ) ).x;
					Label( new Rect( kr.xMax + 6 * u, fy, tw2 + 2, 18 * u ), text, UIFonts.Inter, 10.5f, UIKit.INK3.WithAlpha( a ) );
					fx += kr.width + 6 * u + tw2 + 16 * u;
				}
			}

			// the page: scrolls; fades and rises in when the tab changes
			float y0 = bar.yMax + 8 * u;
			var view = new Rect( p.x, y0, p.width, foot.y - y0 );
			var tab = activeTab;
			if ( tab != null && view.height > 4 )
			{
				float cw = view.width - 8 * u;
				float contentH = 2 * u + tab.ChildrenHeight( cw ) + 12 * u;
				float pe = UIScale.Ease( pageT );
				float pa = a * pe;
				PushClip( view );
				tab.scroll = UIKit.BeginScroll( view, tab.scroll, contentH );
				bool okWas = inputOk; if ( pageT < 1 ) inputOk = false;
				tab.DrawChildren( 0, 2 * u + 4 * u * ( 1 - pe ), cw, pa );
				inputOk = okWas;
				UIKit.EndScroll( view, ref tab.scroll, contentH, a );
				PopClip();
				if ( tab.children.All( c => ! c.Shown ) && ev.type == EventType.Repaint ) Label( new Rect( view.x, view.y + 24 * u, view.width, 20 * u ), "No settings in this section yet.", UIFonts.Inter, 11.5f, UIKit.INK3.WithAlpha( a ), TextAnchor.MiddleCenter );
			}

			// a click on the glass belongs to the panel (and not to the minimap or a stall panel under it)
			if ( ev.type == EventType.MouseDown && inputOk && menu == null && p.Contains( ev.mousePosition ) ) ev.Use();
			inputOk = was;
		}

		string KeyOf( string action, string fallback )
		{
			string l = Input != null ? Input.label( action ) : null;
			return string.IsNullOrEmpty( l ) ? fallback : l;
		}

		void DrawRail( Rect r, float e, bool over )
		{
			var ev = Event.current; float u = UIKit.U;
			float hover = over ? 1 : 0;
			float a = e * Mathf.Lerp( 1, 0.45f, idleT * ( 1 - hover ) );
			if ( ev.type == EventType.Repaint ) UIKit.Glass( r, 14 * u, a );
			bool was = inputOk; inputOk = railT > 0.5f && ! Blocked && ! open;
			float x = r.x + 6 * u, y = r.y + 6 * u;

			bool Btn( string icon, bool isActive, string tip )
			{
				var b = new Rect( x, y, 36 * u, 36 * u );
				bool hot = Hot( b );
				if ( ev.type == EventType.Repaint )
				{
					if ( hot ) UIKit.Rounded( b, UIKit.FILL2.WithAlpha( a ), 10 * u );
					var ink = isActive ? UIKit.AQUA : hot ? UIKit.INK : UIKit.INK2;
					IconKit.DrawAt( b.center.x, b.center.y, 18 * u, icon, ink.WithAlpha( a ) );
					if ( isActive )
					{
						var bar = new Rect( b.x - 6 * u, b.y + b.height * 0.28f, 2, b.height * 0.44f );
						UIKit.Rounded( new Rect( bar.x - 3, bar.y - 2, bar.width + 6, bar.height + 4 ), UIKit.AQUA.WithAlpha( 0.25f * a ), 4 );
						UIKit.Rounded( bar, UIKit.AQUA.WithAlpha( a ), 2 );
					}
				}

				if ( hot ) Tip( tip, null, b, true );
				y += 36 * u + 2 * u;
				if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { ev.Use(); return true; }
				return false;
			}

			void Sep()
			{
				if ( ev.type == EventType.Repaint ) UIKit.Rounded( new Rect( r.center.x - 10 * u, y + 4 * u, 20 * u, 1 ), UIKit.LINE2.WithAlpha( a ), 0 );
				y += 9 * u + 2 * u;
			}

			if ( Btn( "sliders", false, "Settings (" + KeyOf( "settings", "H" ) + ")" ) ) Toggle( true );
			Sep();
			foreach ( var t in tabs ) if ( Btn( t.icon, t == activeTab, t.label ) ) { SelectTab( t.id ); Toggle( true ); }
			Sep();
			if ( Btn( "help", false, "Controls (" + KeyOf( "controls", "F1" ) + ")" ) ) game.controls?.Toggle();
			inputOk = was;
			if ( ev.type == EventType.MouseDown && over && railT > 0.5f && ! Blocked && menu == null ) ev.Use();
		}

		void DrawPhotoHint()
		{
			var ev = Event.current; float u = UIKit.U;
			if ( ev.type != EventType.Repaint || hintT <= 0.01f ) return;
			float a = UIScale.Ease( hintT );
			string key = KeyOf( "photo", "P" ), text = "Exit photo mode";
			var kb = UIKit.KbdSize( key, 9.5f );
			var st = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft );
			float tw = st.CalcSize( new GUIContent( text ) ).x, kw = Mathf.Max( 18 * u, kb.width - 2 * u);
			float wpill = 6 * u + kw + 8 * u + tw + 12 * u, hpill = 5 * u * 2 + 20 * u;
			var r = new Rect( ( Screen.width - wpill ) / 2, Screen.height - 16 * u - hpill, wpill, hpill );
			UIKit.Rounded( r, new Color( 8 / 255f, 12 / 255f, 18 / 255f, 0.45f * a ), hpill / 2 );
			var kr = new Rect( r.x + 6 * u, r.y + ( hpill - 18 * u ) / 2, kw, 18 * u );
			UIKit.Kbd( kr, key, a, 9.5f );
			Label( new Rect( kr.xMax + 8 * u, r.y, tw + 2, hpill ), text, UIFonts.Inter, 11.5f, UIKit.INK2.WithAlpha( a ) );
		}

		// ---- the overlay: the open dropdown, then the tooltip

		public void OnGUIOverlay()
		{
			if ( photo ) return;
			var ev = Event.current; float u = UIKit.U, w = Screen.width, h = Screen.height;
			clip = BIG;

			if ( menu != null )
			{
				var m = menu;
				var anchor = new Rect( GUIUtility.ScreenToGUIPoint( m.anchorScreen ), m.anchorSize );
				var st = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft );
				float mw = anchor.width, ih = 30 * u;
				foreach ( var o in m.opts ) mw = Mathf.Max( mw, st.CalcSize( new GUIContent( o ) ).x + 8 * u + 10 * u + 12 * u + 14 * u + 8 * u );
				float mh = m.opts.Length * ih + 8 * u, maxH = Mathf.Min( h * 0.5f, 320 * u );
				float top = anchor.yMax + 6;
				if ( top + mh > h - 8 ) top = Mathf.Max( 8, anchor.y - 6 - mh );
				float left = Mathf.Clamp( anchor.xMax - mw, 8, w - mw - 8 );
				var box = new Rect( left, top, mw, Mathf.Min( mh, maxH ) );
				float e = UIScale.Ease( Mathf.Clamp01( ( Time.unscaledTime - m.born ) / 0.2f ) );
				box.y += -4 * u * ( 1 - e );

				bool inside = box.Contains( ev.mousePosition );
				if ( ev.type == EventType.Repaint )
				{
					UIKit.Rounded( new Rect( box.x - 2, box.y + 8, box.width + 4, box.height + 4 ), new Color( 0, 0, 0, 0.3f * e ), 12 * u );
					UIKit.Rounded( box, new Color( 13 / 255f, 20 / 255f, 29 / 255f, 0.97f * e ), 10 * u );
					UIKit.Rounded( box, UIKit.LINE2.WithAlpha( e ), 10 * u, 1 );
					for ( int i = 0; i < m.opts.Length; i++ )
					{
						var it = new Rect( box.x + 4 * u, box.y + 4 * u + i * ih, box.width - 8 * u, ih );
						bool hot = it.Contains( ev.mousePosition );
						if ( hot ) UIKit.Rounded( it, UIKit.FILL2.WithAlpha( e ), 6 * u );
						var ink = i == m.sel ? UIKit.AQUA : hot ? UIKit.INK : UIKit.INK2;
						Label( new Rect( it.x + 10 * u, it.y, it.width - 10 * u - 8 * u - 14 * u, it.height ), m.opts[ i ], UIFonts.Inter, 11.5f, ink.WithAlpha( e ) );
						if ( i == m.sel ) IconKit.DrawAt( it.xMax - 8 * u - 7 * u, it.center.y, 14 * u, "check", ink.WithAlpha( e ) );
					}
				}

				if ( ev.type == EventType.MouseDown && ev.button == 0 )
				{
					if ( inside )
					{
						int i = Mathf.FloorToInt( ( ev.mousePosition.y - box.y - 4 * u ) / ih );
						if ( i >= 0 && i < m.opts.Length ) { CloseMenu(); try { m.pick?.Invoke( i ); } catch ( Exception ex ) { Debug.LogException( ex ); } }
					}
					else CloseMenu();
					ev.Use();
				}
				else if ( ev.type == EventType.MouseDown ) { CloseMenu(); ev.Use(); }
				else if ( ev.type == EventType.ScrollWheel ) { CloseMenu(); }
				tipReq = null;
			}

			// the tooltip: after it has been over the same thing for a moment (sooner on the rail); not while dragging or while the mouse is captured
			float dt = Time.unscaledDeltaTime;
			if ( ev.type == EventType.Repaint )
			{
				bool want = tipReq.HasValue && active == null && ( Input == null || ! Input.locked ) && edit == null && menu == null;
				if ( want )
				{
					var q = tipReq.Value; string key = q.text + "\n" + q.sub;
					if ( key != tipKey ) { tipKey = key; tipSince = Time.unscaledTime; tipA = 0; }
					if ( Time.unscaledTime - tipSince >= ( q.left ? 0.22f : 0.52f ) ) { tipShown = q; tipA = Mathf.MoveTowards( tipA, 1, dt / 0.14f ); }
				}
				else { tipKey = null; tipA = Mathf.MoveTowards( tipA, 0, dt / 0.14f ); }
			}

			if ( tipA > 0.01f && tipShown.text != null && ev.type == EventType.Repaint )
			{
				var q = tipShown; float a = tipA;
				var anchor = new Rect( GUIUtility.ScreenToGUIPoint( q.screen ), q.size );
				float maxW = 240 * u, padX = 9 * u, padY = 6 * u;
				var ts = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.UpperLeft, true );
				var ss = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.UpperLeft, true );
				float natural = Mathf.Max( ts.CalcSize( new GUIContent( q.text ) ).x, string.IsNullOrEmpty( q.sub ) ? 0 : ss.CalcSize( new GUIContent( q.sub ) ).x );
				float tw = Mathf.Min( maxW - 2 * padX, natural + 2 ), th = ts.CalcHeight( new GUIContent( q.text ), tw ), sh = string.IsNullOrEmpty( q.sub ) ? 0 : 3 * u + ss.CalcHeight( new GUIContent( q.sub ), tw );
				float bw = tw + 2 * padX, bh = th + sh + 2 * padY, x, y;
				if ( q.left ) { x = anchor.x - bw - 10; y = anchor.center.y - bh / 2; }
				else { x = anchor.center.x - bw / 2; y = anchor.y - bh - 8; if ( y < 8 ) y = anchor.yMax + 8; }
				x = Mathf.Clamp( x, 8, w - bw - 8 ); y = Mathf.Clamp( y, 8, h - bh - 8 );
				var box = new Rect( Mathf.Round( x ), Mathf.Round( y ), bw, bh );
				UIKit.Rounded( new Rect( box.x, box.y + 6, box.width, box.height ), new Color( 0, 0, 0, 0.22f * a ), 10 * u );
				UIKit.Rounded( box, new Color( 8 / 255f, 12 / 255f, 18 / 255f, 0.94f * a ), 7 * u );
				UIKit.Rounded( box, UIKit.LINE2.WithAlpha( a ), 7 * u, 1 );
				ts.normal.textColor = UIKit.INK.WithAlpha( a );
				GUI.Label( new Rect( box.x + padX, box.y + padY, tw, th ), q.text, ts );
				if ( sh > 0 ) { ss.normal.textColor = UIKit.INK3.WithAlpha( a ); GUI.Label( new Rect( box.x + padX, box.y + padY + th + 3 * u, tw, sh ), q.sub, ss ); }
			}
		}
	}
}
