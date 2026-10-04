using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.Util;
using static Tidewater.World.Village.GeoBuilder;

// Port of src/world/Props.js: procedural harbour / village props. Every emitter writes into a Builder `B` (in B's current local
// frame) using the shared material keys: wood, roofMetal, thatch, hard, glass, stone, rope, net, cloth, flag.
//
// Not ported yet: the fish, lobsters, ice and banana leaves (fish/FishProps.js draws them as one instanced GPU mesh). The calls stay
// where the JS has them, as no-ops that touch no random sequence, so the rest of the village is built exactly as it is in the JS.
namespace Tidewater.World.Village
{
	// the fish() options object
	public sealed class FishO
	{
		public string species, kind, pose;
		public double? len, ry, rx, rz, sag, curl, jaw, cloudy, blood, wet, dried, seed;
		public bool? flip;
	}

	// the rowboat() options object
	public sealed class RowboatO
	{
		public double? length, beam, depth, seed, rx, rz;
		public double[] hull, bottom, trim;
		public bool upsideDown, interior = true, oars = true;
	}

	// simple seeded random helper wrapper
	public sealed class Rand
	{
		readonly Mulberry32 fn;
		public Rand( Mulberry32 fn ) { this.fn = fn; }
		public double next() => fn.Next();
		public double range( double a, double b ) => a + ( b - a ) * fn.Next();
		public int @int( int a, int b ) => ( int ) Math.Floor( range( a, b + 1 ) );
		public T pick<T>( IList<T> arr ) => arr[ ( int ) ( Math.Floor( fn.Next() * arr.Count ) % arr.Count ) ];
		public bool chance( double p ) => fn.Next() < p;
	}

	public static class Props
	{
		static readonly Color _col = new Color();

		// sRGB hex -> linear [r, g, b]
		public static double[] lin( double hex ) { _col.setHex( hex ); return new[] { _col.r, _col.g, _col.b }; }
		public static double[] mulc( double[] c, double k ) => new[] { c[ 0 ] * k, c[ 1 ] * k, c[ 2 ] * k };
		public static double[] P( double x, double y, double z ) => new[] { x, y, z };
		public static Vector3 V3( double x, double y, double z ) => new Vector3( x, y, z );

		// vdata helpers
		public static double[] WOOD( double seed, double weather = 0.7, double paint = 0, double pattern = 0 ) => new[] { seed, paint, pattern, weather };
		public static double[] HARD( double seed, double rust = 0, double metal = 0, double rough = 0.5 ) => new[] { seed, rust, metal, rough };

		public static class C
		{
			public static readonly double[] iron = lin( 0x1d1c1b ), galv = lin( 0x9aa0a2 ), rubber = lin( 0x141414 ), brass = lin( 0xb08d4a ),
				rope = lin( 0xb49a6c ), ropeDark = lin( 0x7d6a4a ), ropeBlue = lin( 0x3e6f8e ), white = lin( 0xf2efe6 ), orange = lin( 0xe8622a ),
				red = lin( 0xc23b2e ), yellow = lin( 0xf0c23a ), green = lin( 0x3f7f55 ), blue = lin( 0x2f6fa8 ), black = lin( 0x202020 ),
				fishSilver = lin( 0x9aa6ad ), fishDry = lin( 0x8a6a45 ), glassWarm = lin( 0xfff0d0 ), straw = lin( 0xc9b27a );
		}

		// ---------------------------------------------------------------------------
		// Mooring hardware

		public static void bollard( Builder B, double x, double y, double z, double seed = 0.5 )
		{
			B.lathe( "hard", x, y, z, new[]
			{
				new[] { 0.0, 0.0 }, new[] { 0.21, 0.0 }, new[] { 0.21, 0.045 }, new[] { 0.21, 0.045 }, new[] { 0.14, 0.075 }, new[] { 0.115, 0.14 },
				new[] { 0.11, 0.3 }, new[] { 0.125, 0.37 }, new[] { 0.175, 0.41 }, new[] { 0.18, 0.45 }, new[] { 0.14, 0.49 }, new[] { 0.0, 0.5 },
			}, new O { segs = 14, tint = C.iron, data = HARD( seed, 0.3, 0.35, 0.5 ) } );
			for ( int i = 0; i < 4; i ++ )
			{
				double a = ( double ) i / 4 * Math.PI * 2 + Math.PI / 4;
				B.cyl( "hard", x + Math.Cos( a ) * 0.17, y + 0.045, z + Math.Sin( a ) * 0.17, 0.018, 0.022, 0.025, new O { segs = 6, tint = C.iron, data = HARD( seed, 0.8, 0.4, 0.6 ) } );
			}
		}

		public static void cleat( Builder B, double x, double y, double z, double ry = 0, double seed = 0.5 )
		{
			var t = C.iron; var d = HARD( seed, 0.5, 0.4, 0.5 );
			B.pushAt( x, y, z, ry );
			B.box( "hard", 0, 0.012, 0, 0.26, 0.024, 0.09, new O { tint = t, data = d } );
			B.box( "hard", -0.065, 0.055, 0, 0.05, 0.07, 0.055, new O { tint = t, data = d } );
			B.box( "hard", 0.065, 0.055, 0, 0.05, 0.07, 0.055, new O { tint = t, data = d } );
			B.rod( "hard", P( 0, 0.1, 0 ), P( 0.2, 0.1, 0 ), 0.026, 0.013, new O { segs = 8, tint = t, data = d } );
			B.rod( "hard", P( 0, 0.1, 0 ), P( -0.2, 0.1, 0 ), 0.026, 0.013, new O { segs = 8, tint = t, data = d } );
			B.pop();
		}

		// tire fender hanging against a vertical face; ry = direction the face points to
		public static void tireFender( Builder B, double x, double yTop, double z, double ry = 0, double drop = 0.7, double seed = 0.5 )
		{
			B.pushAt( x, 0, z, ry );
			double cy = yTop - drop;
			B.torus( "hard", 0.12, cy, 0, 0.27, 0.1, new O { rz = Math.PI / 2, radial = 7, tubular = 16, tint = C.rubber, data = HARD( seed, 0, 0, 0.82 ) } );
			B.tube( "rope", sagPoints( P( -0.05, yTop + 0.02, 0 ), P( 0.12, cy + 0.33, 0 ), 0.02, 4 ), 0.016, new O { tint = C.rope, data = new[] { seed, 0, 0, 0 } } );
			B.pop();
		}

		public static void ropeCoil( Builder B, double x, double y, double z, double r0 = 0.09, double r1 = 0.32, int turns = 5, double seed = 0.5, double[] tint = null )
		{
			tint = tint ?? C.rope;
			var pts = new List<Vector3>();
			int n = turns * 12;
			for ( int i = 0; i <= n; i ++ )
			{
				double t = ( double ) i / n;
				double a = t * turns * Math.PI * 2;
				double r = r0 + ( r1 - r0 ) * t;
				pts.Add( new Vector3( x + Math.Cos( a ) * r, y + 0.02 + Math.Sin( a * 3.1 ) * 0.004, z + Math.Sin( a ) * r ) );
			}

			// loose tail
			var last = pts[ pts.Count - 1 ];
			pts.Add( new Vector3( last.x + 0.25, y + 0.02, last.z + 0.3 ) );
			B.tube( "rope", pts, 0.02, new O { radial = 4, tint = tint, data = new[] { seed, 0, 0, 0 } } );
			// a second, smaller layer on top
			var pts2 = new List<Vector3>();
			for ( int i = 0; i <= 24; i ++ )
			{
				double a = ( double ) i / 24 * Math.PI * 4 + 1.3;
				double r = r0 + 0.05 + ( r1 - r0 - 0.1 ) * i / 24;
				pts2.Add( new Vector3( x + Math.Cos( a ) * r, y + 0.055, z + Math.Sin( a ) * r ) );
			}

			B.tube( "rope", pts2, 0.02, new O { radial = 4, tint = tint, data = new[] { seed + 0.3, 0, 0, 0 } } );
		}

