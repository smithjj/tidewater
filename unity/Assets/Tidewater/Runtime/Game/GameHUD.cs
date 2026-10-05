using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// The fishing game's HUD in the web look (src/game/GameHUD.js and the toast and prompt of src/ui/UI.js, with ui/ui.css's tokens through UIKit):
//   top right    the purse: money, the day and the hour and the sea, the cooler (hold) bar, the fuel while aboard, the trap line
//   top centre   the toasts (Toasts)
//   bottom       the interaction prompt (PromptPill)
//   centre       the inventory (I), the fish stand's offer and the chandlery's shelves (the .gm-panel)
// Not ported: the fish finder's sonar readout (the finder is not ported), the backdrop blur and the settings rail the purse makes way for in the JS.
// The fade and rise of the panels are stepped from Tick (IMGUI wants the same controls in its Layout and Repaint passes).
namespace Tidewater.Game
{
	// UI.toast: a pill with an aqua dot and a life line; a repeated message restarts its timer and bumps; at most four.
	public sealed class Toasts
	{
		sealed class T { public string text; public float born, life, bump = -10, leftAt = -1; }
		readonly List<T> list = new List<T>();
		const float IN = 0.24f, OUT = 0.24f, COLLAPSE = 0.42f;

		public void Add( string text, float seconds )
		{
			if ( string.IsNullOrEmpty( text ) ) return;
			float now = Time.unscaledTime;
			var same = list.FirstOrDefault( t => t.text == text && t.leftAt < 0 );
			if ( same != null ) { same.born = now; same.life = seconds; same.bump = now; return; }
			list.Add( new T { text = text, born = now, life = seconds } );
			while ( list.Count( t => t.leftAt < 0 ) > 4 ) list.First( t => t.leftAt < 0 ).leftAt = now;
		}

