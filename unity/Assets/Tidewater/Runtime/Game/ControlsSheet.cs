using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Core;
using UnityEngine;

// The F1 controls sheet (UI.js _buildHelp, ui.css .tw-help): every action that has a help line in the binding table, by group, with the key (or pad glyph) the device in hand
// uses for it, a "How to play" line, and a button that replays the guide. Generated from Bindings.ACTIONS, so a rebind cannot leave it naming a dead key.
// Not ported: the mouse icon in the key cap (it says "Mouse"), the backdrop blur.
namespace Tidewater.Game
{
	public sealed class ControlsSheet
	{
		readonly GameHost game;
		float fade, rise;
		Vector2 scroll;
		public bool open { get; private set; }

		static readonly string[] GROUPS = { "Movement", "Fishing", "Interact", "Interface" };
		const string HOW = "<b>How to play:</b> catch fish, sell them to Joe at the fish stand by the pier, and buy upgrades from Marta at the chandlery by the boathouse. Both are on the map (lower right).";

		public ControlsSheet( GameHost game ) { this.game = game; }

		public void Toggle( bool? force = null )
		{
			bool want = force ?? ! open;
			if ( want == open ) return;
			open = want;
			scroll = Vector2.zero;
			if ( open ) { if ( game.Input != null ) game.Input.ReleaseLock(); }
			game.SyncMenu();
		}

		public void Tick( double dt )
		{
			fade = UIKit.Approach( fade, open ? 1 : 0, 0.24f, ( float ) dt );
			rise = UIKit.Approach( rise, open ? 1 : 0, 0.42f, ( float ) dt );
		}

		bool Pad => game.Input != null && game.Input.device == Device.pad;

		struct Row { public string keys; public bool wasd; public string text, small; }

		List<Row> Rows( string group )
		{
			var rows = new List<Row>();
			var input = game.Input;
			bool pad = Pad;
			foreach ( var a in Bindings.ACTIONS )
			{
				if ( a.group != group || string.IsNullOrEmpty( a.help ) ) continue;
				if ( a.id == "forward" || a.id == "strafe" ) continue; // the movement pair is one row
				if ( a.id == "look" ) { rows.Add( new Row { keys = pad ? ( input.label( "look" ) is string l && l != "" ? l : "RS" ) : "Mouse", text = "Look around", small = pad ? "Right stick" : "Click to capture" } ); continue; }
				string help = a.help, small = null;
				int i = help.IndexOf( "<small>", StringComparison.Ordinal );
				if ( i >= 0 ) { small = help.Substring( i + 7 ).Replace( "</small>", "" ); help = help.Substring( 0, i ); }
				string key = input != null ? input.label( a.id ) : null;
				if ( string.IsNullOrEmpty( key ) ) key = a.kb.Length > 0 ? Bindings.keyGlyph( a.kb[ 0 ].v ) : a.mouse.Length > 0 ? a.mouse[ 0 ].v : a.pad.Length > 0 ? Bindings.padGlyph( a.pad[ 0 ].v ) : "—";
				rows.Add( new Row { keys = key, text = help, small = small } );
			}

			if ( group == "Movement" )
				rows.Insert( 0, pad ? new Row { keys = input.label( "forward" ) is string f && f != "" ? f : "LS", text = "Move", small = "Left stick" } : new Row { wasd = true, text = "Move", small = "W A S D" } );
			return rows;
		}

		// the keys cell: its width and height (a wasd cluster is 3 caps wide and 2 high)
		Vector2 KeysSize( Row r )
		{
			float u = UIKit.U;
			if ( r.wasd ) return new Vector2( Mathf.Max( 68 * u, 3 * 20 * u + 2 * 2 * u ), 42 * u );
			return new Vector2( Mathf.Max( 68 * u, UIKit.KbdSize( r.keys ).width ), 20 * u );
		}

		void DrawKeys( Row r, float x, float cy, float a )
		{
			float u = UIKit.U;
			if ( r.wasd )
			{
				float k = 20 * u, g = 2 * u, y0 = cy - 21 * u;
				UIKit.Kbd( new Rect( x + k + g, y0, k, k ), "W", a );
				UIKit.Kbd( new Rect( x, y0 + k + g, k, k ), "A", a );
				UIKit.Kbd( new Rect( x + k + g, y0 + k + g, k, k ), "S", a );
				UIKit.Kbd( new Rect( x + 2 * ( k + g ), y0 + k + g, k, k ), "D", a );
				return;
			}

			var sz = UIKit.KbdSize( r.keys );
			UIKit.Kbd( new Rect( x, cy - sz.height / 2, sz.width, sz.height ), r.keys, a );
		}

