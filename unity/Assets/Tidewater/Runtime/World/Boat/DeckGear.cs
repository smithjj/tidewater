using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullLines;
using static Tidewater.World.Boat.HullBuilder;

// Port of src/world/boat/DeckGear.js: the working boat's deck gear: pot hauler and davit, coiled warp, buoys, fenders, cleats, the bow
// fittings and anchor, bait tote and barrel, stern light and ensign.
namespace Tidewater.World.Boat
{
	public static class TRAP { public const double L = 0.95, W = 0.55, H = 0.37; }

	public static class HAULER { public const double x = -1.0, z = -0.62, y = 1.34; }

	public static class FLAG { public const double w = 0.5, h = 0.33; }

	public static class DeckGear
	{
		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );

		static readonly Opts STAINLESS = new Opts { color = Palette.stainless, rough = 0.22, metal = 1 };
		static readonly Opts GALV = new Opts { color = 0xa3a7ab, rough = 0.35, metal = 1 };
		static readonly Opts BLACK = new Opts { color = 0x1a1b1d, rough = 0.55, metal = 0 };

		// A pot's footprint, and where the gear sits on the working boat's deck: [x, y (bottom level), z, yaw, colour for the procedural
		// stand-in]. game/Traps.js places the modelled pots from this layout, and the rope coil below sits on top of the stack.
		public static readonly double[][] TRAPS = {
			new[] { 0.66, 0, -3.25, 0.02, 0xd8b21c },
			new[] { 0.66, 1, -3.23, -0.03, 0x2f7a3c },
			new[] { 0.64, 0, -2.2, -0.04, 0xd8b21c },
			new[] { -0.68, 0, -3.25, 0.03, 0x1f2326 },
		};

		public static readonly double[][] FENDERS = { new[] { -1.0, -2.4 }, new[] { -1.0, -1.05 }, new[] { -1.0, 0.65 }, new[] { 1.0, -1.7 } };

		public struct GeoOpts { public BufferGeometry geometry; public Opts opts; }

		static readonly double[] DEFAULT_BUOY_COLORS = { 0xff6a13, 0xf4f1ea, 0x1d4f9c };

		// Maine lobster buoy (foam bullet in the owner's colours) with its spindle stick. Returns geometry / options pairs for the
		// fittings bucket; origin at the buoy's base.
		public static List<GeoOpts> buoyGeometry( double[] colors = null, double stick = 0.35 )
		{
			colors = colors ?? DEFAULT_BUOY_COLORS;
			double[][][] bands = {
				new[] { new[] { 0.0, 0 }, new[] { 0.03, 0.004 }, new[] { 0.055, 0.025 }, new[] { 0.07, 0.07 }, new[] { 0.075, 0.12 }, new[] { 0.075, 0.17 } },
				new[] { new[] { 0.075, 0.17 }, new[] { 0.075, 0.22 }, new[] { 0.075, 0.27 } },
				new[] { new[] { 0.075, 0.27 }, new[] { 0.074, 0.33 }, new[] { 0.068, 0.39 }, new[] { 0.052, 0.435 }, new[] { 0.03, 0.455 }, new[] { 0.0, 0.46 } },
			};
			var o = new List<GeoOpts>();
			for ( int i = 0; i < bands.Length; i ++ ) o.Add( new GeoOpts { geometry = GK.lathe( bands[ i ], 12 ), opts = new Opts { color = colors[ i ], rough = 0.55 } } );
			o.Add( new GeoOpts { geometry = GK.cylinder( 0.011, 0.013, 0.46 + stick + 0.06, 6 ).translate( 0, ( 0.46 + stick - 0.06 ) / 2, 0 ), opts = new Opts { color = 0x9c7a4c, rough = 0.75 } } );
			return o;
		}

		public static void buildDeckGear( GeoKit kit, HullLines L, WheelhouseParts parts )
		{
			buildHauler( kit, L );
			buildCoils( kit, L );
			buildBuoys( kit, L );
			buildFenders( kit, L );
			buildCleats( kit, L );
			buildBow( kit, L );
			buildContainers( kit, L );
			buildStern( kit, L, parts );
		}