		// rope loop thrown over a bollard / post with a tail dropping over the edge
		public static void ropeLoop( Builder B, double x, double y, double z, double R, double[] tailTo = null, double seed = 0.5 )
		{
			B.torus( "rope", x, y, z, R, 0.022, new O { rx = 0.18, rz = 0.1, radial = 5, tubular = 14, tint = C.rope, data = new[] { seed, 0, 0, 0 } } );
			if ( tailTo != null )
				B.tube( "rope", sagPoints( P( x + R * 0.8, y - 0.02, z ), tailTo, 0.15, 8 ), 0.022, new O { tint = C.rope, data = new[] { seed, 0, 0, 0 } } );
		}

		public static void lifeRing( Builder B, double x, double y, double z, double ry = 0, double seed = 0.5 )
		{
			var orange = C.orange; var white = C.white;
			B.pushAt( x, y, z, ry );
			B.torus( "hard", 0, 0, 0, 0.29, 0.06, new O
			{
				rx = Math.PI / 2, radial = 8, tubular = 24,
				tint = Attr.Of( ( px, py, pz, i ) => Math.Floor( ( Math.Atan2( pz, px ) + Math.PI ) / ( Math.PI / 4 ) + 0.5 ) % 2 != 0 ? white : orange ),
				data = HARD( seed, 0, 0, 0.55 ),
			} );
			B.torus( "rope", 0, 0, 0, 0.345, 0.012, new O { rx = Math.PI / 2, radial = 4, tubular = 24, tint = C.white, data = new[] { seed, 0, 0, 0 } } );
			B.pop();
		}

		// ---------------------------------------------------------------------------
		// Lighting fixtures

		// hanging lantern; (x, y, z) = hook point. Returns the local center of the glass.
		public static double[] lantern( Builder B, double x, double y, double z, double seed = 0.5, double scale = 1 )
		{
			double s = scale;
			var t = C.iron; var d = HARD( seed, 0.35, 0.5, 0.45 );
			B.torus( "hard", x, y - 0.02 * s, z, 0.02 * s, 0.005 * s, new O { rx = Math.PI / 2, radial = 3, tubular = 6, tint = t, data = d } );
			B.cyl( "hard", x, y - 0.13 * s, z, 0.02 * s, 0.12 * s, 0.1 * s, new O { segs = 6, capTop = true, capBot = true, tint = t, data = d } );
			B.cyl( "hard", x, y - 0.155 * s, z, 0.125 * s, 0.125 * s, 0.025 * s, new O { segs = 6, capBot = true, tint = t, data = d } );
			double gy = y - 0.355 * s;
			B.cyl( "glass", x, gy, z, 0.085 * s, 0.075 * s, 0.2 * s, new O { segs = 6, capTop = false, tint = C.glassWarm, data = new[] { seed, 1, 1, 0 } } );
			for ( int i = 0; i < 4; i ++ )
			{
				double a = ( double ) i / 4 * Math.PI * 2 + Math.PI / 4;
				B.box( "hard", x + Math.Cos( a ) * 0.085 * s, gy + 0.1 * s, z + Math.Sin( a ) * 0.085 * s, 0.014 * s, 0.2 * s, 0.014 * s, new O { skip = 12, tint = t, data = d } );
			}

			B.cyl( "hard", x, gy - 0.04 * s, z, 0.1 * s, 0.07 * s, 0.04 * s, new O { segs = 6, capBot = true, tint = t, data = d } );
			return P( x, gy + 0.1 * s, z );
		}

		// Wall-mounted porch light: bracket out of a wall facing +z. Returns glass center (world).
		public static Vector3 wallLantern( Builder B, double x, double y, double z, double seed = 0.5 )
		{
			var t = C.iron; var d = HARD( seed, 0.3, 0.5, 0.45 );
			B.box( "hard", x, y, z + 0.01, 0.08, 0.16, 0.02, new O { tint = t, data = d } );
			B.rod( "hard", P( x, y + 0.02, z + 0.01 ), P( x, y + 0.05, z + 0.2 ), 0.01, 0.01, new O { segs = 5, tint = t, data = d } );
			var c = lantern( B, x, y + 0.05, z + 0.2, seed, 0.75 );
			return B.toWorld( c[ 0 ], c[ 1 ], c[ 2 ] );
		}

		// Wooden lamp post with iron arm and lantern. armDir: yaw the arm points to. Returns lantern center (local).
		public static Vector3 lampPost( Builder B, double x, double y, double z, double armYaw, double h = 3.2, double seed = 0.5 )
		{
			var wd = WOOD( seed, 0.75, 0.62, 0 );
			var pc = lin( 0xd6cfbd );
			B.box( "wood", x, y + h / 2, z, 0.14, h, 0.14, new O { grain = 1, tint = pc, data = wd } );
			B.box( "wood", x, y + h + 0.03, z, 0.18, 0.06, 0.18, new O { grain = 0, tint = pc, data = wd } );
			B.pushAt( x, y, z, armYaw );
			var t = C.iron; var d = HARD( seed, 0.35, 0.5, 0.45 );
			double ay = h - 0.18;
			B.box( "hard", 0, ay, 0.35, 0.035, 0.035, 0.62, new O { tint = t, data = d } );
			B.rod( "hard", P( 0, ay - 0.35, 0.075 ), P( 0, ay - 0.01, 0.4 ), 0.012, 0.012, new O { segs = 5, tint = t, data = d } );
			B.torus( "hard", 0, ay - 0.08, 0.2, 0.08, 0.008, new O { ry = Math.PI / 2, rz = Math.PI / 2, radial = 3, tubular = 8, tint = t, data = d } );
			var c = lantern( B, 0, ay - 0.02, 0.6, seed );
			var w = B.toWorld( c[ 0 ], c[ 1 ], c[ 2 ] );
			B.pop();
			return w;
		}

		// low boardwalk / path light: short post with a lantern on top. Returns world light position.
		public static Vector3 pathLight( Builder B, double x, double y, double z, double seed = 0.5 )
		{
			var wd = WOOD( seed, 0.8, 0, 0 );
			B.box( "wood", x, y + 0.55, z, 0.12, 1.1, 0.12, new O { grain = 1, tint = new double[] { 1, 1, 1 }, data = wd } );
			B.box( "wood", x, y + 1.12, z, 0.16, 0.04, 0.16, new O { grain = 0, tint = new double[] { 1, 1, 1 }, data = wd } );
			var t = C.iron; var d = HARD( seed, 0.35, 0.5, 0.45 );
			double gy = y + 1.14;
			B.cyl( "hard", x, gy, z, 0.07, 0.07, 0.03, new O { segs = 8, capBot = true, tint = t, data = d } );
			B.cyl( "glass", x, gy + 0.03, z, 0.06, 0.06, 0.15, new O { segs = 8, capTop = false, tint = C.glassWarm, data = new[] { seed, 1, 1, 0 } } );
			B.cyl( "hard", x, gy + 0.18, z, 0.015, 0.1, 0.07, new O { segs = 8, capBot = true, tint = t, data = d } );
			return B.toWorld( x, gy + 0.1, z );
		}

		// ---------------------------------------------------------------------------
		// Furniture

