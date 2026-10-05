using System;
using UnityEngine;

// The depth sounder, left and vertically centred while the player is swimming below the surface (UI.js setDepth / _drawDepth, the `.tw-depth` styles; AppUI.update feeds it the
// camera's depth under the water at its position): a 14 m window of sounding tape centred on the current depth (ticks every half metre, longer on the metre, the five metres
// numbered, all fading towards the ends), the surface drawn as a small swell line when it is in the window, a marker at the centre, and the depth to a tenth of a metre. It
// fades and slides in over 420 ms.
namespace Tidewater.Game
{
	public sealed class DepthSounder
	{
		float fade;
		bool on;
		float depth;      // metres below the surface at the camera
		float shown;      // the depth the tape is drawn at (the JS redraws only when it moved by more than 4 mm)

		public bool Visible => fade > 0.001f;

		// every frame (after the player and the camera are updated)
		public void Tick( double dtd, GameHost game )
		{
			var host = game.Host; var p = host != null ? host.player : null;
			bool vis = false;
			if ( p != null && ! host.freeCam && p.mode == "swim" )
			{
				float d = ( float ) ( Core.G.cameraWaterHeight - host.simCamera.position.y );
				vis = d > 0.3f;
				if ( vis ) depth = Mathf.Max( 0, d );
			}

			on = vis;
			fade = UIKit.Approach( fade, on ? 1 : 0, 0.42f, ( float ) dtd );
			if ( on && Mathf.Abs( depth - shown ) > 0.004f ) shown = depth;
		}

		static Color A( Color c, float a ) { c.a *= a; return c; }

		static void Hline( float x0, float x1, float y, Color c )
		{
			UIKit.Rounded( new Rect( x0, y, x1 - x0, 1 ), c, 0 );
		}

		public void OnGUI()
		{
			if ( Event.current.type != EventType.Repaint || fade <= 0.001f ) return;
			float u = UIKit.U, e = UIScale.Ease( fade ), a = e;
			float m = shown;

			// the row the flex layout sizes: the tape, the marker, the reading
			float tapeW = 46 * u, tapeH = 208 * u;
			var numStyle = UIKit.Style( UIFonts.MonoMedium, 26, TextAnchor.MiddleRight );
			float numW = Mathf.Max( 3.4f * 0.6f * 26 * u, numStyle.CalcSize( new GUIContent( m.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ) ) ).x );
			float unitFs = 0.92f * 12.5f, unitMargin = 0.35f * unitFs * u;
			var unitStyle = UIKit.Style( UIFonts.Inter, unitFs, TextAnchor.MiddleLeft );
			float unitW = unitStyle.CalcSize( new GUIContent( "m" ) ).x;
			var labStyle = UIKit.Style( UIFonts.Inter, 10.5f, TextAnchor.UpperLeft );
			float labW = labStyle.CalcSize( new GUIContent( "Depth" ) ).x, lh = 10.5f * u * 1.4f;
			float readW = Mathf.Max( numW + unitMargin + unitW, labW ), readH = 26 * u + 2 * u + lh;
			float markW = 4 * u + 10 * u + 10 * u;
			float w = 8 * u + tapeW + markW + readW + 18 * u, h = tapeH + 24 * u;
			var r = new Rect( UIScale.Edge + ( e - 1 ) * 10 * u, ( Screen.height - h ) / 2, w, h );
			UIKit.Glass( r, 18 * u, a );

			// the tape: the canvas, 14 m across its height
			float tx = r.x + 8 * u, ty = r.y + 12 * u;
			float span = 14, ppm = tapeH / span, cy = tapeH / 2;
			Func<float, float, float> fadeAt = ( y, p ) => Mathf.Pow( 1 - Mathf.Min( 1, Mathf.Abs( y - cy ) / cy ), p );
			var ts = UIKit.Style( UIFonts.MonoMedium, Mathf.Max( 8, 208 / 21f ), TextAnchor.MiddleRight );
			int d0 = Mathf.Max( 0, Mathf.FloorToInt( m - span / 2 ) ), d1 = Mathf.CeilToInt( m + span / 2 );
			for ( float d = d0; d <= d1; d += 0.5f )
			{
				float y = Mathf.Round( cy + ( d - m ) * ppm ) + 0.5f;
				float af = fadeAt( y, 1.6f );
				if ( af <= 0.01f ) continue;
				bool whole = Mathf.Approximately( d % 1, 0 ), major = Mathf.Approximately( d % 5, 0 );
				float len = major ? tapeW * 0.42f : whole ? tapeW * 0.22f : tapeW * 0.11f;
				float py = Mathf.Floor( ty + y - 0.5f );
				Hline( tx + tapeW - len, tx + tapeW, py, new Color( 190 / 255f, 238 / 255f, 244 / 255f, ( major ? 0.9f : whole ? 0.5f : 0.28f ) * af * a ) );
				if ( major )
				{
					ts.normal.textColor = new Color( 222 / 255f, 246 / 255f, 250 / 255f, 0.85f * af * a );
					GUI.Label( new Rect( tx, py - 8 * u, tapeW - len - 4 * u, 16 * u ), ( ( int ) d ).ToString(), ts );
				}
			}

			// the surface: a small swell line, when it is in the window
			float ys = cy - m * ppm;
			if ( ys >= -4 )
			{
				var c = new Color( 95 / 255f, 227 / 255f, 212 / 255f, 0.95f * fadeAt( ys, 1.2f ) * a );
				for ( float x = 0; x < tapeW; x += 2 )
				{
					float y = ys + Mathf.Sin( ( x + 1 ) * 0.45f ) * 1.4f;
					UIKit.Rounded( new Rect( tx + x, ty + y - 0.75f, 2.5f, 1.5f ), c, 0 );
				}
			}

			// the marker at the centre (a 10 x 2 aqua bar with its glow), and the reading beside it
			float mx = tx + tapeW + 4 * u, my = r.center.y;
			UIKit.Rounded( new Rect( mx - 3, my - 4, 10 * u + 6, 2 + 8 ), new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.18f * a ), 4 );
			UIKit.Rounded( new Rect( mx - 1.5f, my - 2.5f, 10 * u + 3, 2 + 5 ), new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.3f * a ), 3 );
			UIKit.Rounded( new Rect( mx, my - 1, 10 * u, 2 ), A( UIKit.AQUA, a ), 2 );

			float rx = mx + 10 * u + 10 * u, ry = r.center.y - readH / 2;
			numStyle.normal.textColor = A( UIKit.INK, a );
			GUI.Label( new Rect( rx, ry, numW, 26 * u ), m.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture ), numStyle );
			// the unit shares the number's baseline
			float baseline = ry + 13 * u + 0.35f * 26 * u;
			unitStyle.normal.textColor = A( UIKit.INK3, a );
			GUI.Label( new Rect( rx + numW + unitMargin, baseline - 0.35f * unitFs * u - 10 * u, unitW + 4, 20 * u ), "m", unitStyle );
			labStyle.normal.textColor = A( UIKit.INK3, a );
			GUI.Label( new Rect( rx, ry + 26 * u + 2 * u, labW + 4, lh ), "Depth", labStyle );
		}
	}
}
