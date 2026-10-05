using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tidewater.Core;
using UnityEngine;

// The controls of the settings panel (src/ui/UI.js: Control, SliderControl, ToggleControl, SelectControl, ButtonControl, PresetsControl, InfoControl, TimeOfDayControl,
// BindingControl, Folder, Tab), as a retained model that draws itself in IMGUI. Every control reads and writes its value through a getter and a setter, so what the game changes
// elsewhere (the weather turning the dynamic switch off, T pausing the day) shows without a refresh pass. A control knows its height for a width (Height) and draws into the
// rectangle it is given (Draw); animations step in Tick (never in OnGUI: the Layout and the Repaint of a frame must agree). Sizes are the CSS px of ui.css times UIKit.U.
//
// Not ported: keyboard focus and the arrow keys on a slider (the pad's menu layer, PadUI, is not ported either), the colour control (no panel uses one), the dotted tooltip underline
// is drawn as dots, the page's edge fade.
namespace Tidewater.Game
{
	public abstract class SItem
	{
		public SettingsUI ui;
		public SBox parent;
		public bool hidden;
		public Func<bool> visibleIf;
		public bool Shown => ! hidden && ( visibleIf == null || visibleIf() );
		public abstract float Height( float w );
		public abstract void Draw( Rect r, float a );
		public virtual void Tick( float dt ) { }
		public virtual void Reset() { }
		protected static float U => UIKit.U;
	}

	// a container: Tab and Folder add controls to it
	public class SBox : SItem
	{
		public readonly List<SItem> children = new List<SItem>();

		public T Add<T>( T c ) where T : SItem { c.ui = ui; c.parent = this; children.Add( c ); return c; }

		public SFolder AddFolder( string label, string icon = null, bool open = true, string tip = null ) => Add( new SFolder( label, icon, open, tip ) );
		public SSlider AddSlider( string label, Func<double> get, Action<double> set, double min, double max, double step = 0, string unit = "", bool log = false, Func<double, string> format = null, string tip = null ) => Add( new SSlider( label, get, set, min, max, step, unit, log, format, tip ) );
		public SToggle AddToggle( string label, Func<bool> get, Action<bool> set, string tip = null ) => Add( new SToggle( label, get, set, tip ) );
		public SSelect AddSelect( string label, string[] options, Func<int> get, Action<int> set, string tip = null ) => Add( new SSelect( label, options, get, set, tip ) );
		public SButton AddButton( string label, Action onClick, string icon = null, string variant = "default", string tip = null ) => Add( new SButton( label, onClick, icon, variant, tip ) );
		public SPresets AddPresets( string label, string[] labels, string[] icons, Action<int> apply, int active = -1, Action<int> after = null ) => Add( new SPresets( label, labels, icons, apply, active, after ) );
		public SInfo AddInfo( string label, Func<string> get, string tip = null ) => Add( new SInfo( label, get, tip ) );
		public STimeOfDay AddTimeOfDay( Func<double> get, Action<double> set ) => Add( new STimeOfDay( get, set ) );
		public SBinding AddBinding( Bindings bindings, string action, string label, Func<string> layout ) => Add( new SBinding( bindings, action, label, layout ) );

		public float ChildrenHeight( float w )
		{
			float h = 0;
			foreach ( var c in children ) if ( c.Shown ) h += c.Height( w );
			return h;
		}

		public void DrawChildren( float x, float y, float w, float a )
		{
			foreach ( var c in children )
			{
				if ( ! c.Shown ) continue;
				float h = c.Height( w );
				c.Draw( new Rect( x, y, w, h ), a );
				y += h;
			}
		}

		public override float Height( float w ) => ChildrenHeight( w );
		public override void Draw( Rect r, float a ) => DrawChildren( r.x, r.y, r.width, a );
		public override void Tick( float dt ) { foreach ( var c in children ) c.Tick( dt ); }
		public override void Reset() { foreach ( var c in children ) c.Reset(); }

		public IEnumerable<SItem> All()
		{
			foreach ( var c in children )
			{
				yield return c;
				if ( c is SBox b ) foreach ( var d in b.All() ) yield return d;
			}
		}
	}

	public sealed class STab : SBox
	{
		public string id, label, icon;
		public float act;            // 1 while it is the open tab (the strip's label grows with it)
		public Vector2 scroll;
		public STab( string id, string label, string icon ) { this.id = id; this.label = label; this.icon = icon; }
	}

	// ---- folder: a collapsible group; a folder in a folder is the nested card
	public sealed class SFolder : SBox
	{
		public string label, icon, tip;
		public bool open;
		float openT;

		public SFolder( string label, string icon, bool open, string tip ) { this.label = label; this.icon = icon; this.open = open; this.tip = tip; openT = open ? 1 : 0; }

		bool Nested => parent is SFolder;
		bool FirstOfPage => parent is STab && parent.children.FirstOrDefault( c => c.Shown ) == this;
		float HeadH => ( Nested ? 36 : 42 ) * U;
		float PadX => ( Nested ? 12 : 16 ) * U;
		float BodyFull( float w ) => ChildrenHeight( w - 2 * PadX ) + 8 * U;

		public override float Height( float w )
		{
			float u = U;
			float body = BodyFull( w - ( Nested ? 0 : 0 ) ) * UIScale.Ease( openT );
			return Nested ? 8 * u + HeadH + body : ( FirstOfPage ? 0 : 1 ) + HeadH + body;
		}

		public override void Tick( float dt )
		{
			openT = UIKit.Approach( openT, open ? 1 : 0, 0.42f, dt );
			if ( openT > 0 ) base.Tick( dt );
		}

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			float y = r.y;
			Rect card = r;
			if ( Nested )
			{
				y += 4 * u;
				card = new Rect( r.x, y, r.width, r.height - 8 * u );
				if ( ev.type == EventType.Repaint )
				{
					UIKit.Rounded( card, new Color( 1, 1, 1, 0.025f * a ), 10 * u );
					UIKit.Rounded( card, UIKit.LINE.WithAlpha( a ), 10 * u, 1 );
				}
			}
			else if ( ! FirstOfPage )
			{
				if ( ev.type == EventType.Repaint ) UIKit.Rounded( new Rect( r.x, y, r.width, 1 ), UIKit.LINE.WithAlpha( a ), 0 );
				y += 1;
			}

			var head = new Rect( card.x, y, card.width, HeadH );
			bool hot = ui.Hot( head );
			if ( ev.type == EventType.Repaint )
			{
				if ( hot ) UIKit.Rounded( head, new Color( 1, 1, 1, 0.03f * a ), Nested ? 10 * u : 0 );
				float cx = head.x + PadX;
				if ( icon != null ) { IconKit.DrawAt( cx + 7.5f * u, head.center.y, 15 * u, icon, UIKit.AQUA.WithAlpha( a ) ); cx += 15 * u + 10 * u; }
				var st = UIKit.Style( Nested ? UIFonts.InterMedium : UIFonts.InterSemi, 12.5f, TextAnchor.MiddleLeft );
				st.normal.textColor = UIKit.INK.WithAlpha( a );
				GUI.Label( new Rect( cx, head.y, head.xMax - PadX - 14 * u - 10 * u - cx, head.height ), label, st );
				var cc = new Vector2( head.xMax - PadX - 7 * u, head.center.y );
				var old = GUI.matrix;
				GUIUtility.RotateAroundPivot( Mathf.Lerp( -90, 0, UIScale.Ease( openT ) ), cc );
				IconKit.DrawAt( cc.x, cc.y, 14 * u, "chevron-down", UIKit.INK3.WithAlpha( a ) );
				GUI.matrix = old;
			}

			if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { open = ! open; ev.Use(); }
			if ( tip != null && hot ) ui.Tip( tip, null, head );

