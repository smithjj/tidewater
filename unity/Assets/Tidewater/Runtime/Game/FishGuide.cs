using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Port of src/game/FishGuide.js, the fish guide (J): every species in a list, and the one you pick on the right with only what you have worked out so far (Codex decides what that is: it
// grows with catches and with fish sold). The portrait is FishPortrait's cached studio thumbnail, a blacked-out silhouette until you have caught one. The map shows where you caught it,
// over an estimate of its water that sharpens as you learn the fish (the JS canvas is rebuilt here as a 320 x 320 texture: the minimap's island, the estimate, your catches).
// Drawn in IMGUI with the CSS of .gm-codex / .gm-gi / .gm-gd-* (UIKit); the glass blur and the letter spacing of the unknown names and the pips are not ported.
namespace Tidewater.Game
{
	public sealed class FishGuide
	{
		const int GRID = 96;     // cells across the habitat estimate
		const int MAP_PX = 320;

		readonly GameHost game;
		readonly Func<double, double, double> heightAt; // the terrain, for the estimate
		readonly Dictionary<string, float[]> grids = new Dictionary<string, float[]>(); // species + box -> the habitat estimate grid
		public bool open { get; private set; }
		public string selected = FishTable.FISH_IDS[ 0 ];
		float fade;                 // 0..1: opacity (240 ms) and rise (420 ms)
		float rise;
		Vector2 listScroll, detailScroll;
		Texture2D mapTex; string mapKey;
		int rev;                    // bumped when the log or the order changes (the map and text are rebuilt)
		bool scrollToSel;           // bring the selected species into view on the next draw (scrollIntoView)

		public FishGuide( GameHost game, Func<double, double, double> heightAt ) { this.game = game; this.heightAt = heightAt; }

		public void Dispose()
		{
			if ( mapTex != null ) UnityEngine.Object.DestroyImmediate( mapTex );
			mapTex = null;
		}

		public void Toggle( bool? force = null )
		{
			bool on = force ?? ! open;
			if ( on == open ) return;
			open = on;
			if ( on )
			{
				game.CloseStand();
				game.ToggleInventory( false );
				// start on something you know: the species you caught last, else the first you have
				var log = game.state.log; var last = game.state.lastCatch;
				if ( last != null && log.ContainsKey( last.species ) ) selected = last.species;
				else if ( ! ( log.TryGetValue( selected, out var e ) && e.count > 0 ) ) selected = FishTable.FISH_IDS.FirstOrDefault( id => log.TryGetValue( id, out var l ) && l.count > 0 ) ?? FishTable.FISH_IDS[ 0 ];
				if ( fade <= 0 ) rise = 0;
				detailScroll = Vector2.zero; scrollToSel = true;
				rev ++;
				game.GuideOpened( true );
			}
			else game.GuideOpened( false );
		}

		public void Select( string id )
		{
			if ( ! FishTable.Has( id ) || id == selected ) return;
			selected = id; detailScroll = Vector2.zero; rev ++;
		}

		// the log or the order changed while it is open
		public void Refresh() { rev ++; }

		public void Tick( double dt )
		{
			float step = ( float ) dt;
			fade = Mathf.Clamp01( fade + ( open ? 1 : - 1 ) * step / 0.24f );
			if ( open ) rise = Mathf.Min( 1, rise + step / 0.42f );
			// the thumbnail is drawn here, not in OnGUI (a camera render must not run inside an IMGUI repaint)
			if ( open ) game.fishing?.card?.portrait?.Thumb( selected );
		}

		// ---- the map

		Texture2D Map( Knowledge k )
		{
			string key = k.id + ":" + rev;
			if ( mapTex != null && mapKey == key ) return mapTex;
			mapKey = key;
			if ( mapTex == null ) mapTex = new Texture2D( MAP_PX, MAP_PX, TextureFormat.RGBA32, false, false ) { name = "fish-guide-map", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
			var mm = game.minimap.model;
			var box = Codex.mapBox( k.catches, Minimap.EXT, Minimap.X0, Minimap.Z0 );
			float[] shown = null; bool smooth = k.level >= Codex.MAX_LEVEL;
			var f = FishTable.Get( k.id );
			if ( k.mapBlock > 0 && f.habitat != null && f.habitat.Length > 0 )
			{
				string gkey = $"{k.id}:{Math.Round( box.x0 )}:{Math.Round( box.z0 )}:{Math.Round( box.size )}";
				if ( ! grids.TryGetValue( gkey, out var grid ) ) grids[ gkey ] = grid = Codex.habitatGrid( k.id, box, GRID, ( x, z ) => HabitatAt( x, z ) );
				shown = Codex.coarsen( grid, GRID, k.mapBlock );
			}

			Paint( mapTex, mm, box, shown, smooth, k.catches );
			return mapTex;
		}

		// the habitat weights at a point, or null on land (the estimate needs the terrain)
		IReadOnlyDictionary<string, double> HabitatAt( double x, double z )
		{
			double depth = - heightAt( x, z );
			if ( depth < 0.25 ) return null;
			return FishingGame.HabitatAtPoint( x, z, depth ).ToDictionary( p => p.Key, p => p.Value );
		}

		// FishGuide._drawMap: the sea colour, the island's bake over the box, the estimate, the catches and the ring of the biggest
		static void Paint( Texture2D tex, Minimap mm, MapBox box, float[] shown, bool smooth, List<CatchRecord> catches )
		{
			const int W = MAP_PX, N = Minimap.N;
			var px = new Color32[ W * W ];
			var rgb = new float[ W * W * 3 ];
			// '#0b2c48', then the island's canvas (bilinear, 2 x 2 taps where it is shrunk)
			double ppm = Minimap.PPM, sx = ( box.x0 - Minimap.X0 ) * ppm, sy = ( box.z0 - Minimap.Z0 ) * ppm, sw = box.size * ppm;
			bool island = mm != null && mm.done;
			int taps = sw > W * 1.2 ? 2 : 1;
			for ( int y = 0; y < W; y ++ ) for ( int x = 0; x < W; x ++ )
			{
				float r = 0x0b, g = 0x2c, b = 0x48;
				if ( island )
				{
					float ar = 0, ag = 0, ab = 0, aa = 0;
					for ( int ty = 0; ty < taps; ty ++ ) for ( int tx = 0; tx < taps; tx ++ )
					{
						double u = sx + ( x + ( tx + 0.5 ) / taps ) / W * sw - 0.5, v = sy + ( y + ( ty + 0.5 ) / taps ) / W * sw - 0.5;
						int x0 = ( int ) Math.Floor( u ), y0 = ( int ) Math.Floor( v ); float fx = ( float ) ( u - x0 ), fy = ( float ) ( v - y0 );
						for ( int c = 0; c < 4; c ++ )
						{
							int xi = Math.Min( N - 1, Math.Max( 0, x0 + ( c & 1 ) ) ), yi = Math.Min( N - 1, Math.Max( 0, y0 + ( c >> 1 ) ) );
							float wgt = ( ( c & 1 ) == 1 ? fx : 1 - fx ) * ( ( c >> 1 ) == 1 ? fy : 1 - fy );
							int o = ( yi * N + xi ) * 4;
							float al = mm.rgba[ o + 3 ] / 255f;
							ar += mm.rgba[ o ] * al * wgt; ag += mm.rgba[ o + 1 ] * al * wgt; ab += mm.rgba[ o + 2 ] * al * wgt; aa += al * wgt;
						}
					}

					float inv = 1f / ( taps * taps ); aa *= inv;
					r = ar * inv + r * ( 1 - aa ); g = ag * inv + g * ( 1 - aa ); b = ab * inv + b * ( 1 - aa );
				}

				// the habitat estimate: (80, 235, 220) at alpha 170 * min( 1, v * 1.2 ), blocky until it is known well (nearest), smooth at the top level
				if ( shown != null )
				{
					float cell = ( x + 0.5f ) / W * GRID, cellY = ( y + 0.5f ) / W * GRID, val;
					if ( smooth )
					{
						float cx = cell - 0.5f, cy = cellY - 0.5f; int i0 = ( int ) Mathf.Floor( cx ), j0 = ( int ) Mathf.Floor( cy ); float fx = cx - i0, fy = cy - j0;
						float S( int i, int j ) => Mathf.Min( 1, shown[ Mathf.Clamp( j, 0, GRID - 1 ) * GRID + Mathf.Clamp( i, 0, GRID - 1 ) ] * 1.2f );
						val = Mathf.Lerp( Mathf.Lerp( S( i0, j0 ), S( i0 + 1, j0 ), fx ), Mathf.Lerp( S( i0, j0 + 1 ), S( i0 + 1, j0 + 1 ), fx ), fy );
					}
					else val = Mathf.Min( 1, shown[ Mathf.Min( GRID - 1, ( int ) cellY ) * GRID + Mathf.Min( GRID - 1, ( int ) cell ) ] * 1.2f );
					float al = Mathf.Round( 170 * val ) / 255f;
					r = r * ( 1 - al ) + 80 * al; g = g * ( 1 - al ) + 235 * al; b = b * ( 1 - al ) + 220 * al;
				}

				int i = ( y * W + x ) * 3; rgb[ i ] = r; rgb[ i + 1 ] = g; rgb[ i + 2 ] = b;
			}

			// your catches: white dots with a dark rim, and the ring of the biggest
			CatchRecord best = null;
			foreach ( var c in catches ) if ( c.x != null && c.z != null && ( best == null || c.kg > best.kg ) ) best = c;
			void Disc( double cx, double cy, double rad, float cr, float cg, float cb, float ca ) => Blend( rgb, W, cx, cy, rad, 0, cr, cg, cb, ca );
			void Ring( double cx, double cy, double rad, double lw, float cr, float cg, float cb, float ca ) => Blend( rgb, W, cx, cy, rad, lw, cr, cg, cb, ca );
			foreach ( var c in catches )
			{
				if ( c.x == null || c.z == null ) continue;
				double x = ( c.x.Value - box.x0 ) / box.size * W, y = ( c.z.Value - box.z0 ) / box.size * W;
				Disc( x, y, 4, 255, 255, 255, 0.92f );
				Ring( x, y, 4, 1.5, 8, 20, 30, 0.8f );
			}

			if ( best != null ) Ring( ( best.x.Value - box.x0 ) / box.size * W, ( best.z.Value - box.z0 ) / box.size * W, 8, 2.5, 0xf2, 0xc1, 0x4e, 1f );

			// image rows run top down, a texture's bottom up
			for ( int y = 0; y < W; y ++ ) for ( int x = 0; x < W; x ++ )
			{
				int i = ( y * W + x ) * 3;
				px[ ( W - 1 - y ) * W + x ] = new Color32( ( byte ) Mathf.Clamp( Mathf.Round( rgb[ i ] ), 0, 255 ), ( byte ) Mathf.Clamp( Mathf.Round( rgb[ i + 1 ] ), 0, 255 ), ( byte ) Mathf.Clamp( Mathf.Round( rgb[ i + 2 ] ), 0, 255 ), 255 );
			}

			tex.SetPixels32( px );
			tex.Apply( false );
		}

		// a disc (lw 0) or a ring of width lw about radius rad, anti-aliased by distance
		static void Blend( float[] rgb, int W, double cx, double cy, double rad, double lw, float r, float g, float b, float a )
		{
			double ext = rad + lw / 2 + 1;
			for ( int y = Math.Max( 0, ( int ) ( cy - ext ) ); y <= Math.Min( W - 1, ( int ) ( cy + ext ) ); y ++ )
				for ( int x = Math.Max( 0, ( int ) ( cx - ext ) ); x <= Math.Min( W - 1, ( int ) ( cx + ext ) ); x ++ )
				{
					double d = Math.Sqrt( ( x + 0.5 - cx ) * ( x + 0.5 - cx ) + ( y + 0.5 - cy ) * ( y + 0.5 - cy ) );
					double cov = lw <= 0 ? rad + 0.5 - d : Math.Min( d - ( rad - lw / 2 ), ( rad + lw / 2 ) - d ) + 0.5;
					cov = Math.Max( 0, Math.Min( 1, cov ) ) * a;
					if ( cov <= 0 ) continue;
					int i = ( y * W + x ) * 3;
					rgb[ i ] = ( float ) ( rgb[ i ] * ( 1 - cov ) + r * cov ); rgb[ i + 1 ] = ( float ) ( rgb[ i + 1 ] * ( 1 - cov ) + g * cov ); rgb[ i + 2 ] = ( float ) ( rgb[ i + 2 ] * ( 1 - cov ) + b * cov );
				}
		}

		// ---- drawing

		float a = 1; // the panel's opacity this frame
		Color C( Color c ) { c.a *= a; return c; }
		static string Pips( int level ) => new string( '●', level );
		static string Clock( double hours ) => $"{( ( int ) Math.Floor( hours ) % 24 ):00}:{( int ) Math.Floor( ( hours - Math.Floor( hours ) ) * 60 ):00}";

		void Text( Rect r, string s, Font font, float fs, Color c, TextAnchor al = TextAnchor.UpperLeft, FontStyle fst = FontStyle.Normal )
		{
			var st = UIKit.Style( font, fs, al ); st.fontStyle = fst; st.normal.textColor = C( c ); GUI.Label( r, s, st ); st.fontStyle = FontStyle.Normal;
		}

		public void OnGUI()
		{
			if ( fade <= 0.001f ) return;
			a = UIScale.Ease( fade );
			float u = UIKit.U, W = Screen.width, H = Screen.height;
			var s = game.state; var order = s.todaysOrder;
			var ids = FishTable.FISH_IDS;
			float pw = Mathf.Min( 860 * u, 0.94f * W ), padX = 24 * u, padY = 16 * u;
			float listW = 210 * u, gapC = 16 * u;
			float itemH = 12.5f * u * 1.2f + 16 * u + 2, itemGap = 2 * u;
			float listH = ids.Length * ( itemH + itemGap );
			float headH = 26 * u, footH = 12 * u + 12.5f * u * 1.2f + 16 * u;
			float detailW = pw - 2 * padX - listW - gapC - 8 * u;
			var k = Codex.knowledge( selected, s.log.TryGetValue( selected, out var le ) ? le : null );
			float detailH = Detail( 0, 0, detailW, k, order, false );
			float bodyH = Mathf.Min( Mathf.Max( listH, detailH ), 0.82f * H - 2 * padY - headH - footH );
			float ph = 2 * padY + headH + bodyH + footH;
			float py = ( H - ph ) / 2 + ( 1 - UIScale.Ease( rise ) ) * 0.02f * ph;
			var panel = new Rect( ( W - pw ) / 2, py, pw, ph );
			UIKit.Glass( panel, 16 * u, a );

			float x = panel.x + padX, y = panel.y + padY;
			// the head
			int known = ids.Count( id => s.log.TryGetValue( id, out var l ) && l.count > 0 );
			Text( new Rect( x, y, 300, 24 * u ), "Fish guide", UIFonts.InterSemi, 17, UIKit.INK );
			Text( new Rect( x, y + 4 * u, pw - 2 * padX, 16 * u ), $"{known} of {ids.Length} species caught", UIFonts.Inter, 11.5f, UIKit.INK3, TextAnchor.UpperRight );
			y += headH;

			// the list
			var listR = new Rect( x, y, listW, bodyH );
			if ( scrollToSel && Event.current.type == EventType.Repaint )
			{
				scrollToSel = false;
				float top = Array.IndexOf( ids, selected ) * ( itemH + itemGap );
				if ( top < listScroll.y ) listScroll.y = top; else if ( top + itemH > listScroll.y + bodyH ) listScroll.y = top + itemH - bodyH;
			}

			listScroll = UIKit.BeginScroll( listR, listScroll, listH );
			for ( int i = 0; i < ids.Length; i ++ )
			{
				var id = ids[ i ]; var kn = Codex.knowledge( id, s.log.TryGetValue( id, out var e ) ? e : null );
				var r = new Rect( 0, i * ( itemH + itemGap ), listW - 8 * u, itemH );
				bool sel = id == selected, hover = r.Contains( Event.current.mousePosition ) && listR.Contains( Event.current.mousePosition + new Vector2( listR.x - listScroll.x, listR.y - listScroll.y ) );
				if ( sel ) { UIKit.Rounded( r, C( UIKit.FILL2 ), 10 * u ); UIKit.Rounded( r, C( UIKit.LINE ), 10 * u, 1 ); }
				else if ( hover ) UIKit.Rounded( r, C( UIKit.FILL ), 10 * u );
				var name = kn.name; float nameW = UIKit.Style( UIFonts.InterMedium, 12.5f ).CalcSize( new GUIContent( name ) ).x;
				Text( new Rect( r.x + 12 * u, r.y, r.width, r.height ), name, UIFonts.InterMedium, 12.5f, sel ? UIKit.INK : kn.level > 0 ? UIKit.INK2 : UIKit.INK3, TextAnchor.MiddleLeft );
				if ( kn.level > 0 && order != null && order.species == id ) Text( new Rect( r.x + 12 * u + nameW + 3 * u, r.y, 30, r.height ), "★", UIFonts.InterMedium, 12.5f, UIKit.SUN, TextAnchor.MiddleLeft );
				float pw2 = UIKit.Style( UIFonts.Inter, 11.5f ).CalcSize( new GUIContent( new string( '●', Codex.MAX_LEVEL ) ) ).x + Codex.MAX_LEVEL;
				float px = r.xMax - 12 * u - pw2;
				var on = Pips( kn.level );
				Text( new Rect( px, r.y, pw2, r.height ), new string( '●', Codex.MAX_LEVEL ), UIFonts.Inter, 11.5f, new Color( UIKit.INK3.r, UIKit.INK3.g, UIKit.INK3.b, UIKit.INK3.a * 0.5f ), TextAnchor.MiddleLeft );
				if ( kn.level > 0 ) Text( new Rect( px, r.y, pw2, r.height ), on, UIFonts.Inter, 11.5f, UIKit.AQUA, TextAnchor.MiddleLeft );
				if ( GUI.Button( r, GUIContent.none, GUIStyle.none ) ) Select( id );
			}

			UIKit.EndScroll( listR, ref listScroll, listH, a );

			// the detail
			var detR = new Rect( x + listW + gapC, y, pw - 2 * padX - listW - gapC, bodyH );
			detailScroll = UIKit.BeginScroll( detR, detailScroll, detailH );
			Detail( 0, 0, detailW, k, order, true );
			UIKit.EndScroll( detR, ref detailScroll, detailH, a );
			y += bodyH;

			// the foot
			y += 12 * u;
			Text( new Rect( x, y, pw - 2 * padX - 140 * u, footH - 12 * u ), "Catch more of a fish, and sell it, to learn more about it", UIFonts.Inter, 11.5f, UIKit.INK3, TextAnchor.MiddleLeft );
			string close = $"Close ({game.Input?.label( "codex" ) ?? "J"})";
			float bw = UIKit.ButtonWidth( close );
			if ( open && UIKit.Button( new Rect( panel.xMax - padX - bw, y, bw, footH - 12 * u ), close, true, true, a ) ) Toggle( false );
		}

		// the detail pane: draws it (or measures it) from (x, y) in `width`; returns its height. The blocks are 12u apart (gap).
		float Detail( float x, float y0, float width, Knowledge k, Order order, bool draw )
		{
			float u = UIKit.U, y = y0, gap = 12 * u;
			bool unknown = k.level < 1;
			var ink2 = UIKit.INK2; var ink3 = UIKit.INK3; var ink = UIKit.INK;
			float Flow( float fx, float fy, float w, string md, float fs, float lh, Color c ) => UIKit.Flow( fx, fy, w, md, fs, lh, c, ink, draw, null, false, a );

			// the top: the thumbnail (200u wide, 360 : 170), then the name, the Latin name and the level
			float imgW = 200 * u, imgH = imgW * 170f / 360f, tx = x + imgW + 16 * u, tw = width - imgW - 16 * u;
			string next = k.toNext == null ? "Fully studied" : $"{k.toNext} more {( k.toNext == 1 ? "catch" : "catches" )} to learn more";
			float levelH = 12 * u * 1.2f + 2 * u + 11.5f * u * 1.2f;
			float rightH = 20 * u * 1.2f + ( string.IsNullOrEmpty( k.sci ) ? 0 : 11.5f * u * 1.2f ) + 8 * u + levelH;
			float topH = Mathf.Max( imgH, rightH );
			if ( draw )
			{
				var img = new Rect( x, y + ( topH - imgH ) / 2, imgW, imgH );
				UIKit.Rounded( img, C( new Color( 1, 1, 1, 0.04f ) ), 10 * u );
				var thumb = game.fishing?.card?.portrait?.Thumb( k.id );
				if ( thumb != null )
				{
					// contain: 360 : 170 is the box's own aspect; a blacked-out silhouette at 55 % until the fish is known
					var old = GUI.color;
					GUI.color = unknown ? new Color( 0, 0, 0, 0.55f * a ) : new Color( 1, 1, 1, a );
					GUI.DrawTexture( img, thumb, ScaleMode.ScaleToFit, true );
					GUI.color = old;
				}

				float ty = y + ( topH - rightH ) / 2;
				Text( new Rect( tx, ty, tw, 24 * u ), k.name, UIFonts.InterSemi, 20, ink );
				ty += 20 * u * 1.2f;
				if ( ! string.IsNullOrEmpty( k.sci ) ) { Text( new Rect( tx, ty, tw, 16 * u ), k.sci, UIFonts.Inter, 11.5f, ink3, TextAnchor.UpperLeft, FontStyle.Italic ); ty += 11.5f * u * 1.2f; }
				ty += 8 * u;
				var lst = UIKit.Style( UIFonts.Inter, 11.5f ); float lw = lst.CalcSize( new GUIContent( k.levelName + " " ) ).x;
				Text( new Rect( tx, ty, tw, 16 * u ), k.levelName, UIFonts.Inter, 11.5f, ink2 );
				float pipsW = UIKit.Style( UIFonts.Inter, 11.5f ).CalcSize( new GUIContent( new string( '●', Codex.MAX_LEVEL ) ) ).x + Codex.MAX_LEVEL;
				Text( new Rect( tx + lw, ty, pipsW, 16 * u ), new string( '●', Codex.MAX_LEVEL ), UIFonts.Inter, 11.5f, new Color( UIKit.AQUA.r, UIKit.AQUA.g, UIKit.AQUA.b, 0.35f ) );
				if ( k.level > 0 ) Text( new Rect( tx + lw, ty, pipsW, 16 * u ), Pips( k.level ), UIFonts.Inter, 11.5f, UIKit.AQUA );
				Text( new Rect( tx, ty + 12 * u * 1.2f + 2 * u, tw, 16 * u ), next, UIFonts.Inter, 11.5f, ink3 );
			}

			y += topH + gap;

			if ( unknown )
			{
				y += Flow( x, y, width, "You have not caught one of these yet. Catch one to start its entry.", 12.5f, 1.45f, ink2 );
				return y - y0;
			}

			y += Flow( x, y, width, k.blurb, 12.5f, 1.45f, ink2 ) + gap;

			// the facts grid: the label column is 96u, the rows 8u apart
			float lcol = 96 * u, vx = x + lcol + 12 * u, vw = width - lcol - 12 * u;
			string seen = string.Join( ", ", k.time.seen.Select( p => Codex.PERIOD_LABEL[ p ] ) );
			string bestLine = k.best != null ? $"Your biggest: {Orders.fmtKg( k.best.Value.kg )}{( k.best.Value.cm != null && k.best.Value.cm != 0 ? $" · {Math.Round( k.best.Value.cm.Value )} cm" : "" )}" : "";
			string priceNote = k.price.exact ? "Worked out from your sales" : k.sold > 0 ? $"Sell more to firm this up ({k.sold} sold)" : "A guess: sell some to Joe to learn it";
			GridRow( x, vx, vw, ref y, "Size", Codex.sizeText( k.size ), bestLine, draw );
			GridRow( x, vx, vw, ref y, "Price", Codex.priceText( k.price ), priceNote, draw );
			// where: chips (the aqua line and dot where you have caught one there), or a plain line for a pot-only fish
			{
				string note = ! k.habitat.exact && ! k.habitat.none ? "There may be more places" : "";
				float h = 0;
				if ( k.habitat.none ) h = Flow( vx, y, vw, "Only comes up in a pot", 12.5f, 1.25f, ink );
				else if ( k.habitat.list.Count == 0 ) h = Flow( vx, y, vw, "?", 12.5f, 1.25f, ink3 );
				else h = Chips( vx, y, vw, k.habitat.list, draw );
				if ( note != "" ) h += Flow( vx, y + h, vw, note, 11.5f, 1.25f, ink3 );
				if ( draw ) Text( new Rect( x, y, lcol, 18 * u ), "Found in", UIFonts.Inter, 12.5f, ink3 );
				y += h + 8 * u;
			}

			GridRow( x, vx, vw, ref y, "Active", string.IsNullOrEmpty( k.time.text ) ? "" : k.time.text, seen != "" ? $"You have caught it at {seen}" : "", draw, string.IsNullOrEmpty( k.time.text ) ? "Not worked out yet" : null );
			y += gap - 8 * u;

			// the facts
			if ( k.facts.Count > 0 )
			{
				foreach ( var t in k.facts )
				{
					if ( draw ) Text( new Rect( x + 4 * u, y, 14 * u, 18 * u ), "•", UIFonts.Inter, 12.5f, ink2 );
					y += Flow( x + 16 * u, y, width - 16 * u, t, 12.5f, 1.5f, ink2 );
				}

				y += gap;
			}

			if ( order != null && order.species == k.id ) { y += Flow( x, y, width, $"★ Joe wants one of {Orders.fmtKg( order.minKg )} or bigger today: ×{Orders.ORDER_MULT} on every one you sell.", 11.5f, 1.25f, UIKit.SUN ) + gap; }

			// the map and the history
			bool stack = Screen.width <= 760; // (@media max-width: 760px)
			float mapSide = stack ? Mathf.Min( MAP_PX, width ) : MAP_PX;
			bool hasPos = k.catches.Any( c => c.x != null );
			var table = FishTable.Get( k.id ).habitat;
			string est = k.mapBlock > 0 && table != null && table.Length > 0 ? ( k.level >= Codex.MAX_LEVEL ? "The shading is its water, as well as you know it." : "The shading is a guess at its water: it sharpens as you learn the fish." ) : "";
			string caption = ( hasPos ? "Dots are where you caught it; the ring is your biggest." : "No catch positions recorded yet." ) + " " + est;
			var hist = new List<string>();
			if ( k.first != null ) hist.Add( $"First caught on day {k.first.day} at {Clock( k.first.hour )}." );
			hist.Add( $"{k.caught} caught · {k.sold} sold{( k.earned > 0 ? $" for ${Math.Round( k.earned )}" : "" )}." );
			float tx2 = stack ? x : x + mapSide + 16 * u, tw2 = stack ? width : width - mapSide - 16 * u;
			float capH = Flow( tx2, stack ? y + mapSide + 16 * u : y, tw2, caption, 11.5f, 1.45f, ink3 );
			float hy = ( stack ? y + mapSide + 16 * u : y ) + capH + 8 * u;
			foreach ( var line in hist ) hy += Flow( tx2, hy, tw2, line, 11.5f, 1.6f, ink2 );
			if ( draw )
			{
				var mr = new Rect( x, y, mapSide, mapSide );
				var tex = Map( k );
				var old = GUI.color; GUI.color = new Color( 1, 1, 1, a );
				GUI.DrawTexture( mr, tex, ScaleMode.StretchToFill, true, 0, Color.white, Vector4.zero, new Vector4( 10 * u, 10 * u, 10 * u, 10 * u ) );
				GUI.color = old;
			}

			y = Mathf.Max( y + mapSide, hy );
			return y - y0;
		}

		// one row of the facts grid: the label, the value (or a locked line), a small note under it
		void GridRow( float x, float vx, float vw, ref float y, string label, string value, string note, bool draw, string locked = null )
		{
			float u = UIKit.U, h = 0;
			if ( locked != null ) h += UIKit.Flow( vx, y, vw, locked, 12.5f, 1.25f, UIKit.INK3, UIKit.INK, draw, null, false, a );
			else h += UIKit.Flow( vx, y, vw, value, 12.5f, 1.25f, UIKit.INK, UIKit.INK, draw, null, false, a );
			if ( note != "" ) h += UIKit.Flow( vx, y + h, vw, note, 11.5f, 1.25f, UIKit.INK3, UIKit.INK, draw, null, false, a );
			if ( draw ) Text( new Rect( x, y, 96 * u, 18 * u ), label, UIFonts.Inter, 12.5f, UIKit.INK3 );
			y += h + 8 * u;
		}

		// the habitat chips, wrapping; returns the height
		float Chips( float x, float y, float width, List<HabitatItem> list, bool draw )
		{
			float u = UIKit.U, cx = 0, cy = 0, ch = 18 * u, gap = 4 * u;
			foreach ( var it in list )
			{
				string suffix = it.strength != null && it.strength > 0 ? $"{Math.Round( it.strength.Value * 100 )}%" : null;
				float tw = UIKit.Style( UIFonts.Inter, 11.5f ).CalcSize( new GUIContent( it.label ) ).x + ( suffix != null ? UIKit.Style( UIFonts.Inter, 11.5f ).CalcSize( new GUIContent( suffix ) ).x + 4 * u : 0 ) + ( it.seen ? 10 * u : 0 ) + 16 * u;
				if ( cx > 0 && cx + tw > width ) { cx = 0; cy += ch + gap; }
				if ( draw ) UIKit.Chip( x + cx, y + cy, it.label, suffix, it.seen, a );
				cx += tw + gap;
			}

			return cy + ch + gap;
		}
	}
}
