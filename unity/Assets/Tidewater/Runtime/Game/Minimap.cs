using System;
using System.Collections.Generic;
using Tidewater.World;
using Tidewater.World.Fish;
using JS = Tidewater.Engine.JS;

// Port of src/game/Minimap.js, the model of it: the island baked once from the terrain data into a 640 x 640 image (depth-tinted sea, reef and seagrass, sand,
// grass and forest by height, rock, paths, village pads, the pier, hill shading and a coastline), and each frame where that image and the markers sit in the
// round window (it turns with the view: forward is up; markers beyond the rim sit on it with an arrow; with the fish finder upgrade, a couple of soft rings mark
// the nearest schools). The browser's DOM and CSS are the MinimapView's (IMGUI and a shader); everything here is plain numbers, so the oracle can run it against
// the JS (tools/dump-minimap.mjs -> Editor/MinimapOracle.cs): the bake pixel for pixel, the canvas calls, and the transforms every frame.
//
// The bake runs in row chunks over the first frames (BakeStep, once per Update). The JS draws the village pads and the pier with the 2D canvas, antialiased:
// here the same calls are recorded (Calls) and rasterized by Rect / Stroke with exact area coverage.
namespace Tidewater.Game
{
	public sealed class Minimap
	{
		public const int N = 640; // baked image size (px)
		public const double EXT = 1280; // metres covered by the bake
		public const double X0 = - EXT / 2, Z0 = - 180 - EXT / 2; // world at image (0, 0): the island sits north of the bay
		public const double PPM = N / EXT; // image px per metre
		const int ROWS_PER_FRAME = 48;
		const double BIG_RADIUS = 360; // metres from the centre to the rim of the large map

		// map palette (sRGB 0..255)
		static readonly double[] DEEP = { 12, 44, 78 }, MID = { 22, 104, 150 }, SHALLOW = { 62, 186, 190 }, FOAM = { 186, 232, 226 };
		static readonly double[] GRASS_SEA = { 34, 110, 108 }, RUBBLE = { 30, 84, 104 };
		static readonly double[] SAND = { 232, 216, 172 }, WET = { 206, 190, 150 }, GRASS = { 128, 160, 88 }, SCRUB = { 88, 128, 64 }, FOREST = { 52, 92, 50 };
		static readonly double[] ROCK = { 132, 134, 126 }, PATH = { 205, 182, 136 }, SCARP = { 176, 146, 104 }, PAD = { 196, 184, 162 };

		static double[] mix( double[] a, double[] b, double t ) => new[] { a[ 0 ] + ( b[ 0 ] - a[ 0 ] ) * t, a[ 1 ] + ( b[ 1 ] - a[ 1 ] ) * t, a[ 2 ] + ( b[ 2 ] - a[ 2 ] ) * t };
		static double sat( double x ) => Math.Min( 1, Math.Max( 0, x ) );
		static double smooth( double e0, double e1, double x )
		{
			double t = sat( ( x - e0 ) / ( e1 - e0 ) );
			return t * t * ( 3 - 2 * t );
		}

		// a store into a Uint8ClampedArray: clamp, round half to even
		static byte U8( double v ) => double.IsNaN( v ) || v <= 0 ? ( byte ) 0 : v >= 255 ? ( byte ) 255 : ( byte ) Math.Round( v, MidpointRounding.ToEven );

		// ---- the image (RGBA, row 0 at the north edge: the canvas's top)

		public readonly byte[] rgba = new byte[ N * N * 4 ];
		readonly float[] hgt = new float[ N * N ]; // (a Float32Array in the JS: the coastline reads the rounded heights)
		int row;
		public bool done { get; private set; }
		// the oracle's hooks: keep the image as the JS hands it to putImageData (before the pads and the pier), and skip the bake
		public bool keepPreCanvas; public byte[] preCanvas;
		public void MarkBaked() { done = true; }

