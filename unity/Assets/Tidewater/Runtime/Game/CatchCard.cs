using System;
using UnityEngine;
using Color = UnityEngine.Color;

// Port of GameHUD.showCatch (src/game/GameHUD.js, the `.gm-catch*` styles): the catch card, full screen. The world dims; the species name and the badge stand above, the
// fish lies side-on in its studio light (FishPortrait) between the name and the numbers, with the splash of its landing around it and its shadow under it; the length,
// weight and value, a note about the log and the way out come below. The entrance is the CSS one (the top block rises, the numbers rise later, the badge is stamped on,
// the drops fly out), on the card's own clock.
//
// Deviations (see PORTING.md): the browser blurs the world behind the card (backdrop-filter); here the scrim only darkens it. The fonts are the page's (UIFonts:
// Caveat Brush for the name, Kalam, Inter, JetBrains Mono), but IMGUI has no letter spacing and no per-run weights (the note's bold is synthesized).
namespace Tidewater.Game
{
	public sealed class CatchCard
	{
		static readonly Color INK = new Color( 244 / 255f, 234 / 255f, 214 / 255f ); // #f4ead6
		static readonly Color GOLD = new Color( 240 / 255f, 196 / 255f, 106 / 255f ); // #f0c46a

		public readonly FishPortrait portrait; // (the fish guide draws its thumbnails with it)
		LastCatch info; double price; string note;
		double t;           // the card's clock
		float[] drops;      // per drop: angle, radius, size, delay
		Texture2D scrim, shadow, drop, shine;
		GUIStyle title, small, stat, statLabel, noteStyle, badgeStyle, plain, sub, kbd;
		int styleSize = -1;
		public bool Open { get; private set; }
		// the portrait could be drawn this frame (else the fish stays on the line)
		public bool PortraitOk { get; private set; }

		public CatchCard( Transform parent )
		{
			portrait = new FishPortrait( parent );
		}

		public bool Alive => portrait.Alive;

		public void Dispose()
		{
			portrait.Dispose();
			foreach ( var tx in new[] { scrim, shadow, drop, shine } ) if ( tx != null ) UnityEngine.Object.DestroyImmediate( tx );
			scrim = shadow = drop = shine = null;
		}

		public void Show( LastCatch info, double price, string note )
		{
			this.info = info; this.price = price; this.note = note;
			t = 0; Open = true;
			// splash burst around the fish as it lands in view (GameHUD.showCatch)
			var rnd = new System.Random();
			drops = new float[ 26 * 4 ];
			for ( int i = 0; i < 26; i ++ )
			{
				float a = ( float ) ( ( i / 26.0 ) * Math.PI * 2 + rnd.NextDouble() * 0.3 ), r = ( float ) ( 90 + rnd.NextDouble() * 260 );
				drops[ i * 4 ] = a; drops[ i * 4 + 1 ] = r; drops[ i * 4 + 2 ] = ( float ) ( 4 + rnd.NextDouble() * 12 ); drops[ i * 4 + 3 ] = ( float ) ( 250 + rnd.NextDouble() * 220 ) / 1000f;
			}

			portrait.Show( info.species, info.kg );
		}

		public void Hide()
		{
			Open = false; PortraitOk = false;
			portrait.Hide();
		}

		// ---- layout (the CSS clamps)

		struct Layout
		{
			public float gap, titleSize, sciSize, topH, sw, sh, statW, statGap, statH, valueSize, noteSize, bottomH, y0;
		}

