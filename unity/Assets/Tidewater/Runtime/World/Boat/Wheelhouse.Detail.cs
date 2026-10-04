using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullLines;
using static Tidewater.World.Boat.HullBuilder;

// Wheelhouse.js, continued: lived-in cabin detail (sole, headliner, props, hung oilskin and lifejacket), roof gear and the geometry of
// the animated parts (wheel, throttle, radar array).
namespace Tidewater.World.Boat
{
	public static partial class Wheelhouse
	{
		static void buildCabinDetail( GeoKit kit, HullLines L )
		{
			double y0 = L.deckY;
			double yb = HOUSE.roofUnderY;
			Func<double, double> hullIn = z => Math.Min( houseHalfWidth( L, z ) - HOUSE.wallT, L.halfBreadth( L.tAtSheerZ( z ), y0 ) - L.shell ) - 0.015;

			// ---- teak-and-holly sole from the house back to the console (planks fore and aft)
			var soleOut = new List<double[]>();
			double zA = L.houseBack - 0.02, zB = HOUSE.dash.zFace - 0.005;
			for ( int i = 0; i <= 6; i ++ )
			{
				double z = lerp( zA, zB, i / 6.0 );
				soleOut.Add( new[] { z, -hullIn( z ) } );
			}

			for ( int i = 6; i >= 0; i -- )
			{
				double z = lerp( zA, zB, i / 6.0 );
				soleOut.Add( new[] { z, hullIn( z ) } );
			}

			var sole = GK.slab( soleOut.ToArray(), new double[ 0 ][][], ( u, v, side ) => V( v, y0 + 0.009 - side * 0.009, u ), new SlabOpts { back = false } );
			kit.add( "wood", sole, new Opts { rough = 0.5, pattern = 2 } );

			// ---- headliner battens (varnished) across the roof, and a teak grab rail overhead
			foreach ( double z in new[] { -0.22, 0.26, 0.74 } )
			{
				double w = 2 * ( wallX( L, z, yb ) - HOUSE.wallT ) - 0.02;
				var b = GK.box( 0.045, 0.016, w );
				b.applyMatrix4( GK.mat4( 0, yb - 0.008, z, 0, Math.PI / 2, 0 ) );
				kit.add( "wood", b, new Opts { rough = 0.35 } );
			}

			double gy = yb - 0.06;
			kit.add( "wood", GK.rod( V( -0.32, gy, -0.2 ), V( -0.32, gy, 0.9 ), 0.016, 10 ), new Opts { rough = 0.35 } );
			foreach ( double z in new[] { -0.12, 0.35, 0.82 } ) kit.add( "fittings", GK.rod( V( -0.32, gy, z ), V( -0.32, yb - 0.004, z ), 0.011, 8 ), STAINLESS );

			// ---- overhead rod rack on the port side: three rods with cork grips and reels
			foreach ( double z in new[] { -0.3, 0.35, 0.95 } )
			{
				var br = GK.box( 0.36, 0.012, 0.03 );
				br.translate( 0.64, yb - 0.08, z );
				kit.add( "fittings", br, BLACK_PLASTIC );
				foreach ( double x in new[] { 0.48, 0.8 } ) kit.add( "fittings", GK.rod( V( x, yb - 0.08, z ), V( x, yb - 0.002, z ), 0.005, 6 ), STAINLESS );
			}

			double[] rodX = { 0.54, 0.64, 0.74 };
			double[] rodColors = { 0x1b1c1e, 0x2a3a52, 0x4a1f1a };
			double[] reelColors = { 0x8a8d91, 0xb89040, 0x2b2d30 };
			for ( int i = 0; i < 3; i ++ )
			{
				double x = rodX[ i ];
				double y = yb - 0.066;
				double z0 = -0.42 + i * 0.05, z1 = 1.12;
				kit.add( "fittings", GK.rod( V( x, y, z0 ), V( x, y, z1 ), 0.007, 6, 0.0025 ), new Opts { color = rodColors[ i ], rough = 0.3 } );
				kit.add( "fittings", GK.rod( V( x, y, z0 ), V( x, y, z0 + 0.24 ), 0.013, 8 ), new Opts { color = 0xb48a5c, rough = 0.85 } );
				var reel = GK.cylinder( 0.034, 0.034, 0.03, 12 );
				reel.applyMatrix4( GK.mat4( x, y - 0.045, z0 + 0.3, 0, 0, Math.PI / 2 ) );
				kit.add( "fittings", reel, new Opts { color = reelColors[ i ], rough = 0.3, metal = 1 } );
				kit.add( "fittings", GK.rod( V( x, y - 0.012, z0 + 0.3 ), V( x, y - 0.03, z0 + 0.3 ), 0.004, 5 ), new Opts { color = 0x2b2d30, rough = 0.4 } );
			}

			// ---- port wall: breaker panel below the forward window, oilskin and lifejacket on hooks aft
			{
				double z = 0.56, y = 1.33, x = wallIn( L, 1, z, y );
				var pnl = GK.box( 0.014, 0.22, 0.34 );
				pnl.translate( x - 0.007, y, z );
				kit.add( "fittings", pnl, new Opts { color = 0x3a3d42, rough = 0.5, pattern = 4 } );
				for ( int r = 0; r < 2; r ++ )
				{
					double ry = y + 0.045 - r * 0.1;
					var lt = GK.box( 0.002, 0.014, 0.3 );
					lt.translate( x - 0.015, ry + 0.035, z );
					kit.add( "fittings", lt, new Opts { color = 0xffffff, rough = 0.5, pattern = 8 } );
					for ( int k = 0; k < 8; k ++ )
					{
						var bk = GK.box( 0.016, 0.034, 0.02 );
						bk.translate( x - 0.021, ry, z - 0.135 + k * 0.0386 );
						kit.add( "fittings", bk, new Opts { color = 0x121314, rough = 0.4 } );
						var tog = GK.box( 0.012, 0.012, 0.008 );
						bool red = ( k * 7 + r * 3 ) % 5 == 0;
						tog.translate( x - 0.034, ry + ( red ? -0.007 : 0.007 ), z - 0.135 + k * 0.0386 );
						kit.add( "fittings", tog, new Opts { color = red ? 0xb02a22 : 0x1e1f21, rough = 0.4 } );
					}
				}
			}

			hang( kit, L, -0.165, 1.92, 0xe0a81c, 1.0 ); // yellow oilskin
			hang( kit, L, 0.15, 1.9, 0xe2531a, 0.8, 0.45, true ); // orange lifejacket

			// ---- starboard wall: extinguisher on its bracket, photos and the tide table, torch in a clip
			{
				double z = -0.18, yb0 = y0 + 0.2, x = wallIn( L, -1, z, 0.8 );
				double ex = x + 0.075;
				var body = GK.lathe( new[] { new[] { 0.0, 0 }, new[] { 0.058, 0.0 }, new[] { 0.062, 0.02 }, new[] { 0.062, 0.3 }, new[] { 0.05, 0.35 }, new[] { 0.018, 0.37 }, new[] { 0.0, 0.37 } }, 18 );
				body.translate( ex, yb0, z );
				kit.add( "fittings", body, new Opts { color = 0xb3150f, rough = 0.35 } );
				var valve = GK.box( 0.03, 0.05, 0.05 );
				valve.translate( ex, yb0 + 0.39, z );
				kit.add( "fittings", valve, new Opts { color = 0x1a1a1a, rough = 0.4 } );
				kit.add( "fittings", GK.rod( V( ex, yb0 + 0.41, z ), V( ex + 0.02, yb0 + 0.42, z + 0.09 ), 0.007, 6 ), new Opts { color = 0x8c8f93, rough = 0.3, metal = 1 } );
				kit.add( "fittings", GK.tube( new List<Vector3> { V( ex, yb0 + 0.38, z + 0.02 ), V( ex + 0.03, yb0 + 0.3, z + 0.07 ), V( ex + 0.05, yb0 + 0.15, z + 0.06 ), V( ex + 0.06, yb0 + 0.08, z + 0.03 ) }, 0.008, 12, 5 ), new Opts { color = 0x141414, rough = 0.6 } );
				foreach ( double h in new[] { 0.12, 0.26 } )
				{
					var strap = GK.torus( 0.064, 0.004, 4, 20 );
					strap.applyMatrix4( GK.mat4( ex, yb0 + h, z, Math.PI / 2, 0, 0 ) );
					kit.add( "fittings", strap, STAINLESS );
				}

				var plate = GK.box( 0.006, 0.3, 0.07 );
				plate.translate( x + 0.003, yb0 + 0.19, z );
				kit.add( "fittings", plate, STAINLESS );
				// instruction label on the cylinder
				var lab = GK.cylinder( 0.0625, 0.0625, 0.1, 18, 1, true, -0.9, 1.8 );
				lab.translate( ex, yb0 + 0.19, z );
				kit.add( "fittings", lab, new Opts { color = 0xffffff, rough = 0.5, pattern = 8 } );
			}

			Action<double, double, double, double, double, double, Opts> onWall = ( s, z, y, w, h, roll, opts ) =>
			{
				var q = Geo.Plane( w, h );
				q.applyMatrix4( GK.mat4( 0, 0, 0, 0, s > 0 ? -Math.PI / 2 : Math.PI / 2, 0 ) );
				q.applyMatrix4( new Matrix4().makeRotationX( roll ) );
				q.translate( 0, y, z );
				// follow the wall (it curves with the hull and leans in above the rail): each vertex sits 12 mm off the analytic inner
				// face (the wall mesh is faceted: its chords stand ~1 cm proud of the curve), so no part of the card can dip behind it
				var pos = q.attributes[ "position" ];
				for ( int i = 0; i < pos.count; i ++ ) pos.setX( i, wallIn( L, s, pos.getZ( i ), pos.getY( i ) ) - s * 0.012 );
				q.computeVertexNormals();
				kit.add( "fittings", q, opts );
			};

			onWall( -1, 0.02, 1.47, 0.1, 0.13, 0.06, new Opts { color = 0xffffff, rough = 0.3, pattern = 7, anim = 0.15 } );
			onWall( -1, 0.15, 1.42, 0.12, 0.09, -0.1, new Opts { color = 0xffffff, rough = 0.3, pattern = 7, anim = 0.7 } );
			onWall( -1, 0.36, 1.36, 0.19, 0.25, 0.02, new Opts { color = 0xffffff, rough = 0.9, pattern = 5 } );
			// masking tape holding the tide table
			foreach ( var dd in new[] { new[] { -0.09, 0.12 }, new[] { 0.09, 0.12 } } ) onWall( -1, 0.36 + dd[ 0 ], 1.36 + dd[ 1 ], 0.05, 0.018, 0.5, new Opts { color = 0xd8cfa8, rough = 0.8 } );

			{
				// torch in its clip, forward on the starboard wall
				double z = 0.85, y = 1.3, x = wallIn( L, -1, z, y ) + 0.035;
				kit.add( "fittings", GK.rod( V( x, y, z - 0.1 ), V( x, y, z + 0.1 ), 0.02, 12 ), new Opts { color = 0xf2c21b, rough = 0.45 } );
				kit.add( "fittings", GK.rod( V( x, y, z + 0.1 ), V( x, y, z + 0.13 ), 0.026, 12 ), new Opts { color = 0x1a1a1a, rough = 0.4 } );
				foreach ( double dz in new[] { -0.06, 0.05 } )
				{
					var clip = GK.torus( 0.023, 0.003, 4, 12, Math.PI * 1.3 );
					clip.applyMatrix4( GK.mat4( x, y, z + dz, 0, Math.PI / 2, -0.65 ) );
					kit.add( "fittings", clip, BLACK_PLASTIC );
				}
			}

			// ---- dash top: clipboard with the fishing log, mug of coffee in a holder
			{
				double ytop = HOUSE.dash.yTop + 0.01;
				var m = GK.mat4( 0.74, ytop + 0.004, 1.26, 0, 0.18, 0 );
				var board = GK.box( 0.23, 0.005, 0.31 );
				board.applyMatrix4( m );
				kit.add( "fittings", board, new Opts { color = 0x6b4a2b, rough = 0.7 } );
				var paper = Geo.Plane( 0.21, 0.28 );
				paper.applyMatrix4( GK.mat4( 0, 0.0035, 0.012, -Math.PI / 2, 0, 0 ) );
				paper.applyMatrix4( m );
				kit.add( "fittings", paper, new Opts { color = 0xffffff, rough = 0.9, pattern = 5 } );
				var clip = GK.box( 0.08, 0.014, 0.03 );
				clip.applyMatrix4( GK.mat4( 0, 0.008, 0.14 ) );
				clip.applyMatrix4( m );
				kit.add( "fittings", clip, STAINLESS );
				double mx = -0.76, mz = 1.34;
				var holder = GK.torus( 0.05, 0.006, 6, 18 );
				holder.applyMatrix4( GK.mat4( mx, ytop + 0.035, mz, Math.PI / 2, 0, 0 ) );
				kit.add( "fittings", holder, BLACK_PLASTIC );
				var mug = GK.cylinder( 0.042, 0.038, 0.095, 18, 1, true );
				mug.translate( mx, ytop + 0.048, mz );
				kit.add( "fittings", mug, new Opts { color = 0x1f4a3a, rough = 0.25 } );
				var inner = GK.cylinder( 0.039, 0.036, 0.09, 18, 1, true );
				inner.translate( mx, ytop + 0.05, mz );
				orientTowardsAxis( inner );
				kit.add( "fittings", inner, new Opts { color = 0xe9e4d8, rough = 0.25 } );
				var coffee = Geo.Circle( 0.039, 18 );
				coffee.applyMatrix4( GK.mat4( mx, ytop + 0.082, mz, -Math.PI / 2, 0, 0 ) );
				kit.add( "fittings", coffee, new Opts { color = 0x2a160b, rough = 0.1 } );
				var bas = Geo.Circle( 0.038, 18 );
				bas.applyMatrix4( GK.mat4( mx, ytop + 0.0005, mz, Math.PI / 2, 0, 0 ) );
				kit.add( "fittings", bas, new Opts { color = 0x1f4a3a, rough = 0.4 } );
				var handle = GK.torus( 0.026, 0.006, 6, 12, Math.PI );
				handle.applyMatrix4( GK.mat4( mx - 0.042, ytop + 0.048, mz, 0, 0, Math.PI / 2 ) );
				kit.add( "fittings", handle, new Opts { color = 0x1f4a3a, rough = 0.25 } );
			}

			// ---- port bench: lifejackets, a folded chart; coiled line on the sole aft
			{
				double top = y0 + 0.47;
				double[] dys = { 0, 0.05 };
				for ( int i = 0; i < 2; i ++ )
				{
					double dy = dys[ i ];
					var v = GK.roundedBox( 0.34, 0.05, 0.4, 0.02, 1 );
					v.applyMatrix4( GK.mat4( 0.86, top + 0.025 + dy, 0.28 + i * 0.02, 0, 0.12 * i - 0.05, 0 ) );
					kit.add( "fittings", v, new Opts { color = 0xe2531a, rough = 0.6, pattern = 9 } );
					foreach ( double dz in new[] { -0.08, 0.08 } )
					{
						var st = GK.box( 0.345, 0.052, 0.025 );
						st.applyMatrix4( GK.mat4( 0.86, top + 0.025 + dy, 0.28 + i * 0.02 + dz, 0, 0.12 * i - 0.05, 0 ) );
						kit.add( "fittings", st, new Opts { color = 0x151515, rough = 0.7 } );
					}
				}

				var chart = Geo.Plane( 0.3, 0.22 );
				chart.applyMatrix4( GK.mat4( 0.85, top + 0.002, 0.64, -Math.PI / 2, 0, 0.3 ) );
				kit.add( "fittings", chart, new Opts { color = 0xffffff, rough = 0.85, pattern = 6 } );

				for ( int k = 0; k < 5; k ++ )
				{
					var loop = GK.torus( 0.15 - k * 0.004, 0.011, 6, 28 );
					loop.applyMatrix4( GK.mat4( 0.62 + k * 0.004, y0 + 0.02 + k * 0.02, -0.14 + k * 0.006, Math.PI / 2 + ( k % 2 ) * 0.06, 0, 0 ) );
					var uv = loop.attributes[ "uv" ];
					for ( int j = 0; j < uv.count; j ++ ) uv.setXY( j, uv.getX( j ) * 0.94, uv.getY( j ) );
					kit.add( "fittings", loop, new Opts { color = 0x2c5a8c, rough = 0.8, pattern = 1 } );
				}
			}
		}