		// the canvas calls after the bake: ( "fillRect", style, x, y, w, h ) and ( "strokeRect", style, lineWidth, x, y, w, h ), in the JS's order
		public sealed class Call { public string method, style; public double lineWidth, x, y, w, h; }
		public readonly List<Call> Calls = new List<Call>();

		public void BakeStep( TerrainData T )
		{
			if ( done ) return;
			int res = T.res; double tex = T.texel, org = T.origin;
			Func<double, double, int> idx = ( x, z ) =>
			{
				int i = ( int ) Math.Floor( ( x - org ) / tex ), j = ( int ) Math.Floor( ( z - org ) / tex );
				return i < 0 || j < 0 || i >= res || j >= res ? - 1 : j * res + i;
			};

			int end = Math.Min( N, row + ROWS_PER_FRAME );
			for ( int py = row; py < end; py ++ )
			{
				for ( int px = 0; px < N; px ++ )
				{
					double x = X0 + ( px + 0.5 ) / PPM, z = Z0 + ( py + 0.5 ) / PPM;
					double h = T.HeightAt( x, z );
					hgt[ py * N + px ] = ( float ) h;
					int k = idx( x, z );
					double[] c;
					if ( h < 0 )
					{
						double dep = - h;
						c = dep < 6 ? mix( SHALLOW, MID, smooth( 0.3, 6, dep ) ) : mix( MID, DEEP, smooth( 6, 40, dep ) );
						if ( k >= 0 )
						{
							c = mix( c, GRASS_SEA, ( T.seagrass[ k ] / 255.0 ) * 0.55 );
							c = mix( c, RUBBLE, ( T.rubble[ k ] / 255.0 ) * 0.6 );
						}

						c = mix( c, FOAM, smooth( 0.5, 0.0, dep ) * 0.45 );
					}
					else
					{
						double rock = k >= 0 ? T.rock[ k ] : 0, sand = k >= 0 ? T.sand[ k ] / 255.0 : 0;
						c = mix( GRASS, SCRUB, smooth( 4, 16, h ) );
						c = mix( c, FOREST, smooth( 12, 40, h ) );
						c = mix( c, SAND, Math.Max( sand, smooth( 1.6, 0.4, h ) ) );
						c = mix( c, WET, smooth( 0.5, 0.0, h ) * 0.6 );
						c = mix( c, ROCK, smooth( 0.35, 0.7, rock ) );
						if ( k >= 0 )
						{
							c = mix( c, PATH, ( T.path[ k ] / 255.0 ) * 0.85 );
							c = mix( c, SCARP, ( T.scarp[ k ] / 255.0 ) * 0.5 );
						}

						// hill shading, light from the north-west
						const double e = 2.5;
						double gx = ( T.HeightAt( x + e, z ) - T.HeightAt( x - e, z ) ) / ( 2 * e );
						double gz = ( T.HeightAt( x, z + e ) - T.HeightAt( x, z - e ) ) / ( 2 * e );
						double nl = ( - gx * - 0.6 + - gz * - 0.6 + 1 * 0.53 ) / Math.Sqrt( gx * gx + gz * gz + 1 );
						double s = 0.7 + 0.45 * sat( nl / 0.53 );
						c = new[] { c[ 0 ] * s, c[ 1 ] * s, c[ 2 ] * s };
					}

					int o = ( py * N + px ) * 4;
					rgba[ o ] = U8( c[ 0 ] ); rgba[ o + 1 ] = U8( c[ 1 ] ); rgba[ o + 2 ] = U8( c[ 2 ] ); rgba[ o + 3 ] = 255;
				}
			}

			row = end;
			if ( row < N ) return;

			// coastline: a thin pale line where the height crosses sea level
			for ( int py = 1; py < N - 1; py ++ )
			{
				for ( int px = 1; px < N - 1; px ++ )
				{
					int i = py * N + px; bool a = hgt[ i ] >= 0;
					if ( a && ( hgt[ i - 1 ] < 0 || hgt[ i + 1 ] < 0 || hgt[ i - N ] < 0 || hgt[ i + N ] < 0 ) )
					{
						int o = i * 4;
						rgba[ o ] = U8( rgba[ o ] * 0.3 + 245 * 0.7 ); rgba[ o + 1 ] = U8( rgba[ o + 1 ] * 0.3 + 241 * 0.7 ); rgba[ o + 2 ] = U8( rgba[ o + 2 ] * 0.3 + 228 * 0.7 );
					}
				}
			}

			if ( keepPreCanvas ) preCanvas = ( byte[] ) rgba.Clone();
			Func<double, double, double[]> P = ( x, z ) => new[] { ( x - X0 ) * PPM, ( z - Z0 ) * PPM };

			// village pads (house footprints)
			string padStyle = $"rgb({( int ) PAD[ 0 ]},{( int ) PAD[ 1 ]},{( int ) PAD[ 2 ]})";
			foreach ( var p in T.pads )
			{
				var c = P( p.x, p.z );
				double r = Math.Max( 1.5, p.radius * 0.8 * PPM );
				FillRect( padStyle, PAD, c[ 0 ] - r, c[ 1 ] - r, 2 * r, 2 * r );
			}

			// the pier and its head
			double W = WorldLayout.Pier.width; var pier = WorldLayout.Pier.x;
			var fill = new double[] { 0xd9, 0xc7, 0xa0 };
			const string FILL = "#d9c7a0", STROKE = "rgba(40, 30, 20, 0.55)";
			var a0 = P( pier - Math.Max( W, 3 ) / 2, WorldLayout.Pier.zStart );
			var a1 = P( pier + Math.Max( W, 3 ) / 2, WorldLayout.Pier.zEnd );
			FillRect( FILL, fill, a0[ 0 ], a0[ 1 ], a1[ 0 ] - a0[ 0 ], a1[ 1 ] - a0[ 1 ] );
			StrokeRect( STROKE, 0.6, a0[ 0 ], a0[ 1 ], a1[ 0 ] - a0[ 0 ], a1[ 1 ] - a0[ 1 ] );
			var b0 = P( pier - WorldLayout.Pier.headWidth / 2, WorldLayout.Pier.zEnd - WorldLayout.Pier.headDepth );
			var b1 = P( pier + WorldLayout.Pier.headWidth / 2, WorldLayout.Pier.zEnd );
			FillRect( FILL, fill, b0[ 0 ], b0[ 1 ], b1[ 0 ] - b0[ 0 ], b1[ 1 ] - b0[ 1 ] );
			StrokeRect( STROKE, 0.6, b0[ 0 ], b0[ 1 ], b1[ 0 ] - b0[ 0 ], b1[ 1 ] - b0[ 1 ] );
			done = true;
		}