		Layout Measure( float w, float h )
		{
			var l = new Layout();
			l.gap = Mathf.Clamp( 0.014f * h, 6, 18 );
			l.titleSize = Mathf.Clamp( 0.062f * w, 38, 92 );
			l.sciSize = Mathf.Clamp( 0.013f * w, 13, 19 );
			l.topH = 26 + 8 + l.titleSize + 4 + l.sciSize * 1.4f;
			l.sw = Mathf.Min( 0.94f * w, 1400f, 0.46f * h * 2.4f );
			l.sh = l.sw / 2.4f;
			l.statW = Mathf.Clamp( 0.11f * w, 96, 150 );
			l.statGap = Mathf.Clamp( 0.016f * w, 8, 22 );
			l.valueSize = Mathf.Clamp( 0.022f * w, 20, 32 );
			l.statH = 10 + 11.5f + 4 + l.valueSize * 1.25f + 2 + 14 + 9;
			l.noteSize = Mathf.Clamp( 0.0125f * w, 14, 19 );
			int lines = 1; foreach ( char c in note ?? "" ) if ( c == '\n' ) lines ++;
			l.bottomH = 8 + l.statH + 12 + lines * l.noteSize * 1.4f + 12 + 20;
			float total = l.topH + l.gap + l.sh + l.gap + l.bottomH;
			// (a window too short for it, the Editor's Game view: the stage gives way, which the browser's card does not do)
			float room = h - l.topH - l.bottomH - 2 * l.gap - 24;
			if ( l.sh > room ) { l.sh = Mathf.Max( room, 120 ); l.sw = l.sh * 2.4f; }
			total = l.topH + l.gap + l.sh + l.gap + l.bottomH;
			l.y0 = Mathf.Max( 12, ( h - total ) / 2 );
			return l;
		}

		// once a frame (Update), while the card is open: the portrait is drawn into its texture for this frame's OnGUI
		public void Tick( double dt )
		{
			if ( ! Open ) return;
			t += dt;
			var l = Measure( Screen.width, Screen.height );
			PortraitOk = portrait.Update( dt, Mathf.RoundToInt( l.sw ), Mathf.RoundToInt( l.sh ) );
		}

		// ---- drawing

		static float Bezier( float x1, float y1, float x2, float y2, float x )
		{
			// the CSS cubic-bezier( x1, y1, x2, y2 ) timing function: solve x( s ) = x for the curve parameter s, then y( s )
			if ( x <= 0 ) return 0; if ( x >= 1 ) return 1;
			float s = x;
			for ( int i = 0; i < 8; i ++ )
			{
				float cx = 3 * x1 * s * ( 1 - s ) * ( 1 - s ) + 3 * x2 * s * s * ( 1 - s ) + s * s * s - x;
				float dx = 3 * x1 * ( 1 - s ) * ( 1 - 3 * s ) + 3 * x2 * s * ( 2 - 3 * s ) + 3 * s * s;
				if ( Mathf.Abs( dx ) < 1e-5f ) break;
				s = Mathf.Clamp01( s - cx / dx );
			}
			return 3 * y1 * s * ( 1 - s ) * ( 1 - s ) + 3 * y2 * s * s * ( 1 - s ) + s * s * s;
		}
		static float Ease( float x ) => Bezier( 0.2f, 0.8f, 0.2f, 1f, x ); // --tw-ease

		// a keyframe animation that holds its first state until `delay` (opacity 0 before it, fill-mode forwards after)
		static float Prog( double t, float delaySec, float durSec ) => Mathf.Clamp01( ( float ) ( ( t - delaySec ) / durSec ) );

		static Texture2D Make( int w, int h, Func<float, float, Color> px, TextureWrapMode wrap = TextureWrapMode.Clamp )
		{
			var tex = new Texture2D( w, h, TextureFormat.RGBA32, false, true ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = wrap, filterMode = FilterMode.Bilinear };
			var c = new Color[ w * h ];
			for ( int y = 0; y < h; y ++ ) for ( int x = 0; x < w; x ++ ) c[ y * w + x ] = px( ( x + 0.5f ) / w, ( y + 0.5f ) / h );
			tex.SetPixels( c ); tex.Apply();
			return tex;
		}

