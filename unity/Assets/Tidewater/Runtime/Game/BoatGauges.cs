using System;
using UnityEngine;

// The boat instruments, bottom left while the player is at the helm (UI.js _buildBoat / setBoatGauges, the `.tw-boat` styles; AppUI.update feeds it): the throttle bar, the rpm dial
// with the speed in knots, and the compass with the heading. The two SVGs (the dial's arcs and ticks, the compass ring and its card) are drawn by Hidden/Tidewater/BoatGauge into
// render targets the IMGUI shows; the numbers, the rpm line and the card's letters are IMGUI text. The panel fades and rises in 420 ms when the helm is taken; the throttle fill
// follows its value over 90 ms. Like the JS, the shown values are quantized (rpm to 0.1 %, the heading to 0.1 degree) and the targets are redrawn only when one changes.
namespace Tidewater.Game
{
	public sealed class BoatGauges
	{
		static readonly string[] CARDINALS = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

		Material mat;
		RenderTexture dialRT, compassRT;
		float fade;                     // the panel's opacity / rise, 0..1, linear in time (the CSS transition's input)
		bool on;
		float speed, rpm, thr, thrShown, hdg;
		int dialKey = int.MinValue, compassKey = int.MinValue;
		const float DIAL_M = 10, COMPASS_M = 8; // the margin round each viewBox, units (the glows overflow)

		public bool Visible => fade > 0.001f;

		// every frame (after the player and the boat are updated)
		public void Tick( double dtd, GameHost game )
		{
			float dt = ( float ) dtd;
			var host = game.Host; var p = host != null ? host.player : null;
			var b = p != null && p.mode == "boat" && ! host.freeCam ? p.boat : null;
			on = b != null;
			fade = UIKit.Approach( fade, on ? 1 : 0, 0.42f, dt );
			if ( b != null )
			{
				var f = b.forward( new Engine.Vector3() );
				speed = Mathf.Abs( ( float ) b.speed * 1.94384f );
				rpm = Mathf.Clamp01( ( float ) b.rpm );
				thr = Mathf.Clamp( ( float ) b.throttle, -1, 1 );
				hdg = ( ( float ) ( Engine.MathUtils.radToDeg( Math.Atan2( f.x, - f.z ) ) + 360 ) % 360 + 360 ) % 360;
			}

			thrShown = UIKit.Approach( thrShown, thr, 0.09f, dt ); // (the bar's fill, over 90 ms)
			if ( fade > 0.001f ) Render();
		}

		static float Round1( float v, float q ) => Mathf.Round( v * q ) / q;

		void Render()
		{
			float u = UIKit.U;
			if ( mat == null ) mat = new Material( Shader.Find( "Hidden/Tidewater/BoatGauge" ) ) { hideFlags = HideFlags.HideAndDontSave };
			float sd = 128 * u / 120, sc = 104 * u / 100;
			int dSide = Mathf.CeilToInt( ( 120 + 2 * DIAL_M ) * sd ), cSide = Mathf.CeilToInt( ( 100 + 2 * COMPASS_M ) * sc );
			bool remake = dialRT == null || ! dialRT.IsCreated() || dialRT.width != dSide || compassRT == null || compassRT.width != cSide;
			if ( remake )
			{
				Free();
				dialRT = Make( "boat dial", dSide ); compassRT = Make( "boat compass", cSide );
				dialKey = compassKey = int.MinValue;
			}

			int rq = Mathf.RoundToInt( rpm * 1000 ), hq = Mathf.RoundToInt( hdg * 10 );
			if ( rq != dialKey )
			{
				dialKey = rq;
				mat.SetVector( "_Size", new Vector4( dSide, dSide, 0, 0 ) );
				mat.SetVector( "_View", new Vector4( - DIAL_M, sd, 120, 0 ) );
				mat.SetVector( "_Val", new Vector4( rq / 1000f, rq / 1000f > 0.86f ? 1 : 0, 0, 0 ) );
				Graphics.Blit( Texture2D.blackTexture, dialRT, mat, 0 );
			}

			if ( hq != compassKey )
			{
				compassKey = hq;
				mat.SetVector( "_Size", new Vector4( cSide, cSide, 0, 0 ) );
				mat.SetVector( "_View", new Vector4( - COMPASS_M, sc, 100, 0 ) );
				mat.SetVector( "_Val", new Vector4( hq / 10f, 0, 0, 0 ) );
				Graphics.Blit( Texture2D.blackTexture, compassRT, mat, 1 );
			}
		}