		// ---- the 2D canvas calls, rasterized with exact area coverage (what the browser's antialiasing comes to for a rectangle)

		static double Span( double lo, double hi, int i ) => Math.Max( 0, Math.Min( hi, i + 1 ) - Math.Max( lo, i ) );

		void FillRect( string style, double[] rgb, double x, double y, double w, double h )
		{
			Calls.Add( new Call { method = "fillRect", style = style, x = x, y = y, w = w, h = h } );
			Blend( x, y, x + w, y + h, rgb, 1 );
		}

		void StrokeRect( string style, double lineWidth, double x, double y, double w, double h )
		{
			Calls.Add( new Call { method = "strokeRect", style = style, lineWidth = lineWidth, x = x, y = y, w = w, h = h } );
			double d = lineWidth / 2;
			// the band between the rectangle grown by half the line and shrunk by it
			Blend( x - d, y - d, x + w + d, y + h + d, new double[] { 40, 30, 20 }, 0.55, x + d, y + d, x + w - d, y + h - d );
		}

		// `rgb` over the pixels covered by the box ( minus the inner box, when given ), by area, with `alpha` on top
		void Blend( double x0, double y0, double x1, double y1, double[] rgb, double alpha, double ix0 = 0, double iy0 = 0, double ix1 = -1, double iy1 = -1 )
		{
			bool inner = ix1 > ix0 && iy1 > iy0;
			for ( int j = Math.Max( 0, ( int ) Math.Floor( y0 ) ); j <= Math.Min( N - 1, ( int ) Math.Floor( y1 ) ); j ++ )
			{
				for ( int i = Math.Max( 0, ( int ) Math.Floor( x0 ) ); i <= Math.Min( N - 1, ( int ) Math.Floor( x1 ) ); i ++ )
				{
					double cov = Span( x0, x1, i ) * Span( y0, y1, j );
					if ( inner ) cov -= Span( ix0, ix1, i ) * Span( iy0, iy1, j );
					double a = cov * alpha;
					if ( a <= 0 ) continue;
					int o = ( j * N + i ) * 4;
					for ( int k = 0; k < 3; k ++ ) rgba[ o + k ] = U8( rgba[ o + k ] + ( rgb[ k ] - rgba[ o + k ] ) * a );
				}
			}
		}