		void Textures()
		{
			// the world's scrim: radial-gradient( 70% 60% at 50% 50%, rgba( 10, 22, 34, 0.55 ), rgba( 4, 9, 15, 0.86 ) )
			if ( scrim == null ) scrim = Make( 128, 72, ( u, v ) =>
			{
				float dx = ( u - 0.5f ) / 0.7f, dy = ( v - 0.5f ) / 0.6f, r = Mathf.Clamp01( Mathf.Sqrt( dx * dx + dy * dy ) );
				return Color.Lerp( new Color( 10 / 255f, 22 / 255f, 34 / 255f, 0.55f ), new Color( 4 / 255f, 9 / 255f, 15 / 255f, 0.86f ), r );
			} );
			// the fish's shadow: radial-gradient( closest-side, rgba( 0, 0, 0, 0.5 ), transparent )
			if ( shadow == null ) shadow = Make( 64, 64, ( u, v ) =>
			{
				float r = Mathf.Sqrt( ( u - 0.5f ) * ( u - 0.5f ) + ( v - 0.5f ) * ( v - 0.5f ) ) * 2;
				return new Color( 0, 0, 0, 0.5f * Mathf.Clamp01( 1 - r ) );
			} );
			// a drop: radial-gradient( circle at 35% 35%, rgba( 255, 255, 255, 0.95 ), rgba( 190, 235, 245, 0.55 ) 55%, transparent 72% ), round
			if ( drop == null ) drop = Make( 48, 48, ( u, v ) =>
			{
				if ( ( u - 0.5f ) * ( u - 0.5f ) + ( v - 0.5f ) * ( v - 0.5f ) > 0.25f ) return Color.clear;
				float d = Mathf.Sqrt( ( u - 0.35f ) * ( u - 0.35f ) + ( 1 - v - 0.35f ) * ( 1 - v - 0.35f ) ) / ( 0.65f * 1.41421356f );
				var rim = new Color( 190 / 255f, 235 / 255f, 245 / 255f );
				if ( d <= 0.55f ) { var c = Color.Lerp( new Color( 1, 1, 1 ), rim, d / 0.55f ); c.a = Mathf.Lerp( 0.95f, 0.55f, d / 0.55f ); return c; }
				if ( d <= 0.72f ) { var c = rim; c.a = 0.55f * ( 1 - ( d - 0.55f ) / 0.17f ); return c; }
				return Color.clear;
			} );
			// the record badge: linear-gradient( 100deg, #f3cf8a 0%, #d9a441 40%, #fff0cf 50%, #d9a441 60%, #f3cf8a 100% ) at 250 % width
			if ( shine == null ) shine = Make( 256, 1, ( u, v ) =>
			{
				Color a = new Color( 0xf3 / 255f, 0xcf / 255f, 0x8a / 255f ), b = new Color( 0xd9 / 255f, 0xa4 / 255f, 0x41 / 255f ), c = new Color( 1f, 0xf0 / 255f, 0xcf / 255f );
				return u < 0.4f ? Color.Lerp( a, b, u / 0.4f ) : u < 0.5f ? Color.Lerp( b, c, ( u - 0.4f ) / 0.1f ) : u < 0.6f ? Color.Lerp( c, b, ( u - 0.5f ) / 0.1f ) : Color.Lerp( b, a, ( u - 0.6f ) / 0.4f );
			}, TextureWrapMode.Repeat );
		}

		void Styles( float w )
		{
			int key = Mathf.RoundToInt( w );
			if ( title != null && styleSize == key ) return;
			styleSize = key;
			var l = Measure( w, Screen.height );
			GUIStyle S( Font font, int size, FontStyle fs, TextAnchor al ) { var s = new GUIStyle( GUI.skin.label ) { font = font, fontSize = size, fontStyle = fs, alignment = al, richText = true, wordWrap = false, clipping = TextClipping.Overflow }; s.padding = new RectOffset(); s.margin = new RectOffset(); return s; }
			// (the face per role is the CSS one: h2 Caveat Brush, .gm-catch-sci Kalam italic, .gm-stat b JetBrains Mono, its label Inter 700, .gm-catch-note Kalam, kbd mono)
			title = S( UIFonts.Display, ( int ) l.titleSize, FontStyle.Normal, TextAnchor.MiddleCenter );
			small = S( UIFonts.Hand, ( int ) l.sciSize, FontStyle.Italic, TextAnchor.MiddleCenter );
			stat = S( UIFonts.MonoMedium, ( int ) l.valueSize, FontStyle.Normal, TextAnchor.MiddleCenter );
			statLabel = S( UIFonts.InterBold, 11, FontStyle.Normal, TextAnchor.MiddleCenter );
			noteStyle = S( UIFonts.Hand, ( int ) l.noteSize, FontStyle.Normal, TextAnchor.UpperCenter );
			badgeStyle = S( UIFonts.InterBold, 14, FontStyle.Normal, TextAnchor.MiddleCenter );
			plain = S( UIFonts.Inter, 12, FontStyle.Normal, TextAnchor.MiddleCenter );
			sub = S( UIFonts.Mono, 12, FontStyle.Normal, TextAnchor.MiddleCenter );
			kbd = S( UIFonts.MonoMedium, 11, FontStyle.Normal, TextAnchor.MiddleCenter );
		}