		public static void bench( Builder B, double x, double y, double z, double ry = 0, double len = 1.6, double seed = 0.5, double[] paint = null )
		{
			B.pushAt( x, y, z, ry );
			var tint = paint ?? new double[] { 1, 1, 1 };
			double[] wd( double s ) => WOOD( s, 0.8, paint != null ? 0.55 : 0, 0 );
			for ( int i = 0; i < 3; i ++ ) B.box( "wood", 0, 0.44, -0.13 + i * 0.13, len, 0.04, 0.11, new O { grain = 0, tint = tint, data = wd( seed + i * 0.1 ) } );
			foreach ( double sx in new[] { -len / 2 + 0.15, len / 2 - 0.15 } )
			{
				B.box( "wood", sx, 0.21, -0.13, 0.07, 0.42, 0.07, new O { grain = 1, tint = tint, data = wd( seed + 0.5 ) } );
				B.box( "wood", sx, 0.21, 0.13, 0.07, 0.42, 0.07, new O { grain = 1, tint = tint, data = wd( seed + 0.6 ) } );
				B.box( "wood", sx, 0.4, 0, 0.06, 0.05, 0.4, new O { grain = 2, tint = tint, data = wd( seed + 0.7 ) } );
				B.beam( "wood", P( sx, 0.42, -0.2 ), P( sx, 0.9, -0.28 ), 0.06, 0.05, new O { tint = tint, data = wd( seed + 0.8 ) } );
			}

			B.box( "wood", 0, 0.68, -0.245, len, 0.1, 0.03, new O { grain = 0, rx = -0.16, tint = tint, data = wd( seed + 0.3 ) } );
			B.box( "wood", 0, 0.84, -0.27, len, 0.1, 0.03, new O { grain = 0, rx = -0.16, tint = tint, data = wd( seed + 0.4 ) } );
			B.pop();
		}

		public static void bucket( Builder B, double x, double y, double z, double[] tint = null, double seed = 0.5 )
		{
			tint = tint ?? C.blue;
			B.lathe( "hard", x, y, z, new[]
			{
				new[] { 0.0, 0.0 }, new[] { 0.1, 0.0 }, new[] { 0.1, 0.0 }, new[] { 0.135, 0.28 }, new[] { 0.142, 0.295 }, new[] { 0.13, 0.29 }, new[] { 0.095, 0.03 }, new[] { 0.0, 0.03 },
			}, new O { segs = 12, tint = tint, data = HARD( seed, 0, 0, 0.5 ) } );
			B.torus( "hard", x, y + 0.29, z, 0.14, 0.006, new O { rz = Math.PI / 2, arc = Math.PI, radial = 4, tubular = 10, tint = C.galv, data = HARD( seed, 0.2, 0.8, 0.4 ) } );
		}

		// ---------------------------------------------------------------------------
		// Fish, lobsters and their displays: modelled and drawn by fish/FishProps.js (not ported yet). No-ops here.

		public static void fish( Builder B, double x, double y, double z, FishO o = null ) { }
		public static void iceBed( Builder B, double x, double y, double z, double r, double seed = 0.5 ) { }
		public static void bananaLeaf( Builder B, double x, double y, double z, double ry = 0, double len = 0.9, double seed = 0.5, double tilt = 0 ) { }
		public static void lobster( Builder B, double x, double y, double z, double ry = 0, double len = 0.3, double seed = 0.5, double rx = 0 ) { }

		// Twine from (a) down to a loop around a fish's tail stalk at (b).
		public static void fishTwine( Builder B, double[] a, double[] b, double seed = 0.5, double loop = 0.018 )
		{
			B.tube( "rope", new[] { new Vector3( a[ 0 ], a[ 1 ], a[ 2 ] ), new Vector3( b[ 0 ], b[ 1 ] + loop * 0.5, b[ 2 ] ) }, 0.0035, new O { radial = 3, tint = C.rope, data = new[] { seed, 0, 0, 0 } } );
			var pts = new List<Vector3>();
			for ( int i = 0; i <= 8; i ++ )
			{
				double t = ( double ) i / 8 * Math.PI * 2;
				pts.Add( new Vector3( b[ 0 ] + Math.Cos( t ) * loop, b[ 1 ] + Math.Sin( t * 2 ) * 0.003, b[ 2 ] + Math.Sin( t ) * loop * 0.6 ) );
			}

			B.tube( "rope", pts, 0.003, new O { radial = 3, tint = C.rope, data = new[] { seed, 0, 0, 0 } } );
		}

		// Galvanised S-hook hanging from a rail of radius railR at (x, yRail, z); the fish hangs from its
		// lower bend, at y = yRail - railR - 0.12 (returned).
		public static double sHook( Builder B, double x, double yRail, double z, double railR = 0.06, double seed = 0.5 )
		{
			var d = HARD( seed, 0.35, 0.85, 0.35 );
			double r = railR + 0.012;
			B.torus( "hard", x, yRail, z, r, 0.0045, new O { ry = Math.PI / 2, arc = Math.PI * 1.25, rz = -Math.PI * 0.1, radial = 4, tubular = 8, tint = C.galv, data = d } );
			double y0 = yRail - r;
			B.rod( "hard", P( x, y0 + 0.004, z - 0.004 ), P( x, y0 - 0.08, z ), 0.0045, 0.0045, new O { segs = 4, tint = C.galv, data = d } );
			B.torus( "hard", x, y0 - 0.1, z, 0.022, 0.0045, new O { ry = Math.PI / 2, rz = Math.PI, arc = Math.PI * 1.2, radial = 4, tubular = 8, tint = C.galv, data = d } );
			return y0 - 0.12;
		}

		public static void cleaningTable( Builder B, double x, double y, double z, double ry = 0, double seed = 0.5 )
		{
			B.pushAt( x, y, z, ry );
			double[] wd( double s ) => WOOD( s, 0.75, 0, 0 );
			for ( int i = 0; i < 5; i ++ ) B.box( "wood", 0, 0.88, -0.26 + i * 0.13, 1.35, 0.045, 0.12, new O { grain = 0, data = wd( seed + i * 0.13 ) } );
			foreach ( double sx in new[] { -0.6, 0.6 } ) foreach ( double sz in new[] { -0.27, 0.27 } ) B.box( "wood", sx, 0.43, sz, 0.07, 0.86, 0.07, new O { grain = 1, data = wd( seed + sx + sz ) } );
			B.box( "wood", 0, 0.25, 0, 1.25, 0.03, 0.5, new O { grain = 0, data = wd( seed + 0.9 ) } );
			B.box( "wood", 0, 0.8, -0.28, 1.25, 0.1, 0.03, new O { grain = 0, data = wd( seed + 0.4 ) } );
			// cutting board with a snapper cut behind the head, the knife beside it
			B.box( "wood", 0.2, 0.915, 0.02, 0.56, 0.025, 0.34, new O { grain = 0, tint = new[] { 1.25, 1.2, 1.1 }, data = WOOD( seed + 0.2, 0.25, 0, 0 ) } );
			double board = 0.928;
			fish( B, 0.17, board, 0.02 );
			fish( B, 0.21, board, 0.035 );
			// fillet knife: steel blade, dark wooden handle with brass rivets
			B.pushAt( 0.26, board + 0.002, 0.13, -0.45 );
			var blade = new[] { new[] { 0, 0.011 }, new[] { 0.13, 0.009 }, new[] { 0.175, 0.002 }, new[] { 0.19, -0.004 }, new[] { 0.12, -0.009 }, new[] { 0, -0.01 } };
			var bladePts = new Vector3[ blade.Length ];
			for ( int i = 0; i < blade.Length; i ++ ) bladePts[ i ] = new Vector3( blade[ i ][ 0 ], 0, blade[ i ][ 1 ] );
			B.slab( "hard", bladePts, 0.0016, new O { up = new Vector3( 0, 1, 0 ), tint = lin( 0xc8ccd0 ), data = HARD( seed, 0.05, 0.95, 0.18 ) } );
			B.box( "wood", -0.055, 0.009, 0, 0.11, 0.018, 0.024, new O { grain = 0, tint = C.black, data = WOOD( seed, 0.2, 0.8, 0 ) } );
			foreach ( double rx in new[] { -0.085, -0.03 } ) B.cyl( "hard", rx, 0.0175, 0, 0.0035, 0.0035, 0.002, new O { segs = 5, tint = lin( 0xb08d3a ), data = HARD( seed, 0.1, 0.9, 0.3 ) } );
			B.pop();
			// blood from the cut, smeared on the board
			var smear = new Vector3[ 9 ];
			for ( int i = 0; i < 9; i ++ )
			{
				double a = ( double ) i / 9 * Math.PI * 2;
				smear[ i ] = new Vector3( 0.215 + Math.Cos( a ) * 0.05 * ( 1 + 0.25 * Math.Sin( a * 3 + seed * 9 ) ), board + 0.0005, 0.04 + Math.Sin( a ) * 0.028 );
			}

			B.slab( "hard", smear, 0.0008, new O { up = new Vector3( 0, 1, 0 ), tint = lin( 0x4a0808 ), data = HARD( seed, 0, 0, 0.15 ) } );
			// a blackfin tuna waiting its turn
			fish( B, -0.33, 0.9025, -0.06 );
			bucket( B, -0.3, 0.265, 0.05, C.white, seed );
			B.pop();
		}

