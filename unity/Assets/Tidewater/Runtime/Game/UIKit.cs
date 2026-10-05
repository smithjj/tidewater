using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// The web UI's look in IMGUI (src/ui/ui.css tokens and the .tw-glass / kbd / .gm-btn / .gm-chip pieces) for the guide screens: the colours, a rounded rectangle, the glass panel,
// a key cap, a pill button and a chip, and a small inline text flow for the prose that carries <b> and <kbd> (the HTML the JS guide writes). Sizes are CSS px times UIScale.U.
// Not ported: the glass's backdrop blur (IMGUI cannot sample the screen), letter spacing (the eyebrows are just upper case), the panel's drop shadow beyond a soft edge.
//
// Markup of Flow: plain words, <b>bold</b>, <kbd>KEY</kbd>; <kbd bind="rodUse">LMB</kbd> shows the glyph the input has for that action (the text stays when there is none),
// <b word="rodUse">left mouse button</b> becomes that glyph on a pad (resolveLabels' [data-bind] and [data-word]); <c tone="sun">★</c> colours a run (the .gm-up / .gm-down / .gm-star marks; sun, aqua, coral, ink3).
namespace Tidewater.Game
{
	public static class UIKit
	{
		public static readonly Color AQUA = new Color( 95 / 255f, 227 / 255f, 212 / 255f );
		public static readonly Color SUN = new Color( 255 / 255f, 184 / 255f, 107 / 255f );
		public static readonly Color CORAL = new Color( 255 / 255f, 122 / 255f, 133 / 255f );
		public static readonly Color INK = new Color( 238 / 255f, 246 / 255f, 248 / 255f );
		public static readonly Color INK2 = new Color( 222 / 255f, 236 / 255f, 242 / 255f, 0.76f );
		public static readonly Color INK3 = new Color( 200 / 255f, 222 / 255f, 232 / 255f, 0.52f );
		public static readonly Color INK4 = new Color( 190 / 255f, 215 / 255f, 228 / 255f, 0.32f );
		public static readonly Color GLASS = new Color( 12 / 255f, 18 / 255f, 26 / 255f, 0.62f );
		public static readonly Color FILL = new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.07f );
		public static readonly Color FILL2 = new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.12f );
		public static readonly Color LINE = new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.12f );
		public static readonly Color LINE2 = new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.2f );

		public static float U => UIScale.U;

		static Texture2D gloss;
		static readonly Dictionary<long, GUIStyle> styles = new Dictionary<long, GUIStyle>();

		// a label style for a font and a CSS px size (cached; the Editor resets styles when a Play session ends, so a style with no font is rebuilt)
		public static GUIStyle Style( Font font, float cssPx, TextAnchor align = TextAnchor.UpperLeft, bool wrap = false )
		{
			int size = Mathf.Max( 6, Mathf.RoundToInt( cssPx * U ) );
			long key = ( ( long ) ( font != null ? font.name.GetHashCode() : 0 ) << 24 ) ^ ( size << 8 ) ^ ( ( int ) align << 1 ) ^ ( wrap ? 1 : 0 );
			if ( styles.TryGetValue( key, out var s ) && s.font == font && font != null ) return s;
			s = new GUIStyle( GUI.skin.label ) { font = font, fontSize = size, alignment = align, wordWrap = wrap, richText = false, clipping = TextClipping.Overflow, padding = new RectOffset(), margin = new RectOffset() };
			styles[ key ] = s;
			return s;
		}

		public static void Rounded( Rect r, Color c, float radius, float border = 0 )
		{
			if ( c.a <= 0 ) return;
			GUI.DrawTexture( r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, new Vector4( border, border, border, border ), new Vector4( radius, radius, radius, radius ) );
		}

		// .tw-glass: the glass fill with a faint highlight along the top 40 %, the 1 px line, and the inset top edge
		public static void Glass( Rect r, float radius, float alpha = 1 )
		{
			if ( gloss == null )
			{
				gloss = new Texture2D( 1, 32, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
				for ( int y = 0; y < 32; y ++ ) gloss.SetPixel( 0, y, new Color( 1, 1, 1, 0.045f * ( y + 0.5f ) / 32f ) ); // linear-gradient( 180deg, 4.5 % white, 0 at 40 %): the texture's top row is the brightest
				gloss.Apply();
			}

			var shadow = new Rect( r.x - 2, r.y + 6, r.width + 4, r.height + 4 );
			Rounded( shadow, new Color( 0, 0, 0, 0.16f * alpha ), radius + 4 );
			// the page behind a blurred glass is dimmer than the fill alone: a touch more opacity stands in for the blur
			Rounded( r, new Color( GLASS.r, GLASS.g, GLASS.b, Mathf.Min( 1, 0.78f ) * alpha ), radius );
			var old = GUI.color; GUI.color = new Color( 1, 1, 1, alpha );
			GUI.DrawTexture( new Rect( r.x, r.y, r.width, r.height * 0.4f ), gloss, ScaleMode.StretchToFill, true );
			GUI.color = old;
			Rounded( r, new Color( LINE.r, LINE.g, LINE.b, LINE.a * alpha ), radius, 1 );
		}

		// kbd: a mono key cap, min 20u wide and 20u high
		public static Rect KbdSize( string text, float fs = 10.5f )
		{
			var st = Style( UIFonts.MonoMedium, fs );
			float w = Mathf.Max( 20 * U, st.CalcSize( new GUIContent( text ) ).x + 10 * U );
			return new Rect( 0, 0, w, 20 * U );
		}

		public static void Kbd( Rect r, string text, float alpha = 1, float fs = 10.5f )
		{
			Rounded( r, new Color( FILL.r, FILL.g, FILL.b, FILL.a * alpha ), 4 * U );
			Rounded( r, new Color( LINE2.r, LINE2.g, LINE2.b, LINE2.a * alpha ), 4 * U, 1 );
			var st = Style( UIFonts.MonoMedium, fs, TextAnchor.MiddleCenter );
			st.normal.textColor = new Color( INK.r, INK.g, INK.b, alpha );
			GUI.Label( r, text, st );
		}

		// .gm-btn: a pill (aqua with dark text, or the ghost fill). Returns true on a click.
		public static bool Button( Rect r, string text, bool ghost = false, bool enabled = true, float alpha = 1 )
		{
			bool hover = enabled && r.Contains( Event.current.mousePosition );
			var fill = ghost ? FILL2 : AQUA;
			if ( hover ) fill = ghost ? new Color( FILL2.r, FILL2.g, FILL2.b, 0.2f ) : Color.Lerp( AQUA, Color.white, 0.18f );
			fill.a *= ( enabled ? 1 : 0.4f ) * alpha;
			Rounded( r, fill, r.height / 2 );
			var st = Style( UIFonts.InterSemi, 12.5f, TextAnchor.MiddleCenter );
			var ink = ghost ? INK : new Color( 11 / 255f, 20 / 255f, 24 / 255f );
			ink.a *= ( enabled ? 1 : 0.4f ) * alpha;
			st.normal.textColor = ink;
			GUI.Label( r, text, st );
			bool was = GUI.enabled; GUI.enabled = enabled && was;
			bool click = GUI.Button( r, GUIContent.none, GUIStyle.none );
			GUI.enabled = was;
			return click;
		}

		public static float ButtonWidth( string text ) => Style( UIFonts.InterSemi, 12.5f ).CalcSize( new GUIContent( text ) ).x + 32 * U;

		// .gm-chip: a pill with the fill and the line (seen: the aqua line and a dot). Returns its width.
		public static float Chip( float x, float y, string text, string suffix, bool seen, float alpha = 1 )
		{
			var st = Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft );
			var sx = Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft );
			float tw = st.CalcSize( new GUIContent( text ) ).x, sw = suffix != null ? sx.CalcSize( new GUIContent( suffix ) ).x + 4 * U : 0, dot = seen ? 10 * U : 0;
			float w = tw + sw + dot + 16 * U, h = 18 * U;
			var r = new Rect( x, y, w, h );
			Rounded( r, new Color( FILL.r, FILL.g, FILL.b, FILL.a * alpha ), h / 2 );
			Rounded( r, seen ? new Color( AQUA.r, AQUA.g, AQUA.b, 0.6f * alpha ) : new Color( LINE.r, LINE.g, LINE.b, LINE.a * alpha ), h / 2, 1 );
			float cx = x + 8 * U;
			if ( seen ) { Rounded( new Rect( cx, y + h / 2 - 2.5f * U, 5 * U, 5 * U ), new Color( AQUA.r, AQUA.g, AQUA.b, alpha ), 3 * U ); cx += 10 * U; }
			st.normal.textColor = new Color( INK.r, INK.g, INK.b, alpha );
			GUI.Label( new Rect( cx, y, tw + 2, h ), text, st );
			if ( suffix != null ) { sx.normal.textColor = new Color( INK3.r, INK3.g, INK3.b, INK3.a * alpha ); GUI.Label( new Rect( cx + tw + 4 * U, y, sw, h ), suffix, sx ); }
			return w;
		}

		// .gm-mini: the small pill beside a row (Sell, Release). Returns true on a click.
		public static bool Mini( Rect r, string text, float alpha = 1, bool enabled = true )
		{
			Rounded( r, new Color( FILL.r, FILL.g, FILL.b, FILL.a * alpha ), r.height / 2 );
			Rounded( r, new Color( LINE.r, LINE.g, LINE.b, LINE.a * alpha ), r.height / 2, 1 );
			var st = Style( UIFonts.InterMedium, 11.5f, TextAnchor.MiddleCenter );
			st.normal.textColor = new Color( INK2.r, INK2.g, INK2.b, INK2.a * alpha );
			GUI.Label( r, text, st );
			bool was = GUI.enabled; GUI.enabled = enabled && was;
			bool click = GUI.Button( r, GUIContent.none, GUIStyle.none );
			GUI.enabled = was;
			return click;
		}

		public static float MiniWidth( string text ) => Style( UIFonts.InterMedium, 11.5f ).CalcSize( new GUIContent( text ) ).x + 16 * U + 2;

		// a scroll area with no bars of its own (a thin thumb is drawn after): the content is `contentH` tall and 8u narrower than the view
		public static Vector2 BeginScroll( Rect view, Vector2 pos, float contentH )
		{
			return GUI.BeginScrollView( view, pos, new Rect( 0, 0, view.width - 8 * U, Mathf.Max( contentH, view.height ) ), false, false, GUIStyle.none, GUIStyle.none );
		}

		public static void EndScroll( Rect view, ref Vector2 pos, float contentH, float alpha = 1 )
		{
			GUI.EndScrollView();
			float max = Mathf.Max( 0, contentH - view.height );
			pos.y = Mathf.Clamp( pos.y, 0, max );
			if ( max > 0 )
			{
				float th = Mathf.Max( 24, view.height * view.height / contentH ), ty = view.y + ( view.height - th ) * ( pos.y / max );
				Rounded( new Rect( view.xMax - 4, ty, 3, th ), new Color( 1, 1, 1, 0.22f * alpha ), 2 );
			}
		}

		// a horizontal two-colour ramp (linear-gradient( 90deg, a, b )) as a texture
		static readonly Dictionary<long, Texture2D> ramps = new Dictionary<long, Texture2D>();
		public static Texture2D Ramp( Color a, Color b )
		{
			long key = ( ( long ) ColorUtility.ToHtmlStringRGBA( a ).GetHashCode() << 32 ) ^ ( uint ) ColorUtility.ToHtmlStringRGBA( b ).GetHashCode();
			if ( ramps.TryGetValue( key, out var t ) && t != null ) return t;
			t = new Texture2D( 64, 1, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
			for ( int x = 0; x < 64; x ++ ) t.SetPixel( x, 0, Color.Lerp( a, b, x / 63f ) );
			t.Apply();
			ramps[ key ] = t;
			return t;
		}

		// a ramp drawn into a pill
		public static void RampPill( Rect r, Color a, Color b, float alpha = 1 )
		{
			if ( r.width <= 0.5f ) return;
			float rad = Mathf.Min( r.height / 2, r.width / 2 );
			GUI.DrawTexture( r, Ramp( a, b ), ScaleMode.StretchToFill, true, 0, new Color( 1, 1, 1, alpha ), Vector4.zero, new Vector4( rad, rad, rad, rad ) );
		}

		// a vertical two-colour ramp (linear-gradient( 180deg, top, bottom )) in a rounded rect
		static readonly Dictionary<long, Texture2D> vramps = new Dictionary<long, Texture2D>();
		public static void VRampPill( Rect r, Color top, Color bottom, float radius, float alpha = 1 )
		{
			long key = ( ( long ) ColorUtility.ToHtmlStringRGBA( top ).GetHashCode() << 32 ) ^ ( uint ) ColorUtility.ToHtmlStringRGBA( bottom ).GetHashCode();
			if ( ! vramps.TryGetValue( key, out var t ) || t == null )
			{
				t = new Texture2D( 1, 32, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
				for ( int y = 0; y < 32; y ++ ) t.SetPixel( 0, y, Color.Lerp( bottom, top, y / 31f ) ); // (a texture's row 0 is the bottom)
				t.Apply();
				vramps[ key ] = t;
			}

			GUI.DrawTexture( r, t, ScaleMode.StretchToFill, true, 0, new Color( 1, 1, 1, alpha ), Vector4.zero, new Vector4( radius, radius, radius, radius ) );
		}

		// radial-gradient( ellipse at center, rgba( 3, 9, 15, 0.38 ), rgba( 3, 9, 15, 0.72 ) ) over the screen (the controls sheet's scrim), scaled by `alpha`
		static Texture2D scrim;
		public static void Scrim( Rect r, float alpha )
		{
			if ( scrim == null )
			{
				scrim = new Texture2D( 64, 36, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
				for ( int y = 0; y < 36; y ++ ) for ( int x = 0; x < 64; x ++ )
				{
					float dx = ( x + 0.5f ) / 64 * 2 - 1, dy = ( y + 0.5f ) / 36 * 2 - 1, d = Mathf.Clamp01( Mathf.Sqrt( dx * dx + dy * dy ) / Mathf.Sqrt( 2 ) ); // (farthest corner)
					scrim.SetPixel( x, y, new Color( 3 / 255f, 9 / 255f, 15 / 255f, Mathf.Lerp( 0.38f, 0.72f, d ) ) );
				}

				scrim.Apply();
			}

			var o = GUI.color; GUI.color = new Color( 1, 1, 1, alpha );
			GUI.DrawTexture( r, scrim, ScaleMode.StretchToFill, true );
			GUI.color = o;
		}

		// the glass line colour names of the markup
		static Color Tone( string name )
		{
			switch ( name ) { case "sun": return SUN; case "aqua": return AQUA; case "coral": return CORAL; case "ink3": return INK3; case "ink2": return INK2; default: return INK; }
		}

		// move `v` toward `target` so that a full 0 → 1 swing takes `seconds` (the CSS transition on opacity)
		public static float Approach( float v, float target, float seconds, float dt ) => seconds <= 0 ? target : Mathf.MoveTowards( v, target, dt / seconds );

		// ---- inline text with <b> and <kbd>

		static readonly Regex TAG = new Regex( @"<(/?)(b|kbd|c)((?:\s+(?:bind|word|tone)=""[^""]*"")?)\s*>", RegexOptions.Compiled );
		static readonly Regex ATTR = new Regex( @"(bind|word|tone)=""([^""]*)""", RegexOptions.Compiled );

		struct Item { public string text; public bool bold, kbd; public bool space; public bool toned; public Color tone; public float w; }

		// the label an action shows for the device in hand (null: leave the text)
		public delegate string Labeller( string action );

		static List<Item> Parse( string markup, Labeller label, bool pad, float fs )
		{
			var items = new List<Item>();
			bool bold = false, kbd = false, toned = false; string bind = null, word = null; Color tone = INK;
			int at = 0;
			void Text( string s )
			{
				if ( s.Length == 0 ) return;
				if ( kbd )
				{
					if ( bind != null ) { var v = label?.Invoke( bind ); if ( ! string.IsNullOrEmpty( v ) ) s = v; }
					items.Add( new Item { text = s, kbd = true } );
					return;
				}

				if ( bold && word != null && pad ) { var v = label?.Invoke( word ); if ( ! string.IsNullOrEmpty( v ) ) s = v; }
				// words, the spaces between them kept as a flag on the next word (three spaces or more: the 12u gap between the items of a wrapped row)
				foreach ( var part in Regex.Split( s, @"(\s+)" ) )
				{
					if ( part.Length == 0 ) continue;
					if ( char.IsWhiteSpace( part[ 0 ] ) ) { items.Add( new Item { space = true, w = part.Length >= 3 ? 12 * U : 0 } ); continue; }
					items.Add( new Item { text = part, bold = bold, toned = toned, tone = tone } );
				}
			}

			foreach ( Match m in TAG.Matches( markup ) )
			{
				Text( markup.Substring( at, m.Index - at ) );
				at = m.Index + m.Length;
				bool close = m.Groups[ 1 ].Value == "/";
				string tag = m.Groups[ 2 ].Value;
				if ( tag == "b" ) { bold = ! close; word = null; if ( ! close ) { var a = ATTR.Match( m.Groups[ 3 ].Value ); if ( a.Success && a.Groups[ 1 ].Value == "word" ) word = a.Groups[ 2 ].Value; } }
				else if ( tag == "c" ) { toned = ! close; if ( ! close ) { var a = ATTR.Match( m.Groups[ 3 ].Value ); tone = a.Success && a.Groups[ 1 ].Value == "tone" ? Tone( a.Groups[ 2 ].Value ) : INK; } }
				else { kbd = ! close; bind = null; if ( ! close ) { var a = ATTR.Match( m.Groups[ 3 ].Value ); if ( a.Success && a.Groups[ 1 ].Value == "bind" ) bind = a.Groups[ 2 ].Value; } }
			}

			Text( markup.Substring( at ) );
			return items;
		}

		// Lay `markup` out in `width` from (x, y): draws it when `draw`, and returns its height. `lineH` is the line height as a multiple of the font size (the CSS line-height).
		public static float Flow( float x, float y, float width, string markup, float fs, float lineH, Color ink, Color bold, bool draw, Labeller label = null, bool pad = false, float alpha = 1 )
			=> Flow( x, y, width, markup, fs, lineH, ink, bold, draw, label, pad, alpha, out _ );

		// (also reports the width of the widest line)
		public static float Flow( float x, float y, float width, string markup, float fs, float lineH, Color ink, Color bold, bool draw, Labeller label, bool pad, float alpha, out float usedW )
		{
			usedW = 0;
			var items = Parse( markup, label, pad, fs );
			var reg = Style( UIFonts.Inter, fs ); var bst = Style( UIFonts.InterSemi, fs );
			float lh = fs * U * lineH, cx = 0, cy = 0, spaceW = reg.CalcSize( new GUIContent( "a a" ) ).x - reg.CalcSize( new GUIContent( "aa" ) ).x;
			float kh = 20 * U;
			bool gap = false, first = true; float gapW = 0;
			foreach ( var it in items )
			{
				if ( it.space ) { gap = ! first; gapW = it.w > 0 ? it.w : spaceW; continue; }
				float w, h = lh;
				if ( it.kbd ) { w = Mathf.Max( 20 * U, Style( UIFonts.MonoMedium, 10.5f ).CalcSize( new GUIContent( it.text ) ).x + 10 * U ); h = Mathf.Max( lh, kh ); }
				else w = ( it.bold ? bst : reg ).CalcSize( new GUIContent( it.text ) ).x;
				float lead = gap && cx > 0 ? gapW : 0;
				if ( cx > 0 && cx + lead + w > width + 0.5f ) { cx = 0; cy += lh; lead = 0; }
				cx += lead;
				if ( draw )
				{
					if ( it.kbd ) Kbd( new Rect( x + cx, y + cy + ( lh - kh ) / 2, w, kh ), it.text, alpha, 10.5f );
					else
					{
						var st = it.bold ? bst : reg;
						var c = it.toned ? it.tone : it.bold ? bold : ink; c.a *= alpha; st.normal.textColor = c;
						GUI.Label( new Rect( x + cx, y + cy, w + 2, lh ), it.text, st );
					}
				}

				cx += w;
				usedW = Mathf.Max( usedW, cx );
				gap = false; first = false;
			}

			return cy + lh;
		}
	}
}

namespace Tidewater.Game
{
	public static class UIColorExt
	{
		// the colour with its alpha multiplied
		public static UnityEngine.Color WithAlpha( this UnityEngine.Color c, float a ) { c.a *= a; return c; }
	}
}