		static void Text( Rect r, string s, GUIStyle st, Color c, float alpha, bool shadow = false )
		{
			if ( shadow )
			{
				var o = r; o.y += 2; st.normal.textColor = new Color( 0, 0, 0, 0.35f * alpha ); GUI.Label( o, s, st );
				o.y += 6; o.x += 0; st.normal.textColor = new Color( 0, 0, 0, 0.2f * alpha ); GUI.Label( o, s, st );
			}
			c.a *= alpha; st.normal.textColor = c; GUI.Label( r, s, st );
		}

		static void Rounded( Rect r, Color c, float radius, float border = 0 )
		{
			GUI.DrawTexture( r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, c, new Vector4( border, border, border, border ), new Vector4( radius, radius, radius, radius ) );
		}

		public void OnGUI( float remaining01, string rodUseKey, string interactKey )
		{
			if ( ! Open || info == null ) return;
			float w = Screen.width, h = Screen.height;
			Textures(); Styles( w );
			var f = FishTable.Get( info.species );
			var l = Measure( w, h );
			float fadeIn = Ease( Prog( t, 0, 0.3f ) ), scrimIn = Ease( Prog( t, 0, 0.42f ) );

			// the world dims
			var old = GUI.color;
			GUI.color = new Color( 1, 1, 1, scrimIn );
			GUI.DrawTexture( new Rect( 0, 0, w, h ), scrim, ScaleMode.StretchToFill, true );
			GUI.color = old;

			float y = l.y0;
			// ---- the top block: rises 700 ms after 120 ms
			{
				float p = Ease( Prog( t, 0.12f, 0.7f ) ), a = p * fadeIn, dy = ( 1 - p ) * 14;
				var eye = new Rect( 0, y + dy, w, 26 );
				DrawBadge( eye, fadeIn );
				Text( new Rect( 0, y + 34 + dy, w, l.titleSize ), f.name, title, new Color( 0xfb / 255f, 0xf1 / 255f, 0xdc / 255f ), a, true );
				Text( new Rect( 0, y + 34 + l.titleSize + 4 + dy, w, l.sciSize * 1.4f ), f.sci ?? "", small, new Color( INK.r, INK.g, INK.b, 0.66f ), a );
			}

			y += l.topH + l.gap;
			// ---- the stage: the splash, the shadow, the fish
			var stage = new Rect( ( w - l.sw ) / 2, y, l.sw, l.sh );
			{
				float sp = Ease( Prog( t, 0.35f, 0.9f ) ) * fadeIn;
				GUI.color = new Color( 1, 1, 1, sp );
				GUI.DrawTexture( new Rect( stage.x + stage.width * 0.12f, stage.yMax - stage.height * 0.06f - stage.height * 0.16f, stage.width * 0.76f, stage.height * 0.16f ), shadow, ScaleMode.StretchToFill, true );
				if ( PortraitOk && portrait.Texture != null )
				{
					GUI.color = new Color( 1, 1, 1, fadeIn );
					GUI.DrawTexture( stage, portrait.Texture, ScaleMode.StretchToFill, true );
				}

				// the drops fly out of the stage's centre (left 50 %, top 52 %)
				var origin = new Vector2( stage.x + stage.width * 0.5f, stage.y + stage.height * 0.52f );
				for ( int i = 0; drops != null && i < drops.Length / 4; i ++ )
				{
					float a = drops[ i * 4 ], r = drops[ i * 4 + 1 ], size = drops[ i * 4 + 2 ], delay = drops[ i * 4 + 3 ];
					float k = Prog( t, delay, 1.1f );
					if ( k <= 0 || k >= 1 ) continue;
					float m = Bezier( 0.2f, 0.7f, 0.3f, 1f, k );
					// opacity: 0 -> 1 over the first 12 %, then back to 0, each segment on the timing function
					float op = k < 0.12f ? Bezier( 0.2f, 0.7f, 0.3f, 1f, k / 0.12f ) : 1 - Bezier( 0.2f, 0.7f, 0.3f, 1f, ( k - 0.12f ) / 0.88f );
					Vector2 to = new Vector2( Mathf.Cos( a ) * r * 1.8f, Mathf.Sin( a ) * r * 0.55f - 40 );
					float sc = Mathf.Lerp( 0.4f, 1f, m ), d = size * sc;
					var pos = origin + to * m;
					GUI.color = new Color( 1, 1, 1, op * fadeIn );
					GUI.DrawTexture( new Rect( pos.x - d / 2, pos.y - d / 2, d, d ), drop, ScaleMode.StretchToFill, true );
				}

				GUI.color = old;
			}

			y += l.sh + l.gap;
			// ---- the bottom block: rises after 520 ms
			{
				float p = Ease( Prog( t, 0.52f, 0.7f ) ), a = p * fadeIn, dy = ( 1 - p ) * 14;
				float by = y + 8 + dy;
				float tw = 3 * l.statW + 2 * l.statGap, x0 = ( w - tw ) / 2;
				inch( out double inchV, out double lbV );
				Stat( new Rect( x0, by, l.statW, l.statH ), "LENGTH", $"{info.cm:F0}<size={( int ) ( l.valueSize * 0.55f )}> cm</size>", $"{inchV:F1} in", false, a, l );
				Stat( new Rect( x0 + l.statW + l.statGap, by, l.statW, l.statH ), "WEIGHT", $"{( info.kg < 1 ? info.kg.ToString( "F2" ) : info.kg.ToString( "F1" ) )}<size={( int ) ( l.valueSize * 0.55f )}> kg</size>", $"{lbV:F1} lb", false, a, l );
				Stat( new Rect( x0 + 2 * ( l.statW + l.statGap ), by, l.statW, l.statH ), "VALUE", $"${price:F0}", info.kept ? "in the cooler" : "let go", true, a, l );

				by += l.statH + 12;
				var nc = ! info.kept ? new Color( 1f, 0x9a / 255f, 0x8a / 255f ) : new Color( INK.r, INK.g, INK.b, 0.78f );
				int lines = 1; foreach ( char c in note ) if ( c == '\n' ) lines ++;
				Text( new Rect( 0, by, w, lines * l.noteSize * 1.4f ), note, noteStyle, nc, a );
				by += lines * l.noteSize * 1.4f + 12;
				Foot( by, w, a, rodUseKey, interactKey, remaining01 );
			}

			GUI.color = old;
		}

