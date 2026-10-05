using System;
using Tidewater.Player;
using Tidewater.World;
using Tidewater.World.Fish;
using UnityEngine;
using Color = UnityEngine.Color;
using Vector2 = UnityEngine.Vector2;

// The minimap on screen (Minimap.js's DOM and the `.gm-map*` styles): the Minimap model (oracle-exact against the JS) is fed from the player and the game each frame,
// the window (the glass disc, the island image turned to the view, the vignette) is a shader into a render target (MinimapWindow.shader), and IMGUI puts the
// markers, the N on the rim, the arrow in the middle, the fish finder's rings and the large map's hint on top of it.
//
// Deviations (see PORTING.md): no backdrop blur on the glass, no settings rail to make room for (the JS moves the map left in short windows), the cooler / vendor
// panels are centred here and do not push the map aside.
namespace Tidewater.Game
{
	public sealed class MinimapView
	{
		readonly Minimap map = new Minimap();
		public Minimap model => map;

		Texture2D island; // the baked image, rows bottom to top
		Material mat;
		RenderTexture rt;
		Texture2D disc, ring, tri;
		Texture2D iconFish, iconAnchor, iconBoat, iconTrap, iconMe;
		float shown = 1; // the card owns the screen: the map steps aside (opacity eased over 300 ms)
		public float opacity => shown;
		readonly float[] ringOp = new float[ 2 ];
		bool drawn;
		GUIStyle nStyle, labelStyle;
		int styleKey = -1;

		// the window's layout this frame (GUI px)
		struct Lay { public float u, panel, pad, view, R, Rp, cx, cy, margin; }
		Lay lay;

		public bool Alive => rt != null && mat != null;

		public void Dispose()
		{
			foreach ( UnityEngine.Object o in new UnityEngine.Object[] { island, mat, rt, disc, ring, tri } ) if ( o != null ) UnityEngine.Object.DestroyImmediate( o );
			island = null; mat = null; rt = null; disc = ring = tri = null;
			drawn = false;
		}

		Lay Layout( bool big )
		{
			float w = Screen.width, h = Screen.height, u = UIScale.U;
			var l = new Lay { u = u };
			l.panel = big ? Mathf.Min( 0.78f * h, 0.78f * w ) : ( w <= 640 ? 128 : 184 ) * u;
			l.pad = ( big ? 8 : 5 ) * u;
			l.view = l.panel - 2 * l.pad - 2; // (the glass's 1px border)
			l.R = l.view / 2; l.Rp = l.panel / 2;
			l.cx = big ? w / 2 : w - UIScale.Edge - l.Rp;
			l.cy = big ? h / 2 : h - UIScale.Edge - l.Rp;
			l.margin = 40 * u; // room for the shadow
			return l;
		}

		// once a frame (Update): the model, the island image, the window into its render target
		public void Tick( double dt, GameHost g, PlayerHost host, bool hidden )
		{
			var inp = host.input;
			if ( inp != null )
			{
				if ( inp.actHit( "map" ) ) map.toggleBig( inp.label( "map" ) );
				if ( inp.actHit( "cancel" ) ) map.toggleBig( "", false );
			}

			shown += ( ( hidden ? 0f : 1f ) - shown ) * ( 1 - Mathf.Exp( - ( float ) dt / 0.1f ) );
			var T = host.terrainData;
			if ( T == null ) return;
			lay = Layout( map.big );

			var p = host.player; var q = host.simCamera.quaternion; var st = g.state;
			var boat = host.ActiveBoat();
			var v = new Minimap.View
			{
				dt = dt, x = host.simCamera.position.x, z = host.simCamera.position.z,
				// the matrix elements the JS reads: the camera's local +z in the world (forward is its negative)
				e8 = 2 * ( q.x * q.z + q.w * q.y ), e10 = 1 - 2 * ( q.x * q.x + q.y * q.y ),
				mode = p.mode, size = lay.view, finder = st.stats.finder,
				sets = st.sets, groups = FishSchoolsView.instance != null && FishSchoolsView.instance.schools != null ? FishSchoolsView.instance.schools.groups : null,
			};
			if ( boat != null ) { v.boatX = boat.position.x; v.boatZ = boat.position.z; }
			var b = p.boat;
			if ( b != null && b.anchor != null && b.anchor.down ) { v.anchorDown = true; v.anchorX = b.anchor.x; v.anchorZ = b.anchor.z; }
			map.Update( v, T );

			if ( map.done && island == null ) UploadIsland();
			if ( mat == null ) mat = new Material( Shader.Find( "Hidden/Tidewater/MinimapWindow" ) ) { hideFlags = HideFlags.HideAndDontSave };
			int side = Mathf.CeilToInt( lay.panel + 2 * lay.margin );
			if ( rt == null || rt.width != side )
			{
				if ( rt != null ) UnityEngine.Object.DestroyImmediate( rt );
				rt = new RenderTexture( side, side, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB ) { name = "minimap", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, antiAliasing = 1 };
			}

			if ( ! map.sized ) { drawn = false; return; }
			mat.SetVector( "_Size", new Vector4( side, side, 0, 0 ) );
			mat.SetVector( "_Geo", new Vector4( side / 2f, side / 2f, lay.R, lay.Rp ) );
			mat.SetVector( "_Map", new Vector4( ( float ) map.rot, ( float ) map.scale, ( float ) map.ax, ( float ) map.ay ) );
			mat.SetVector( "_Map2", new Vector4( Minimap.N, island != null ? 1 : 0, lay.u, map.big ? 40 : 18 ) );
			mat.SetVector( "_Look", new Vector4( shown, map.big ? 0.4f : 0.45f, map.big ? 0.12f : 0.08f, 0 ) );
			Graphics.Blit( island != null ? island : Texture2D.blackTexture, rt, mat, 0 );
			drawn = true;
		}