		static RenderTexture Make( string name, int side )
		{
			var rt = new RenderTexture( side, side, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB ) { name = name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, antiAliasing = 1 };
			rt.Create();
			return rt;
		}

		void Free()
		{
			if ( dialRT != null ) UnityEngine.Object.DestroyImmediate( dialRT );
			if ( compassRT != null ) UnityEngine.Object.DestroyImmediate( compassRT );
			dialRT = compassRT = null;
		}

		public void Dispose()
		{
			Free();
			if ( mat != null ) UnityEngine.Object.DestroyImmediate( mat );
			mat = null;
		}

		static Color A( Color c, float a ) { c.a *= a; return c; }

		public void OnGUI()
		{
			if ( Event.current.type != EventType.Repaint || fade <= 0.001f || dialRT == null || compassRT == null ) return;
			float u = UIKit.U, e = UIScale.Ease( fade ), a = e;
			var inkC = A( UIKit.INK, a );

			// the text the flex row is sized by
			var mono = UIKit.Style( UIFonts.Mono, 10.5f, TextAnchor.MiddleCenter );
			float chW = mono.CalcSize( new GUIContent( "0" ) ).x, lh = 10.5f * u * 1.4f;
			float thrColW = Mathf.Max( 8 * u, 4.6f * chW );
			float thrColH = 92 * u + 8 * u + lh;
			float contentH = Mathf.Max( thrColH, Mathf.Max( 128 * u, 104 * u ) );
			float w = 16 * u + thrColW + 16 * u + 128 * u + 16 * u + 104 * u + 18 * u, h = contentH + 24 * u;
			var r = new Rect( UIScale.Edge, Screen.height - UIScale.Edge - h + ( 1 - e ) * 14 * u, w, h );
			UIKit.Glass( r, 20 * u, a );

			// the throttle: the bar (fills grow from its middle), the zero line, the percentage
			float x = r.x + 16 * u, y0 = r.y + 12 * u + ( contentH - thrColH ) / 2;
			var bar = new Rect( x + ( thrColW - 8 * u ) / 2, y0, 8 * u, 92 * u );
			UIKit.Rounded( bar, new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.1f * a ), 4 * u );
			UIKit.Rounded( bar, new Color( 170 / 255f, 215 / 255f, 235 / 255f, 0.1f * a ), 4 * u, 1 );
			float half = 46 * u, mid = bar.y + half;
			float pos = Mathf.Max( 0, thrShown ), neg = Mathf.Max( 0, - thrShown );
			if ( pos > 0.002f ) UIKit.VRampPill( new Rect( bar.x, mid - half * pos, bar.width, half * pos ), UIKit.AQUA, UIKit.AQUA.WithAlpha( 0.35f ), 0, a ); // (bottom 35 % -> top full: the ramp's top colour is the far end)
			if ( neg > 0.002f ) UIKit.VRampPill( new Rect( bar.x, mid, bar.width, half * neg ), UIKit.SUN.WithAlpha( 0.35f ), UIKit.SUN, 0, a );
			UIKit.Rounded( new Rect( bar.x, mid - 0.5f, bar.width, 1 ), new Color( 1, 1, 1, 0.55f * a ), 0 );
			int tq = Mathf.RoundToInt( thr * 100 );
			string tt = tq == 0 ? "0%" : ( tq > 0 ? "+" : "−" ) + Mathf.Abs( tq ) + "%";
			mono.normal.textColor = tq < 0 ? A( UIKit.SUN, a ) : A( UIKit.INK2, a );
			GUI.Label( new Rect( x, bar.yMax + 8 * u, thrColW, lh ), tt, mono );