			float bodyH = BodyFull( card.width ) * UIScale.Ease( openT );
			if ( bodyH > 0.5f && openT > 0 )
			{
				var clip = new Rect( card.x, head.yMax, card.width, bodyH );
				bool still = openT >= 1 || openT <= 0;
				ui.PushClip( clip );
				GUI.BeginGroup( clip );
				bool was = ui.inputOk; if ( ! still ) ui.inputOk = false; // (no clicks on what a moving fold has half hidden)
				DrawChildren( PadX, 0, card.width - 2 * PadX, a );
				ui.inputOk = was;
				GUI.EndGroup();
				ui.PopClip();
			}
		}
	}

	// ---- one control: the label row (with the reset affordance), flash and modified state
	public abstract class Ctl : SItem
	{
		public string label, tip;
		public bool enabled = true;
		protected float modT, flashT;
		public abstract bool Modified { get; }

		public override void Tick( float dt )
		{
			modT = UIKit.Approach( modT, Modified ? 1 : 0, 0.14f, dt );
			if ( flashT > 0 ) flashT = Mathf.Max( 0, flashT - dt / 0.8f );
		}

		public void Flash() { flashT = 1; }

		protected void DrawFlash( Rect r, float a )
		{
			if ( flashT <= 0 || Event.current.type != EventType.Repaint ) return;
			float u = U;
			UIKit.Rounded( new Rect( r.x - 8 * u, r.y, r.width + 16 * u, r.height ), UIKit.AQUA.WithAlpha( 0.16f * UIScale.Ease( flashT ) * a ), 6 * u );
		}

		// the label with its reset button at (x, y), a 20u row; returns the right edge it needs
		protected float DrawLabel( float x, float y, float maxW, float a )
		{
			float u = U; var ev = Event.current;
			var st = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.MiddleLeft );
			float tw = Mathf.Min( st.CalcSize( new GUIContent( label ) ).x, Mathf.Max( 10 * u, maxW - 24 * u ) );
			var tr = new Rect( x, y, tw, 20 * u );
			var rb = new Rect( x + tw + 6 * u, y + u, 18 * u, 18 * u );
			bool resetHot = modT > 0.5f && ui.Hot( rb );
			if ( ev.type == EventType.Repaint )
			{
				st.normal.textColor = ( modT > 0.5f ? UIKit.INK : UIKit.INK2 ).WithAlpha( a );
				st.clipping = TextClipping.Clip;
				GUI.Label( tr, label, st );
				st.clipping = TextClipping.Overflow;
				if ( tip != null )
				{
					var dot = new Color( 200 / 255f, 222 / 255f, 232 / 255f, 0.35f * a );
					for ( float dx = 0; dx < tw; dx += 3 * u ) UIKit.Rounded( new Rect( x + dx, tr.yMax - 3 * u, Mathf.Max( 1, u ), Mathf.Max( 1, u ) ), dot, 0 );
				}

				if ( modT > 0.01f )
				{
					if ( resetHot ) UIKit.Rounded( rb, UIKit.SUN.WithAlpha( 0.16f * a ), 5 * u );
					IconKit.DrawAt( rb.center.x, rb.center.y, 11 * u * ( 0.6f + 0.4f * modT ), "reset", UIKit.SUN.WithAlpha( ( resetHot ? 1f : 0.85f ) * modT * a ) );
				}
			}

			if ( ev.type == EventType.MouseDown && ev.button == 0 )
			{
				if ( resetHot ) { Reset(); ev.Use(); }
				else if ( ev.clickCount == 2 && ui.Hot( tr ) ) { Reset(); ev.Use(); }
			}

			if ( resetHot ) ui.Tip( "Reset to default", null, rb );
			else if ( tip != null && ui.Hot( tr ) ) ui.Tip( tip, "Double-click to reset", tr );
			return rb.xMax;
		}

		protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	}

	// ---- slider
	public sealed class SSlider : Ctl
	{
		readonly Func<double> get; readonly Action<double> set;
		readonly double min, max, step; readonly bool log; readonly string unit; readonly Func<double, string> format;
		readonly int decimals; readonly double def, t0;
		bool dragging; float baseT, baseX, dragT; bool fine; float grow = 1; bool hotNow;

		public SSlider( string label, Func<double> get, Action<double> set, double min, double max, double step, string unit, bool log, Func<double, string> format, string tip )
		{
			this.label = label; this.get = get; this.set = set; this.tip = tip;
			if ( max < min ) { var t = min; min = max; max = t; }
			this.min = min; this.max = max; this.step = step > 0 ? step : 0; this.log = log && min > 0; this.unit = unit ?? ""; this.format = format;
			decimals = this.step > 0 ? DecimalsOf( this.step ) : AutoDecimals( max - min );
			def = get();
			t0 = ! this.log && min < 0 && max > 0 ? ToT( 0 ) : 0;
		}

		static int DecimalsOf( double step )
		{
			if ( ! ( step > 0 ) || double.IsInfinity( step ) ) return 0;
			string s = step.ToString( "R", Inv );
			int e = s.IndexOf( "E-", StringComparison.Ordinal );
			if ( e >= 0 ) return int.Parse( s.Substring( e + 2 ), Inv );
			int d = s.IndexOf( '.' );
			return d < 0 ? 0 : s.Length - d - 1;
		}

		static int AutoDecimals( double range ) => range <= 1 ? 3 : range <= 10 ? 2 : range <= 100 ? 1 : 0;
		static bool Differs( double a, double b ) => Math.Abs( a - b ) > 1e-9 * Math.Max( 1, Math.Abs( b ) );
		static double Clamp( double v, double lo, double hi ) => Math.Max( lo, Math.Min( hi, v ) );

		public double ToT( double v )
		{
			if ( double.IsNaN( v ) || double.IsInfinity( v ) ) return 0;
			if ( log ) return Clamp( Math.Log( v / min ) / Math.Log( max / min ), 0, 1 );
			return max > min ? Clamp( ( v - min ) / ( max - min ), 0, 1 ) : 0;
		}

		double FromT( double t ) => Quantize( log ? min * Math.Pow( max / min, t ) : min + ( max - min ) * t );

		public double Quantize( double v )
		{
			v = Clamp( v, min, max );
			if ( step > 0 )
			{
				double bs = log ? 0 : min;
				v = bs + Math.Floor( ( v - bs ) / step + 0.5 ) * step; // (JS Math.round: halves go up)
				v = Clamp( Math.Round( v, Math.Min( 12, decimals + 3 ) ), min, max );
			}
			else if ( log ) v = double.Parse( v.ToString( "G6", Inv ), Inv );
			return v;
		}

		string Num( double v )
		{
			int d = decimals;
			if ( log && step <= 0 ) { double a = Math.Abs( v ); d = a < 0.1 ? 4 : a < 1 ? 3 : a < 10 ? 2 : a < 100 ? 1 : 0; }
			string s = v.ToString( "F" + d, Inv );
			if ( Regex.IsMatch( s, @"^-0(\.0*)?$" ) ) s = s.Substring( 1 );
			return s.Replace( "-", "−" );
		}

		public override bool Modified => Differs( get(), def );
		public override void Reset() { if ( ! Modified ) return; set( def ); Flash(); }
		public override void Tick( float dt )
		{
			base.Tick( dt );
			grow = UIKit.Approach( grow, dragging ? 1.18f : hotNow ? 1.1f : 1, 0.14f, dt * 0.18f ); // (a 0.18 swing in 140 ms)
		}

		void SetFromT( double t ) { double v = FromT( t ); if ( Differs( v, get() ) ) set( v ); }

		public override float Height( float w ) => ( 6 + 20 + 2 + 20 + 6 ) * U;

		public void Commit( string text )
		{
			if ( double.TryParse( text.Replace( ',', '.' ).Replace( '−', '-' ), NumberStyles.Float, Inv, out double n ) && ! double.IsInfinity( n ) )
			{
				double q = Quantize( n );
				if ( Differs( q, get() ) ) set( q );
			}
		}

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			float x = r.x, w = r.width, y = r.y + 6 * u;
			double v = get();
			DrawFlash( r, a );

			// the readout: number and unit, or the format's text
			string num = format != null ? SafeFormat( v ) : Num( v );
			string un = format != null ? null : unit;
			var vs = UIKit.Style( UIFonts.Mono, 11.5f, TextAnchor.MiddleRight );
			var us = UIKit.Style( UIFonts.Mono, 11.5f * 0.92f, TextAnchor.MiddleLeft );
			float nw = vs.CalcSize( new GUIContent( num ) ).x;
			float gap = string.IsNullOrEmpty( un ) ? 0 : ( Regex.IsMatch( un, "^(°|%|×|x)$" ) ? 0.05f : 0.35f ) * 11.5f * u;
			float uw = string.IsNullOrEmpty( un ) ? 0 : us.CalcSize( new GUIContent( un ) ).x;
			float vw = nw + gap + uw + 12 * u, vr = x + w + 6 * u;
			var vbox = new Rect( vr - vw, y + 1 * u, vw, 18 * u );
			bool editing = ui.edit != null && ui.edit.owner == this;
			DrawLabel( x, y, vbox.x - 8 * u - x, a );

			if ( editing )
			{
				var ib = new Rect( vr - 88 * u, y, 88 * u, 20 * u );
				ui.DrawEdit( ib, a );
			}
			else
			{
				bool vhot = ui.Hot( vbox );
				if ( ev.type == EventType.Repaint )
				{
					if ( vhot ) UIKit.Rounded( vbox, UIKit.FILL2.WithAlpha( a ), 5 * u );
					vs.normal.textColor = UIKit.INK.WithAlpha( a );
					GUI.Label( new Rect( vbox.x + 6 * u, vbox.y, nw + 2, vbox.height ), num, vs );
					if ( uw > 0 ) { us.normal.textColor = UIKit.INK3.WithAlpha( a ); GUI.Label( new Rect( vbox.x + 6 * u + nw + gap, vbox.y, uw + 2, vbox.height ), un, us ); }
				}

				if ( vhot ) ui.Tip( "Click to type a value", null, vbox );
				if ( ev.type == EventType.MouseDown && ev.button == 0 && vhot && enabled )
				{
					ui.BeginEdit( this, v.ToString( "F" + Math.Max( decimals, 4 ), Inv ).TrimEnd( '0' ).TrimEnd( '.' ), Commit );
					ev.Use();
				}
			}

			// the track
			float ty = y + 20 * u + 2 * u, cy = ty + 10 * u;
			var track = new Rect( x, ty, w, 20 * u );
			float railX = x + 7 * u, railW = Mathf.Max( 1, w - 14 * u );
			double t = ToT( v );
			float tx = railX + ( float ) t * railW;
			bool onTrack = ui.Hot( track ) && ! editing;
			hotNow = onTrack || dragging;
			if ( ev.type == EventType.Repaint )
			{
				var rail = new Rect( railX, cy - 2 * u, railW, 4 * u );
				UIKit.Rounded( rail, new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.14f * a ), 2 * u );
				float fa = ( float ) Math.Min( t, t0 ), fb = ( float ) Math.Max( t, t0 );
				if ( fb > fa )
				{
					var fill = new Rect( railX + fa * railW, rail.y, ( fb - fa ) * railW, rail.height );
					UIKit.Rounded( new Rect( fill.x - 2 * u, fill.y - 3 * u, fill.width + 4 * u, fill.height + 6 * u ), UIKit.AQUA.WithAlpha( 0.1f * a ), 5 * u );
					UIKit.RampPill( fill, UIKit.AQUA.WithAlpha( 0.5f ), UIKit.AQUA, a );
				}

				if ( modT > 0.01f )
				{
					float mx = railX + ( float ) ToT( def ) * railW;
					UIKit.Rounded( new Rect( mx - 1, rail.y - 4 * u, 2, rail.height + 8 * u ), UIKit.SUN.WithAlpha( 0.7f * modT * a ), 1 );
				}

				float tr = 7 * u * grow;
				if ( dragging ) { Dot( tx, cy, tr + 9 * u, UIKit.AQUA.WithAlpha( 0.1f * a ) ); Dot( tx, cy, tr + 6 * u, UIKit.AQUA.WithAlpha( 0.2f * a ) ); }
				else if ( onTrack ) Dot( tx, cy, tr + 4 * u, UIKit.AQUA.WithAlpha( 0.18f * a ) );
				Dot( tx, cy + u, tr + 0.5f, new Color( 0, 0, 0, 0.35f * a ) );
				Dot( tx, cy, tr, new Color( 244 / 255f, 251 / 255f, 252 / 255f, a ) );
			}

			if ( ev.type == EventType.MouseDown && ev.button == 0 && onTrack && enabled && ui.active == null )
			{
				bool onThumb = Mathf.Abs( ev.mousePosition.x - tx ) <= 7 * u && Mathf.Abs( ev.mousePosition.y - cy ) <= 7 * u;
				float t1 = onThumb ? ( float ) t : Mathf.Clamp01( ( ev.mousePosition.x - railX ) / railW );
				fine = ev.shift; baseT = t1; baseX = ev.mousePosition.x; dragT = t1;
				dragging = true; ui.active = this;
				if ( ! onThumb ) SetFromT( t1 );
				ev.Use();
			}
			else if ( dragging && ev.type == EventType.MouseDrag )
			{
				bool f = ev.shift;
				if ( f != fine ) { baseT = dragT; baseX = ev.mousePosition.x; fine = f; }
				float t1 = Mathf.Clamp01( baseT + ( ev.mousePosition.x - baseX ) / railW * ( fine ? 0.1f : 1f ) );
				dragT = t1;
				SetFromT( t1 );
				ev.Use();
			}
			else if ( dragging && ( ev.type == EventType.MouseUp ) ) { dragging = false; if ( ui.active == this ) ui.active = null; ev.Use(); }
			if ( dragging && ui.active != this ) dragging = false;
		}

		string SafeFormat( double v ) { try { return format( v ); } catch ( Exception ) { return Num( v ); } }

		static void Dot( float cx, float cy, float r, Color c ) => UIKit.Rounded( new Rect( cx - r, cy - r, 2 * r, 2 * r ), c, r );
	}

	// ---- toggle
	public sealed class SToggle : Ctl
	{
		readonly Func<bool> get; readonly Action<bool> set; readonly bool def;
		float onT;

		public SToggle( string label, Func<bool> get, Action<bool> set, string tip ) { this.label = label; this.get = get; this.set = set; this.tip = tip; def = get(); onT = def ? 1 : 0; }
		public override bool Modified => get() != def;
		public override void Reset() { if ( ! Modified ) return; set( def ); Flash(); }
		public override void Tick( float dt ) { base.Tick( dt ); onT = UIKit.Approach( onT, get() ? 1 : 0, 0.24f, dt ); }
		public override float Height( float w ) => 36 * U;

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			DrawFlash( r, a );
			float cy = r.center.y;
			var sw = new Rect( r.xMax - 34 * u, cy - 10 * u, 34 * u, 20 * u );
			DrawLabel( r.x, cy - 10 * u, sw.x - 8 * u - r.x, a );
			bool hot = ui.Hot( r ) && enabled;
			if ( ev.type == EventType.Repaint )
			{
				float e = UIScale.Ease( onT );
				var off = new Color( 170 / 255f, 215 / 255f, 235 / 255f, hot ? 0.22f : 0.16f );
				var on = UIKit.AQUA; on.a = 0.92f;
				if ( e > 0.01f ) UIKit.Rounded( new Rect( sw.x - 2 * u, sw.y - 2 * u, sw.width + 4 * u, sw.height + 4 * u ), UIKit.AQUA.WithAlpha( 0.18f * e * a ), 12 * u );
				UIKit.Rounded( sw, Color.Lerp( off, on, e ).WithAlpha( a ), 10 * u );
				UIKit.Rounded( sw, Color.Lerp( new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.12f ), new Color( 170 / 255f, 1f, 245 / 255f, 0.6f ), e ).WithAlpha( a ), 10 * u, 1 );
				float kx = sw.x + 3 * u + e * 14 * u;
				UIKit.Rounded( new Rect( kx, sw.y + 4 * u, 14 * u, 14 * u ), new Color( 0, 0, 0, 0.3f * a ), 7 * u );
				UIKit.Rounded( new Rect( kx, sw.y + 3 * u, 14 * u, 14 * u ), Color.Lerp( new Color( 217 / 255f, 230 / 255f, 234 / 255f ), Color.white, e ).WithAlpha( a ), 7 * u );
			}

			// the whole row toggles; the second click of a double click is the reset gesture
			if ( ev.type == EventType.MouseDown && ev.button == 0 && hot && ev.clickCount <= 1 ) { set( ! get() ); ev.Use(); }
		}
	}

	// ---- select: segmented up to 4 options, a dropdown beyond
	public sealed class SSelect : Ctl
	{
		readonly string[] options; readonly Func<int> get; readonly Action<int> set; readonly int def;
		float ind;
		bool Seg => options.Length <= 4;

		public SSelect( string label, string[] options, Func<int> get, Action<int> set, string tip ) { this.label = label; this.options = options; this.get = get; this.set = set; this.tip = tip; def = get(); ind = Mathf.Max( 0, def ); }
		public override bool Modified => get() != def;
		public override void Reset() { if ( ! Modified ) return; set( def ); Flash(); }
		public override void Tick( float dt ) { base.Tick( dt ); ind = Mathf.MoveTowards( ind, Mathf.Max( 0, get() ), dt / 0.24f * Mathf.Max( 1, Mathf.Abs( get() - ind ) ) ); }
		public override float Height( float w ) => Seg ? ( 6 + 20 + 6 + 34 + 6 ) * U : 40 * U;

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			DrawFlash( r, a );
			int idx = get();
			if ( Seg )
			{
				float y = r.y + 6 * u;
				DrawLabel( r.x, y, r.width, a );
				var seg = new Rect( r.x, y + 20 * u + 6 * u, r.width, 34 * u );
				float cell = ( seg.width - 6 * u ) / options.Length;
				if ( ev.type == EventType.Repaint )
				{
					UIKit.Rounded( seg, new Color( 0, 0, 0, 0.24f * a ), 9 * u );
					UIKit.Rounded( seg, UIKit.LINE.WithAlpha( a ), 9 * u, 1 );
					if ( idx >= 0 )
					{
						var ir = new Rect( seg.x + 3 * u + ind * cell, seg.y + 3 * u, cell, 28 * u );
						UIKit.VRampPill( ir, UIKit.AQUA.WithAlpha( 0.22f ), UIKit.AQUA.WithAlpha( 0.1f ), 7 * u, a );
						UIKit.Rounded( ir, UIKit.AQUA.WithAlpha( 0.45f * a ), 7 * u, 1 );
					}
				}

				for ( int i = 0; i < options.Length; i++ )
				{
					var b = new Rect( seg.x + 3 * u + i * cell, seg.y + 3 * u, cell, 28 * u );
					bool hot = ui.Hot( b ) && enabled;
					if ( ev.type == EventType.Repaint )
					{
						var st = UIKit.Style( UIFonts.InterMedium, 11.5f, TextAnchor.MiddleCenter );
						st.normal.textColor = ( hot || i == idx ? UIKit.INK : UIKit.INK3 ).WithAlpha( a );
						st.clipping = TextClipping.Clip;
						GUI.Label( b, options[ i ], st );
						st.clipping = TextClipping.Overflow;
					}

					if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { if ( i != idx ) set( i ); ev.Use(); }
				}
			}
			else
			{
				float cy = r.center.y;
				var st = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.MiddleLeft );
				float lw = st.CalcSize( new GUIContent( label ) ).x + 24 * u + 6 * u;
				float ddW = Mathf.Clamp( r.width - lw - 12 * u, r.width * 0.44f, r.width * 0.62f );
				var dd = new Rect( r.xMax - ddW, cy - 15 * u, ddW, 30 * u );
				DrawLabel( r.x, cy - 10 * u, dd.x - 12 * u - r.x, a );
				bool openMe = ui.menu != null && ui.menu.owner == this;
				bool hot = ui.Hot( dd ) && enabled;
				if ( ev.type == EventType.Repaint )
				{
					UIKit.Rounded( dd, ( hot ? UIKit.FILL2 : UIKit.FILL ).WithAlpha( a ), 8 * u );
					UIKit.Rounded( dd, ( openMe ? UIKit.AQUA.WithAlpha( 0.55f ) : hot ? UIKit.LINE2 : UIKit.LINE ).WithAlpha( a ), 8 * u, 1 );
					var ts = UIKit.Style( UIFonts.InterMedium, 11.5f, TextAnchor.MiddleLeft );
					ts.normal.textColor = UIKit.INK.WithAlpha( a ); ts.clipping = TextClipping.Clip;
					GUI.Label( new Rect( dd.x + 10 * u, dd.y, dd.width - 10 * u - 8 * u - 14 * u - 6 * u, dd.height ), idx >= 0 ? options[ idx ] : "—", ts );
					ts.clipping = TextClipping.Overflow;
					var cc = new Vector2( dd.xMax - 8 * u - 7 * u, dd.center.y );
					var old = GUI.matrix;
					if ( openMe ) GUIUtility.RotateAroundPivot( 180, cc );
					IconKit.DrawAt( cc.x, cc.y, 14 * u, "chevron-down", UIKit.INK3.WithAlpha( a ) );
					GUI.matrix = old;
				}

				if ( ev.type == EventType.MouseDown && ev.button == 0 && hot )
				{
					if ( openMe ) ui.CloseMenu();
					else ui.OpenMenu( this, dd, options, idx, i => { if ( i != get() ) set( i ); } );
					ev.Use();
				}
			}
		}
	}

	// ---- button
	public sealed class SButton : Ctl
	{
		readonly Action onClick; readonly string icon, variant;
		public SButton( string label, Action onClick, string icon, string variant, string tip ) { this.label = label; this.onClick = onClick; this.icon = icon; this.variant = variant; this.tip = tip; }
		public override bool Modified => false;
		public override float Height( float w ) => 44 * U;

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			var b = new Rect( r.x, r.y + 5 * u, r.width, 34 * u );
			bool hot = ui.Hot( b ) && enabled;
			if ( ev.type == EventType.Repaint )
			{
				Color ink;
				if ( variant == "primary" )
				{
					UIKit.Rounded( new Rect( b.x - 1, b.y - 1, b.width + 2, b.height + 2 ), UIKit.AQUA.WithAlpha( 0.6f * a ), 10 * u );
					UIKit.VRampPill( b, hot ? new Color( 162 / 255f, 247 / 255f, 236 / 255f ) : new Color( 138 / 255f, 241 / 255f, 229 / 255f ), hot ? new Color( 95 / 255f, 227 / 255f, 212 / 255f ) : new Color( 79 / 255f, 214 / 255f, 199 / 255f ), 9 * u, a );
					ink = new Color( 3 / 255f, 32 / 255f, 28 / 255f );
				}
				else if ( variant == "ghost" )
				{
					if ( hot ) UIKit.Rounded( b, UIKit.FILL.WithAlpha( a ), 9 * u );
					UIKit.Rounded( b, UIKit.LINE2.WithAlpha( a ), 9 * u, 1 );
					ink = hot ? UIKit.INK : UIKit.INK2;
				}
				else
				{
					UIKit.Rounded( b, ( hot ? new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.18f ) : UIKit.FILL2 ).WithAlpha( a ), 9 * u );
					UIKit.Rounded( b, UIKit.LINE2.WithAlpha( a ), 9 * u, 1 );
					ink = UIKit.INK;
				}

				var st = UIKit.Style( UIFonts.InterSemi, 12.5f, TextAnchor.MiddleLeft );
				float tw = st.CalcSize( new GUIContent( label ) ).x, iw = icon != null ? 16 * u + 8 * u : 0;
				float x0 = b.center.x - ( tw + iw ) / 2;
				if ( icon != null ) IconKit.DrawAt( x0 + 8 * u, b.center.y, 16 * u, icon, ink.WithAlpha( a ) );
				st.normal.textColor = ink.WithAlpha( a );
				GUI.Label( new Rect( x0 + iw, b.y, tw + 2, b.height ), label, st );
			}

			if ( tip != null && hot ) ui.Tip( tip, null, b );
			if ( ev.type == EventType.MouseDown && ev.button == 0 && hot ) { ev.Use(); try { onClick?.Invoke(); } catch ( Exception e ) { Debug.LogException( e ); } }
		}
	}

	// ---- presets: a row of chips; picking one runs its apply
	public sealed class SPresets : Ctl
	{
		readonly string[] labels, icons; readonly Action<int> apply, after;
		public int active;
		public SPresets( string label, string[] labels, string[] icons, Action<int> apply, int active, Action<int> after ) { this.label = label; this.labels = labels; this.icons = icons; this.apply = apply; this.active = active; this.after = after; }
		public override bool Modified => false;
		public override float Height( float w ) => ( 6 + ( label != null ? 20 + 6 : 6 ) + 58 + 6 ) * U;

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			float y = r.y + 6 * u;
			if ( label != null )
			{
				if ( ev.type == EventType.Repaint ) { var st = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.MiddleLeft ); st.normal.textColor = UIKit.INK2.WithAlpha( a ); GUI.Label( new Rect( r.x, y, r.width, 20 * u ), label, st ); }
				y += 20 * u;
			}

			y += 6 * u;
			int n = Mathf.Clamp( labels.Length, 1, 4 );
			float cw = ( r.width - ( n - 1 ) * 6 * u ) / n;
			for ( int i = 0; i < labels.Length; i++ )
			{
				var c = new Rect( r.x + ( i % n ) * ( cw + 6 * u ), y + ( i / n ) * 64 * u, cw, 58 * u );
				bool hot = ui.Hot( c ) && enabled, on = i == active;
				if ( ev.type == EventType.Repaint )
				{
					if ( on )
					{
						UIKit.Rounded( new Rect( c.x - 3 * u, c.y - 3 * u, c.width + 6 * u, c.height + 6 * u ), UIKit.AQUA.WithAlpha( 0.1f * a ), 13 * u );
						UIKit.VRampPill( c, UIKit.AQUA.WithAlpha( 0.18f ), UIKit.AQUA.WithAlpha( 0.06f ), 10 * u, a );
						UIKit.Rounded( c, UIKit.AQUA.WithAlpha( 0.5f * a ), 10 * u, 1 );
					}
					else
					{
						UIKit.Rounded( c, ( hot ? UIKit.FILL2 : UIKit.FILL ).WithAlpha( a ), 10 * u );
						UIKit.Rounded( c, UIKit.LINE.WithAlpha( a ), 10 * u, 1 );
					}

					float top = c.y + ( c.height - ( 20 + 5 + 10.5f * 1.2f ) * u ) / 2;
					var ink = on ? UIKit.INK : hot ? UIKit.INK : UIKit.INK3;
					if ( icons != null && i < icons.Length && icons[ i ] != null ) IconKit.DrawAt( c.center.x, top + 10 * u, 20 * u, icons[ i ], ( on ? UIKit.AQUA : ink ).WithAlpha( a ) );
					var st = UIKit.Style( UIFonts.InterMedium, 10.5f, TextAnchor.UpperCenter );
					st.normal.textColor = ink.WithAlpha( a ); st.clipping = TextClipping.Clip;
					GUI.Label( new Rect( c.x + 4 * u, top + 25 * u, c.width - 8 * u, 14 * u ), labels[ i ], st );
					st.clipping = TextClipping.Overflow;
				}

				if ( ev.type == EventType.MouseDown && ev.button == 0 && hot )
				{
					active = i;
					try { apply( i ); after?.Invoke( i ); } catch ( Exception e ) { Debug.LogException( e ); }
					ev.Use();
				}
			}
		}
	}

	// ---- info: a live read-only value
	public sealed class SInfo : Ctl
	{
		readonly Func<string> get; string text = "—"; float age = 10;
		public SInfo( string label, Func<string> get, string tip ) { this.label = label; this.get = get; this.tip = tip; }
		public override bool Modified => false;
		public override float Height( float w ) => 27 * U;
		public override void Tick( float dt )
		{
			age += dt;
			if ( age < 0.25f ) return; // ~4 Hz, as the JS
			age = 0;
			try { var v = get(); text = string.IsNullOrEmpty( v ) ? "—" : v; } catch ( Exception ) { text = "—"; }
		}

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			int i = parent.children.IndexOf( this );
			if ( i > 0 && parent.children[ i - 1 ] is SInfo && ev.type == EventType.Repaint )
				for ( float dx = 0; dx < r.width; dx += 4 * u ) UIKit.Rounded( new Rect( r.x + dx, r.y, 2 * u, 1 ), UIKit.LINE.WithAlpha( a ), 0 );
			var vs = UIKit.Style( UIFonts.Mono, 11.5f, TextAnchor.MiddleRight );
			float vw = Mathf.Min( vs.CalcSize( new GUIContent( text ) ).x, r.width * 0.7f );
			var row = new Rect( r.x, r.y + 6 * u, r.width, 15 * u );
			DrawLabel( r.x, r.y + 3 * u, r.width - vw - 12 * u, a, false );
			if ( ev.type == EventType.Repaint ) { vs.normal.textColor = UIKit.INK.WithAlpha( a ); vs.clipping = TextClipping.Clip; GUI.Label( new Rect( r.xMax - vw - 2, row.y - 2 * u, vw + 2, 19 * u ), text, vs ); vs.clipping = TextClipping.Overflow; }
		}

		// (an info row has no reset affordance)
		void DrawLabel( float x, float y, float maxW, float a, bool reset )
		{
			float u = U; var ev = Event.current;
			var st = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.MiddleLeft );
			float tw = Mathf.Min( st.CalcSize( new GUIContent( label ) ).x, maxW );
			var tr = new Rect( x, y, tw, 20 * u );
			if ( ev.type == EventType.Repaint ) { st.normal.textColor = UIKit.INK2.WithAlpha( a ); st.clipping = TextClipping.Clip; GUI.Label( tr, label, st ); st.clipping = TextClipping.Overflow; }
			if ( tip != null && ui.Hot( tr ) ) ui.Tip( tip, null, tr );
		}
	}

	// ---- time of day: the sun-path dial and the 24 h strip (UI.js TimeOfDayControl)
	public sealed class STimeOfDay : Ctl
	{
		readonly Func<double> get; readonly Action<double> set; readonly double def;
		bool dragDial, dragTrack;
		static Texture2D skyTex, seaTex, trackTex, glowTex, hazeTex, moonTex, glintTex;
		static float lastHour = -1;

		// the dial's own units (the JS svg viewBox)
		const float TW = 300, TH = 148, CX = 150, CY = 100, RX = 118, UP = 74, DOWN = 32;

		// [ hour, zenith, horizon ]
		static readonly (double h, string z, string hz)[] SKY_KEYS =
		{
			( 0, "#040915", "#0b1730" ), ( 4.4, "#060d22", "#18244a" ), ( 5.3, "#18264f", "#b0616a" ), ( 6.1, "#35609e", "#f2a86e" ), ( 7.6, "#3d7cc2", "#a8d2ec" ), ( 12, "#2c74c6", "#c2e4f6" ),
			( 16.3, "#3778be", "#b6daee" ), ( 17.4, "#3a5d9c", "#f1a25f" ), ( 18.1, "#262f5d", "#dc6a4e" ), ( 18.9, "#101a3c", "#473358" ), ( 19.8, "#050b1a", "#0f1b36" ), ( 24, "#040915", "#0b1730" ),
		};

		static readonly (double end, string name)[] PHASES =
		{
			( 4.8, "Night" ), ( 5.6, "Dawn" ), ( 6.4, "Sunrise" ), ( 7.2, "Golden hour" ), ( 10.5, "Morning" ), ( 13.5, "Midday" ), ( 16.8, "Afternoon" ), ( 17.6, "Golden hour" ), ( 18.4, "Sunset" ), ( 19.3, "Dusk" ), ( 24, "Night" ),
		};

		static Color Hex( string s ) { ColorUtility.TryParseHtmlString( s, out var c ); return c; }

		static (Color z, Color hz) SkyAt( double hour )
		{
			double hh = ( ( hour % 24 ) + 24 ) % 24;
			for ( int i = 0; i < SKY_KEYS.Length - 1; i++ )
			{
				var a = SKY_KEYS[ i ]; var b = SKY_KEYS[ i + 1 ];
				if ( hh >= a.h && hh <= b.h )
				{
					double t = ( hh - a.h ) / ( ( b.h - a.h ) != 0 ? b.h - a.h : 1 );
					float s = ( float ) ( t * t * ( 3 - 2 * t ) );
					return ( Color.Lerp( Hex( a.z ), Hex( b.z ), s ), Color.Lerp( Hex( a.hz ), Hex( b.hz ), s ) );
				}
			}

			return ( Hex( SKY_KEYS[ 0 ].z ), Hex( SKY_KEYS[ 0 ].hz ) );
		}

		public static string PhaseAt( double hh ) { foreach ( var p in PHASES ) if ( hh < p.end ) return p.name; return "Night"; }

		public static string FmtClock( double hours )
		{
			int m = ( int ) Math.Round( ( ( ( hours % 24 ) + 24 ) % 24 ) * 60 ) % 1440;
			return $"{m / 60:D2}:{m % 60:D2}";
		}

		static (float x, float y, float elev) Point( double hours )
		{
			double th = Math.PI - ( ( hours - 6 ) / 12 ) * Math.PI, s = Math.Sin( th );
			return ( ( float ) ( CX + RX * Math.Cos( th ) ), ( float ) ( CY - ( s >= 0 ? UP : DOWN ) * s ), ( float ) s );
		}

		static double HoursOf( float x, float y )
		{
			double nx = ( x - CX ) / RX, dy = CY - y, ny = dy >= 0 ? dy / UP : dy / DOWN, th = Math.Atan2( ny, nx );
			return ( ( ( 6 + ( Math.PI - th ) / Math.PI * 12 ) % 24 ) + 24 ) % 24;
		}

		public STimeOfDay( Func<double> get, Action<double> set ) { label = "Time of day"; this.get = get; this.set = set; def = get(); }
		public override bool Modified => Math.Abs( get() - def ) > 1e-9;
		public override void Reset() { if ( ! Modified ) return; set( def ); Flash(); }
		public override float Height( float w ) => ( 6 + 20 + 8 ) * U + w * TH / TW + ( 10 + 24 + 6 ) * U;

		void Set( double h ) { if ( Math.Abs( h - get() ) > 1e-9 ) set( h ); }

		static Texture2D Make( int w, int h ) => new Texture2D( w, h, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

		static void EnsureTextures()
		{
			if ( skyTex == null ) { skyTex = Make( 1, 32 ); lastHour = -1; }
			if ( seaTex == null ) { seaTex = Make( 1, 32 ); lastHour = -1; }
			if ( trackTex == null )
			{
				trackTex = Make( 256, 1 );
				for ( int x = 0; x < 256; x++ ) { var (z, hz) = SkyAt( x / 255.0 * 24 ); trackTex.SetPixel( x, 0, Color.Lerp( z, hz, 0.55f ) ); }
				trackTex.Apply();
			}

			// radial gradients: the sun's glow, the horizon haze, the glint (a soft ellipse): white-tinted by the stops of the JS svg
			if ( glowTex == null )
			{
				glowTex = Make( 64, 64 ); hazeTex = Make( 64, 64 ); glintTex = Make( 32, 64 );
				for ( int y = 0; y < 64; y++ ) for ( int x = 0; x < 64; x++ )
				{
					float d = Mathf.Clamp01( Mathf.Sqrt( ( x - 31.5f ) * ( x - 31.5f ) + ( y - 31.5f ) * ( y - 31.5f ) ) / 32f );
					Color g = d < 0.28f ? Color.Lerp( new Color( 1, 0.945f, 0.839f, 0.95f ), new Color( 1, 0.788f, 0.541f, 0.5f ), d / 0.28f ) : Color.Lerp( new Color( 1, 0.788f, 0.541f, 0.5f ), new Color( 1, 0.722f, 0.42f, 0 ), ( d - 0.28f ) / 0.72f );
					glowTex.SetPixel( x, y, g );
					hazeTex.SetPixel( x, y, new Color( 1, 0.69f + 0.0f * d, 0.44f, Mathf.Lerp( 0.8f, 0, d ) ) );
				}

				for ( int y = 0; y < 64; y++ ) for ( int x = 0; x < 32; x++ )
				{
					float d = Mathf.Clamp01( Mathf.Abs( ( x - 15.5f ) / 16f ) );
					glintTex.SetPixel( x, y, new Color( 1, 0.863f, 0.682f, Mathf.Lerp( 0.9f, 0, y / 63f ) * ( 1 - d * d ) ) );
				}

				glowTex.Apply(); hazeTex.Apply(); glintTex.Apply();
			}

			if ( moonTex == null )
			{
				moonTex = Make( 64, 64 );
				for ( int y = 0; y < 64; y++ ) for ( int x = 0; x < 64; x++ )
				{
					// the crescent of the JS mask: a disc of r 5.4 minus a disc of r 4.5 offset by (2.7, -1.9); 64 px for 14 units
					float px = ( x - 31.5f ) / 64f * 14f, py = ( y - 31.5f ) / 64f * 14f; // (y up in texture space: flip below)
					float d1 = Mathf.Sqrt( px * px + py * py ) - 5.4f, d2 = Mathf.Sqrt( ( px - 2.7f ) * ( px - 2.7f ) + ( py + 1.9f ) * ( py + 1.9f ) ) - 4.5f;
					float cov = Mathf.Clamp01( 0.5f - d1 * 4f ) * Mathf.Clamp01( 0.5f + d2 * 4f );
					moonTex.SetPixel( x, 63 - y, new Color( 227 / 255f, 236 / 255f, 1, cov ) );
				}

				moonTex.Apply();
			}
		}

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			EnsureTextures();
			double hrs = ( ( get() % 24 ) + 24 ) % 24;
			float y = r.y + 6 * u;
			DrawFlash( r, a );

			// the clock readout
			string clock = FmtClock( hrs );
			var vs = UIKit.Style( UIFonts.Mono, 14f, TextAnchor.MiddleRight );
			float vw = vs.CalcSize( new GUIContent( clock ) ).x + 12 * u, vr = r.xMax + 6 * u;
			var vbox = new Rect( vr - vw, y, vw, 20 * u );
			DrawLabel( r.x, y, vbox.x - 8 * u - r.x, a );
			bool editing = ui.edit != null && ui.edit.owner == this;
			if ( editing ) ui.DrawEdit( new Rect( vr - 88 * u, y, 88 * u, 20 * u ), a );
			else
			{
				bool vhot = ui.Hot( vbox );
				if ( ev.type == EventType.Repaint )
				{
					if ( vhot ) UIKit.Rounded( vbox, UIKit.FILL2.WithAlpha( a ), 5 * u );
					vs.normal.textColor = UIKit.SUN.WithAlpha( a );
					GUI.Label( new Rect( vbox.x + 6 * u, vbox.y, vbox.width - 12 * u + 2, vbox.height ), clock, vs );
				}

				if ( vhot ) ui.Tip( "Click to type a time", null, vbox );
				if ( ev.type == EventType.MouseDown && ev.button == 0 && vhot ) { ui.BeginEdit( this, clock, CommitTime ); ev.Use(); }
			}

			// the dial
			float dh = r.width * TH / TW;
			var dial = new Rect( r.x, y + 20 * u + 8 * u, r.width, dh );
			float s = r.width / TW;
			var sun = Point( hrs );
			float e = sun.elev;
			if ( ev.type == EventType.Repaint )
			{
				var (zen, hor) = SkyAt( hrs );
				var sea0 = Color.Lerp( hor, Hex( "#04121c" ), 0.72f );
				if ( Mathf.Abs( ( float ) hrs - lastHour ) > 1e-6f ) { lastHour = ( float ) hrs; FillRamp( skyTex, zen, hor ); FillRamp( seaTex, sea0, Hex( "#03101a" ) ); }
				// the sky's gradient runs over its svg rect (to the horizon line); the sea's fixed pair is dark enough that its own start colour is all that moves
				float rad = 10 * u, hy = ( CY + 1 ) * s;
				DrawTex( new Rect( dial.x, dial.y, dial.width, hy ), skyTex, new Vector4( rad, rad, 0, 0 ), a );
				float night = Mathf.Clamp01( ( 0.08f - e ) / 0.3f );
				if ( night > 0.01f ) DrawStars( dial, s, night * a );
				float low = e > -0.25f ? Mathf.Pow( 1 - Mathf.Min( 1, Mathf.Abs( e ) / 0.6f ), 2 ) : 0;
				if ( low > 0.01f ) { var hz = new Rect( dial.x + ( sun.x - 96 ) * s, dial.y + ( CY - 30 ) * s, 192 * s, 60 * s ); DrawClipped( hz, dial, hazeTex, low * 0.85f * a, new Rect( dial.x, dial.y, dial.width, hy ) ); }
				DrawTex( new Rect( dial.x, dial.y + hy, dial.width, dial.height - hy ), seaTex, new Vector4( 0, 0, rad, rad ), a );
				float gry = 6 + ( 1 - Mathf.Clamp01( e ) ) * 16;
				if ( e > 0 )
				{
					float ga = 0.85f * Mathf.Pow( 1 - e, 1.5f ) * a, grx = 2.5f + ( 1 - Mathf.Clamp01( e ) ) * 3;
					GUI.color = new Color( 1, 1, 1, ga );
					GUI.DrawTexture( new Rect( dial.x + ( sun.x - grx ) * s, dial.y + CY * s + 8 * 0.2f * s, grx * 2 * s, gry * 0.95f * 2 * s * 0.5f * 2 ), glintTex );
					GUI.color = Color.white;
				}

				UIKit.Rounded( new Rect( dial.x, dial.y + ( CY + 0.5f ) * s, dial.width, 1 ), new Color( 1, 1, 1, 0.3f * a ), 0 );
				Arc( dial, s, true, new Color( 1, 1, 1, 0.4f * a ) ); Arc( dial, s, false, new Color( 1, 1, 1, 0.2f * a ) );
				for ( int hr = 0; hr < 24; hr += 3 ) { var p = Point( hr ); float rr = ( hr % 6 != 0 ? 1.1f : 1.8f ) * s; Dot( dial.x + p.x * s, dial.y + p.y * s, rr, new Color( 1, 1, 1, 0.6f * a ) ); }
				var ls = UIKit.Style( UIFonts.Mono, 8.5f * s / u, TextAnchor.MiddleCenter ); ls.normal.textColor = new Color( 1, 1, 1, 0.62f * a );
				foreach ( var (t, lx, ly) in new[] { ( "06", CX - RX, CY + 13 ), ( "12", CX, CY - UP - 7 ), ( "18", CX + RX, CY + 13 ), ( "00", CX, CY + DOWN + 11 ) } )
					GUI.Label( new Rect( dial.x + ( lx - 14 ) * s, dial.y + ( ly - 6 ) * s, 28 * s, 12 * s ), t, ls );
				if ( night > 0.01f )
				{
					var m = Point( hrs + 12 );
					GUI.color = new Color( 1, 1, 1, night * 0.95f * a );
					GUI.DrawTexture( new Rect( dial.x + ( m.x - 7 ) * s, dial.y + ( m.y - 7 ) * s, 14 * s, 14 * s ), moonTex );
					GUI.color = Color.white;
				}

				float so = e < -0.02f ? 0.8f : 1;
				float gl = Mathf.Clamp( ( e + 0.12f ) / 0.25f, 0.2f, 1 );
				var sc = new Vector2( dial.x + sun.x * s, dial.y + sun.y * s );
				GUI.color = new Color( 1, 1, 1, gl * so * a );
				GUI.DrawTexture( new Rect( sc.x - 24 * s, sc.y - 24 * s, 48 * s, 48 * s ), glowTex );
				GUI.color = Color.white;
				bool shot = ui.Hot( dial ) || dragDial || dragTrack;
				float dr = ( shot ? 8.5f : 7 ) * s;
				Dot( sc.x, sc.y, dr + 1.5f * s, new Color( 1, 1, 1, 0.92f * so * a ) );
				Dot( sc.x, sc.y, dr, ( e < 0 ? Hex( "#ffd2a0" ) : Color.Lerp( Hex( "#ffe2b4" ), Hex( "#fff8ec" ), Mathf.Clamp01( e * 2.2f ) ) ).WithAlpha( so * a ) );
				var ps = UIKit.Style( UIFonts.InterSemi, 11f * s / u, TextAnchor.MiddleLeft );
				string phase = PhaseAt( hrs );
				ps.normal.textColor = new Color( 0, 20 / 255f, 40 / 255f, 0.28f * a );
				foreach ( var o in new[] { new Vector2( -1, 0 ), new Vector2( 1, 0 ), new Vector2( 0, -1 ), new Vector2( 0, 1 ) } ) GUI.Label( new Rect( dial.x + 14 * s + o.x, dial.y + 12 * s + o.y, 140 * s, 22 * s ), phase, ps );
				ps.normal.textColor = new Color( 1, 1, 1, 0.94f * a );
				GUI.Label( new Rect( dial.x + 14 * s, dial.y + 12 * s, 140 * s, 22 * s ), phase, ps );
				UIKit.Rounded( dial, UIKit.LINE.WithAlpha( a ), rad, 1 );
			}

			// the strip
			var track = new Rect( r.x + 6 * u, dial.yMax + 10 * u, r.width - 12 * u, 24 * u );
			var bar = new Rect( track.x, track.y + 2 * u, track.width, 6 * u );
			float kx = bar.x + ( float ) ( hrs / 24 ) * bar.width;
			bool thot = ui.Hot( track );
			if ( ev.type == EventType.Repaint )
			{
				float rr = bar.height / 2;
				GUI.DrawTexture( bar, trackTex, ScaleMode.StretchToFill, true, 0, new Color( 1, 1, 1, a ), Vector4.zero, new Vector4( rr, rr, rr, rr ) );
				UIKit.Rounded( bar, new Color( 1, 1, 1, 0.1f * a ), rr, 1 );
				float kr = 6 * u * ( thot || dragTrack ? 1.15f : 1 ), ky = track.y + 5 * u;
				Dot( kx, ky, kr + 2 * u, new Color( 10 / 255f, 14 / 255f, 20 / 255f, 0.85f * a ) );
				Dot( kx, ky, kr + 5 * u, UIKit.SUN.WithAlpha( 0.2f * a ) );
				Dot( kx, ky, kr, UIKit.SUN.WithAlpha( a ) );
				var sc2 = UIKit.Style( UIFonts.Mono, 9f, TextAnchor.UpperLeft ); sc2.normal.textColor = UIKit.INK4.WithAlpha( a );
				for ( int i = 0; i < 5; i++ )
				{
					string t = ( i * 6 ).ToString( "D2" ); float tw2 = sc2.CalcSize( new GUIContent( t ) ).x;
					float tx = bar.x + i / 4f * bar.width; tx = i == 0 ? tx : i == 4 ? tx - tw2 : tx - tw2 / 2;
					GUI.Label( new Rect( tx, track.yMax - 10 * u, tw2 + 2, 10 * u ), t, sc2 );
				}
			}

			// dragging the dial or the strip (Shift: whole quarter hours)
			if ( ev.type == EventType.MouseDown && ev.button == 0 && ui.active == null && ! editing )
			{
				if ( ui.Hot( dial ) ) { dragDial = true; ui.active = this; ev.Use(); Set( FromDial( dial, ev ) ); }
				else if ( thot ) { dragTrack = true; ui.active = this; ev.Use(); Set( FromTrack( bar, ev ) ); }
			}
			else if ( ( dragDial || dragTrack ) && ev.type == EventType.MouseDrag ) { Set( dragDial ? FromDial( dial, ev ) : FromTrack( bar, ev ) ); ev.Use(); }
			else if ( ( dragDial || dragTrack ) && ev.type == EventType.MouseUp ) { dragDial = dragTrack = false; if ( ui.active == this ) ui.active = null; ev.Use(); }
			if ( ( dragDial || dragTrack ) && ui.active != this ) dragDial = dragTrack = false;
		}

		static double Snap( double hh, Event e ) => e.shift ? Math.Round( hh * 4 ) / 4 : Math.Round( hh * 60 ) / 60;
		static double FromDial( Rect dial, Event e ) { double hh = Snap( HoursOf( ( e.mousePosition.x - dial.x ) / dial.width * TW, ( e.mousePosition.y - dial.y ) / dial.height * TH ), e ); return hh >= 24 ? 0 : hh; }
		static double FromTrack( Rect bar, Event e ) { double hh = Snap( Math.Max( 0, Math.Min( 1, ( e.mousePosition.x - bar.x ) / Math.Max( 1, bar.width ) ) ) * 24, e ); return Math.Min( hh, 24 - 1.0 / 60 ); }

		void CommitTime( string text )
		{
			var m = Regex.Match( text.Trim(), @"^(\d{1,2})\s*[:h]\s*(\d{1,2})$", RegexOptions.IgnoreCase );
			double hh = m.Success ? double.Parse( m.Groups[ 1 ].Value, Inv ) + double.Parse( m.Groups[ 2 ].Value, Inv ) / 60 : double.TryParse( text.Trim().Replace( ',', '.' ), NumberStyles.Float, Inv, out var d ) ? d : double.NaN;
			if ( ! double.IsNaN( hh ) && ! double.IsInfinity( hh ) ) Set( ( ( hh % 24 ) + 24 ) % 24 );
		}

		static void FillRamp( Texture2D t, Color top, Color bottom )
		{
			int n = t.height;
			for ( int y = 0; y < n; y++ ) t.SetPixel( 0, y, Color.Lerp( bottom, top, y / ( n - 1f ) ) ); // (row 0 is the bottom)
			t.Apply();
		}

		static void DrawTex( Rect r, Texture2D t, Vector4 radii, float a ) => GUI.DrawTexture( r, t, ScaleMode.StretchToFill, true, 0, new Color( 1, 1, 1, a ), Vector4.zero, radii );

		// a texture inside `clip` only (the haze ellipse above the horizon)
		static void DrawClipped( Rect r, Rect view, Texture2D t, float a, Rect clip )
		{
			GUI.BeginClip( clip );
			GUI.color = new Color( 1, 1, 1, a );
			GUI.DrawTexture( new Rect( r.x - clip.x, r.y - clip.y, r.width, r.height ), t );
			GUI.color = Color.white;
			GUI.EndClip();
		}

		static void Dot( float cx, float cy, float rad, Color c ) => UIKit.Rounded( new Rect( cx - rad, cy - rad, 2 * rad, 2 * rad ), c, rad );

		static void DrawStars( Rect dial, float s, float alpha )
		{
			int seed = 11;
			float Rnd() { seed = ( int ) ( ( long ) seed * 16807 % 2147483647 ); return seed / 2147483647f; }
			for ( int i = 0; i < 26; i++ )
			{
				float x = 6 + Rnd() * ( TW - 12 ), y = 5 + Rnd() * ( CY - 16 ), rr = 0.45f + Rnd() * 0.75f, o = 0.35f + Rnd() * 0.65f;
				Dot( dial.x + x * s, dial.y + y * s, rr * s, new Color( 1, 1, 1, o * alpha ) );
			}
		}

		// the sun's path: the day arc above the horizon, the night arc squashed below it, dotted
		static void Arc( Rect dial, float s, bool up, Color c )
		{
			float ry = up ? UP : DOWN;
			for ( int i = 0; i <= 44; i++ )
			{
				float th = Mathf.PI * i / 44f * ( up ? 1 : -1 );
				float x = CX - RX * Mathf.Cos( th * ( up ? 1 : -1 ) * ( up ? 1 : -1 ) ), y = CY - ry * Mathf.Sin( Mathf.Abs( th ) );
				x = CX - RX * Mathf.Cos( Mathf.Abs( th ) );
				if ( ! up ) y = CY + ry * Mathf.Sin( Mathf.Abs( th ) );
				if ( i % 2 == 0 ) Dot( dial.x + x * s, dial.y + y * s, 0.6f * s, c );
			}
		}
	}

	// ---- binding: one rebindable action (UI.js BindingControl)
	public sealed class SBinding : Ctl
	{
		readonly Bindings bindings; readonly string action; readonly Func<string> layout;
		readonly Dictionary<Device, List<Bind>> defaults = new Dictionary<Device, List<Bind>>();
		public bool capturing; int captureFrame;
		string hint, doneHint;
		float xT; float capT;
		const string HINT = "Press a key, a mouse button or a controller button";
		const string CAPTURE_HINT = "Press a key, mouse button or controller button · Esc cancels · Shift+Esc binds Esc · Del clears";

		public SBinding( Bindings bindings, string action, string label, Func<string> layout )
		{
			this.bindings = bindings; this.action = action; this.layout = layout;
			this.label = label ?? bindings.meta( action )?.label ?? action;
			tip = "Click, then press a key, mouse button or controller button.";
			foreach ( var d in Bindings.DEVICES ) defaults[ d ] = bindings.defaults( action, d );
			hint = HINT;
		}

		public override bool Modified
		{
			get
			{
				foreach ( var d in Bindings.DEVICES )
				{
					var mine = bindings.list( action, d ); var def = defaults[ d ];
					if ( mine.Count != def.Count ) return true;
					for ( int i = 0; i < mine.Count; i++ ) if ( mine[ i ].v != def[ i ].v || mine[ i ].sign != def[ i ].sign ) return true;
				}

				return false;
			}
		}

		public override void Reset() { bindings.reset( action ); Flash(); hint = HINT; Changed(); }
		void Changed() { bindings.save(); }
		public override void Tick( float dt ) { base.Tick( dt ); capT = UIKit.Approach( capT, capturing ? 1 : 0, 0.24f, dt ); }

		public void BeginCapture()
		{
			if ( capturing || ! enabled ) return;
			ui.capture?.EndCapture();
			capturing = true; captureFrame = Time.frameCount; ui.capture = this;
			hint = CAPTURE_HINT;
			if ( ui.Input != null ) ui.Input.capturing = true;
		}

		public void EndCapture( bool restoreHint = true )
		{
			if ( ! capturing ) return;
			capturing = false;
			if ( ui.capture == this ) ui.capture = null;
			if ( ui.Input != null ) ui.Input.capturing = false;
			if ( restoreHint ) hint = HINT;
		}

		// the first input pressed this frame, polled by the panel while this row is capturing
		public bool Ready => capturing && Time.frameCount - captureFrame >= 2;

		public void Assign( GameInput.CaptureResult c )
		{
			if ( c.cancel ) { EndCapture(); return; }
			if ( c.clear ) { bindings.clear( action ); EndCapture(); Changed(); return; }
			var lost = bindings.add( action, c.device, new Bind( c.v ) );
			EndCapture();
			Changed();
			if ( lost.Count > 0 ) hint = "Taken from " + string.Join( ", ", lost.Select( id => bindings.meta( id )?.label ?? id ) );
		}

		struct Cap { public Device device; public string v; public string text; public float w; }

		List<Cap> Caps()
		{
			var list = new List<Cap>();
			string lay = layout?.Invoke() ?? "xbox";
			foreach ( var d in Bindings.DEVICES ) foreach ( var e in bindings.list( action, d ) )
			{
				string text = Bindings.Glyph( d, e.v, lay );
				list.Add( new Cap { device = d, v = e.v, text = text, w = UIKit.KbdSize( text ).width + 3 * U + 15 * U } );
			}

			return list;
		}

		float KeysHeight( float w, List<Cap> caps )
		{
			float u = U, x = 0; int rows = 1;
			if ( caps.Count == 0 ) return 24 * u;
			foreach ( var c in caps ) { if ( x > 0 && x + c.w > w ) { rows++; x = 0; } x += c.w + 6 * u; }
			return Mathf.Max( 24 * u, rows * 24 * u + ( rows - 1 ) * 6 * u );
		}

		float HintHeight( float w ) => UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.UpperLeft, true ).CalcHeight( new GUIContent( hint ), w );

		public override float Height( float w ) => ( 10 + 20 + 7 ) * U + KeysHeight( w, Caps() ) + 5 * U + HintHeight( w ) + 10 * U + 1;

		public override void Draw( Rect r, float a )
		{
			float u = U; var ev = Event.current;
			var caps = Caps();
			var row = new Rect( r.x, r.y, r.width, r.height - 1 );
			bool rowHot = ui.Hot( row ) && enabled;
			if ( ev.type == EventType.Repaint && capT > 0.01f )
			{
				GUI.DrawTexture( row, UIKit.Ramp( UIKit.AQUA.WithAlpha( 0.14f ), UIKit.AQUA.WithAlpha( 0 ) ), ScaleMode.StretchToFill, true, 0, new Color( 1, 1, 1, capT * a ), Vector4.zero, new Vector4( 8 * u, 8 * u, 8 * u, 8 * u ) );
			}

			DrawFlash( row, a );
			float x0 = r.x, y = r.y + 10 * u;
			float reset = DrawLabel( x0, y, r.width, a );
			xT = UIKit.Approach( xT, rowHot ? 1 : 0, 0.15f, 0.016f ); // (the × shows while the row is hovered)

			// the caps
			float ky = y + 20 * u + 7 * u, cx = 0, cy = 0;
			bool clickedX = false;
			if ( caps.Count == 0 && ev.type == EventType.Repaint )
			{
				var st = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft ); st.normal.textColor = UIKit.INK3.WithAlpha( a );
				GUI.Label( new Rect( x0, ky, 40 * u, 24 * u ), "—", st );
			}

			foreach ( var c in caps )
			{
				if ( cx > 0 && cx + c.w > r.width ) { cx = 0; cy += 30 * u; }
				var kb = UIKit.KbdSize( c.text );
				var kr = new Rect( x0 + cx, ky + cy + ( 24 * u - kb.height ) / 2, kb.width, kb.height );
				var xr = new Rect( kr.xMax + 3 * u, ky + cy + ( 24 * u - 15 * u ) / 2, 15 * u, 15 * u );
				bool xhot = rowHot && xT > 0.5f && ui.Hot( xr );
				if ( ev.type == EventType.Repaint )
				{
					if ( c.device == Device.pad )
					{
						UIKit.VRampPill( kr, UIKit.AQUA.WithAlpha( 0.24f ), UIKit.AQUA.WithAlpha( 0.1f ), 4 * u, a );
						UIKit.Rounded( kr, UIKit.AQUA.WithAlpha( 0.45f * a ), 4 * u, 1 );
						var st = UIKit.Style( UIFonts.MonoMedium, 10.5f, TextAnchor.MiddleCenter ); st.normal.textColor = UIKit.INK.WithAlpha( a );
						GUI.Label( kr, c.text, st );
					}
					else if ( c.device == Device.mouse )
					{
						UIKit.Rounded( kr, UIKit.FILL.WithAlpha( a ), 4 * u ); UIKit.Rounded( kr, UIKit.LINE2.WithAlpha( a ), 4 * u, 1 );
						var st = UIKit.Style( UIFonts.MonoMedium, 10.5f, TextAnchor.MiddleCenter ); st.normal.textColor = UIKit.INK2.WithAlpha( a );
						GUI.Label( kr, c.text, st );
					}
					else UIKit.Kbd( kr, c.text, a );

					if ( xT > 0.01f )
					{
						UIKit.Rounded( xr, new Color( 1, 1, 1, ( xhot ? 0.2f : 0.1f ) * xT * a ), 8 * u );
						IconKit.DrawAt( xr.center.x, xr.center.y, 9 * u, "close", UIKit.INK2.WithAlpha( xT * a ) );
					}
				}

				if ( ev.type == EventType.MouseDown && ev.button == 0 && xhot )
				{
					bindings.remove( action, c.device, c.v ); Changed(); hint = HINT; clickedX = true; ev.Use();
				}

				cx += c.w + 6 * u;
			}

			// the hint
			float hy = ky + KeysHeight( r.width, caps ) + 5 * u;
			if ( ev.type == EventType.Repaint )
			{
				var hs = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.UpperLeft, true );
				hs.normal.textColor = ( capturing ? UIKit.AQUA : UIKit.INK3 ).WithAlpha( a );
				GUI.Label( new Rect( x0, hy, r.width, HintHeight( r.width ) ), hint, hs );
				UIKit.Rounded( new Rect( r.x, r.yMax - 1, r.width, 1 ), new Color( 1, 1, 1, 0.06f * a ), 0 );
			}

			// a click on the row waits for the next input
			if ( ev.type == EventType.MouseDown && ev.button == 0 && rowHot && ! clickedX ) { BeginCapture(); ev.Use(); }
			_ = reset;
		}
	}
}