		// ---------------------------------------------------------------------------
		// Fishing gear

		// Fisherman's float. kind 0: egg float, 1: lobster spar buoy, 2: round float
		public static void buoy( Builder B, double x, double y, double z, double[] colA, double[] colB, int kind = 0, double seed = 0.5, O o = null )
		{
			o = o ?? new O();
			B.pushAt( x, y, z, o.ry ?? 0, o.rx ?? 0, o.rz ?? 0 );
			var d = HARD( seed, 0, 0, 0.42 );
			if ( kind == 0 )
			{
				B.lathe( "hard", 0, 0, 0, new[] { new[] { 0.0, 0.0 }, new[] { 0.08, 0.025 }, new[] { 0.125, 0.12 }, new[] { 0.1, 0.25 }, new[] { 0.04, 0.305 }, new[] { 0.0, 0.31 } }, new O
				{
					segs = 8, tint = Attr.Of( ( px, py, pz, i ) => py > 0.15 ? colA : colB ), data = d,
				} );
				B.torus( "hard", 0, 0.335, 0, 0.025, 0.008, new O { rx = Math.PI / 2, radial = 3, tubular = 6, tint = C.black, data = d } );
			}
			else if ( kind == 1 )
			{
				B.lathe( "hard", 0, 0, 0, new[] { new[] { 0.0, 0.0 }, new[] { 0.05, 0.0 }, new[] { 0.09, 0.08 }, new[] { 0.09, 0.32 }, new[] { 0.05, 0.4 }, new[] { 0.0, 0.4 } }, new O
				{
					segs = 8, tint = Attr.Of( ( px, py, pz, i ) => py > 0.14 && py < 0.26 ? colB : colA ), data = d,
				} );
				B.cyl( "wood", 0, 0.38, 0, 0.012, 0.014, 0.5, new O { segs = 5, data = WOOD( seed, 0.8, 0, 0 ) } );
				B.cyl( "wood", 0, -0.12, 0, 0.014, 0.012, 0.14, new O { segs = 5, data = WOOD( seed, 0.8, 0, 0 ) } );
			}
			else
			{
				B.lathe( "hard", 0, 0, 0, new[] { new[] { 0.0, 0.0 }, new[] { 0.08, 0.03 }, new[] { 0.09, 0.12 }, new[] { 0.0, 0.2 } }, new O { segs = 7, tint = colA, data = d } );
			}

			B.pop();
		}

		// a string of floats hanging from two points (on a wall or between posts)
		public static void buoyString( Builder B, double[] a, double[] b, int count, Rand rand, double sag = 0.25 )
		{
			var pts = sagPoints( a, b, sag, 10 );
			B.tube( "rope", pts, 0.01, new O { radial = 4, tint = C.rope, data = new[] { rand.next(), 0, 0, 0 } } );
			var pal = new[] { new[] { C.orange, C.white }, new[] { C.red, C.white }, new[] { C.yellow, C.black }, new[] { C.white, C.blue }, new[] { C.green, C.yellow }, new[] { C.orange, C.orange } };
			for ( int i = 0; i < count; i ++ )
			{
				double t = ( i + 0.5 ) / count;
				var p = pts[ ( int ) JS.Round( t * 10 ) ];
				var cc = rand.pick( pal );
				int kind = rand.chance( 0.5 ) ? 0 : 2;
				buoy( B, p.x, p.y - 0.34, p.z, cc[ 0 ], cc[ 1 ], kind, rand.next(), new O { rz = rand.range( -0.15, 0.15 ) } );
			}
		}

		// oar lying from p0 (grip) to p1 (blade tip)
		public static void oar( Builder B, double[] p0, double[] p1, double seed = 0.5, double[] bladeTint = null )
		{
			var v = new Vector3( p1[ 0 ] - p0[ 0 ], p1[ 1 ] - p0[ 1 ], p1[ 2 ] - p0[ 2 ] );
			double L = v.length();
			v.divideScalar( L );
			double bs = L - 0.55;
			var pb = P( p0[ 0 ] + v.x * bs, p0[ 1 ] + v.y * bs, p0[ 2 ] + v.z * bs );
			B.rod( "wood", p0, pb, 0.022, 0.024, new O { segs = 6, data = WOOD( seed, 0.55, 0, 0 ) } );
			B.beam( "wood", pb, p1, 0.14, 0.018, new O { tint = bladeTint ?? new double[] { 1, 1, 1 }, data = WOOD( seed + 0.2, 0.5, bladeTint != null ? 0.6 : 0, 0 ) } );
		}

		// ---------------------------------------------------------------------------
		// Rowboat. Local frame: origin at the keel midpoint, length along z (bow +z), gunwale at y = D.
		// upsideDown flips it over so it rests on its gunwales: the midship gunwale sits on the sand and the
		// ends (the sheer rises toward bow and stern) dig in, as a boat settles into soft sand. The inside of
		// the hull is kept so the space under it stays closed (dark, no sand seen through a gap).

		struct HullSection { public double halfB, sheer, keel, z; }

		static HullSection hullSection( double t, double L, double Bm, double D )
		{
			// t: 0 stern (transom) .. 1 bow (stem)
			double f = t < 0.42 ? 0.7 + 0.3 * Math.Sin( Math.PI / 2 * t / 0.42 ) : Math.Pow( Math.Cos( Math.PI / 2 * ( t - 0.42 ) / 0.58 ), 0.85 );
			double halfB = Bm / 2 * f;
			double sheer = D + 0.09 * Math.Pow( 2 * t - 1, 2 ) + 0.1 * t * t;
			double keel = 0.05 * Math.Pow( Math.Max( 0, 0.45 - t ) / 0.45, 2 ) + 0.42 * Math.Pow( Math.Max( 0, t - 0.62 ) / 0.38, 2.2 );
			return new HullSection { halfB = halfB, sheer = sheer, keel = keel, z = ( t - 0.5 ) * L };
		}

		sealed class HullV { public double[] p, n; public double s, t; }