		// ---- per frame

		// what Update needs of the game: the view's forward ( the matrix elements e[ 8 ], e[ 10 ]: the camera's local +z in the world, the forward is their negative ),
		// where the camera is, and the things the markers stand for
		public sealed class View
		{
			public double dt, x, z, e8, e10;
			public string mode = "walk"; // the player's mode: "walk", "swim", "boat", "deck" ...
			public double size; // the round window's width in px
			public bool finder; // the fish finder upgrade
			public double? boatX, boatZ; // the boat (null: none)
			public bool anchorDown; public double anchorX, anchorZ; // the anchor of the boat you last boarded
			public IList<Tidewater.Game.TrapSet> sets; // the trap line (an entry per slot of the licence; null where there is no set)
			public IList<FishGroup> groups; // the schools the reef is simulating
		}

		public sealed class Marker
		{
			public string id, kind; // kind: "joe", "marta", "boat", "anch", "trap"
			public bool visible, edge, hot;
			public double x, y; // px from the window's top-left (the last written: kept while it is hidden, as the JS leaves it)
			public double arrow = double.NaN; // the edge arrow's rotation (rad)
			public bool placed;
		}

		public readonly List<Marker> markers = new List<Marker>();
		public readonly Marker north = new Marker { id = "north" };
		public readonly Marker[] fish = { new Marker { id = "fish0" }, new Marker { id = "fish1" } };

		public bool big { get; private set; }
		public string label { get; private set; } = "";
		public double radiusM = 110; // world radius shown (m), eased between on foot and at sea
		public double R; // the window's radius (px)
		// the image's transform in the window (CSS: translate( R, R ) rotate( rot ) scale( s ) translate( ax, ay ), origin top-left)
		public double rot, scale, ax, ay;
		public bool meTurns; public double meHeading; // the big map is north up: the arrow in the middle turns instead
		public bool sized; // the window has a size (the first frames it has none)
		public readonly List<SchoolReading> fishPts = new List<SchoolReading>();
		double fishT;
		HashSet<string> hot = new HashSet<string>();
		readonly int trapLimit;

		public Minimap( int trapLimit = Gear.TRAP_LIMIT )
		{
			this.trapLimit = trapLimit;
			markers.Add( new Marker { id = "joe", kind = "joe" } );
			markers.Add( new Marker { id = "marta", kind = "marta" } );
			markers.Add( new Marker { id = "boat", kind = "boat" } );
			markers.Add( new Marker { id = "anchor", kind = "anch" } );
			for ( int i = 0; i < trapLimit; i ++ ) markers.Add( new Marker { id = "trap" + i, kind = "trap" } );
		}

		// the large map: open / close (on = null toggles). key is the label of the binding, for the hint under it
		public void toggleBig( string key = "", bool? on = null )
		{
			bool want = on ?? ! big;
			if ( want == big ) return;
			big = want;
			label = want ? $"{key}  close map" : "";
		}

		public void highlight( IEnumerable<string> ids )
		{
			hot = new HashSet<string>( ids );
			foreach ( var m in markers ) m.hot = hot.Contains( m.id );
		}

		static readonly string[] AT_SEA = { "boat", "deck", "swim" };