		// a coat on a hook of the port wall: oilskin (scale 1) or a short bulky lifejacket vest
		static void hang( GeoKit kit, HullLines L, double z, double yTop, double color, double scale, double hood = 1.0, bool vest = false )
		{
			double x = wallIn( L, 1, z, yTop );
			kit.add( "fittings", GK.rod( V( x, yTop + 0.02, z ), V( x - 0.05, yTop + 0.04, z ), 0.006, 6 ), STAINLESS );
			double H = 0.66 * scale;
			double HB = H * ( vest ? 0.62 : 1.0 ); // a lifejacket is a short, bulky vest: no sleeves
			double bulk = vest ? 1.5 : 1.0;
			var opts = new Opts { color = color, rough = 0.6, pattern = 9 };
			var dim = new Opts { color = new Color( color ).multiplyScalar( 0.85 ).getHex(), rough = 0.6, pattern = 9 };
			var trim = new Opts { color = new Color( color ).multiplyScalar( 0.6 ).getHex(), rough = 0.6, pattern = 9 };
			double CLEAR = 0.012; // stand-off from the analytic wall face (its chords stand ~1 cm proud)
			int K = 24;
			double FRONT = Math.PI / 2;

			// how close a section point is to the centre front: 1 on the placket, ~0 elsewhere
			// ( a - FRONT + PI is always >= PI/2 here, so JS' % is the Python floored modulo )
			Func<double, double> placket = a => Math.Exp( -Math.Pow( ( ( a - FRONT + Math.PI ) % ( Math.PI * 2 ) - Math.PI ) / 0.26, 2 ) / 2 );

			// body: stacked horizontal D sections -- flat back pressed against the wall, front bulging -- so the garment reads by its
			// outline (sloping shoulders, a waist the sleeves hang clear of, a flared hem) instead of being a body of revolution; the
			// front centre carries a zip flap and the hem rows pucker into folds where the cloth runs out of hanger
			Func<double, double, double, double, double, List<Vector3>> torsoRow = ( y, w, d, flap, fold ) =>
			{
				var row = new List<Vector3>();
				for ( int i = 0; i < K; i ++ )
				{
					double a = i * Math.PI * 2 / K;
					double f = fold * Math.Cos( 5 * a + 1.3 );
					double zz = z + w * ( 1 + f ) * Math.Cos( a );
					row.Add( V( wallIn( L, 1, zz, y ) - CLEAR - d * ( 1 + 1.5 * f ) * Math.Max( 0.0, Math.Sin( a ) ) - flap * placket( a ), y, zz ) );
				}

				return row;
			};

			//   dy    half-width  depth   folds
			double[][] TORSO = {
				new[] { 0.00, 0.050, 0.024, 0.0 }, // collar opening
				new[] { 0.07, 0.084, 0.038, 0.0 },
				new[] { 0.16, 0.132, 0.044, 0.0 },
				new[] { 0.19, 0.148, 0.045, 0.0 },
				new[] { 0.23, 0.160, 0.046, 0.0 }, // shoulder point
				new[] { 0.34, 0.140, 0.046, 0.0 }, // armpit
				new[] { 0.52, 0.112, 0.040, 0.0 }, // waist, the sleeves hang clear of it
				new[] { 0.72, 0.106, 0.032, 0.0 },
				new[] { 0.88, 0.114, 0.028, 0.03 },
				new[] { 0.95, 0.121, 0.026, 0.06 },
				new[] { 1.00, 0.126, 0.024, 0.09 }, // hem, a little flare
			};

			// ( half-width, depth ) of the body skin at dy, interpolated between the rows
			Func<double, double[]> torsoAt = dy =>
			{
				for ( int k = 0; k < TORSO.Length - 1; k ++ )
				{
					double d0 = TORSO[ k ][ 0 ], w0 = TORSO[ k ][ 1 ], e0 = TORSO[ k ][ 2 ];
					double d1 = TORSO[ k + 1 ][ 0 ], w1 = TORSO[ k + 1 ][ 1 ], e1 = TORSO[ k + 1 ][ 2 ];
					if ( dy <= d1 )
					{
						double t = ( dy - d0 ) / ( d1 - d0 );
						return new[] { ( w0 + ( w1 - w0 ) * t ) * scale, ( e0 + ( e1 - e0 ) * t ) * scale * bulk };
					}
				}

				return new[] { TORSO[ TORSO.Length - 1 ][ 1 ] * scale, TORSO[ TORSO.Length - 1 ][ 2 ] * scale * bulk };
			};

			// a point on the front skin: dy below the collar, dz across from the centre, `off` proud
			Func<double, double, double, Vector3> skin = ( dy, dz, off ) =>
			{
				var wd = torsoAt( dy );
				double w = wd[ 0 ], d = wd[ 1 ];
				double c = Math.Max( -1.0, Math.Min( 1.0, dz / w ) );
				double y = yTop - 0.01 - dy * HB;
				return V( wallIn( L, 1, z + dz, y ) - CLEAR - d * Math.Sqrt( 1 - c * c ) - 0.01 * scale * placket( Math.Acos( c ) ) - off, y, z + dz );
			};

			// a raised pad that follows the skin: placket, pockets, tape
			Action<double, double, double, double, double, int, int, Opts> patch = ( dy0, dy1, dz0, dz1, off, rows, cols, o ) =>
			{
				var ring = new List<List<Vector3>>();
				for ( int r = 0; r <= rows; r ++ )
				{
					double dy = dy0 + ( dy1 - dy0 ) * r / rows;
					var outer = new List<Vector3>(); var inner = new List<Vector3>();
					for ( int c = 0; c <= cols; c ++ ) outer.Add( skin( dy, dz1 + ( dz0 - dz1 ) * c / cols, off ) );
					for ( int c = 0; c <= cols; c ++ ) inner.Add( skin( dy, dz0 + ( dz1 - dz0 ) * c / cols, -0.006 ) );
					outer.AddRange( inner );
					ring.Add( outer );
				}

				kit.add( "fittings", GK.loft( ring, true ), o );
				kit.add( "fittings", GK.fanCap( ring[ 0 ], V( 0, 1, 0 ) ), o );
				kit.add( "fittings", GK.fanCap( ring[ ring.Count - 1 ], V( 0, -1, 0 ) ), o );
			};

			var body = TORSO.Select( t => torsoRow( yTop - 0.01 - t[ 0 ] * HB, t[ 1 ] * scale, t[ 2 ] * scale * bulk, 0.01 * scale, t[ 3 ] ) ).ToList();
			var g = GK.loft( body, true );
			// shade the same centre band a little darker, so the zip flap reads as a crease, and the hem a good deal darker, as a hem band
			GK.paintVertices( g, ( v, i ) => new Color( color ).multiplyScalar( ( 1 - 0.3 * placket( ( i % ( K + 1 ) ) * Math.PI * 2 / K ) ) * ( ( int ) Math.Floor( ( double ) i / ( K + 1 ) ) == TORSO.Length - 1 ? 0.72 : 1 ) ).getHex() );
			kit.add( "fittings", g, opts );
			kit.add( "fittings", GK.fanCap( body[ 0 ], V( 0, 1, 0 ) ), opts );
			kit.add( "fittings", GK.fanCap( body[ body.Count - 1 ], V( 0, -1, 0 ) ), opts );

			// front closure: a storm flap down the middle, shut with snaps
			double hw = 0.013 * scale;
			patch( 0.1, 0.99, -hw, hw, 0.007 * scale, 14, 2, vest ? trim : dim );

			for ( int k = 0; k < 5; k ++ )
			{
				var p = skin( 0.2 + k * 0.17, 0, 0.0135 * scale );
				var sn = GK.cylinder( 0.008 * scale, 0.008 * scale, 0.006, 10 );
				sn.applyMatrix4( GK.mat4( p.x, p.y, p.z, 0, 0, Math.PI / 2 ) );
				kit.add( "fittings", sn, new Opts { color = 0x2b2d30, rough = 0.4 } );
			}

			if ( vest )
			{
				// waist strap around the foam, buckled at the front, reflective tape on the chest
				foreach ( double dy in new[] { 0.58 } )
				{
					var wd = torsoAt( dy ); double w = wd[ 0 ], d = wd[ 1 ];
					var wd2 = torsoAt( dy + 0.1 ); double w2 = wd2[ 0 ], d2 = wd2[ 1 ];
					var strap = new List<List<Vector3>> { torsoRow( yTop - 0.01 - dy * HB, w + 0.005, d + 0.006, 0, 0 ), torsoRow( yTop - 0.01 - ( dy + 0.1 ) * HB, w2 + 0.005, d2 + 0.006, 0, 0 ) };
					kit.add( "fittings", GK.loft( strap, true ), new Opts { color = 0x1d1f22, rough = 0.55 } );
				}

				var bp = skin( 0.63, 0, 0.018 );
				kit.add( "fittings", GK.box( 0.012, 0.04 * scale, 0.05 * scale ).translate( bp.x, bp.y, bp.z ), STAINLESS );

				foreach ( double sd in new double[] { -1, 1 } )
				{
					patch( 0.2, 0.26, Math.Min( sd * 0.04 * scale, sd * 0.1 * scale ), Math.Max( sd * 0.04 * scale, sd * 0.1 * scale ), 0.005, 2, 4, new Opts { color = 0xd8d9d2, rough = 0.35 } );
					patch( 0.4, 0.46, Math.Min( sd * 0.05 * scale, sd * 0.11 * scale ), Math.Max( sd * 0.05 * scale, sd * 0.11 * scale ), 0.005, 2, 4, new Opts { color = 0xd8d9d2, rough = 0.35 } );
				}
			}
			else
			{
				// patch pockets low on the front, each under a flap
				foreach ( double sd in new double[] { -1, 1 } )
				{
					double a0 = sd * 0.024 * scale, b0 = sd * 0.09 * scale;
					double za = Math.Min( a0, b0 ), zb = Math.Max( a0, b0 );
					patch( 0.58, 0.72, za, zb, 0.007 * scale, 4, 6, dim );
					patch( 0.545, 0.6, za - 0.004, zb + 0.004, 0.012 * scale, 2, 6, trim );
				}
			}

			// sleeves: leave the shoulder from inside it, hang clear of the waist and close on a cuff
			foreach ( double sd in vest ? new double[ 0 ] : new double[] { -1, 1 } )
			{
				//   dy    z offset  z radius  depth radius  stand-off from the wall
				double[][] ARM = {
					new[] { 0.17, 0.075, 0.016, 0.008, 0.022 }, // buried in the shoulder
					new[] { 0.24, 0.100, 0.028, 0.022, 0.032 },
					new[] { 0.32, 0.122, 0.036, 0.028, 0.038 },
					new[] { 0.42, 0.134, 0.035, 0.028, 0.040 },
					new[] { 0.54, 0.140, 0.032, 0.026, 0.039 },
					new[] { 0.66, 0.142, 0.030, 0.024, 0.037 },
					new[] { 0.76, 0.137, 0.028, 0.022, 0.035 },
					new[] { 0.80, 0.135, 0.031, 0.025, 0.038 }, // cuff flare
				};
				var arm = new List<List<Vector3>>();

				foreach ( var q in ARM )
				{
					double dy = q[ 0 ], za = q[ 1 ], rz = q[ 2 ], rx = q[ 3 ], xo = q[ 4 ];
					double y = yTop - 0.01 - dy * H;
					double zc = z + sd * za * scale;
					var row = new List<Vector3>();
					for ( int i = 0; i < 12; i ++ )
					{
						double a = i * Math.PI * 2 / 12;
						double zz = zc + rz * scale * Math.Cos( a );
						row.Add( V( wallIn( L, 1, zz, y ) - CLEAR - xo * scale - rx * scale * Math.Sin( a ), y, zz ) );
					}

					arm.Add( row );
				}

				var sleeve = GK.loft( arm, true );
				GK.paintVertices( sleeve, ( v, i ) => new Color( color ).multiplyScalar( ( int ) Math.Floor( i / 13.0 ) == ARM.Length - 1 ? 0.7 : 1 ).getHex() ); // cuff band
				kit.add( "fittings", sleeve, opts );
				kit.add( "fittings", GK.fanCap( arm[ 0 ], V( 0, 1, 0 ) ), opts );
				kit.add( "fittings", GK.fanCap( arm[ arm.Count - 1 ], V( 0, -1, 0 ) ), trim );
			}

			// the loop sewn into the collar, over the hook, and the collar rolled around it
			var loop = GK.torus( 0.026, 0.004, 6, 16 );
			loop.applyMatrix4( GK.mat4( x - 0.025, yTop + 0.014, z, 0, Math.PI / 2, 0 ) );
			kit.add( "fittings", loop, dim );
			var col = GK.torus( 0.048 * scale, 0.020 * scale, 6, 14 );
			col.applyMatrix4( GK.mat4( x - 0.028 * scale, yTop - 0.045 * scale, z, Math.PI / 2, 0, 0, 0.62, 1, 1 ) );
			kit.add( "fittings", col, opts );

			if ( hood > 0 )
			{
				//   dy below the collar top  half-width  depth
				double[][] HOOD = {
					new[] { 0.03, 0.028, 0.010 },
					new[] { 0.06, 0.052, 0.022 },
					new[] { 0.10, 0.060, 0.026 },
					new[] { 0.135, 0.050, 0.018 },
				};
				var hd = HOOD.Select( q => torsoRow( yTop - q[ 0 ] * scale, q[ 1 ] * scale, q[ 2 ] * scale * hood, 0, 0 ) ).ToList();
				kit.add( "fittings", GK.loft( hd, true ), dim );
				kit.add( "fittings", GK.fanCap( hd[ 0 ], V( 0, 1, 0 ) ), dim );
			}
		}