		public static void rowboat( Builder B, double x, double y, double z, double ry = 0, RowboatO o = null )
		{
			o = o ?? new RowboatO();
			double L = o.length ?? 4.1, Bm = o.beam ?? 1.38, D = o.depth ?? 0.52;
			double seed = o.seed ?? 0.5;
			var hullCol = o.hull ?? lin( 0x2f7f8f );
			var bottomCol = o.bottom ?? lin( 0x8f3a2a );
			var trimCol = o.trim ?? C.white;
			bool upside = o.upsideDown;
			B.pushAt( x, y, z, ry, o.rx ?? 0, ( upside ? Math.PI : 0 ) + ( o.rz ?? 0 ) );
			if ( upside ) B.push( mat4( 0, -( D + 0.035 ), 0 ) );

			const int NT = 16, NS = 10;
			double[] secPoint( double t, double s )
			{
				// s in [-1, 1] across the section (port .. starboard): round bilge, slight V near the keel
				var S = hullSection( t, L, Bm, D );
				double a = Math.Abs( s );
				double ang = a * Math.PI / 2;
				double xx = S.halfB * JS.Sign( s ) * Math.Pow( Math.Sin( ang ), 0.85 );
				double yy = S.keel + ( S.sheer - S.keel ) * ( 1 - Math.Pow( Math.Cos( ang ), 1.25 ) );
				return new[] { xx, yy, S.z };
			}

			var outer = new List<HullV>(); var inner = new List<HullV>();
			const double eps = 1e-3;
			void buildSurface( double inset, List<HullV> list )
			{
				for ( int j = 0; j <= NS; j ++ )
				{
					for ( int i = 0; i <= NT; i ++ )
					{
						double t = 0.02 + 0.96 * i / NT, s = -1 + 2.0 * j / NS;
						var p = secPoint( t, s );
						var pt = secPoint( Math.Min( 1, t + eps ), s ); var pt2 = secPoint( Math.Max( 0, t - eps ), s );
						var ps = secPoint( t, Math.Min( 1, s + eps ) ); var ps2 = secPoint( t, Math.Max( -1, s - eps ) );
						var dt = new Vector3( pt[ 0 ] - pt2[ 0 ], pt[ 1 ] - pt2[ 1 ], pt[ 2 ] - pt2[ 2 ] );
						var ds = new Vector3( ps[ 0 ] - ps2[ 0 ], ps[ 1 ] - ps2[ 1 ], ps[ 2 ] - ps2[ 2 ] );
						var n = dt.clone().cross( ds ).normalize();
						// make the normal point outward (away from the boat centerline / downward)
						double cx = p[ 0 ], cy = p[ 1 ] - D * 0.9;
						if ( n.x * cx + n.y * cy < 0 ) n.negate();
						list.Add( new HullV { p = new[] { p[ 0 ] - n.x * inset, p[ 1 ] - n.y * inset, p[ 2 ] - n.z * inset }, n = new[] { n.x, n.y, n.z }, s = s, t = t } );
					}
				}
			}

			buildSurface( 0, outer );
			double girth( int j ) => Math.Abs( -1 + 2.0 * j / NS ) * ( Bm * 0.5 + D ) * 0.9;
			Part hullPart( List<HullV> list, bool flip ) => gridPart( NT, NS, ( i, j ) =>
			{
				var v = list[ j * ( NT + 1 ) + i ];
				return flip ? new GridVertex( v.p[ 0 ], v.p[ 1 ], v.p[ 2 ], -v.n[ 0 ], -v.n[ 1 ], -v.n[ 2 ], v.t * L, girth( j ) )
					: new GridVertex( v.p[ 0 ], v.p[ 1 ], v.p[ 2 ], v.n[ 0 ], v.n[ 1 ], v.n[ 2 ], v.t * L, girth( j ) );
			} );

			double waterline = D * 0.42;
			B.add( "wood", hullPart( outer, false ), new Matrix4(),
				Attr.Of( ( px, py, pz, i ) => py < waterline ? bottomCol : hullCol ),
				WOOD( seed, 0.5, 0.7, 1 ) );
			if ( ! upside || o.interior )
			{
				buildSurface( 0.03, inner );
				B.add( "wood", hullPart( inner, true ), new Matrix4(), lin( 0xd8d2c0 ), WOOD( seed + 0.3, 0.55, 0.6, 5 ) );
			}

			// gunwales, keel, transom
			foreach ( int side in new[] { -1, 1 } )
			{
				var pts = new List<Vector3>();
				for ( int i = 0; i <= NT; i ++ )
				{
					double t = 0.02 + 0.96 * i / NT;
					var p = secPoint( t, side );
					pts.Add( new Vector3( p[ 0 ] - side * 0.012, p[ 1 ] + 0.01, p[ 2 ] ) );
				}

				B.tube( "wood", pts, 0.028, new O { radial = 4, tint = trimCol, data = WOOD( seed + 0.1, 0.6, 0.65, 0 ) } );
			}

			var keelPts = new List<Vector3>();
			for ( int i = 0; i <= NT; i ++ )
			{
				double t = 0.02 + 0.96 * i / NT;
				var p = secPoint( t, 0 );
				keelPts.Add( new Vector3( 0, p[ 1 ] - 0.015, p[ 2 ] ) );
			}

			B.tube( "wood", keelPts, 0.028, new O { radial = 4, tint = bottomCol, data = WOOD( seed + 0.2, 0.6, 0.6, 0 ) } );
			var tr = new Vector3[ 11 ];
			for ( int j = 0; j <= 10; j ++ )
			{
				var p = secPoint( 0.02, -1 + 2.0 * j / 10 );
				tr[ j ] = new Vector3( p[ 0 ], p[ 1 ], p[ 2 ] );
			}

			B.add( "wood", slabPart( tr, 0.035, new Vector3( 1, 0, 0 ), new Vector3( 0, 0, -1 ) ), new Matrix4(), hullCol, WOOD( seed + 0.4, 0.55, 0.7, 6 ) );

			if ( ! upside )
			{
				// thwarts and oars
				foreach ( double t in new[] { 0.3, 0.62 } )
				{
					var S = hullSection( t, L, Bm, D );
					B.box( "wood", 0, S.sheer - 0.17, S.z, S.halfB * 1.85, 0.035, 0.22, new O { grain = 0, tint = trimCol, data = WOOD( seed + t, 0.6, 0.6, 0 ) } );
				}

				if ( o.oars )
				{
					oar( B, P( -0.3, D * 0.55, -1.2 ), P( -0.35, D * 0.4, 1.35 ), seed + 0.1 );
					oar( B, P( 0.32, D * 0.55, -1.1 ), P( 0.28, D * 0.45, 1.4 ), seed + 0.7 );
				}
			}

			if ( upside ) B.pop();
			B.pop();
		}

		// Derelict hull half buried in the sand: keel, broken ribs and a few surviving planks.
		// Local frame: length along z, keel at y = 0 (bury it by passing y below the sand).
		public static void wreck( Builder B, double x, double y, double z, double ry, Rand rand, double? length = null, double? beam = null, double? depth = null, double rx = 0, double rz = 0 )
		{
			double L = length ?? 7.5, Bm = beam ?? 2.6, D = depth ?? 1.3;
			B.pushAt( x, y, z, ry, rx, rz );
			double[] wd() { double s = rand.next(); return WOOD( s, rand.range( 0.9, 1.0 ) ); }
			var tone = new[] { 0.78, 0.74, 0.7 };
			var keel = new List<Vector3>();
			for ( int i = 0; i <= 10; i ++ )
			{
				double t = ( double ) i / 10;
				keel.Add( new Vector3( 0, 0.12 * Math.Pow( 2 * t - 1, 4 ) + ( t > 0.85 ? ( t - 0.85 ) * 3.5 : 0 ), ( t - 0.5 ) * L ) );
			}

			B.tube( "wood", keel, 0.1, new O { radial = 5, tint = tone, data = wd() } );
			int nr = 11;
			for ( int i = 1; i < nr; i ++ )
			{
				double t = ( double ) i / nr;
				double half = Bm / 2 * Math.Pow( Math.Sin( Math.PI * ( 0.08 + 0.84 * t ) ), 0.7 );
				double zz = ( t - 0.5 ) * L;
				foreach ( int side in new[] { -1, 1 } )
				{
					if ( rand.chance( 0.18 ) ) continue;
					double keep = rand.chance( 0.35 ) ? rand.range( 0.35, 0.8 ) : 1; // broken ribs
					var pts = new List<Vector3>();
					for ( int k = 0; k <= 6; k ++ )
					{
						double a = ( double ) k / 6 * keep;
						double ang = a * Math.PI / 2;
						pts.Add( new Vector3( side * half * Math.Sin( ang ), D * ( 1 - Math.Cos( ang ) ) * 1.05 + 0.05, zz + rand.range( -0.02, 0.02 ) ) );
					}

					B.tube( "wood", pts, 0.055, new O { radial = 4, tint = tone, data = wd() } );
				}
			}

			// a few planks clinging to one side
			for ( int k = 0; k < 3; k ++ )
			{
				double a = ( 0.25 + k * 0.14 ) * Math.PI / 2;
				double t0 = 0.2 + rand.range( 0, 0.1 ), t1 = 0.55 + rand.range( 0, 0.2 );
				var p0 = P( Bm / 2 * 0.95 * Math.Sin( a ) + 0.05, D * ( 1 - Math.Cos( a ) ) + 0.05, ( t0 - 0.5 ) * L );
				var p1 = P( Bm / 2 * 0.95 * Math.Sin( a ) + 0.05, D * ( 1 - Math.Cos( a ) ) + 0.05, ( t1 - 0.5 ) * L );
				B.beam( "wood", p0, p1, 0.035, 0.2, new O { roll = -a, tint = tone, data = wd() } );
			}

			B.pop();
		}