		public void Update( View v, TerrainData T )
		{
			BakeStep( T );
			double size = v.size;
			sized = size > 0;
			if ( ! sized ) return;
			R = size / 2;

			// forward (xz) of the view
			double fx = - v.e8, fz = - v.e10;
			double fl = JS.Hypot( fx, fz );
			if ( fl < 1e-4 ) { fx = 0; fz = - 1; }
			else { fx /= fl; fz /= fl; }

			// the large map is north up: the map frame is fixed and the arrow in the middle turns instead
			double heading = Math.Atan2( fx, - fz );
			if ( big ) { fx = 0; fz = - 1; }

			meTurns = big; meHeading = heading;

			double rx = - fz, rz = fx; // right
			double x = v.x, z = v.z;

			// zoom: close on foot, wider at sea
			double want = big ? BIG_RADIUS : Array.IndexOf( AT_SEA, v.mode ) >= 0 ? 240 : 110;
			radiusM += ( want - radiusM ) * ( 1 - Math.Exp( - v.dt * 1.5 ) );
			double kpm = R / radiusM; // px per metre

			// map: player at the centre, forward up
			rot = - Math.PI / 2 - Math.Atan2( fz, fx );
			scale = kpm / PPM;
			ax = - ( x - X0 ) * PPM; ay = - ( z - Z0 ) * PPM;

			bool place( Marker m, double dx, double dz, double edgeInset, bool arrow )
			{
				double sx = ( dx * rx + dz * rz ) * kpm, sy = - ( dx * fx + dz * fz ) * kpm;
				double r = JS.Hypot( sx, sy ), max = R - edgeInset;
				bool edge = r > max;
				if ( edge ) { sx *= max / r; sy *= max / r; }
				m.x = R + sx; m.y = R + sy; m.placed = true;
				if ( arrow && edge ) m.arrow = Math.Atan2( sx, - sy );
				return edge;
			}

			foreach ( var m in markers )
			{
				double? qx = null, qz = null; bool hide = false;
				switch ( m.id )
				{
					case "joe": qx = Stalls.STAND[ 0 ]; qz = Stalls.STAND[ 1 ]; break;
					case "marta": qx = Stalls.CHANDLERY[ 0 ]; qz = Stalls.CHANDLERY[ 1 ]; break;
					case "boat":
						if ( v.boatX != null ) { qx = v.boatX; qz = v.boatZ; }
						hide = v.mode == "boat" || v.mode == "deck";
						break;
					case "anchor":
						// the anchor of the boat you last boarded, while it is down: an orange anchor where it lies
						if ( v.anchorDown ) { qx = v.anchorX; qz = v.anchorZ; }
						break;
					default:
						// the trap line: a marker pool, one per pot the licence allows in the water (hidden when that slot has no set)
						int i = int.Parse( m.id.Substring( 4 ) );
						var s = v.sets != null && i < v.sets.Count ? v.sets[ i ] : null;
						if ( s != null ) { qx = s.x; qz = s.z; }
						break;
				}

				m.visible = qx != null && ! hide;
				if ( ! m.visible ) continue;
				m.edge = place( m, qx.Value - x, qz.Value - z, 12, true );
			}

			// the N on the rim
			place( north, 0, - 1e6, 9, false );

			// fish finder: the schools the reef is actually simulating, refreshed now and then. Only those inside the map's own radius, so a ring is never
			// left clamped to the rim (they have no arrow).
			fishT -= v.dt;
			if ( v.finder && fishT <= 0 )
			{
				fishT = 1.5;
				fishPts.Clear();
				fishPts.AddRange( Sonar.fishNear( v.groups, x, z, radiusM * 0.85, 2, 25 ) );
			}

			for ( int i = 0; i < fish.Length; i ++ )
			{
				SchoolReading q = v.finder && i < fishPts.Count ? fishPts[ i ] : null;
				fish[ i ].visible = q != null;
				if ( q != null ) place( fish[ i ], q.x - x, q.z - z, 14, false );
			}
		}
	}
}