		void UploadIsland()
		{
			// the texture's rows run bottom to top, the image's from the north edge down
			const int N = Minimap.N;
			var flipped = new byte[ N * N * 4 ];
			for ( int r = 0; r < N; r ++ ) Buffer.BlockCopy( map.rgba, ( N - 1 - r ) * N * 4, flipped, r * N * 4, N * 4 );
			island = new Texture2D( N, N, TextureFormat.RGBA32, false, false ) { name = "minimap-island", hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
			island.SetPixelData( flipped, 0 );
			island.Apply( false, false );
		}

		// ---- the IMGUI pieces

		static Texture2D Make( int n, Func<float, float, float> alpha )
		{
			var t = new Texture2D( n, n, TextureFormat.RGBA32, true, false ) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
			var c = new Color[ n * n ];
			for ( int y = 0; y < n; y ++ ) for ( int x = 0; x < n; x ++ ) c[ y * n + x ] = new Color( 1, 1, 1, alpha( ( x + 0.5f ) / n, ( y + 0.5f ) / n ) );
			t.SetPixels( c ); t.Apply( true );
			return t;
		}

		void Textures()
		{
			// a disc, a ring (the fish finder's border: 1.5 px on a 12 px radius) and an upward triangle (the edge arrow), white, antialiased
			if ( disc == null ) disc = Make( 128, ( x, y ) => Mathf.Clamp01( 64 - Mathf.Sqrt( ( x - 0.5f ) * ( x - 0.5f ) + ( y - 0.5f ) * ( y - 0.5f ) ) * 128 + 0.5f ) );
			if ( ring == null ) ring = Make( 128, ( x, y ) =>
			{
				float d = Mathf.Sqrt( ( x - 0.5f ) * ( x - 0.5f ) + ( y - 0.5f ) * ( y - 0.5f ) ) * 128;
				return Mathf.Clamp01( 64 - d + 0.5f ) * Mathf.Clamp01( d - 56 + 0.5f );
			} );
			if ( tri == null ) tri = Make( 64, ( x, y ) =>
			{
				// apex at the top centre, base along the bottom: |x - 0.5| <= 0.5 * (y_from_top)
				float fromTop = 1 - y, half = 0.5f * fromTop;
				return Mathf.Clamp01( ( half - Mathf.Abs( x - 0.5f ) ) * 64 + 0.5f );
			} );
			if ( iconFish == null ) iconFish = Resources.Load<Texture2D>( "ui/map-fish" );
			if ( iconAnchor == null ) iconAnchor = Resources.Load<Texture2D>( "ui/map-anchor" );
			if ( iconBoat == null ) iconBoat = Resources.Load<Texture2D>( "ui/map-boat" );
			if ( iconTrap == null ) iconTrap = Resources.Load<Texture2D>( "ui/map-trap" );
			if ( iconMe == null ) iconMe = Resources.Load<Texture2D>( "ui/map-me" );
		}

		void Styles( float u )
		{
			int key = Mathf.RoundToInt( u * 100 );
			if ( nStyle != null && nStyle.fontSize > 0 && styleKey == key ) return;
			styleKey = key;
			nStyle = new GUIStyle( GUI.skin.label ) { font = UIFonts.InterBold, fontSize = Mathf.RoundToInt( 9 * u ), alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
			nStyle.padding = new RectOffset(); nStyle.margin = new RectOffset(); nStyle.normal.textColor = new Color( 0x0b / 255f, 0x14 / 255f, 0x18 / 255f );
			labelStyle = new GUIStyle( nStyle ) { font = UIFonts.MonoMedium, fontSize = Mathf.RoundToInt( 10.5f * u ), alignment = TextAnchor.UpperCenter };
			labelStyle.normal.textColor = new Color( 200 / 255f, 222 / 255f, 232 / 255f, 0.52f );
		}

		static void Disc( float cx, float cy, float r, Color c, Texture2D t )
		{
			if ( c.a <= 0.001f || r <= 0 ) return;
			var old = GUI.color; GUI.color = c;
			GUI.DrawTexture( new Rect( cx - r, cy - r, 2 * r, 2 * r ), t );
			GUI.color = old;
		}

		static readonly Color SUN = new Color( 1f, 0xb8 / 255f, 0x6b / 255f ), AQUA = new Color( 0x5f / 255f, 0xe3 / 255f, 0xd4 / 255f ), BOAT = new Color( 0xf2 / 255f, 0xef / 255f, 0xe6 / 255f ),
			TRAP = new Color( 0xe8 / 255f, 0xa3 / 255f, 0x3d / 255f ), ANCH = new Color( 0xee / 255f, 0x5a / 255f, 0x1e / 255f ), DARK = new Color( 0x0b / 255f, 0x14 / 255f, 0x18 / 255f );

		public void OnGUI()
		{
			if ( Event.current.type != EventType.Repaint || ! drawn || rt == null || shown < 0.01f || ! map.sized ) return;
			Textures(); Styles( lay.u );
			float u = lay.u, a = shown;
			var old = GUI.color; var oldM = GUI.matrix;
			float side = rt.width;
			GUI.color = Color.white;
			GUI.DrawTexture( new Rect( lay.cx - side / 2, lay.cy - side / 2, side, side ), rt, ScaleMode.StretchToFill, true );

			// the markers are in the window's own frame (its top-left at the centre less R)
			float ox = lay.cx - lay.R, oy = lay.cy - lay.R;
			float t = Time.unscaledTime;
			foreach ( var m in map.markers )
			{
				if ( ! m.visible ) continue;
				float cx = ox + ( float ) m.x, cy = oy + ( float ) m.y;
				Color fill; Texture2D icon;
				switch ( m.kind ) { case "joe": fill = SUN; icon = iconFish; break; case "marta": fill = AQUA; icon = iconAnchor; break; case "boat": fill = BOAT; icon = iconBoat; break; case "anch": fill = ANCH; icon = iconAnchor; break; default: fill = TRAP; icon = iconTrap; break; }
				float r = 10 * u * ( m.edge ? 0.82f : 1f );
				if ( m.hot )
				{
					// gm-map-pulse: 1.3 s, the ring grows to 10u and fades, then comes back
					float k = ( t % 1.3f ) / 1.3f, spread, al;
					if ( k < 0.6f ) { float e = UIScale.EaseIO( k / 0.6f ); spread = 10 * u * e; al = 0.6f * ( 1 - e ); }
					else { float e = UIScale.EaseIO( ( k - 0.6f ) / 0.4f ); spread = 10 * u * ( 1 - e ); al = 0.6f * e; }
					Disc( cx, cy, r + 1.5f + spread, new Color( 1, 1, 1, al * a ), disc );
				}

				Disc( cx, cy + 1, r + 2.5f, new Color( 0, 0, 0, 0.35f * a ), disc ); // 0 1px 3px rgba( 0, 0, 0, 0.55 ), a soft edge
				Disc( cx, cy, r + 1.5f, new Color( 1, 1, 1, 0.85f * a ), disc );        // 0 0 0 1.5px rgba( 255, 255, 255, 0.85 )
				fill.a = a; Disc( cx, cy, r, fill, disc );
				if ( icon != null ) { GUI.color = new Color( DARK.r, DARK.g, DARK.b, a ); float s = 2 * r * 0.64f; GUI.DrawTexture( new Rect( cx - s / 2, cy - s / 2, s, s ), icon ); }
				if ( m.edge && ! double.IsNaN( m.arrow ) )
				{
					// the arrow: a 10u x 7u triangle above the marker, turned about its centre toward the rim
					GUIUtility.RotateAroundPivot( ( float ) ( m.arrow * 180 / Math.PI ), new Vector2( cx, cy ) );
					GUI.color = new Color( 1, 1, 1, 0.9f * a );
					GUI.DrawTexture( new Rect( cx - 5 * u, cy - 19 * u, 10 * u, 7 * u ), tri );
					GUI.matrix = oldM;
				}
			}

			// the fish finder's rings: 24u, a 1.5 px aqua border, growing from 0.45 to 1.15 and fading over 2.2 s (the second one half a cycle later)
			for ( int i = 0; i < map.fish.Length; i ++ )
			{
				var f = map.fish[ i ];
				ringOp[ i ] += ( ( f.visible ? 0.9f : 0f ) - ringOp[ i ] ) * ( 1 - Mathf.Exp( - Time.unscaledDeltaTime / 0.2f ) );
				if ( ringOp[ i ] < 0.005f || ! f.placed ) continue;
				float ph = ( ( t + ( i == 1 ? 1.1f : 0f ) ) % 2.2f ) / 2.2f, e = UIScale.EaseIO( ph );
				float d = 24 * u * Mathf.Lerp( 0.45f, 1.15f, e );
				GUI.color = new Color( AQUA.r, AQUA.g, AQUA.b, 0.9f * ( 1 - e ) * ringOp[ i ] / 0.9f * a );
				GUI.DrawTexture( new Rect( ox + ( float ) f.x - d / 2, oy + ( float ) f.y - d / 2, d, d ), ring );
			}

			// the N on the rim
			{
				float cx = ox + ( float ) map.north.x, cy = oy + ( float ) map.north.y, r = 7 * u;
				Disc( cx, cy + 1, r + 1.5f, new Color( 0, 0, 0, 0.3f * a ), disc );
				Disc( cx, cy, r, new Color( 1, 1, 1, 0.9f * a ), disc );
				var c = nStyle.normal.textColor; nStyle.normal.textColor = new Color( c.r, c.g, c.b, a );
				GUI.color = Color.white; GUI.Label( new Rect( cx - r, cy - r, 2 * r, 2 * r ), "N", nStyle );
			}

			// the arrow in the middle: 18u, its centre 1u above the window's (the large map is north up: it turns instead of the map)
			if ( iconMe != null )
			{
				float s = 18 * u, cx = lay.cx, cy = lay.cy - u;
				if ( map.meTurns ) GUIUtility.RotateAroundPivot( ( float ) ( map.meHeading * 180 / Math.PI ), new Vector2( cx, cy ) );
				GUI.color = new Color( 0, 0, 0, 0.6f * a ); GUI.DrawTexture( new Rect( cx - s / 2, cy - s / 2 + u, s, s ), iconMe ); // drop-shadow( 0 1px 2px rgba( 0, 0, 0, 0.6 ) )
				GUI.color = new Color( 1, 1, 1, a ); GUI.DrawTexture( new Rect( cx - s / 2, cy - s / 2, s, s ), iconMe );
				GUI.matrix = oldM;
			}

			// the large map's hint under the window
			if ( map.big && ! string.IsNullOrEmpty( map.label ) )
			{
				var r = new Rect( lay.cx - 200, lay.cy + lay.Rp - 2 * u + 4 * u, 400, 20 * u );
				var c = labelStyle.normal.textColor;
				labelStyle.normal.textColor = new Color( 0, 0, 0, 0.7f * a ); GUI.Label( new Rect( r.x, r.y + 1, r.width, r.height ), map.label, labelStyle );
				labelStyle.normal.textColor = new Color( c.r, c.g, c.b, 0.52f * a ); GUI.Label( r, map.label, labelStyle );
			}

			GUI.color = old; GUI.matrix = oldM;
		}
	}
}