		// ---------------------------------------------------------------------------
		// Racks, fences, stands

		// Net drying rack: two T-posts with a pole, a net draped over it. Local frame: along x.
		public static void netRack( Builder B, double x, double gy, double z, double ry, double len = 3.2, double[] netTint = null, double seed = 0.5, Func<double, double, double> groundFn = null )
		{
			netTint = netTint ?? lin( 0x3f6f5f );
			B.pushAt( x, 0, z, ry );
			double h = 1.95;
			var wd = WOOD( seed, 0.85, 0, 0 );
			foreach ( double sx in new[] { -len / 2, len / 2 } )
			{
				double g = groundFn != null ? groundFn( sx, 0 ) : gy;
				B.cyl( "wood", sx, g - 0.4, 0, 0.055, 0.065, h + 0.45, new O { segs = 6, data = wd } );
				B.box( "wood", sx, g + h - 0.02, 0, 0.08, 0.08, 0.6, new O { grain = 2, data = wd } );
			}

			double gm = groundFn != null ? ( groundFn( -len / 2, 0 ) + groundFn( len / 2, 0 ) ) / 2 : gy;
			B.rod( "wood", P( -len / 2 - 0.2, gm + h + 0.05, 0 ), P( len / 2 + 0.2, gm + h + 0.05, 0 ), 0.04, 0.04, new O { segs = 6, data = WOOD( seed + 0.3, 0.8, 0, 0 ) } );
			// draped net
			const int NX = 18, NY = 12;
			double top = gm + h + 0.09;
			double drop = h - 0.35;
			var part = gridPart( NX, NY, ( i, j ) =>
			{
				double u = ( double ) i / NX, v = ( double ) j / NY; // v: 0 front bottom -> 1 back bottom
				double xx = ( u - 0.5 ) * ( len - 0.3 );
				double side = v < 0.5 ? 1 : -1;
				double a = Math.Abs( v - 0.5 ) * 2; // 0 at the pole, 1 at the bottom edges
				double fold = Math.Sin( u * 23 + v * 3 ) * 0.04 + Math.Sin( u * 7.3 ) * 0.05;
				double yy = top - a * drop + Math.Sin( u * Math.PI ) * a * 0.1;
				double zz = side * ( 0.04 + a * 0.22 + fold * a );
				return new GridVertex( xx, yy, zz, 0, 0.2, side, xx, v * drop * 2 );
			} );
			B.add( "net", part, new Matrix4(), netTint, Attr.Of( ( px, py, pz, i ) => new[] { seed, Math.Min( 1, Math.Max( 0, ( top - py ) / drop ) ) * 0.8, 0.045, 0 } ) );
			// float line along the bottom edges
			foreach ( int side in new[] { -1, 1 } )
			{
				for ( int i = 0; i < 5; i ++ )
				{
					double xx = ( ( i + 0.5 ) / 5 - 0.5 ) * ( len - 0.4 );
					buoy( B, xx, top - drop - 0.06, side * 0.27, C.orange, C.orange, 2, seed + i * 0.1, new O { rx = side * 0.2 } );
				}
			}

			B.pop();
		}

		// A-frame fish drying rack with rows of split, salted fish hung by the tail. Local frame along x.
		public static void fishRack( Builder B, double x, double gy, double z, double ry, double len = 2.6, double seed = 0.5, Rand rand = null )
		{
			B.pushAt( x, gy, z, ry );
			var wd = WOOD( seed, 0.9, 0, 0 );
			foreach ( double sx in new[] { -len / 2, len / 2 } )
			{
				B.rod( "wood", P( sx, -0.2, -0.7 ), P( sx, 2.05, 0.05 ), 0.035, 0.03, new O { segs = 5, data = wd } );
				B.rod( "wood", P( sx, -0.2, 0.7 ), P( sx, 2.05, -0.05 ), 0.035, 0.03, new O { segs = 5, data = wd } );
			}

			var fr = new Rand( new Mulberry32( ToUint32( Math.Floor( seed * 4294967296 ) ) ) ); // the caller's sequence stays as it was
			foreach ( var pp in new[] { new[] { 1.95, 0 }, new[] { 1.25, -0.36 }, new[] { 1.25, 0.36 } } )
			{
				double py = pp[ 0 ], pz = pp[ 1 ];
				B.rod( "wood", P( -len / 2 - 0.15, py, pz ), P( len / 2 + 0.15, py, pz ), 0.025, 0.025, new O { segs = 5, data = WOOD( seed + py, 0.85, 0, 0 ) } );
				int n = ( int ) Math.Floor( len / 0.22 );
				for ( int i = 0; i < n; i ++ )
				{
					if ( rand != null && rand.chance( 0.4 ) ) continue;
					double l = ( rand != null ? rand.range( 0.3, 0.42 ) : 0.36 ) * 1.35;
					double xx = -len / 2 + 0.15 + i * ( len - 0.3 ) / ( n - 1 ) + fr.range( -0.03, 0.03 );
					double drop = fr.range( 0.05, 0.1 );
					double s = seed + i * 0.07 + py;
					fishTwine( B, P( xx, py - 0.02, pz ), P( xx, py - drop, pz ), s, 0.012 );
					// the fish itself (not ported): its options draw from the fish sequence
					fr.range( -0.25, 0.25 ); fr.range( -0.25, 0.25 ); fr.range( -0.35, 0.1 );
					fish( B, xx, py - drop, pz );
				}
			}

			B.pop();
		}

		public static uint ToUint32( double x ) => double.IsNaN( x ) || double.IsInfinity( x ) ? 0u : unchecked( ( uint ) ( long ) Math.Truncate( x ) );

		// Fence along a local polyline of [x, z] points. style: 'picket' | 'rail'
		public static void fence( Builder B, double[][] pts, Func<double, double, double> groundFn, string style = "picket", double[] tint = null, double seed = 0.5, Colliders colliders = null, Func<double, double, Vector3> toWorld = null )
		{
			tint = tint ?? C.white;
			bool painted = style == "picket";
			var one = new double[] { 1, 1, 1 };
			for ( int s = 0; s < pts.Length - 1; s ++ )
			{
				double x0 = pts[ s ][ 0 ], z0 = pts[ s ][ 1 ], x1 = pts[ s + 1 ][ 0 ], z1 = pts[ s + 1 ][ 1 ];
				double L = JS.Hypot( x1 - x0, z1 - z0 );
				int n = ( int ) Math.Max( 1, JS.Round( L / 2.0 ) );
				double yaw = Math.Atan2( x1 - x0, z1 - z0 );
				for ( int i = 0; i <= n; i ++ )
				{
					if ( s > 0 && i == 0 ) continue;
					double t = ( double ) i / n;
					double px = x0 + ( x1 - x0 ) * t, pz = z0 + ( z1 - z0 ) * t;
					double g = groundFn( px, pz );
					B.box( "wood", px, g + 0.42, pz, 0.09, 1.1, 0.09, new O { grain = 1, tint = painted ? tint : one, data = WOOD( seed + i * 0.1, 0.8, painted ? 0.5 : 0, 0 ) } );
				}

				for ( int i = 0; i < n; i ++ )
				{
					double ta = ( double ) i / n, tb = ( double ) ( i + 1 ) / n;
					double ax = x0 + ( x1 - x0 ) * ta, az = z0 + ( z1 - z0 ) * ta, bx = x0 + ( x1 - x0 ) * tb, bz = z0 + ( z1 - z0 ) * tb;
					double ga = groundFn( ax, az ), gb = groundFn( bx, bz );
					foreach ( double ry in painted ? new[] { 0.25, 0.7 } : new[] { 0.35, 0.78 } )
					{
						B.beam( "wood", P( ax, ga + ry, az ), P( bx, gb + ry, bz ), 0.03, 0.08, new O { tint = painted ? tint : one, data = WOOD( seed + ry + i, 0.8, painted ? 0.45 : 0, 0 ) } );
					}

					if ( painted )
					{
						double L2 = JS.Hypot( bx - ax, bz - az );
						int np = ( int ) Math.Floor( L2 / 0.14 );
						for ( int k = 0; k < np; k ++ )
						{
							double tt = ( k + 0.5 ) / np;
							double px = ax + ( bx - ax ) * tt, pz = az + ( bz - az ) * tt;
							double g = ga + ( gb - ga ) * tt;
							double h = 0.9 + ( ( k * 7 + i * 3 ) % 5 ) * 0.012;
							B.box( "wood", px, g + h / 2 - 0.05, pz, 0.075, h, 0.022, new O { grain = 1, ry = yaw + Math.PI / 2, tint = tint, data = WOOD( seed + k * 0.37, 0.8, 0.45, 0 ) } );
							B.box( "wood", px, g + h - 0.03, pz, 0.053, 0.053, 0.022, new O { grain = 1, ry = yaw + Math.PI / 2, rz = Math.PI / 4, tint = tint, data = WOOD( seed + k * 0.37, 0.8, 0.45, 0 ) } );
						}
					}
				}

				if ( colliders != null && toWorld != null )
				{
					var a = toWorld( x0, z0 ); var b = toWorld( x1, z1 );
					double cx = ( a.x + b.x ) / 2, cz = ( a.z + b.z ) / 2;
					double g = Math.Max( groundFn( x0, z0 ), groundFn( x1, z1 ) );
					colliders.addBox( new Vector3( cx, g + 0.5, cz ), new Vector3( 0.06, 0.6, L / 2 ), Math.Atan2( b.x - a.x, b.z - a.z ), tag: "fence" );
				}
			}
		}