			// the dial
			float dx = x + thrColW + 16 * u, dy = r.y + 12 * u + ( contentH - 128 * u ) / 2, sd = 128 * u / 120;
			DrawRT( dialRT, new Rect( dx - DIAL_M * sd, dy - DIAL_M * sd, dialRT.width, dialRT.height ), a );
			bool red = dialKey / 1000f > 0.86f;
			var rs = UIKit.Style( UIFonts.Mono, 8.5f * 128 / 120, TextAnchor.MiddleCenter );
			rs.normal.textColor = red ? A( UIKit.CORAL, a ) : new Color( 200 / 255f, 222 / 255f, 232 / 255f, 0.6f * a );
			GUI.Label( new Rect( dx + 60 * sd - 40 * u, dy + 105 * sd - 0.33f * 8.5f * sd - 6 * u, 80 * u, 12 * u ), "rpm " + Mathf.RoundToInt( dialKey / 10f ) + "%", rs );

			// the speed and its unit, centred in the dial (the 4 u of bottom padding lifts them)
			var ss = UIKit.Style( UIFonts.MonoMedium, 30, TextAnchor.MiddleCenter );
			ss.normal.textColor = inkC;
			float spdH = 30 * u, uH = lh, blockH = spdH + 4 * u + uH, top = dy + ( 124 * u - blockH ) / 2;
			GUI.Label( new Rect( dx, top, 128 * u, spdH ), speed.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ), ss );
			var us = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.MiddleCenter );
			us.normal.textColor = A( UIKit.INK3, a );
			GUI.Label( new Rect( dx, top + spdH + 4 * u, 128 * u, uH ), "knots", us );

			// the compass
			float cx = dx + 128 * u + 16 * u, cy = r.y + 12 * u + ( contentH - 104 * u ) / 2, sc = 104 * u / 100;
			DrawRT( compassRT, new Rect( cx - COMPASS_M * sc, cy - COMPASS_M * sc, compassRT.width, compassRT.height ), a );
			var ctr = new Vector2( cx + 52 * u, cy + 52 * u );
			float hd = compassKey / 10f;
			var oldM = GUI.matrix;
			var ls = UIKit.Style( UIFonts.InterSemi, 9 * 104 / 100f, TextAnchor.MiddleCenter );
			for ( int i = 0; i < 4; i++ )
			{
				// the card's frame (the letter at its card angle d, the card turned by minus the heading, the letter turned with it)
				float d = i * 90, ang = ( d - 90 - hd ) * Mathf.Deg2Rad;
				var p = ctr + 29 * sc * new Vector2( Mathf.Cos( ang ), Mathf.Sin( ang ) );
				GUI.matrix = oldM;
				GUIUtility.RotateAroundPivot( d - hd, p );
				ls.normal.textColor = i == 0 ? A( UIKit.SUN, a ) : new Color( 230 / 255f, 245 / 255f, 250 / 255f, 0.75f * a );
				GUI.Label( new Rect( p.x - 10 * u, p.y - 7 * u, 20 * u, 14 * u ), "NESW"[ i ].ToString(), ls );
			}

			GUI.matrix = oldM;
			var ns = UIKit.Style( UIFonts.MonoMedium, 15, TextAnchor.MiddleCenter );
			ns.normal.textColor = inkC;
			float numH = 15 * u * 1.1f, cardH = lh, hb = numH + cardH, htop = cy + ( 104 * u - hb ) / 2;
			GUI.Label( new Rect( cx, htop, 104 * u, numH ), ( Mathf.RoundToInt( hd ) % 360 ).ToString( "000" ) + "°", ns );
			var cs = UIKit.Style( UIFonts.InterSemi, 10.5f, TextAnchor.MiddleCenter );
			cs.normal.textColor = A( UIKit.AQUA, a );
			GUI.Label( new Rect( cx, htop + numH, 104 * u, cardH ), CARDINALS[ Mathf.RoundToInt( hd / 45 ) % 8 ], cs );
		}

		static void DrawRT( RenderTexture rt, Rect r, float a )
		{
			var old = GUI.color;
			GUI.color = new Color( 1, 1, 1, a );
			GUI.DrawTexture( r, rt, ScaleMode.StretchToFill, true );
			GUI.color = old;
		}
	}
}