		void inch( out double inches, out double lb ) { inches = info.cm / 2.54; lb = info.kg * 2.20462; }

		// the badge: stamped on at 900 ms (scale 2.4 -> 1, turning to -3 degrees); the record one shines
		void DrawBadge( Rect eyebrow, float alpha )
		{
			string text = info.record ? "★ NEW RECORD" : info.newSpecies ? "NEW SPECIES" : "CATCH";
			var size = badgeStyle.CalcSize( new GUIContent( text ) );
			float bw = size.x + 24 + 8, bh = size.y + 8;
			var r = new Rect( eyebrow.center.x - bw / 2, eyebrow.center.y - bh / 2, bw, bh );
			float k = Prog( t, 0.9f, 0.52f );
			float scale, rot, op;
			if ( k <= 0 ) return;
			if ( k < 0.7f ) { float e = Bezier( 0.3f, 1.5f, 0.5f, 1f, k / 0.7f ); scale = Mathf.LerpUnclamped( 2.4f, 0.94f, e ); rot = Mathf.LerpUnclamped( -8, -3, e ); op = Mathf.Clamp01( e ); }
			else { float e = Bezier( 0.3f, 1.5f, 0.5f, 1f, ( k - 0.7f ) / 0.3f ); scale = Mathf.LerpUnclamped( 0.94f, 1f, e ); rot = -3; op = 1; }
			var m = GUI.matrix;
			GUIUtility.ScaleAroundPivot( new Vector2( scale, scale ), r.center );
			GUIUtility.RotateAroundPivot( rot, r.center );
			var old = GUI.color;
			float a = op * alpha;
			if ( info.record )
			{
				// the shine slides across (background-position 100 % -> -150 % over 2.6 s, after 1.6 s)
				float cyc = ( float ) ( t >= 1.6 ? ( ( t - 1.6 ) % 2.6 ) / 2.6 : 0 );
				float pos = Mathf.Lerp( 1f, -1.5f, Bezier( 0.65f, 0, 0.35f, 1, cyc ) );
				if ( t < 1.6 ) pos = 1f;
				GUI.color = new Color( 1, 1, 1, a );
				GUI.DrawTextureWithTexCoords( r, shine, new Rect( 0.6f * pos, 0, 0.4f, 1 ) );
				badgeStyle.normal.textColor = new Color( 0x2a / 255f, 0x16 / 255f, 0x04 / 255f, a );
			}
			else if ( info.newSpecies )
			{
				Rounded( r, new Color( 0x6f / 255f, 0xd6 / 255f, 0xc6 / 255f, a ), 4 );
				badgeStyle.normal.textColor = new Color( 0x06 / 255f, 0x24 / 255f, 0x20 / 255f, a );
			}
			else
			{
				Rounded( r, new Color( INK.r, INK.g, INK.b, 0.35f * a ), 4, 1 );
				badgeStyle.normal.textColor = new Color( INK.r, INK.g, INK.b, 0.8f * a );
			}

			GUI.Label( r, text, badgeStyle );
			GUI.color = old;
			GUI.matrix = m;
		}