		// Water tank on a timber stand. Returns height of the stand top.
		public static double waterTank( Builder B, double x, double gy, double z, double seed = 0.5, bool galvanized = true )
		{
			var wd = WOOD( seed, 0.85, 0, 0 );
			double h = 1.7, s = 0.62;
			foreach ( double sx in new[] { -s, s } ) foreach ( double sz in new[] { -s, s } ) B.box( "wood", x + sx, gy + h / 2 - 0.2, z + sz, 0.12, h + 0.4, 0.12, new O { grain = 1, data = wd } );
			foreach ( double sz in new[] { -s, s } ) B.beam( "wood", P( x - s, gy + 0.3, z + sz ), P( x + s, gy + h - 0.2, z + sz ), 0.04, 0.12, new O { data = wd } );
			for ( int i = 0; i < 9; i ++ ) B.box( "wood", x, gy + h + 0.02, z - 0.7 + i * 0.175, 1.5, 0.045, 0.16, new O { grain = 0, data = WOOD( seed + i * 0.1, 0.85, 0, 0 ) } );
			double ty = gy + h + 0.045;
			if ( galvanized )
			{
				B.cyl( "roofMetal", x, ty, z, 0.6, 0.6, 1.25, new O { segs = 20, capTop = false, swapUV = true, tint = C.galv, data = new[] { seed, 0.55, 1, 0 } } );
				B.cyl( "roofMetal", x, ty + 1.25, z, 0.08, 0.62, 0.22, new O { segs = 20, swapUV = false, tint = C.galv, data = new[] { seed + 0.5, 0.6, 1, 0 } } );
			}
			else
			{
				B.cyl( "hard", x, ty, z, 0.6, 0.6, 1.25, new O { segs = 20, capTop = false, tint = lin( 0x2a2e2c ), data = HARD( seed, 0, 0, 0.55 ) } );
				B.cyl( "hard", x, ty + 1.25, z, 0.1, 0.6, 0.15, new O { segs = 20, tint = lin( 0x2a2e2c ), data = HARD( seed, 0, 0, 0.55 ) } );
			}

			B.rod( "hard", P( x + 0.5, ty + 0.1, z ), P( x + 0.8, ty + 0.1, z ), 0.03, 0.03, new O { segs = 6, tint = C.galv, data = HARD( seed, 0.4, 0.8, 0.4 ) } );
			B.rod( "hard", P( x + 0.8, ty + 0.1, z ), P( x + 0.8, gy + 0.6, z ), 0.03, 0.03, new O { segs = 6, tint = C.galv, data = HARD( seed, 0.4, 0.8, 0.4 ) } );
			return ty;
		}

		// stack of firewood logs against something; local frame along x
		public static void woodpile( Builder B, double x, double gy, double z, double ry, Rand rand )
		{
			B.pushAt( x, gy, z, ry );
			for ( int row = 0; row < 4; row ++ )
			{
				int n = 6 - ( row > 2 ? 1 : 0 );
				for ( int i = 0; i < n; i ++ )
				{
					double r = rand.range( 0.055, 0.075 );
					double xx = -0.5 + i * 0.16 + ( row % 2 ) * 0.08;
					double ht = 0.5 + rand.range( -0.05, 0.05 );
					double ryy = rand.range( -0.05, 0.05 );
					B.cyl( "wood", xx, r + row * 0.13, -0.25, r, r, ht, new O
					{
						segs = 7, capTop = true, capBot = true, rx = Math.PI / 2, ry = ryy,
						tint = new[] { 1.05, 0.95, 0.85 }, data = WOOD( rand.next(), 0.35, 0, 0 ),
					} );
				}
			}

			B.pop();
		}

		// Pennant flag on a pole. The flag material orients the cloth downwind on the GPU.
		public static void flagPole( Builder B, double x, double gy, double z, double h = 5, double[] flagTint = null, double seed = 0.5 )
		{
			flagTint = flagTint ?? C.red;
			B.cyl( "wood", x, gy - 0.3, z, 0.035, 0.06, h + 0.3, new O { segs = 7, tint = C.white, data = WOOD( seed, 0.6, 0.6, 0 ) } );
			B.lathe( "hard", x, gy + h, z, new[] { new[] { 0.0, 0.0 }, new[] { 0.05, 0.02 }, new[] { 0.05, 0.06 }, new[] { 0.0, 0.09 } }, new O { segs = 8, tint = C.brass, data = HARD( seed, 0.2, 0.9, 0.35 ) } );
			var w = B.toWorld( x, 0, z );
			double L = 1.3, H = 0.55;
			// geometry is laid out at the pole's world position (for correct culling bounds); the flag
			// material swings it downwind on the GPU using vdata (distance from pole, pole x/z)
			var part = gridPart( 10, 3, ( i, j ) =>
			{
				double a = ( double ) i / 10 * L;
				double hh = H * ( 1 - 0.75 * i / 10 );
				double yy = gy + h - 0.1 - ( ( double ) j / 3 - 0.5 ) * hh - H / 2;
				return new GridVertex( a, yy, 0, 0, 0, 1, a, ( double ) j / 3 * hh );
			} );
			var m = B.frame.clone().invert().multiply( new Matrix4().makeTranslation( w.x, 0, w.z ) );
			B.add( "flag", part, m, flagTint, Attr.Of( ( px, py, pz, i ) => new[] { seed, px, w.x, w.z } ) );
		}

		// Laundry line between two local points with a few hanging cloths.
		public static void laundryLine( Builder B, double[] a, double[] b, Rand rand, double[][] colors )
		{
			var pts = sagPoints( a, b, 0.18, 10 );
			B.tube( "rope", pts, 0.006, new O { radial = 3, tint = C.white, data = new[] { rand.next(), 0, 0, 0 } } );
			var dir = new Vector3( b[ 0 ] - a[ 0 ], 0, b[ 2 ] - a[ 2 ] ).normalize();
			double yaw = Math.Atan2( dir.x, dir.z ) - Math.PI / 2;
			int n = rand.@int( 3, 5 );
			for ( int i = 0; i < n; i ++ )
			{
				double t = ( i + 0.7 ) / ( n + 0.6 );
				var p = pts[ ( int ) JS.Round( t * 10 ) ];
				double w = rand.range( 0.4, 0.75 ), h = rand.range( 0.45, 0.8 );
				var part = gridPart( 3, 4, ( ii, jj ) =>
				{
					double x = ( ( double ) ii / 3 - 0.5 ) * w, y = -( double ) jj / 4 * h;
					return new GridVertex( x, y, Math.Sin( ii * 1.9 + jj ) * 0.02, 0, 0, 1, x + w / 2, -y );
				} );
				var m = mat4( p.x, p.y + 0.01, p.z, yaw );
				double cs = rand.next();
				B.add( "cloth", part, m, rand.pick( colors ), Attr.Of( ( px, py, pz, ix ) => new[] { cs, Math.Min( 1, -py / 0.8 ), 0, 0 } ) );
			}
		}