		// flip a lathe / open cylinder's winding so its faces point inward (the inside of the mug)
		static void orientTowardsAxis( BufferGeometry g )
		{
			var idx = ( int[] ) g.index.array.Clone();
			for ( int i = 0; i < idx.Length; i += 3 ) { int t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t; }
			g.setIndex( idx );
			var n = g.attributes[ "normal" ];
			for ( int i = 0; i < n.count; i ++ ) n.setXYZ( i, -n.getX( i ), -n.getY( i ), -n.getZ( i ) );
		}

		// ------------------------------------------------------------------ roof gear: mast, radars, lights, antennas

		static void buildRoofGear( GeoKit kit, HullLines L, WheelhouseParts parts )
		{
			double yr = roofTopY( 0 );
			double mz = -0.5;

			// mast
			var mb = GK.box( 0.18, 0.025, 0.18 );
			mb.translate( 0, yr + 0.005, mz );
			kit.add( "fittings", mb, WHITE_PAINT );
			kit.add( "fittings", GK.rod( V( 0, yr, mz ), V( 0, 3.9, mz ), 0.042, 14, 0.03 ), WHITE_PAINT );

			// radome on a forward bracket
			double ry = 3.02;
			var arm = GK.box( 0.1, 0.035, 0.34 );
			arm.translate( 0, ry - 0.02, mz + 0.19 );
			kit.add( "fittings", arm, WHITE_PAINT );
			var plate = GK.box( 0.34, 0.015, 0.34 );
			plate.translate( 0, ry, mz + 0.3 );
			kit.add( "fittings", plate, WHITE_PAINT );
			kit.add( "fittings", GK.rod( V( 0, ry - 0.02, mz + 0.04 ), V( 0, ry - 0.2, mz + 0.01 ), 0.012, 6 ), WHITE_PAINT );
			var radome = GK.lathe( new[] { new[] { 0.0, 0 }, new[] { 0.285, 0 }, new[] { 0.3, 0.018 }, new[] { 0.3, 0.095 }, new[] { 0.29, 0.14 }, new[] { 0.245, 0.188 }, new[] { 0.16, 0.222 }, new[] { 0.07, 0.234 }, new[] { 0.0, 0.236 } }, 24 );
			radome.translate( 0, ry + 0.008, mz + 0.3 );
			kit.add( "fittings", radome, new Opts { color = 0xf3f2ee, rough = 0.4 } );
			var band = GK.cylinder( 0.302, 0.302, 0.02, 24, 1, true );
			band.translate( 0, ry + 0.03, mz + 0.3 );
			kit.add( "fittings", band, new Opts { color = 0x3a3d42, rough = 0.5 } );

			// spreader with side lights, masthead light
			double sy = 3.45;
			var spreader = GK.box( 0.78, 0.03, 0.045 );
			spreader.translate( 0, sy - 0.05, mz );
			kit.add( "fittings", spreader, WHITE_PAINT );
			foreach ( double s in new double[] { 1, -1 } )
			{
				double hx = s * 0.4;
				var housing = GK.box( 0.055, 0.07, 0.11 );
				housing.translate( hx, sy, mz + 0.02 );
				kit.add( "fittings", housing, BLACK_PLASTIC );
				var lens = GK.box( 0.014, 0.05, 0.085 );
				lens.translate( hx + s * 0.033, sy, mz + 0.03 );
				kit.add( "glow", lens, new Opts { color = s > 0 ? 0xff1a0e : 0x14ff5a, rough = 0.2, pattern = 0 } );
				// inboard screen
				var scr = GK.box( 0.004, 0.07, 0.12 );
				scr.translate( hx - s * 0.03, sy, mz + 0.02 );
				kit.add( "fittings", scr, BLACK_PLASTIC );
			}

			var mhBase = GK.cylinder( 0.036, 0.036, 0.03, 12 );
			mhBase.translate( 0, 3.915, mz );
			kit.add( "fittings", mhBase, BLACK_PLASTIC );
			var mhLens = GK.cylinder( 0.03, 0.03, 0.065, 12 );
			mhLens.translate( 0, 3.962, mz );
			kit.add( "glow", mhLens, new Opts { color = 0xfff3dc, rough = 0.2, pattern = 0 } );
			var mhCap = GK.cylinder( 0.034, 0.038, 0.016, 12 );
			mhCap.translate( 0, 4.003, mz );
			kit.add( "fittings", mhCap, BLACK_PLASTIC );

			// VHF whips on ratchet mounts at the aft roof corners (sway in the vertex shader)
			foreach ( double s in new double[] { 1, -1 } )
			{
				double x = s * 1.02, z = -0.78, y = roofTopY( 1.02 );
				var mount = GK.roundedBox( 0.06, 0.07, 0.06, 0.01, 1 );
				mount.translate( x, y + 0.035, z );
				kit.add( "fittings", mount, STAINLESS );
				double len = s > 0 ? 2.4 : 1.2;
				var whip = GK.cylinder( 0.005, 0.013, len, 8, 10 );
				whip.translate( x, y + 0.07 + len / 2, z );
				GK.auxVertices( whip, ( p, i ) => new[] { 0.3, 0, 3, Math.Max( 0, ( p.y - y - 0.07 ) / len ) } );
				kit.add( "fittings", whip, new Opts { color = 0xf4f4f1 } );
			}

			// open-array radar pedestal (array is animated, built in BoatModel)
			double pz = 0.8;
			var ped = GK.roundedBox( 0.26, 0.28, 0.3, 0.03, 1 );
			ped.translate( 0, yr + 0.14, pz );
			kit.add( "fittings", ped, new Opts { color = 0xf3f2ee, rough = 0.4 } );
			parts.radarPivot = V( 0, yr + 0.3, pz );

			// spotlight and horn on the front of the roof
			double sx = 0.42, szz = 1.05, syy = roofTopY( 0.42 );
			kit.add( "fittings", GK.rod( V( sx, syy, szz ), V( sx, syy + 0.1, szz ), 0.03, 10 ), BLACK_PLASTIC );
			var head = GK.cylinder( 0.065, 0.06, 0.15, 16 );
			head.applyMatrix4( GK.mat4( sx, syy + 0.17, szz, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", head, STAINLESS );
			var sl = Geo.Circle( 0.058, 16 );
			sl.translate( sx, syy + 0.17, szz + 0.0755 );
			kit.add( "glow", sl, new Opts { color = 0xfff1d6, rough = 0.2, pattern = 4 } );
			var horn = GK.lathe( new[] { new[] { 0.0, 0 }, new[] { 0.018, 0.0 }, new[] { 0.016, 0.1 }, new[] { 0.024, 0.17 }, new[] { 0.05, 0.22 }, new[] { 0.046, 0.222 }, new[] { 0.0, 0.2 } }, 16 );
			horn.applyMatrix4( GK.mat4( -sx, roofTopY( 0.42 ) + 0.07, szz - 0.12, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", horn, STAINLESS );
			kit.add( "fittings", GK.rod( V( -sx, roofTopY( 0.42 ), szz ), V( -sx, roofTopY( 0.42 ) + 0.07, szz ), 0.012, 6 ), STAINLESS );

			// the owner's buoy colours displayed on the roof
			double bx = 0.62, bz = -0.72;
			var bracket = GK.box( 0.12, 0.03, 0.12 );
			bracket.translate( bx, roofTopY( bx ) + 0.015, bz );
			kit.add( "fittings", bracket, STAINLESS );
			foreach ( var go in DeckGear.buoyGeometry() )
			{
				go.geometry.translate( bx, roofTopY( bx ) + 0.03, bz );
				kit.add( "fittings", go.geometry, go.opts );
			}

			// deck floodlights under the roof overhang, aimed at the work deck
			foreach ( double s in new double[] { 1, -1 } )
			{
				var fl = GK.roundedBox( 0.13, 0.07, 0.08, 0.01, 1 );
				fl.translate( s * 0.55, HOUSE.roofUnderY - 0.035, HOUSE.roofZ0 + 0.14 );
				kit.add( "fittings", fl, BLACK_PLASTIC );
				var lens = Geo.Plane( 0.11, 0.06 );
				lens.applyMatrix4( GK.mat4( s * 0.55, HOUSE.roofUnderY - 0.071, HOUSE.roofZ0 + 0.14, Math.PI / 2 + 0.35, 0, 0 ) );
				kit.add( "glow", lens, new Opts { color = 0xfff1d6, rough = 0.2, pattern = 4 } );
			}

			// life ring on the port house side
			double lx = wallX( L, -0.02, 1.72 ) + 0.05;
			var ringG = GK.torus( 0.235, 0.05, 8, 24 );
			ringG.applyMatrix4( GK.mat4( lx, 1.72, -0.02, 0, Math.PI / 2, 0 ) );
			GK.paintVertices( ringG, ( p, i ) =>
			{
				double a = Math.Atan2( p.y - 1.72, p.z + 0.02 );
				return Math.Abs( ( ( a / ( Math.PI / 2 ) ) % 1 + 1 ) % 1 - 0.5 ) > 0.4 ? 0xf2f0ea : 0xf25a12;
			} );
			kit.add( "fittings", ringG, new Opts { rough = 0.6 } );
			var hook = GK.box( 0.03, 0.05, 0.08 );
			hook.translate( lx - 0.03, 1.72 + 0.26, -0.02 );
			kit.add( "fittings", hook, STAINLESS );
		}

		// Steering wheel: varnished mahogany destroyer wheel with a brass hub.
		// Local frame: +Z is the shaft axis (into the dash), spokes in the XY plane.
		public static BufferGeometry wheelGeometry()
		{
			var list = new List<BufferGeometry>();
			var wood = new Opts { rough = 0.3, metal = 0, color = 0xe8b898 };
			double R = 0.2;
			list.Add( GK.prepare( GK.torus( R, 0.017, 8, 40 ), wood ) );
			for ( int i = 0; i < 6; i ++ )
			{
				double a = i * Math.PI / 3 + Math.PI / 6; // king spoke straight up when centred
				var dir = V( Math.Cos( a ), Math.Sin( a ), 0 );
				var spoke = GK.cylinder( 0.009, 0.013, R - 0.05, 8 );
				spoke.applyMatrix4( GK.alignY( dir.clone().multiplyScalar( 0.05 + ( R - 0.05 ) / 2 ), dir ) );
				list.Add( GK.prepare( spoke, wood ) );
				var handle = GK.lathe( new[] { new[] { 0.0, 0 }, new[] { 0.011, 0.0 }, new[] { 0.013, 0.02 }, new[] { 0.017, 0.045 }, new[] { 0.012, 0.06 }, new[] { 0.01, 0.07 }, new[] { 0.015, 0.078 }, new[] { 0.0, 0.086 } }, 8 );
				handle.applyMatrix4( GK.alignY( dir.clone().multiplyScalar( R - 0.005 ), dir ) );
				list.Add( GK.prepare( handle, wood ) );
			}

			var hub = GK.lathe( new[] { new[] { 0.0, -0.03 }, new[] { 0.05, -0.03 }, new[] { 0.058, -0.012 }, new[] { 0.058, 0.012 }, new[] { 0.05, 0.028 }, new[] { 0.0, 0.03 } }, 20 );
			hub.applyMatrix4( new Matrix4().makeRotationX( Math.PI / 2 ) );
			list.Add( GK.prepare( hub, wood ) );
			var cap = GK.sphere( 0.032, 16, 6, 0, Math.PI * 2, 0, Math.PI / 2 );
			cap.applyMatrix4( new Matrix4().makeRotationX( -Math.PI / 2 ) );
			cap.translate( 0, 0, -0.028 );
			list.Add( GK.prepare( cap, new Opts { color = 0xc8a050, rough = 0.25, metal = 1, pattern = 1 } ) );
			return GeoKit.mergePrepared( list );
		}

		// Throttle lever, local origin at the pivot, lever pointing +Y (neutral).
		public static BufferGeometry throttleGeometry()
		{
			var list = new List<BufferGeometry>();
			list.Add( GK.prepare( GK.rod( V( 0, -0.01, 0 ), V( 0, 0.15, 0.015 ), 0.008, 8 ), STAINLESS ) );
			var knob = GK.sphere( 0.022, 14, 10 );
			knob.scale( 1, 1.15, 1 );
			knob.translate( 0, 0.165, 0.016 );
			list.Add( GK.prepare( knob, BLACK_PLASTIC ) );
			var boss = GK.cylinder( 0.02, 0.02, 0.05, 12 );
			boss.applyMatrix4( new Matrix4().makeRotationZ( Math.PI / 2 ) );
			list.Add( GK.prepare( boss, STAINLESS ) );
			return GeoKit.mergePrepared( list );
		}

		// Open-array radar antenna, local origin on the rotation axis at the pedestal top.
		public static BufferGeometry radarArrayGeometry()
		{
			var list = new List<BufferGeometry>();
			list.Add( GK.prepare( GK.cylinder( 0.05, 0.06, 0.06, 14 ), new Opts { color = 0xf3f2ee, rough = 0.4 } ) );
			var bar = GK.roundedBox( 1.05, 0.12, 0.1, 0.035, 2 );
			bar.translate( 0, 0.09, 0 );
			list.Add( GK.prepare( bar, new Opts { color = 0xf3f2ee, rough = 0.4 } ) );
			var stripe = GK.box( 0.95, 0.028, 0.004 );
			stripe.translate( 0, 0.09, 0.051 );
			list.Add( GK.prepare( stripe, new Opts { color = 0x2c2f35, rough = 0.5 } ) );
			return GeoKit.mergePrepared( list );
		}
	}
}