		// ------------------------------------------------------------------ lobster traps

		static void buildHauler( GeoKit kit, HullLines L )
		{
			double t = L.tAtSheerZ( -0.78 );
			double xg = -( L.sheerX( t ) - 0.035 ), yg = L.sheerY( t ) + 0.045;

			// davit arm from the rail up and outboard, with a snatch block
			var pts = new List<Vector3> { V( xg, yg, -0.8 ), V( xg - 0.02, yg + 0.45, -0.78 ), V( xg - 0.05, 1.86, -0.76 ), V( xg - 0.13, 2.06, -0.74 ), V( xg - 0.3, 2.11, -0.73 ), V( xg - 0.43, 2.07, -0.72 ) };
			kit.add( "fittings", GK.tube( pts, 0.03, 32, 8 ), STAINLESS );
			var foot = GK.roundedBox( 0.12, 0.02, 0.16, 0.008, 1 );
			foot.translate( xg, yg + 0.01, -0.8 );
			kit.add( "fittings", foot, STAINLESS );
			var tip = pts[ pts.Count - 1 ];
			var tipCap = GK.sphere( 0.03, 10, 6 );
			tipCap.translate( tip.x, tip.y, tip.z );
			kit.add( "fittings", tipCap, STAINLESS );
			kit.add( "fittings", GK.rod( tip, tip.clone().add( V( 0, -0.12, 0 ) ), 0.01, 6 ), STAINLESS );
			var bc = tip.clone().add( V( 0, -0.22, 0 ) );
			foreach ( double dz in new[] { -0.022, 0.022 } )
			{
				var cheek = GK.cylinder( 0.085, 0.085, 0.01, 16 );
				cheek.applyMatrix4( GK.mat4( bc.x, bc.y, bc.z + dz, Math.PI / 2, 0, 0 ) );
				kit.add( "fittings", cheek, GALV );
			}

			var sheave = GK.cylinder( 0.07, 0.07, 0.034, 16 );
			sheave.applyMatrix4( GK.mat4( bc.x, bc.y, bc.z, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", sheave, new Opts { color = 0x2b2d30, rough = 0.6 } );

			// hauler: post, hydraulic motor and V-groove sheave facing aft
			double x = HAULER.x, y = HAULER.y, z = HAULER.z;
			kit.add( "fittings", GK.rod( V( x, L.deckY, z + 0.1 ), V( x, y - 0.1, z + 0.1 ), 0.038, 12 ), GALV );
			var plate = GK.box( 0.2, 0.012, 0.2 );
			plate.translate( x, L.deckY + 0.006, z + 0.1 );
			kit.add( "fittings", plate, GALV );
			var motor = GK.cylinder( 0.075, 0.075, 0.17, 18 );
			motor.applyMatrix4( GK.mat4( x, y, z + 0.13, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", motor, new Opts { color = 0x3a4048, rough = 0.45, metal = 0.3 } );
			var head = GK.lathe( new[] { new[] { 0.0, -0.05 }, new[] { 0.2, -0.05 }, new[] { 0.205, -0.04 }, new[] { 0.1, -0.006 }, new[] { 0.1, 0.006 }, new[] { 0.205, 0.04 }, new[] { 0.2, 0.05 }, new[] { 0.0, 0.05 } }, 28 );
			head.applyMatrix4( GK.mat4( x, y, z, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", head, GALV );
			var hubCap = GK.cylinder( 0.04, 0.05, 0.03, 12 );
			hubCap.applyMatrix4( GK.mat4( x, y, z - 0.06, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", hubCap, GALV );
			// hydraulic hoses down to the deck
			kit.add( "fittings", GK.tube( new List<Vector3> { V( x + 0.05, y, z + 0.2 ), V( x + 0.12, y - 0.2, z + 0.3 ), V( x + 0.1, 0.6, z + 0.28 ), V( x + 0.06, L.deckY + 0.02, z + 0.25 ) }, 0.012, 20, 5 ), BLACK );
			kit.add( "fittings", GK.tube( new List<Vector3> { V( x - 0.05, y, z + 0.2 ), V( x - 0.02, y - 0.25, z + 0.32 ), V( x + 0.02, 0.62, z + 0.3 ), V( x + 0.02, L.deckY + 0.02, z + 0.28 ) }, 0.012, 20, 5 ), BLACK );

			// pot warp reeved from the block into the hauler and down to a loose pile
			var rope = new List<Vector3> { bc.clone().add( V( 0.07, 0, 0 ) ), V( x - 0.3, y + 0.35, z + 0.02 ), V( x - 0.12, y + 0.2, z ), V( x + 0.2, y - 0.02, z - 0.01 ), V( x + 0.16, y - 0.25, z - 0.01 ), V( x + 0.05, 0.72, z - 0.1 ), V( x + 0.2, L.deckY + 0.02, z - 0.35 ) };
			kit.add( "fittings", GK.tube( rope, 0.009, 48, 4 ), new Opts { color = 0xe4c235, rough = 0.8, pattern = 1 } );
		}

		// ------------------------------------------------------------------ coiled pot warp

		static BufferGeometry coil( double cx, double cy, double cz, int turns, double radius, double ropeR, double seed )
		{
			var pts = new List<Vector3>();
			int perTurn = 12;
			int n = turns * perTurn;
			for ( int k = 0; k <= n; k ++ )
			{
				double f = ( double ) k / n;
				double a = ( double ) k / perTurn * Math.PI * 2 + seed;
				double r = radius * ( 1 - 0.12 * f ) + 0.012 * Math.Sin( k * 0.9 + seed * 3 );
				double y = cy + ropeR + f * turns * ropeR * 1.35 + 0.004 * Math.Sin( k * 1.7 + seed );
				pts.Add( V( cx + Math.Cos( a ) * r, y, cz + Math.Sin( a ) * r ) );
			}

			// tail leading off the top of the coil
			var last = pts[ pts.Count - 1 ];
			pts.Add( V( last.x * 0.7 + cx * 0.3 + 0.12, last.y - 0.01, last.z + 0.2 ) );
			pts.Add( V( last.x + 0.35, cy + ropeR, last.z + 0.4 ) );
			return GK.tube( pts, ropeR, turns * 21 + 10, 4 );
		}

		static void buildCoils( GeoKit kit, HullLines L )
		{
			kit.add( "fittings", coil( -0.5, L.deckY, -2.15, 5, 0.22, 0.011, 0.3 ), new Opts { color = 0xe4c235, rough = 0.8, pattern = 1 } );
			double topTrap = L.deckY + 0.03 + TRAP.H + 0.012;
			kit.add( "fittings", coil( -0.68, topTrap, -3.2, 4, 0.19, 0.01, 1.7 ), new Opts { color = 0x2f6f4f, rough = 0.8, pattern = 1 } );
		}

		// ------------------------------------------------------------------ buoys lying on deck

		static void buildBuoys( GeoKit kit, HullLines L )
		{
			double[][] place = {
				new[] { -0.2, L.deckY + 0.075, -2.75, Math.PI / 2, 0.4 },
				new[] { 0.12, L.deckY + 0.075, -3.0, Math.PI / 2, -0.9 },
			};
			foreach ( var p in place )
			{
				var m = GK.mat4( p[ 0 ], p[ 1 ], p[ 2 ], p[ 3 ], p[ 4 ], 0, 1, 1, 1, "YXZ" );
				foreach ( var go in buoyGeometry( null, 0.3 ) )
				{
					go.geometry.translate( 0, -0.23, 0 );
					go.geometry.applyMatrix4( m );
					kit.add( "fittings", go.geometry, go.opts );
				}
			}
		}

		// ------------------------------------------------------------------ fenders

		static void buildFenders( GeoKit kit, HullLines L )
		{
			double[][] prof = { new[] { 0.0, 0 }, new[] { 0.03, 0.004 }, new[] { 0.055, 0.016 }, new[] { 0.07, 0.04 }, new[] { 0.075, 0.07 }, new[] { 0.075, 0.43 }, new[] { 0.07, 0.46 }, new[] { 0.055, 0.484 }, new[] { 0.03, 0.496 }, new[] { 0.0, 0.5 } };
			foreach ( var sz in FENDERS )
			{
				double s = sz[ 0 ], z = sz[ 1 ];
				double yTop = 0.86;
				double hullX = L.hullXAt( z, yTop - 0.05 );
				double fx = s * ( hullX + 0.08 );
				var f = GK.lathe( prof, 12 );
				f.translate( fx, yTop - 0.5, z );
				kit.add( "fittings", f, new Opts { color = 0x1d3a66, rough = 0.45 } );
				var eye = GK.torus( 0.018, 0.006, 5, 10 );
				eye.translate( fx, yTop + 0.018, z );
				kit.add( "fittings", eye, new Opts { color = 0x1d3a66, rough = 0.45 } );
				// fender line up and over the gunwale to a cleat inside
				double t = L.tAtSheerZ( z );
				double ys = L.sheerY( t ) + 0.05;
				double xo = s * ( L.sheerX( t ) + 0.01 ), xi = s * ( L.sheerX( t ) - L.shell - 0.05 );
				var line = new List<Vector3> { V( fx, yTop + 0.03, z ), V( fx - s * 0.01, ( yTop + ys ) / 2, z ), V( xo, ys, z ), V( ( xo + xi ) / 2, ys + 0.012, z ), V( xi, ys - 0.03, z ) };
				kit.add( "fittings", GK.tube( line, 0.006, 16, 4 ), new Opts { color = 0xf0efe8, rough = 0.8, pattern = 1 } );
			}
		}

		// ------------------------------------------------------------------ cleats

		static List<BufferGeometry> cleat( Vector3 pos, double yaw, double len = 0.2 )
		{
			var parts = new List<BufferGeometry>();
			var horn = GK.cylinder( 0.011, 0.011, len * 0.7, 8 );
			horn.applyMatrix4( GK.mat4( 0, 0.045, 0, Math.PI / 2, 0, 0 ) );
			parts.Add( horn );
			foreach ( double s in new double[] { 1, -1 } )
			{
				var tipC = GK.cylinder( 0.006, 0.011, len * 0.15, 8 );
				tipC.applyMatrix4( GK.mat4( 0, 0.045, s * len * 0.425, s * Math.PI / 2, 0, 0 ) );
				parts.Add( tipC );
				var foot = GK.cylinder( 0.013, 0.018, 0.045, 8 );
				foot.translate( 0, 0.0225, s * len * 0.22 );
				parts.Add( foot );
			}

			var m = GK.mat4( pos.x, pos.y, pos.z, 0, yaw, 0 );
			foreach ( var p in parts ) p.applyMatrix4( m );
			return parts;
		}

		static void buildCleats( GeoKit kit, HullLines L )
		{
			var list = new List<BufferGeometry>();
			foreach ( double s in new double[] { 1, -1 } )
			{
				foreach ( double z in new[] { -3.6, -1.35 } )
				{
					double t = L.tAtSheerZ( z );
					list.AddRange( cleat( V( s * ( L.sheerX( t ) - 0.04 ), L.sheerY( t ) + 0.048, z ), 0 ) );
				}
			}

			double tb = L.tAtSheerZ( 3.35 );
			list.AddRange( cleat( V( 0, foredeckY( L, tb, 0 ), 3.35 ), Math.PI / 2, 0.24 ) );
			foreach ( var g in list ) kit.add( "fittings", g, STAINLESS );
		}

		// ------------------------------------------------------------------ bow: stem head, roller, anchor

		static void buildBow( GeoKit kit, HullLines L )
		{
			double yTip = L.sheerY( 1 );
			// stem head fitting wrapping the bow tip
			var head = GK.roundedBox( 0.09, 0.05, 0.3, 0.015, 2 );
			head.translate( 0, yTip + 0.03, L.zBow - 0.08 );
			kit.add( "fittings", head, STAINLESS );
			foreach ( double s in new double[] { 1, -1 } )
			{
				var cheek = GK.box( 0.008, 0.07, 0.16 );
				cheek.translate( s * 0.04, yTip + 0.08, L.zBow + 0.02 );
				kit.add( "fittings", cheek, STAINLESS );
			}

			var roller = GK.cylinder( 0.03, 0.03, 0.07, 12 );
			roller.applyMatrix4( GK.mat4( 0, yTip + 0.085, L.zBow + 0.07, 0, 0, Math.PI / 2 ) );
			kit.add( "fittings", roller, BLACK );

			// plow anchor stowed on the roller: shank over the roller, plowshare hanging against the stem
			var sa = V( 0, yTip + 0.1, L.zBow - 0.45 ); var sb = V( 0, yTip + 0.12, L.zBow + 0.13 );
			kit.add( "fittings", GK.rod( sa, sb, 0.017, 8 ), GALV );
			var knuckle = GK.cylinder( 0.025, 0.025, 0.07, 10 );
			knuckle.applyMatrix4( GK.mat4( sb.x, sb.y, sb.z, 0, 0, Math.PI / 2 ) );
			kit.add( "fittings", knuckle, GALV );
			var plowDir = V( 0, -0.85, -0.4 ).normalize();
			var plow = Geo.Cone( 0.12, 0.34, 4 );
			plow.rotateY( Math.PI / 4 );
			plow.scale( 1, 1, 0.38 );
			plow.applyMatrix4( GK.alignY( sb.clone().add( V( 0, -0.02, 0.02 ) ).addScaledVector( plowDir, 0.17 ), plowDir ) );
			kit.add( "fittings", plow, GALV );
			// rode running aft to the deck pipe
			double t = L.tAtSheerZ( L.zBow - 0.75 );
			double pipeY = foredeckY( L, t, 0 );
			var dp = GK.cylinder( 0.035, 0.045, 0.04, 14 );
			dp.translate( 0, pipeY + 0.02, L.zBow - 0.75 );
			kit.add( "fittings", dp, STAINLESS );
			kit.add( "fittings", GK.tube( new List<Vector3> { sa, V( 0, lerp( sa.y, pipeY, 0.6 ) + 0.03, L.zBow - 0.6 ), V( 0, pipeY + 0.04, L.zBow - 0.75 ) }, 0.012, 10, 5 ), GALV );
		}

		// ------------------------------------------------------------------ bait tote and barrel

		static void buildContainers( GeoKit kit, HullLines L )
		{
			// bait tote beside the hauler
			var tote = Geo.Cylinder( 0.43, 0.37, 0.36, 4, 1 );
			tote.rotateY( Math.PI / 4 );
			tote.scale( 1, 1, 0.62 );
			tote.translate( -0.72, L.deckY + 0.18, -1.45 );
			kit.add( "fittings", tote, new Opts { color = 0x2a64b0, rough = 0.55 } );
			var bait = GK.box( 0.52, 0.02, 0.33 );
			bait.translate( -0.72, L.deckY + 0.352, -1.45 );
			kit.add( "fittings", bait, new Opts { color = 0x5a2c22, rough = 0.35 } );

			// bait barrel against the port rail aft of the wheelhouse
			double bx = 0.86, bz = -0.8;
			var barrel = GK.cylinder( 0.26, 0.26, 0.78, 20 );
			barrel.translate( bx, L.deckY + 0.39, bz );
			kit.add( "fittings", barrel, new Opts { color = 0x1f5ea6, rough = 0.5 } );
			foreach ( double y in new[] { 0.26, 0.52 } )
			{
				var rib = GK.torus( 0.262, 0.012, 5, 24 );
				rib.applyMatrix4( GK.mat4( bx, L.deckY + y, bz, Math.PI / 2, 0, 0 ) );
				kit.add( "fittings", rib, new Opts { color = 0x1f5ea6, rough = 0.5 } );
			}

			var lid = GK.cylinder( 0.27, 0.27, 0.03, 20 );
			lid.translate( bx, L.deckY + 0.795, bz );
			kit.add( "fittings", lid, new Opts { color = 0x1b4f8c, rough = 0.5 } );
		}

		// ------------------------------------------------------------------ stern: light and ensign

		static void buildStern( GeoKit kit, HullLines L, WheelhouseParts parts )
		{
			double y0 = L.sheerY( 0 ) + 0.045;
			var bas = GK.cylinder( 0.03, 0.035, 0.03, 12 );
			bas.translate( 0, y0 + 0.015, L.zAft + 0.03 );
			kit.add( "fittings", bas, BLACK );
			var lens = GK.cylinder( 0.025, 0.025, 0.05, 12 );
			lens.translate( 0, y0 + 0.055, L.zAft + 0.03 );
			kit.add( "glow", lens, new Opts { color = 0xfff3dc, rough = 0.2, pattern = 0 } );
			var cap = GK.cylinder( 0.03, 0.028, 0.012, 12 );
			cap.translate( 0, y0 + 0.086, L.zAft + 0.03 );
			kit.add( "fittings", cap, BLACK );

			// flag staff in a socket at the starboard quarter
			double sx = -( L.sheerX( 0.02 ) - 0.08 ), sz = L.zAft + 0.14;
			double top = y0 + 1.0;
			kit.add( "fittings", GK.rod( V( sx, y0 - 0.02, sz ), V( sx, top, sz ), 0.014, 8, 0.01 ), new Opts { color = 0xf1efe8, rough = 0.3 } );
			var finial = GK.sphere( 0.02, 8, 6 );
			finial.translate( sx, top + 0.015, sz );
			kit.add( "fittings", finial, new Opts { color = 0xc8a050, rough = 0.25, metal = 1 } );
			var socket = GK.cylinder( 0.022, 0.026, 0.06, 10 );
			socket.translate( sx, y0 + 0.01, sz );
			kit.add( "fittings", socket, STAINLESS );

			// flag: rest pose streams aft; the fittings material animates it (pattern 2)
			double w = FLAG.w, h = FLAG.h;
			int nu = 12, nv = 6;
			var pos = new List<double>(); var uvs = new List<double>(); var idx = new List<int>();
			for ( int j = 0; j <= nv; j ++ )
			{
				for ( int i = 0; i <= nu; i ++ )
				{
					double u = ( double ) i / nu, v = ( double ) j / nv;
					pos.Add( sx ); pos.Add( top - 0.03 - h + v * h ); pos.Add( sz - u * w );
					uvs.Add( u ); uvs.Add( v );
				}
			}

			for ( int j = 0; j < nv; j ++ )
			{
				for ( int i = 0; i < nu; i ++ )
				{
					int a = j * ( nu + 1 ) + i, b = a + 1, c = a + nu + 2, d = a + nu + 1;
					idx.AddRange( new[] { a, b, c, a, c, d } );
				}
			}

			foreach ( bool flip in new[] { false, true } )
			{
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
				var ix = flip ? idx.Select( ( _, k ) => idx[ k - ( k % 3 ) + ( 2 - ( k % 3 ) ) ] ).ToList() : new List<int>( idx );
				g.setIndex( ix );
				g.computeVertexNormals();
				GK.auxVertices( g, ( p, k ) => new[] { 0.7, 0, 2, uvs[ k * 2 ] } );
				kit.add( "fittings", g, new Opts { color = 0xffffff } );
			}

			parts.flagPivot = V( sx, 0, sz );
		}
	}
}