		// ---------------------------------------------------------------------------
		// Instanced props (barrels, crates, lobster traps)

		public static void buildBarrelProto( Builder B )
		{
			var prof = new double[ 7 ][];
			for ( int i = 0; i <= 6; i ++ )
			{
				double y = ( double ) i / 6 * 0.88;
				prof[ i ] = new[] { 0.25 + 0.035 * Math.Sin( Math.PI * y / 0.88 ), y };
			}

			B.lathe( "wood", 0, 0, 0, prof, new O { segs = 14, rRef = 0.27, tint = new double[] { 1, 1, 1 }, data = WOOD( 0.3, 0.55, 0, 5 ) } );
			B.cyl( "wood", 0, 0.82, 0, 0.255, 0.255, 0.01, new O { segs = 14, capTop = true, tint = new[] { 0.95, 0.9, 0.85 }, data = WOOD( 0.7, 0.6, 0, 6 ) } );
			foreach ( double y in new[] { 0.08, 0.26, 0.62, 0.8 } )
			{
				double r = 0.25 + 0.035 * Math.Sin( Math.PI * y / 0.88 ) + 0.004;
				B.cyl( "hard", 0, y - 0.025, 0, r, r, 0.05, new O { segs = 14, capTop = false, tint = C.iron, data = HARD( 0.4, 0.45, 0.45, 0.55 ) } );
			}
		}

		public static void buildCrateProto( Builder B )
		{
			double w = 0.62, d = 0.42, h = 0.4;
			double[] wd( double s ) => WOOD( s, 0.65, 0, 0 );
			foreach ( int sx in new[] { -1, 1 } ) foreach ( int sz in new[] { -1, 1 } )
			{
				B.box( "wood", sx * ( w / 2 - 0.02 ), h / 2, sz * ( d / 2 - 0.02 ), 0.04, h, 0.04, new O { grain = 1, data = wd( 0.1 + sx * 0.2 + sz * 0.3 ) } );
			}

			for ( int i = 0; i < 3; i ++ )
			{
				double y = 0.07 + i * 0.13;
				foreach ( int sz in new[] { -1, 1 } ) B.box( "wood", 0, y, sz * ( d / 2 - 0.005 ), w - 0.02, 0.09, 0.012, new O { grain = 0, data = wd( 0.2 + i * 0.1 + sz * 0.05 ) } );
				foreach ( int sx in new[] { -1, 1 } ) B.box( "wood", sx * ( w / 2 - 0.005 ), y, 0, 0.012, 0.09, d - 0.02, new O { grain = 2, data = wd( 0.5 + i * 0.1 + sx * 0.05 ) } );
			}

			B.box( "wood", 0, 0.015, 0, w - 0.03, 0.02, d - 0.03, new O { grain = 0, data = wd( 0.8 ) } );
			for ( int i = 0; i < 3; i ++ ) B.box( "wood", 0, h - 0.01, -d / 2 + 0.08 + i * 0.13, w, 0.018, 0.09, new O { grain = 0, skip = 8, data = wd( 0.9 + i * 0.1 ) } );
		}

		public static void buildTrapProto( Builder B )
		{
			// wooden slat lobster pot: 0.9 long (x), 0.5 wide (z), half-round top
			double L = 0.9, W = 0.5, R = 0.25;
			double[] wd( double s ) => WOOD( s, 0.8, 0, 0 );
			foreach ( int sz in new[] { -1, 1 } ) B.box( "wood", 0, 0.025, sz * ( W / 2 - 0.02 ), L, 0.05, 0.04, new O { grain = 0, data = wd( 0.1 + sz * 0.1 ) } );
			for ( int i = 0; i < 5; i ++ ) B.box( "wood", 0, 0.052, -W / 2 + 0.06 + i * 0.095, L - 0.04, 0.012, 0.06, new O { grain = 0, data = wd( 0.3 + i * 0.05 ) } );
			foreach ( double sx in new[] { -L / 2 + 0.03, 0, L / 2 - 0.03 } )
			{
				B.torus( "wood", sx, 0.05, 0, R - 0.01, 0.014, new O { rx = -Math.PI / 2, rz = Math.PI / 2, arc = Math.PI, radial = 3, tubular = 8, tint = new[] { 0.9, 0.85, 0.8 }, data = wd( 0.6 + sx ) } );
			}

			for ( int i = 0; i < 7; i ++ )
			{
				double a = ( i + 0.5 ) / 7 * Math.PI;
				double yy = 0.05 + Math.Sin( a ) * ( R - 0.005 ), zz = Math.Cos( a ) * ( R - 0.005 );
				B.box( "wood", 0, yy, zz, L - 0.02, 0.012, 0.045, new O { grain = 0, rx = -( a - Math.PI / 2 ), data = wd( 0.7 + i * 0.05 ) } );
			}

			// net ends (half discs)
			foreach ( double sx in new[] { -L / 2 + 0.03, L / 2 - 0.03 } )
			{
				var pts = new Vector3[ 11 ];
				for ( int i = 0; i <= 10; i ++ )
				{
					double a = ( double ) i / 10 * Math.PI;
					pts[ i ] = new Vector3( sx, 0.05 + Math.Sin( a ) * ( R - 0.01 ), Math.Cos( a ) * ( R - 0.01 ) );
				}

				B.add( "net", slabPart( pts, 0.0, new Vector3( 0, 0, 1 ), new Vector3( 1, 0, 0 ) ), new Matrix4(), lin( 0x4a6a58 ), new[] { 0.5, 0, 0.035, 0 } );
			}

			B.tube( "rope", sagPoints( P( -0.1, 0.05 + R, 0 ), P( 0.1, 0.05 + R, 0 ), -0.12, 6 ), 0.01, new O { radial = 4, tint = C.ropeBlue, data = new[] { 0.3, 0, 0, 0 } } );
		}
	}

	// Repeated props (barrels, crates, lobster traps). Each prototype is built once; every add()
	// stamps a transformed copy straight into the merged static batches of the target builder,
	// so repeated props cost no extra draw calls or shadow casters.
	public sealed class InstancedProps
	{
		readonly Builder target;
		readonly Dictionary<string, Builder> protos = new Dictionary<string, Builder>();
		readonly Dictionary<string, int> counts = new Dictionary<string, int> { { "barrel", 0 }, { "crate", 0 }, { "trap", 0 } };
		readonly Matrix4 _m = new Matrix4();

		public InstancedProps( Builder target )
		{
			this.target = target;
			protos[ "barrel" ] = proto( Props.buildBarrelProto );
			protos[ "crate" ] = proto( Props.buildCrateProto );
			protos[ "trap" ] = proto( Props.buildTrapProto );
		}

		static Builder proto( Action<Builder> fn ) { var B = new Builder(); fn( B ); return B; }

		public void add( string type, double x, double y, double z, double ry = 0, double[] color = null, double rx = 0, double rz = 0 )
		{
			var p = protos[ type ];
			int n = counts[ type ] ++;
			double seedOffset = ( ( n * 0.6180339887 + type.Length * 0.137 ) % 1 ) * 7.0;
			GeoBuilder.mat4( x, y, z, ry, rx, rz, _m );
			foreach ( var kv in p.batches ) target.batch( kv.Key ).addBatch( kv.Value, _m, color, seedOffset );
		}

		public int count { get { int n = 0; foreach ( var v in counts.Values ) n += v; return n; } }
	}
}