		public void OnGUI()
		{
			if ( Event.current.type != EventType.Repaint || list.Count == 0 ) return;
			float u = UIKit.U, now = Time.unscaledTime, w = Screen.width;
			foreach ( var t in list ) if ( t.leftAt < 0 && now - t.born >= t.life ) t.leftAt = now;
			list.RemoveAll( t => t.leftAt >= 0 && now - t.leftAt > 0.36f + 0.06f );

			var st = UIKit.Style( UIFonts.InterMedium, 12.5f, TextAnchor.MiddleLeft );
			float maxW = Mathf.Min( 0.86f * w, 520 * u ), h = 12.5f * u * 1.21f + 16 * u, gap = 8 * u;
			float y = UIScale.Edge + 3 * 12.5f * u;
			foreach ( var t in list )
			{
				float inT = UIScale.Ease( Mathf.Clamp01( ( now - t.born ) / IN ) );
				float leave = t.leftAt < 0 ? 0 : Mathf.Clamp01( ( now - t.leftAt ) / OUT );
				float collapse = t.leftAt < 0 ? 0 : Mathf.Clamp01( ( now - t.leftAt ) / COLLAPSE );
				float alpha = inT * ( 1 - UIScale.Ease( leave ) );
				float dy = t.leftAt < 0 ? ( 1 - inT ) * -8 * u : -6 * u * UIScale.Ease( leave );
				// the text, cut with an ellipsis when it would not fit
				string text = t.text; float room = maxW - ( 13 + 6 + 10 + 16 ) * u;
				if ( st.CalcSize( new GUIContent( text ) ).x > room )
				{
					while ( text.Length > 1 && st.CalcSize( new GUIContent( text + "…" ) ).x > room ) text = text.Substring( 0, text.Length - 1 );
					text += "…";
				}

				float tw = st.CalcSize( new GUIContent( text ) ).x, bw = ( 13 + 6 + 10 + 16 ) * u + tw;
				var r = new Rect( ( w - bw ) / 2, y + dy, bw, h );
				var m = GUI.matrix;
				float bump = Mathf.Clamp01( ( now - t.bump ) / 0.42f );
				float sc = ( t.leftAt < 0 ? 0.98f + 0.02f * inT : 0.98f + 0.02f * ( 1 - leave ) ) * ( bump < 1 ? 1 + 0.035f * Mathf.Sin( Mathf.Min( bump, 0.4f ) / 0.4f * Mathf.PI / 2 ) * ( bump < 0.4f ? 1 : 1 - ( bump - 0.4f ) / 0.6f ) : 1 );
				GUIUtility.ScaleAroundPivot( new Vector2( sc, sc ), r.center );
				UIKit.Glass( r, h / 2, alpha );
				float cy = r.y + h / 2;
				UIKit.Rounded( new Rect( r.x + 13 * u - 4 * u, cy - 7 * u, 14 * u, 14 * u ), new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.18f * alpha ), 7 * u );
				UIKit.Rounded( new Rect( r.x + 13 * u, cy - 3 * u, 6 * u, 6 * u ), new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, alpha ), 3 * u );
				st.normal.textColor = new Color( UIKit.INK.r, UIKit.INK.g, UIKit.INK.b, alpha );
				GUI.Label( new Rect( r.x + ( 13 + 6 + 10 ) * u, r.y, tw + 4, h ), text, st );
				// the life line: aqua at the middle, thinning to nothing at the ends, shrinking toward the middle
				float life = t.leftAt < 0 ? Mathf.Clamp01( ( now - t.born ) / t.life ) : 1;
				float lw = ( r.width - 32 * u ) * ( 1 - life );
				if ( lw > 1 )
				{
					var clear = new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0 ); var full = new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.7f * alpha );
					UIKit.RampPill( new Rect( r.center.x - lw / 2, r.yMax - 1.5f, lw / 2, 1 ), clear, full );
					UIKit.RampPill( new Rect( r.center.x, r.yMax - 1.5f, lw / 2, 1 ), full, clear );
				}

				GUI.matrix = m;
				y += ( h + gap ) * ( 1 - collapse );
			}
		}
	}

	// UI.setPrompt: the key cap and the line, bottom centre. The cap rings twice when the prompt changes.
	public sealed class PromptPill
	{
		float fade, bumpAt = -10;
		string key = "", text = "";

		public void OnGUI( string newKey, string newText )
		{
			if ( Event.current.type != EventType.Repaint ) return;
			float u = UIKit.U, now = Time.unscaledTime, w = Screen.width, h = Screen.height;
			bool on = ! string.IsNullOrEmpty( newKey );
			if ( on && ( newKey != key || ( newText ?? "" ) != text ) ) { key = newKey; text = newText ?? ""; bumpAt = now; }
			fade = UIKit.Approach( fade, on ? 1 : 0, 0.24f, Time.unscaledDeltaTime );
			if ( fade <= 0.001f ) return;
			float a = UIScale.Ease( fade );

			var ks = UIKit.Style( UIFonts.InterSemi, 11.5f, TextAnchor.MiddleCenter ); var ts = UIKit.Style( UIFonts.InterMedium, 14, TextAnchor.MiddleLeft );
			float kw = Mathf.Max( 28 * u, ks.CalcSize( new GUIContent( key ) ).x + 16 * u ), kh = 28 * u;
			float tw = ts.CalcSize( new GUIContent( text ) ).x;
			float bw = 6 * u + kw + 10 * u + tw + 18 * u, bh = kh + 12 * u;
			float bottom = h - Mathf.Max( 72 * u, 0.13f * h );
			var r = new Rect( ( w - bw ) / 2, bottom - bh + ( 1 - a ) * 10 * u, bw, bh );
			UIKit.Glass( r, bh / 2, a );
			var cap = new Rect( r.x + 6 * u, r.y + 6 * u, kw, kh );

			// the ring: 0 → 12u over 1.4 s, twice
			float ring = ( now - bumpAt ) / 1.4f;
			if ( ring >= 0 && ring < 2 )
			{
				float t = UIScale.Ease( ring % 1 ), sp = 12 * u * t;
				UIKit.Rounded( new Rect( cap.x - 2 - sp, cap.y - 2 - sp, cap.width + 4 + 2 * sp, cap.height + 4 + 2 * sp ), new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.65f * ( 1 - t ) * a ), 8 * u + sp + 2, sp );
			}

			UIKit.Rounded( new Rect( cap.x, cap.y + 2, cap.width, cap.height ), new Color( 0, 0, 0, 0.3f * a ), 8 * u + 2 );
			UIKit.Rounded( cap, new Color( 0x8f / 255f, 0xa9 / 255f, 0xb1 / 255f, a ), 8 * u );
			UIKit.VRampPill( new Rect( cap.x + 1, cap.y + 1, cap.width - 2, cap.height - 3 ), new Color( 0xf6 / 255f, 0xfc / 255f, 0xfd / 255f ), new Color( 0xd6 / 255f, 0xe7 / 255f, 0xeb / 255f ), 7 * u, a );
			ks.normal.textColor = new Color( 0x0a / 255f, 0x1a / 255f, 0x22 / 255f, a );
			GUI.Label( new Rect( cap.x, cap.y - 1, cap.width, cap.height ), key, ks );
			ts.normal.textColor = new Color( UIKit.INK.r, UIKit.INK.g, UIKit.INK.b, a );
			GUI.Label( new Rect( cap.xMax + 10 * u, r.y, tw + 4, bh ), text, ts );
		}
	}

	public sealed class GameHUD
	{
		readonly GameHost game;
		float fade, rise;          // the panel's opacity (240 ms) and rise (420 ms)
		int kind;                  // 1 the inventory, 2 a trader's panel (kept while it fades out)
		Vendor vendor;
		Vector2 scroll;
		float lastMoney = -1, bumpAt = -10;

		public GameHUD( GameHost game ) { this.game = game; }

		GameState S => game.state;
		static string Inv( double v ) => v.ToString( "0.##", System.Globalization.CultureInfo.InvariantCulture );
		static string Kg( double v ) => v.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture );
		static string Plain( double v ) => "$" + Inv( v );
		static string Money( double v ) => "$" + ( ( long ) Math.Round( v ) ).ToString( "N0", System.Globalization.CultureInfo.InvariantCulture );
		static Color A( Color c, float a ) { c.a *= a; return c; }

		public void Tick( double dt )
		{
			int want = game.openVendor != null ? 2 : game.inventoryOpen ? 1 : 0;
			if ( want != 0 )
			{
				if ( fade <= 0.001f || kind != want || vendor != game.openVendor ) scroll = Vector2.zero;
				kind = want; vendor = game.openVendor;
			}

			fade = UIKit.Approach( fade, want != 0 ? 1 : 0, 0.24f, ( float ) dt );
			rise = UIKit.Approach( rise, want != 0 ? 1 : 0, 0.42f, ( float ) dt );
			if ( game.state != null && lastMoney >= 0 && lastMoney != ( float ) game.state.money ) bumpAt = Time.unscaledTime;
			if ( game.state != null ) lastMoney = ( float ) game.state.money;
		}

		public void OnGUI()
		{
			if ( S == null ) return;
			Purse();
			if ( fade > 0.001f ) Panel();
		}

		// ---- the purse

		struct Seg { public float w; public Action<float, float, float> draw; } // draw( x, centre y, alpha )

		static Seg Txt( string s, Font font, float fs, Color c )
		{
			var st = UIKit.Style( font, fs, TextAnchor.MiddleLeft );
			float w = st.CalcSize( new GUIContent( s ) ).x;
			return new Seg { w = w, draw = ( x, cy, a ) => { st.normal.textColor = A( c, a ); GUI.Label( new Rect( x, cy - 20 * UIKit.U, w + 4, 40 * UIKit.U ), s, st ); } };
		}

		// .gm-cooler-bar: 64u by 5u
		static Seg Bar( float frac, Color fill )
		{
			float u = UIKit.U;
			return new Seg { w = 64 * u, draw = ( x, cy, a ) =>
			{
				var r = new Rect( x, cy - 2.5f * u, 64 * u, 5 * u );
				UIKit.Rounded( r, A( UIKit.FILL2, a ), 3 * u );
				if ( frac > 0 ) UIKit.Rounded( new Rect( r.x, r.y, Mathf.Max( 5 * u, r.width * Mathf.Clamp01( frac ) ), r.height ), A( fill, a ), 3 * u );
			} };
		}

		static Seg Row( float gap, params Seg[] parts )
		{
			float w = parts.Sum( p => p.w ) + gap * ( parts.Length - 1 );
			return new Seg { w = w, draw = ( x, cy, a ) => { foreach ( var p in parts ) { p.draw( x, cy, a ); x += p.w + gap; } } };
		}

		void Purse()
		{
			if ( ! game.showHud ) return;
			var s = S; var st = s.stats; float u = UIKit.U;
						var segs = new List<Seg>();
			segs.Add( Txt( Money( s.money ), UIFonts.MonoMedium, 14, UIKit.SUN ) );

			// the world clock: the day, then the hour, then what the sea is doing, with a dot before the last two
			var clockParts = new List<Seg> { Txt( $"Day {s.day}", UIFonts.Inter, 12.5f, UIKit.INK3 ), Row( 8 * u, Txt( "·", UIFonts.Inter, 12.5f, UIKit.INK3 ), Txt( GameText.fmtClock( game.clock.hour ), UIFonts.Mono, 12.5f, UIKit.INK ) ) };
			string sea = game.weather != null ? game.weather.name : null;
			if ( ! string.IsNullOrEmpty( sea ) ) clockParts.Add( Row( 8 * u, Txt( "·", UIFonts.Inter, 12.5f, UIKit.INK3 ), Txt( sea, UIFonts.Inter, 12.5f, UIKit.INK2 ) ) );
			segs.Add( Row( 8 * u, clockParts.ToArray() ) );

			// the cooler (the hold, once it is upgraded)
			double kg = s.holdKg;
			segs.Add( Row( 8 * u, Txt( s.upgrades[ "hold" ] > 0 ? "Hold" : "Cooler", UIFonts.Inter, 12.5f, UIKit.INK2 ), Bar( ( float ) ( kg / st.holdKg ), kg > st.holdKg * 0.9 ? UIKit.CORAL : UIKit.AQUA ),
				Txt( $"{kg.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture )} / {Inv( st.holdKg )} kg", UIFonts.Inter, 12.5f, UIKit.INK2 ) ) );

			// the boat's instruments: the fuel while aboard
			var pl = game.Player;
			if ( pl != null && ( pl.mode == "boat" || pl.mode == "deck" ) )
				segs.Add( Row( 8 * u, Txt( "Fuel", UIFonts.Inter, 12.5f, UIKit.INK2 ), Bar( ( float ) ( s.fuelL / st.fuelL ), s.fuelL < st.fuelL * 0.15 ? UIKit.CORAL : UIKit.SUN ),
					Txt( $"{Math.Round( s.fuelL ).ToString( "0", System.Globalization.CultureInfo.InvariantCulture )} L", UIFonts.MonoMedium, 12.5f, UIKit.INK ) ) );

			// the trap line, once there is one
			if ( s.mayTrap || s.sets.Count > 0 )
				segs.Add( Row( 8 * u, Txt( "Traps", UIFonts.Inter, 12.5f, UIKit.INK2 ), Txt( $"{s.sets.Count} set · {s.traps} aboard", UIFonts.MonoMedium, 12.5f, UIKit.INK ) ) );

			float gap = 12 * u, padX = 16 * u, h = 16 * u + 14 * u * 1.21f;
			float w = segs.Sum( q => q.w ) + gap * ( segs.Count - 1 ) + 2 * padX;
			var r = new Rect( Screen.width - UIScale.Edge - w, UIScale.Edge, w, h );
			if ( Event.current.type != EventType.Repaint ) return;
			// is-bump: a swell when the money changes
			var m = GUI.matrix;
			float b = Mathf.Clamp01( ( Time.unscaledTime - bumpAt ) / 0.42f );
			if ( b < 1 ) { float k = b < 0.3f ? Mathf.Sin( b / 0.3f * Mathf.PI / 2 ) : Mathf.Cos( ( b - 0.3f ) / 0.7f * Mathf.PI / 2 ); float sc = 1 + 0.08f * k; GUIUtility.ScaleAroundPivot( new Vector2( sc, sc ), r.center ); }
			UIKit.Glass( r, h / 2 );
			float x = r.x + padX;
			foreach ( var q in segs ) { q.draw( x, r.center.y, 1 ); x += q.w + gap; }
			GUI.matrix = m;
		}

		// ---- the panels

		// .gm-market: the movers as flex items (each stays whole), 12u apart across and 8u down; returns the height
		static float Market( float x, float y, float width, List<string> items, bool draw, float a )
		{
			float u = UIKit.U, cx = 0, cy = 0, lh = 11.5f * u * 1.21f;
			foreach ( var it in items )
			{
				float w = 0; UIKit.Flow( 0, 0, 100000, it, 11.5f, 1.21f, UIKit.INK2, UIKit.INK, false, null, false, 1, out w );
				if ( cx > 0 && cx + w > width + 0.5f ) { cx = 0; cy += lh + 8 * u; }
				if ( draw ) UIKit.Flow( x + cx, y + cy, w + 4, it, 11.5f, 1.21f, UIKit.INK2, UIKit.INK, true, null, false, a );
				cx += w + 12 * u;
			}

			return cy + lh;
		}

		// one fish row's cells, as the stand and the cooler list them
		struct FishRow { public int id; public string name; public bool record; public string cm, kg; public string price, mark; public Color markC; public bool star; }

		FishRow Fish( InventoryFish f )
		{
			var s = S; double m = s.mulFor( f.species );
			var row = new FishRow { id = f.id, name = FishTable.Get( f.species ).name, record = f.record, cm = $"{Inv( Math.Round( f.cm ) )} cm", kg = $"{Kg( f.kg )} kg", price = Plain( s.priceOf( f ) ), star = s.orderMulFor( f ) > 1 };
			if ( m > 1.001 ) { row.mark = "▲"; row.markC = UIKit.AQUA; } else if ( m < 0.999 ) { row.mark = "▼"; row.markC = UIKit.CORAL; }
			return row;
		}

		void Panel()
		{
			float u = UIKit.U, W = Screen.width, H = Screen.height, a = UIScale.Ease( fade );
			bool live = game.openVendor != null || game.inventoryOpen;
			var s = S;
			float pw = 420 * u, padX = 24 * u, padY = 16 * u, inner = pw - 2 * padX;
			bool inv = kind == 1, shop = kind == 2 && vendor != null && vendor.kind == "shop";
			string title, sub;
			if ( inv ) { title = s.upgrades[ "hold" ] > 0 ? "Fish hold" : "Cooler"; sub = $"{s.inventory.Count} fish · {s.holdKg.ToString( "F1", System.Globalization.CultureInfo.InvariantCulture )} of {Inv( s.stats.holdKg )} kg · worth {Plain( s.holdValue )}"; }
			else if ( shop ) { title = vendor.name; sub = $"{vendor.greeting} · You have {Money( s.money )}"; }
			else
			{
				title = vendor != null ? vendor.name : "Fish buyer";
				string g = vendor != null && vendor.greeting != "" ? vendor.greeting : "Let's see what you caught.", idle = vendor != null && vendor.idle != "" ? vendor.idle : "Come back when you've got fish.";
				sub = s.inventory.Count > 0 ? g : idle;
			}

			// the head and the boards above the list
			float h2 = 17 * u * 1.21f, subH = 11.5f * u * 1.21f;
			float head = h2 + 4 * u + subH + 12 * u;
			string order = kind == 2 && ! shop ? GameText.orderBoard( s ) : "";
			var market = kind == 2 && ! shop ? GameText.marketRows( s ) : new List<string>();
			if ( market.Count > 0 ) market.Insert( 0, "<c tone=\"ink3\">Today ·</c>" );
			float orderH = order != "" ? 16 * u + UIKit.Flow( 0, 0, inner, order, 11.5f, 1.21f, UIKit.INK2, UIKit.INK, false ) : 0;
			float marketH = market.Count > 0 ? 16 * u + Market( 0, 0, inner, market, false, 1 ) : 0;
			float footH = 12 * u + 31 * u;

			// the list content, measured then drawn in a scroll view
			float listAvail = Mathf.Max( 80 * u, 0.70f * H - 2 * padY - head - orderH - marketH - footH );
			float contentH = shop ? ShopList( 0, inner - 8 * u, false, 1, false ) : FishList( 0, inner - 8 * u, false, 1, false, inv );
			float listH = Mathf.Min( contentH, listAvail );
			float ph = 2 * padY + head + orderH + marketH + listH + footH;
			var panel = new Rect( ( W - pw ) / 2, ( H - ph ) / 2 + ( 1 - UIScale.Ease( rise ) ) * 0.02f * ph, pw, ph );

			UIKit.Glass( panel, 16 * u, a );
			float x = panel.x + padX, y = panel.y + padY;
			var ts = UIKit.Style( UIFonts.InterSemi, 17, TextAnchor.UpperLeft ); ts.normal.textColor = A( UIKit.INK, a );
			GUI.Label( new Rect( x, y, inner, h2 + 2 ), title, ts ); y += h2 + 4 * u;
			var ss = UIKit.Style( UIFonts.InterMedium, 11.5f, TextAnchor.UpperLeft ); ss.normal.textColor = A( UIKit.INK3, a );
			GUI.Label( new Rect( x, y, inner, subH + 2 ), sub, ss ); y += subH + 12 * u;
			if ( order != "" ) { UIKit.Flow( x, y + 8 * u, inner, order, 11.5f, 1.21f, UIKit.INK2, UIKit.INK, true, null, false, a ); y += orderH; }
			if ( market.Count > 0 ) { Market( x, y + 8 * u, inner, market, true, a ); y += marketH; }

			bool was = GUI.enabled; GUI.enabled = was && live;
			var view = new Rect( x, y, inner, listH );
			scroll = UIKit.BeginScroll( view, scroll, contentH );
			if ( shop ) ShopList( 0, inner - 8 * u, true, a, true ); else FishList( 0, inner - 8 * u, true, a, true, inv );
			UIKit.EndScroll( view, ref scroll, contentH, a );
			y += listH + 12 * u;

			// the foot
			float bh = 31 * u;
			string leave = $"Leave ({game.Input?.label( "interact" ) ?? "E"})", close = $"Close ({game.Input?.label( "cooler" ) ?? "I"})";
			var fs = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft ); fs.normal.textColor = A( UIKit.INK3, a );
			if ( inv )
			{
				GUI.Label( new Rect( x, y, inner - 120 * u, bh ), "Sell at the fish stand by the pier", fs );
				float bw = UIKit.ButtonWidth( close );
				if ( UIKit.Button( new Rect( x + inner - bw, y, bw, bh ), close, true, true, a ) ) game.ToggleInventory( false );
			}
			else if ( shop )
			{
				GUI.Label( new Rect( x, y, inner - 120 * u, bh ), "Upgrades take effect at once", fs );
				float bw = UIKit.ButtonWidth( leave );
				if ( UIKit.Button( new Rect( x + inner - bw, y, bw, bh ), leave, true, true, a ) ) game.CloseStand();
			}
			else
			{
				float bw = UIKit.ButtonWidth( leave );
				if ( UIKit.Button( new Rect( x, y, bw, bh ), leave, true, true, a ) ) game.CloseStand();
				string all = $"Sell all · {Plain( s.holdValue )}"; float aw = UIKit.ButtonWidth( all );
				if ( UIKit.Button( new Rect( x + inner - aw, y, aw, bh ), all, false, s.inventory.Count > 0, a ) ) game.SellAll();
			}

			GUI.enabled = was;
		}

		// the cooler and the stand's fish rows (.gm-row): the name (and "record"), the length, the weight, the price with its marks, and the button
		float FishList( float x, float width, bool draw, float a, bool unused, bool inv )
		{
			float u = UIKit.U, y = 0;
			var s = S;
			var rows = s.inventory.ToList().Select( Fish ).ToList();
			if ( rows.Count == 0 )
			{
				y += 16 * u;
				if ( draw ) { var es = UIKit.Style( UIFonts.InterMedium, 12.5f, TextAnchor.UpperCenter ); es.normal.textColor = A( UIKit.INK3, a ); GUI.Label( new Rect( x, y, width, 20 * u ), inv ? "Nothing yet. Cast from the pier, the beach or the boat." : "Your cooler is empty.", es ); }
				y += 12.5f * u * 1.21f + 16 * u;
			}
			else
			{
				string btn = inv ? "Release" : "Sell";
				var mono = UIKit.Style( UIFonts.Mono, 12.5f ); var monoM = UIKit.Style( UIFonts.MonoMedium, 12.5f );
				float cmW = rows.Max( r => mono.CalcSize( new GUIContent( r.cm ) ).x ), kgW = rows.Max( r => mono.CalcSize( new GUIContent( r.kg ) ).x );
				float valW = rows.Max( r => mono.CalcSize( new GUIContent( r.price ) ).x + ( r.mark != null ? 3 * u + mono.CalcSize( new GUIContent( r.mark ) ).x : 0 ) + ( r.star ? 3 * u + mono.CalcSize( new GUIContent( "★" ) ).x : 0 ) );
				float bw = UIKit.MiniWidth( btn ), gap = 12 * u, rowH = 16 * u + 20 * u;
				foreach ( var r in rows )
				{
					if ( draw )
					{
						float cy = y + rowH / 2 - 0.5f;
						var ns = UIKit.Style( UIFonts.InterMedium, 12.5f, TextAnchor.MiddleLeft ); ns.normal.textColor = A( UIKit.INK, a );
						float nw = ns.CalcSize( new GUIContent( r.name ) ).x;
						GUI.Label( new Rect( x, y, nw + 4, rowH ), r.name, ns );
						if ( r.record ) { var rs = UIKit.Style( UIFonts.InterMedium, 10.4f, TextAnchor.MiddleLeft ); rs.normal.textColor = A( UIKit.AQUA, a ); GUI.Label( new Rect( x + nw + 4 * u, y, 60 * u, rowH ), "record", rs ); }
						float cx = x + width - bw - gap - valW - gap - kgW - gap - cmW;
						var cs = UIKit.Style( UIFonts.Mono, 12.5f, TextAnchor.MiddleLeft ); cs.normal.textColor = A( UIKit.INK3, a ); GUI.Label( new Rect( cx, y, cmW + 4, rowH ), r.cm, cs );
						cx += cmW + gap; cs.normal.textColor = A( UIKit.INK2, a ); GUI.Label( new Rect( cx, y, kgW + 4, rowH ), r.kg, cs );
						cx += kgW + gap; cs.normal.textColor = A( UIKit.SUN, a ); GUI.Label( new Rect( cx, y, valW + 4, rowH ), r.price, cs );
						float px = cx + mono.CalcSize( new GUIContent( r.price ) ).x;
						if ( r.mark != null ) { px += 3 * u; cs.normal.textColor = A( r.markC, a ); GUI.Label( new Rect( px, y, 30, rowH ), r.mark, cs ); px += mono.CalcSize( new GUIContent( r.mark ) ).x; }
						if ( r.star ) { px += 3 * u; cs.normal.textColor = A( UIKit.SUN, a ); GUI.Label( new Rect( px, y, 30, rowH ), "★", cs ); }
						cx += valW + gap;
						if ( UIKit.Mini( new Rect( cx, cy - 10 * u, bw, 20 * u ), btn, a ) ) { if ( inv ) s.release( r.id ); else game.Sell( new[] { r.id } ); }
						UIKit.Rounded( new Rect( x, y + rowH, width, 1 ), A( UIKit.LINE, a ), 0 );
					}

					y += rowH + 1;
				}
			}

			// the fish log under the cooler's list
			if ( inv )
			{
				var lines = s.log.Where( k => FishTable.Has( k.Key ) ).Select( k => $"{FishTable.Get( k.Key ).name}: {k.Value.count} caught, best {Kg( k.Value.bestKg )} kg · {Inv( Math.Round( k.Value.bestCm ?? Math.Round( FishTable.fishLengthCm( k.Key, k.Value.bestKg ) ) ) )} cm" ).ToList();
				if ( lines.Count > 0 )
				{
					y += 12 * u; float lh = 11.5f * u * 1.5f;
					if ( draw )
					{
						var ls = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleLeft ); ls.normal.textColor = A( UIKit.INK3, a );
						var bs = UIKit.Style( UIFonts.InterSemi, 11.5f, TextAnchor.MiddleLeft ); bs.normal.textColor = A( UIKit.INK3, a );
						GUI.Label( new Rect( x, y, width, lh ), "Fish log", bs );
						for ( int i = 0; i < lines.Count; i ++ ) GUI.Label( new Rect( x, y + lh * ( i + 1 ), width, lh ), lines[ i ], ls );
					}

					y += lh * ( lines.Count + 1 );
				}
			}

			return y;
		}

		// the chandlery's shelves (.gm-shop-row): the boats, the diesel, the trap line, then every upgrade track
		struct Shelf { public string name, small, button, have; public bool enabled; public Action click; }

		float ShopList( float x, float width, bool draw, float a, bool unused )
		{
			float u = UIKit.U, y = 0;
			var s = S;
			var shelves = new List<Shelf>();
			foreach ( var b in Gear.BOATS )
			{
				bool own = s.ownsBoat( b.id ); string id = b.id;
				shelves.Add( new Shelf { name = b.name, small = own ? "Moored by the pier" : "Moored by the pier · yours to take out once bought", button = own ? null : Money( b.cost ), enabled = b.cost <= s.money, have = "Yours", click = () => game.BuyBoat( id ) } );
			}

			double missing = s.stats.fuelL - s.fuelL;
			shelves.Add( new Shelf { name = $"Diesel · ${Gear.FUEL_PRICE.ToString( "F2", System.Globalization.CultureInfo.InvariantCulture )} / L", small = $"Tank: {Math.Round( s.fuelL )} of {s.stats.fuelL} L", button = missing > 0.5 ? $"Fill · {Plain( s.refuelCost() )}" : null, enabled = s.money >= Gear.FUEL_PRICE, have = "Full", click = () => game.Refuel() } );
			bool licensed = s.mayTrap; var lic = Gear.nextLevel( s.upgrades, "trapLicence" ); var licTrack = Gear.Track( "trapLicence" );
			bool hasBoat = s.ownsBoat( licTrack.boat );
			shelves.Add( new Shelf { name = $"{licTrack.name}: {( licensed ? "held" : "none" )}", small = licensed ? $"{s.traps} aboard · {s.sets.Count} of {Gear.TRAP_LIMIT} in the water" : $"Set and haul lobster pots (max {Gear.TRAP_LIMIT} in the water)",
				button = ! hasBoat ? null : lic != null ? Plain( lic.cost ) : null, enabled = lic != null && lic.cost <= s.money, have = ! hasBoat ? "Needs the lobster boat" : "Held", click = () => game.Buy( "trapLicence" ) } );
			shelves.Add( new Shelf { name = $"Lobster traps · ${Gear.TRAP_PRICE} each", small = licensed ? $"{s.traps} aboard (max {Gear.TRAP_LIMIT})" : "Licence required",
				button = licensed && s.traps < Gear.TRAP_LIMIT ? $"Buy 1 · ${Gear.TRAP_PRICE}" : null, enabled = s.money >= Gear.TRAP_PRICE, have = licensed ? "Full" : "Licence", click = () => game.BuyTraps( 1 ) } );
			foreach ( var t in Gear.UPGRADES_LIST.Where( t => t.key != "trapLicence" ) )
			{
				var cur = t.levels[ s.upgrades[ t.key ] ]; var next = Gear.nextLevel( s.upgrades, t.key ); string key = t.key;
				bool locked = t.boat != null && ! s.ownsBoat( t.boat );
				shelves.Add( new Shelf { name = $"{t.name}: {( next != null ? next.label : cur.label )}", small = $"Now: {cur.label}", button = locked ? null : next != null ? Plain( next.cost ) : null, enabled = next != null && next.cost <= s.money,
					have = locked ? $"Needs the {Gear.Boat( t.boat ).name.ToLowerInvariant()}" : "Top of the line", click = () => game.Buy( key ) } );
			}

			var hs = UIKit.Style( UIFonts.Inter, 11.5f );
			float colW = 0;
			foreach ( var sh in shelves ) colW = Mathf.Max( colW, sh.button != null ? UIKit.ButtonWidth( sh.button ) : hs.CalcSize( new GUIContent( sh.have ) ).x );
			float rowH = 16 * u + 12.5f * u * 1.21f + 2 * u + 11.5f * u * 1.21f;
			foreach ( var sh in shelves )
			{
				if ( draw )
				{
					var ns = UIKit.Style( UIFonts.InterMedium, 12.5f, TextAnchor.UpperLeft ); ns.normal.textColor = A( UIKit.INK, a );
					GUI.Label( new Rect( x, y + 8 * u, width - colW - 12 * u, 20 * u ), sh.name, ns );
					var sm = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.UpperLeft ); sm.normal.textColor = A( UIKit.INK3, a );
					GUI.Label( new Rect( x, y + 8 * u + 12.5f * u * 1.21f + 2 * u, width - colW - 12 * u, 20 * u ), sh.small, sm );
					if ( sh.button != null )
					{
						float bw = UIKit.ButtonWidth( sh.button );
						if ( UIKit.Button( new Rect( x + width - bw, y + ( rowH - 31 * u ) / 2, bw, 31 * u ), sh.button, false, sh.enabled, a ) ) sh.click();
					}
					else
					{
						var hv = UIKit.Style( UIFonts.Inter, 11.5f, TextAnchor.MiddleRight ); hv.normal.textColor = A( UIKit.INK3, a );
						GUI.Label( new Rect( x + width - colW - 4, y, colW + 4, rowH ), sh.have, hv );
					}

					UIKit.Rounded( new Rect( x, y + rowH, width, 1 ), A( UIKit.LINE, a ), 0 );
				}

				y += rowH + 1;
			}

			return y;
		}
	}
}