		float TextH( Row r, float w )
		{
			float u = UIKit.U;
			float h = UIKit.Flow( 0, 0, w, r.text, 12.5f, 1.3f, UIKit.INK2, UIKit.INK, false );
			if ( ! string.IsNullOrEmpty( r.small ) ) h += 1 * u + UIKit.Flow( 0, 0, w, r.small, 10.5f, 1.21f, UIKit.INK3, UIKit.INK3, false );
			return h;
		}

		public void OnGUI()
		{
			if ( fade <= 0.001f ) return;
			float u = UIKit.U, W = Screen.width, H = Screen.height, a = UIScale.Ease( fade );
			var ev = Event.current;

			// the scrim: a vignette over the world
			if ( ev.type == EventType.Repaint ) UIKit.Scrim( new Rect( 0, 0, W, H ), a );

			float pad = 24 * u, cardW = Mathf.Min( W - 2 * pad, 780 * u ), padX = 24 * u, padTop = 22 * u, padBot = 24 * u;
			float inner = cardW - 2 * padX;
			int cols = W <= 560 ? 1 : W <= 900 ? 2 : 3;
			float gapX = 28 * u, colW = ( inner - ( cols - 1 ) * gapX ) / cols;

			// the sections, measured: each is a heading and its rows
			var secs = GROUPS.Select( g => ( name: g, rows: Rows( g ) ) ).ToList();
			float headH = 11.5f * u * 1.21f + 8 * u;
			var secH = new List<float>();
			var rowH = new List<List<float>>();
			foreach ( var s in secs )
			{
				var hs = new List<float>(); float h = headH;
				foreach ( var r in s.rows )
				{
					var ks = KeysSize( r );
					float rh = Mathf.Max( 40 * u, 10 * u + Mathf.Max( ks.y, TextH( r, colW - ks.x - 12 * u ) ) ) + 1;
					hs.Add( rh ); h += rh;
				}

				rowH.Add( hs ); secH.Add( h );
			}

			// the grid: rows of `cols` sections, each row as tall as its tallest, 8u apart
			var gridRows = new List<float>();
			for ( int i = 0; i < secs.Count; i += cols ) gridRows.Add( secs.Skip( i ).Take( cols ).Select( ( s, j ) => secH[ i + j ] ).Max() );
			float gridH = gridRows.Sum() + 8 * u * ( gridRows.Count - 1 );
			float footH = 16 * u + 1 + 16 * u;
			float btnW = UIKit.ButtonWidth( "Replay the guide" ), btnH = 31 * u;
			bool stackFoot = W <= 640;
			float howW = stackFoot ? inner : inner - btnW - 16 * u;
			float howH = UIKit.Flow( 0, 0, howW, HOW, 11.5f, 1.5f, UIKit.INK2, UIKit.INK, false );
			footH += stackFoot ? howH + 16 * u + btnH : Mathf.Max( howH, btnH );

			float headBlock = 20 * u * 1.21f + 4 * u + 12.5f * u * 1.21f + 18 * u;
			float bodyH = gridH + footH;
			float maxH = H - 2 * pad;
			float ch = Mathf.Min( padTop + headBlock + bodyH + padBot, maxH );
			var card = new Rect( ( W - cardW ) / 2, ( H - ch ) / 2 + ( 1 - UIScale.Ease( rise ) ) * 10 * u, cardW, ch );
			bool was = GUI.enabled; GUI.enabled = was && open;

			// a click on the scrim closes it
			if ( open && ev.type == EventType.MouseDown && ! card.Contains( ev.mousePosition ) ) { ev.Use(); Toggle( false ); GUI.enabled = was; return; }

			UIKit.Glass( card, 18 * u, a );
			float x = card.x + padX, y = card.y + padTop;
			var t = UIKit.Style( UIFonts.InterSemi, 20, TextAnchor.UpperLeft ); t.normal.textColor = UIKit.INK.WithAlpha( a );
			GUI.Label( new Rect( x, y, inner, 26 * u ), "Controls", t );
			var p = UIKit.Style( UIFonts.Inter, 12.5f, TextAnchor.UpperLeft ); p.normal.textColor = UIKit.INK3.WithAlpha( a );
			GUI.Label( new Rect( x, y + 20 * u * 1.21f + 4 * u, inner, 20 * u ), Pad ? "Controller, or keyboard and mouse." : "Click the view to capture the mouse. Esc releases it.", p );

			// the close button: a 30u icon button with a cross
			var cb = new Rect( card.xMax - padX - 30 * u + 4 * u, y - 4 * u, 30 * u, 30 * u );
			bool hot = cb.Contains( ev.mousePosition ) && open;
			if ( hot ) UIKit.Rounded( cb, UIKit.FILL2.WithAlpha( a ), 8 * u );
			var xs = UIKit.Style( UIFonts.Inter, 18, TextAnchor.MiddleCenter ); xs.normal.textColor = ( hot ? UIKit.INK : UIKit.INK3 ).WithAlpha( a );
			GUI.Label( cb, "×", xs );
			if ( GUI.Button( cb, GUIContent.none, GUIStyle.none ) ) Toggle( false );

			y += headBlock;
			var view = new Rect( x - 2, y, inner + 4, card.yMax - padBot - y );
			scroll = UIKit.BeginScroll( new Rect( view.x, view.y, view.width + 8 * u, view.height ), scroll, bodyH );
			float gy = 0;
			for ( int gi = 0, ri = 0; gi < secs.Count; gi += cols, ri ++ )
			{
				for ( int j = 0; j < cols && gi + j < secs.Count; j ++ )
				{
					var s = secs[ gi + j ];
					float sx = 2 + j * ( colW + gapX ), sy = gy;
					var hs = UIKit.Style( UIFonts.InterSemi, 11.5f, TextAnchor.UpperLeft ); hs.normal.textColor = UIKit.AQUA.WithAlpha( a );
					GUI.Label( new Rect( sx, sy, colW, headH ), s.name, hs ); sy += headH;
					for ( int k = 0; k < s.rows.Count; k ++ )
					{
						var r = s.rows[ k ]; float rh = rowH[ gi + j ][ k ];
						UIKit.Rounded( new Rect( sx, sy, colW, 1 ), UIKit.LINE.WithAlpha( a ), 0 );
						var ks = KeysSize( r ); float cy = sy + 1 + ( rh - 1 ) / 2;
						DrawKeys( r, sx, cy, a );
						float tx = sx + ks.x + 12 * u, tw = colW - ks.x - 12 * u, th = TextH( r, tw );
						float ty = cy - th / 2;
						ty += UIKit.Flow( tx, ty, tw, r.text, 12.5f, 1.3f, UIKit.INK2, UIKit.INK, true, null, false, a );
						if ( ! string.IsNullOrEmpty( r.small ) ) UIKit.Flow( tx, ty + 1 * u, tw, r.small, 10.5f, 1.21f, UIKit.INK3, UIKit.INK3, true, null, false, a );
						sy += rh;
					}
				}

				gy += gridRows[ ri ] + 8 * u;
			}

			// the foot: how to play, and the replay button
			float fy = gridH + 16 * u;
			UIKit.Rounded( new Rect( 2, fy, inner, 1 ), UIKit.LINE.WithAlpha( a ), 0 );
			fy += 1 + 16 * u;
			UIKit.Flow( 2, fy, howW, HOW, 11.5f, 1.5f, UIKit.INK2, UIKit.INK, true, null, false, a );
			var rb = stackFoot ? new Rect( 2, fy + howH + 16 * u, btnW, btnH ) : new Rect( 2 + inner - btnW, fy + ( Mathf.Max( howH, btnH ) - btnH ) / 2, btnW, btnH );
			if ( UIKit.Button( rb, "Replay the guide", true, true, a ) ) { Toggle( false ); if ( game.guide != null ) game.guide.Replay(); }
			UIKit.EndScroll( new Rect( view.x, view.y, view.width + 8 * u, view.height ), ref scroll, bodyH, a );
			GUI.enabled = was;
		}
	}
}