		void Stat( Rect r, string label, string value, string sub_, bool isValue, float alpha, Layout l )
		{
			Rounded( r, new Color( 232 / 255f, 220 / 255f, 192 / 255f, 0.09f * alpha ), 6 );
			Rounded( r, new Color( 214 / 255f, 180 / 255f, 110 / 255f, 0.38f * alpha ), 6, 1 );
			Text( new Rect( r.x, r.y + 10, r.width, 12 ), label, statLabel, new Color( 222 / 255f, 190 / 255f, 125 / 255f, 0.9f ), alpha );
			Text( new Rect( r.x, r.y + 10 + 11.5f + 4, r.width, l.valueSize * 1.25f ), value, stat, isValue ? GOLD : new Color( 0xfb / 255f, 0xf1 / 255f, 0xdc / 255f ), alpha );
			Text( new Rect( r.x, r.y + 10 + 11.5f + 4 + l.valueSize * 1.25f + 2, r.width, 14 ), sub_, this.sub, new Color( INK.r, INK.g, INK.b, 0.5f ), alpha );
		}

		void Foot( float y, float w, float alpha, string rodUseKey, string interactKey, float remaining01 )
		{
			// [Click] or [E] to continue ----- (the time left)
			var tag = kbd;
			float k1 = tag.CalcSize( new GUIContent( rodUseKey ) ).x + 12, k2 = tag.CalcSize( new GUIContent( interactKey ) ).x + 12;
			float or = plain.CalcSize( new GUIContent( " or " ) ).x, cont = plain.CalcSize( new GUIContent( " to continue" ) ).x, bar = 80, gap = 8;
			float total = k1 + or + k2 + cont + gap + bar, x = ( w - total ) / 2;
			var dim = new Color( INK.r, INK.g, INK.b, 0.5f );
			Kbd( new Rect( x, y, k1, 20 ), rodUseKey, tag, alpha ); x += k1;
			Text( new Rect( x, y, or, 20 ), " or ", plain, dim, alpha ); x += or;
			Kbd( new Rect( x, y, k2, 20 ), interactKey, tag, alpha ); x += k2;
			Text( new Rect( x, y, cont, 20 ), " to continue", plain, dim, alpha ); x += cont + gap;
			var br = new Rect( x, y + 9, bar, 2 );
			Rounded( br, new Color( INK.r, INK.g, INK.b, 0.15f * alpha ), 2 );
			Rounded( new Rect( br.x, br.y, bar * Mathf.Clamp01( remaining01 ), br.height ), new Color( INK.r, INK.g, INK.b, 0.55f * alpha ), 2 );
		}

		static void Kbd( Rect r, string key, GUIStyle st, float alpha )
		{
			Rounded( r, new Color( INK.r, INK.g, INK.b, 0.08f * alpha ), 4 );
			Rounded( r, new Color( INK.r, INK.g, INK.b, 0.3f * alpha ), 4, 1 );
			st.normal.textColor = new Color( INK.r, INK.g, INK.b, alpha );
			GUI.Label( r, key, st );
		}
	}
}
