using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullLines;
using static Tidewater.World.Boat.HullBuilder;

// Port of src/world/boat/Wheelhouse.js: the wheelhouse of the lobster boat: walls, windshield, roof, console and helm, seating, cabin
// detail and roof gear, built into the GeoKit buckets.
namespace Tidewater.World.Boat
{
	// anchor points and animated-part pivots the wheelhouse hands to the model (the JS `parts` object)
	public sealed class WheelhouseParts
	{
		public Vector3 wheelCenter, wheelAxis, throttlePivot, radarPivot, flagPivot;
		public Dictionary<string, object> extra = new Dictionary<string, object>();
	}

	// Wheelhouse dimensions (boat frame)
	public static class HOUSE
	{
		public const double wallT = 0.045, wsBottomY = 1.48, wsTopY = 2.24, roofUnderY = 2.31, roofZ0 = -0.95, roofZ1 = 1.34, winBottom = 1.56, winTop = 2.12, helmX = -0.55, seatZ = 0.22;
		public static class dash { public const double zFace = 0.98, yKnee = 1.08, zTop = 1.16, yTop = 1.4, zBack = 1.41, halfW = 1.1; }
	}

	public static partial class Wheelhouse
	{
		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );

		static readonly Opts STAINLESS = new Opts { color = Palette.stainless, rough = 0.22, metal = 1 };
		static readonly Opts BLACK_PLASTIC = new Opts { color = 0x1a1b1d, rough = 0.55, metal = 0 };
		static readonly Opts WHITE_PAINT = new Opts { color = 0xf1f0eb, rough = 0.35, metal = 0 };
		static readonly Opts FRAME = new Opts { color = 0xa4a8ab, rough = 0.55, metal = 1 }; // weathered, oxidised aluminium
		static readonly Opts VINYL = new Opts { color = 0x1f2a3a, rough = 0.6, metal = 0 };

		public static double wsZ( HullLines L, double y ) => L.houseFront - ( y - HOUSE.wsBottomY ) * ( 0.25 / 0.76 );
		public static double wallX( HullLines L, double z, double y ) => houseHalfWidth( L, z ) - Math.Max( 0, y - 1.15 ) * 0.04;
		public static double roofTopY( double x ) => 2.375 + 0.035 * ( 1 - Math.Pow( x / 1.35, 2 ) );

		// Offset a convex polygon (list of [u, v]) outward by d.
		static double[][] offsetPoly( double[][] poly, double d )
		{
			int n = poly.Length;
			bool ccw = area2( poly ) > 0;
			var lines = new List<double[]>();
			for ( int i = 0; i < n; i ++ )
			{
				var a = poly[ i ]; var b = poly[ ( i + 1 ) % n ];
				double ex = b[ 0 ] - a[ 0 ], ey = b[ 1 ] - a[ 1 ];
				double l = JS.Hypot( ex, ey );
				double nx = ey / l, ny = -ex / l; // right-hand normal (outward for CCW)
				if ( ! ccw ) { nx = -nx; ny = -ny; }
				lines.Add( new[] { a[ 0 ] + nx * d, a[ 1 ] + ny * d, ex, ey } );
			}

			var o = new double[ n ][];
			for ( int i = 0; i < n; i ++ )
			{
				var l1 = lines[ ( i + n - 1 ) % n ]; var l2 = lines[ i ];
				double den = l1[ 2 ] * l2[ 3 ] - l1[ 3 ] * l2[ 2 ];
				double s = ( ( l2[ 0 ] - l1[ 0 ] ) * l2[ 3 ] - ( l2[ 1 ] - l1[ 1 ] ) * l2[ 2 ] ) / den;
				o[ i ] = new[] { l1[ 0 ] + l1[ 2 ] * s, l1[ 1 ] + l1[ 3 ] * s };
			}

			return o;
		}

		static double area2( double[][] poly )
		{
			double a = 0;
			for ( int i = 0; i < poly.Length; i ++ ) { var p = poly[ i ]; var q = poly[ ( i + 1 ) % poly.Length ]; a += p[ 0 ] * q[ 1 ] - q[ 0 ] * p[ 1 ]; }
			return a;
		}

		static readonly double[] QUAD_UVS = { 0, 0, 1, 0, 1, 1, 0, 1 };

		// Quad from four corner points (counter-clockwise seen from the front) with 0..1 or metric UVs.
		static BufferGeometry quad( Vector3 a, Vector3 b, Vector3 c, Vector3 d, double[] uvs = null )
		{
			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( new double[] { a.x, a.y, a.z, b.x, b.y, b.z, c.x, c.y, c.z, d.x, d.y, d.z }, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs ?? QUAD_UVS, 2 ) );
			g.setIndex( new[] { 0, 1, 2, 0, 2, 3 } );
			g.computeVertexNormals();
			return g;
		}

		public static void buildWheelhouse( GeoKit kit, HullLines L, WheelhouseParts parts )
		{
			buildWalls( kit, L );
			buildWindshield( kit, L );
			buildRoof( kit, L );
			buildConsole( kit, L, parts );
			buildSeating( kit, L );
			buildCabinDetail( kit, L );
			buildRoofGear( kit, L, parts );
		}

		// ------------------------------------------------------------------ side walls

		static double[][][] sideWindowHoles( HullLines L )
		{
			double y0 = HOUSE.winBottom, y1 = HOUSE.winTop;
			Func<double, double> front = y => wsZ( L, y ) - 0.1;
			return new[]
			{
				new[] { new[] { 0.3, y0 }, new[] { 0.78, y0 }, new[] { 0.78, y1 }, new[] { 0.3, y1 } },
				new[] { new[] { 0.84, y0 }, new[] { front( y0 ), y0 }, new[] { front( y1 ), y1 }, new[] { 0.84, y1 } },
			};
		}

		static void buildWalls( GeoKit kit, HullLines L )
		{
			double wallT = HOUSE.wallT, roofUnderY = HOUSE.roofUnderY;
			double z0 = L.houseBack, z1 = L.houseFront;
			var outline = new List<double[]>();
			int NB = 8;
			for ( int i = 0; i <= NB; i ++ )
			{
				double z = lerp( z0, z1, ( double ) i / NB );
				outline.Add( new[] { z, L.sheerY( L.tAtSheerZ( z ) ) } );
			}

			outline.Add( new[] { z1, HOUSE.wsBottomY } );
			outline.Add( new[] { wsZ( L, roofUnderY ), roofUnderY } );
			outline.Add( new[] { z0, roofUnderY } );
			var holes = sideWindowHoles( L );

			foreach ( double s in new double[] { 1, -1 } )
			{
				Func<double, double, int, Vector3> map = ( u, v, side ) => V( s * ( wallX( L, u, v ) - side * wallT ), v, u );
				// the inner face is painted panelling (seams, screws, grime); the outside stays glossy gelcoat
				var wall = GK.slab( outline.ToArray(), holes, map );
				GK.auxVertices( wall, ( p, i ) => Math.Abs( p.x ) < wallX( L, p.z, p.y ) - wallT * 0.5 ? new[] { 0.5, 0, 2, 0 } : new[] { 0.3, 0, 0, 0 } );
				kit.add( "gelcoat", wall, new Opts { color = Palette.gelcoat } );

				foreach ( var h in holes )
				{
					// aluminum frame ring through the wall, proud of both faces
					var outer = offsetPoly( h, 0.024 );
					Func<double, double, int, Vector3> fmap = ( u, v, side ) => V( s * ( wallX( L, u, v ) + 0.008 - side * ( wallT + 0.016 ) ), v, u );
					kit.add( "fittings", GK.slab( outer, new[] { offsetPoly( h, -0.006 ) }, fmap ), FRAME );

					// (no glass in the openings: the windows are left open)

					// black rubber gasket lining the opening, inside the frame ring
					Func<double, double, int, Vector3> gmap = ( u, v, side ) => V( s * ( wallX( L, u, v ) + 0.004 - side * ( wallT + 0.008 ) ), v, u );
					kit.add( "fittings", GK.slab( offsetPoly( h, -0.005 ), new[] { offsetPoly( h, -0.014 ) }, gmap ), new Opts { color = 0x0e0f10, rough = 0.8 } );

					// pop rivets around the frame on the inside face
					var ring = offsetPoly( h, 0.011 );
					for ( int i = 0; i < ring.Length; i ++ )
					{
						var a = ring[ i ]; var b = ring[ ( i + 1 ) % ring.Length ];
						double len = JS.Hypot( b[ 0 ] - a[ 0 ], b[ 1 ] - a[ 1 ] );
						double n = Math.Max( 2, JS.Round( len / 0.075 ) );
						for ( int k = 0; k < n; k ++ )
						{
							double u = lerp( a[ 0 ], b[ 0 ], ( k + 0.5 ) / n ), v = lerp( a[ 1 ], b[ 1 ], ( k + 0.5 ) / n );
							double xr = s * ( wallX( L, u, v ) + 0.008 - ( wallT + 0.016 ) - 0.001 );
							var rv = GK.cylinder( 0.0045, 0.0045, 0.003, 6 );
							rv.applyMatrix4( GK.mat4( xr, v, u, 0, 0, Math.PI / 2 ) );
							kit.add( "fittings", rv, new Opts { color = 0xc9ccd0, rough = 0.35, metal = 1 } );
						}
					}
				}

				// sliding-window latch
				double lz = 0.81, ly = 1.84;
				var lb = GK.box( 0.02, 0.06, 0.025 );
				lb.translate( s * ( wallX( L, lz, ly ) - wallT - 0.012 ), ly, lz );
				kit.add( "fittings", lb, BLACK_PLASTIC );
			}

			// house front below the windshield (coaming)
			double tF = L.tAtSheerZ( L.houseFront );
			double hw = houseHalfWidth( L, L.houseFront );
			var cOutline = new List<double[]>();
			for ( int i = 0; i <= 10; i ++ )
			{
				double x = lerp( -hw, hw, ( double ) i / 10 );
				cOutline.Add( new[] { x, foredeckY( L, tF, x ) - 0.012 } );
			}

			cOutline.Add( new[] { hw, HOUSE.wsBottomY } ); cOutline.Add( new[] { -hw, HOUSE.wsBottomY } );
			var coaming = GK.slab( cOutline.ToArray(), new double[ 0 ][][], ( u, v, side ) => V( u, v, L.houseFront - side * 0.04 ) );
			GK.auxVertices( coaming, ( p, i ) => p.z < L.houseFront - 0.02 ? new[] { 0.5, 0, 2, 0 } : new[] { 0.3, 0, 0, 0 } );
			kit.add( "gelcoat", coaming, new Opts { color = Palette.gelcoat } );
		}

		// ------------------------------------------------------------------ windshield

		static void buildWindshield( GeoKit kit, HullLines L )
		{
			double wsBottomY = HOUSE.wsBottomY, roofUnderY = HOUSE.roofUnderY;
			var rake = new Vector3( 0, 0.76, -0.25 ).normalize();
			var normal = new Vector3( 0, 0.25, 0.76 ).normalize();
			var bas = V( 0, wsBottomY, L.houseFront );
			double vTop = ( roofUnderY - wsBottomY ) / rake.y;
			Func<double, double, Vector3> at = ( u, v ) => { var r = bas.clone().addScaledVector( rake, v ); r.x = u; return r; };
			Func<double, double> halfAt = v => { var p = at( 0, v ); return wallX( L, p.z, p.y ); };

			double xb = halfAt( 0 ), xt = halfAt( vTop );
			var outline = new[] { new[] { -xb, 0 }, new[] { xb, 0 }, new[] { xt, vTop }, new[] { -xt, vTop } };
			double v0 = 0.07, v1 = 0.75;
			var holes = new[]
			{
				new[] { new[] { -0.36, v0 }, new[] { 0.36, v0 }, new[] { 0.36, v1 }, new[] { -0.36, v1 } },
				new[] { new[] { 0.42, v0 }, new[] { halfAt( v0 ) - 0.09, v0 }, new[] { halfAt( v1 ) - 0.09, v1 }, new[] { 0.42, v1 } },
				new[] { new[] { -0.42, v0 }, new[] { -0.42, v1 }, new[] { -halfAt( v1 ) + 0.09, v1 }, new[] { -halfAt( v0 ) + 0.09, v0 } },
			};
			double T = 0.05;
			Func<double, double, int, Vector3> map = ( u, v, side ) => at( u, v ).addScaledVector( normal, -side * T );
			kit.add( "gelcoat", GK.slab( outline, holes, map ), new Opts { color = Palette.gelcoat, rough = 0.3 } );

			foreach ( var h in holes )
			{
				var outer = offsetPoly( h, 0.022 );
				kit.add( "fittings", GK.slab( outer, new[] { offsetPoly( h, -0.006 ) }, ( u, v, side ) => at( u, v ).addScaledVector( normal, 0.008 - side * ( T + 0.016 ) ) ), FRAME );
				// (no glass: open windows)
			}

			// pantograph wipers on the centre and starboard panes
			foreach ( var ua in new[] { new[] { 0.0, 0.25 }, new[] { -0.72, 0.2 } } )
			{
				double u = ua[ 0 ], ang = ua[ 1 ];
				var pivot = at( u, v0 + 0.03 ).addScaledVector( normal, 0.012 );
				var dir = rake.clone().applyAxisAngle( normal, ang );
				var blade = GK.rod( pivot, pivot.clone().addScaledVector( dir, 0.52 ), 0.006, 5 );
				kit.add( "fittings", blade, BLACK_PLASTIC );
				kit.add( "fittings", GK.rod( pivot.clone().addScaledVector( normal, -0.01 ), pivot.clone().addScaledVector( normal, 0.012 ), 0.014, 8 ), BLACK_PLASTIC );
			}
		}

		// ------------------------------------------------------------------ roof

		static double roofHalf( HullLines L, double z )
		{
			double zc = Math.Min( Math.Max( z, L.houseBack ), 1.18 );
			double hw = wallX( L, zc, HOUSE.roofUnderY ) + 0.09;
			double rc = 0.22;
			double d = Math.Min( z - HOUSE.roofZ0, HOUSE.roofZ1 - z );
			if ( d < rc ) hw -= rc - Math.Sqrt( Math.Max( 0, rc * rc - Math.Pow( rc - d, 2 ) ) );
			return hw;
		}

		static void buildRoof( GeoKit kit, HullLines L )
		{
			double yb = HOUSE.roofUnderY;
			int N = 22;
			var profiles = new List<List<Vector3>>();
			// cluster stations near both ends for the rounded corners, plus a pair on each end wall of the wheelhouse so the headliner
			// pattern (per-vertex aux) switches inside the wall, not across the roof
			var zs = new List<double>();
			for ( int i = 0; i <= N; i ++ )
			{
				double f = ( double ) i / N;
				double g0 = 0.5 - 0.5 * Math.Cos( Math.PI * f );
				zs.Add( lerp( HOUSE.roofZ0, HOUSE.roofZ1, lerp( f, g0, 0.6 ) ) );
			}

			foreach ( double zw in new[] { L.houseBack, L.houseFront } )
				foreach ( double dz in new[] { -0.012, 0.012 } )
				{
					double z = zw + dz;
					if ( z > HOUSE.roofZ0 + 0.02 && z < HOUSE.roofZ1 - 0.02 ) zs.Add( z );
				}

			zs = SortStable( zs );
			foreach ( double z in zs )
			{
				double hw = roofHalf( L, z );
				// inner face of the side wall under the roof (the headliner ends there)
				double wIn = wallX( L, Math.Min( Math.Max( z, L.houseBack ), L.houseFront ), yb ) - HOUSE.wallT;
				double[][] half = {
					new[] { 0, yb }, new[] { wIn * 0.5, yb }, new[] { wIn - 0.01, yb }, new[] { wIn + 0.01, yb }, new[] { hw - 0.03, yb }, new[] { hw - 0.008, yb + 0.008 }, new[] { hw, yb + 0.025 },
					new[] { hw - 0.004, roofTopY( hw ) - 0.012 }, new[] { hw - 0.02, roofTopY( hw ) }, new[] { hw * 0.75, roofTopY( hw * 0.75 ) },
					new[] { hw * 0.5, roofTopY( hw * 0.5 ) }, new[] { hw * 0.25, roofTopY( hw * 0.25 ) }, new[] { 0, roofTopY( 0 ) },
				};
				var loop = half.Select( p => V( p[ 0 ], p[ 1 ], z ) ).ToList();
				for ( int k = half.Length - 2; k >= 1; k -- ) loop.Add( V( -half[ k ][ 0 ], half[ k ][ 1 ], z ) );
				profiles.Add( loop );
			}

			int N2 = profiles.Count - 1;
			var g = GK.loft( profiles, true );
			orientOutwardFn( g, p => V( p.x, p.y - 2.345, 0 ) );
			metricUV( g, p => new[] { p.x, p.z } );
			// non-skid on top; the underside is headliner over the wheelhouse, gelcoat on the overhangs
			GK.auxVertices( g, ( p, i ) =>
			{
				if ( p.y > yb + 0.03 ) return new[] { 0.35, 0, 1, 0 };
				bool inside = p.z > L.houseBack && p.z < L.houseFront && Math.Abs( p.x ) < wallX( L, Math.Min( Math.Max( p.z, L.houseBack ), L.houseFront ), yb ) - HOUSE.wallT;
				return new[] { 0.35, 0, inside ? 3.0 : 0.0, 0 };
			} );
			kit.add( "gelcoat", g, new Opts { color = Palette.gelcoat } );
			kit.add( "gelcoat", GK.fanCap( profiles[ 0 ], V( 0, 0, -1 ) ), new Opts { color = Palette.gelcoat, rough = 0.3 } );
			kit.add( "gelcoat", GK.fanCap( profiles[ N2 ], V( 0, 0, 1 ) ), new Opts { color = Palette.gelcoat, rough = 0.3 } );

			// stainless grab rails along the roof edges
			foreach ( double s in new double[] { 1, -1 } )
			{
				double x = s * 1.1;
				double y = roofTopY( 1.1 ) + 0.07;
				kit.add( "fittings", GK.rod( V( x, y, -0.7 ), V( x, y, 0.95 ), 0.013, 8 ), STAINLESS );
				foreach ( double z in new[] { -0.7, -0.12, 0.45, 0.95 } ) kit.add( "fittings", GK.rod( V( x, roofTopY( 1.1 ) - 0.01, z ), V( x, y, z ), 0.011, 6 ), STAINLESS );
			}
		}

		// Array.sort( ( a, b ) => a - b ) on numbers (stable)
		static List<double> SortStable( List<double> v )
		{
			var order = Enumerable.Range( 0, v.Count ).ToList();
			order.Sort( ( ia, ib ) => { int c = v[ ia ].CompareTo( v[ ib ] ); return c != 0 ? c : ia.CompareTo( ib ); } );
			return order.Select( i => v[ i ] ).ToList();
		}

		static void orientOutwardFn( BufferGeometry g, Func<Vector3, Vector3> fn )
		{
			var p = g.attributes[ "position" ]; var n = g.attributes[ "normal" ];
			double dot = 0;
			var a = V( 0, 0, 0 ); var b = V( 0, 0, 0 );
			for ( int i = 0; i < p.count; i ++ )
			{
				a.fromBufferAttribute( p, i ); b.fromBufferAttribute( n, i );
				dot += b.dot( fn( a ) );
			}

			if ( dot < 0 )
			{
				var idx = ( int[] ) g.index.array.Clone();
				for ( int i = 0; i < idx.Length; i += 3 ) { int t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t; }
				g.setIndex( idx );
				g.computeVertexNormals();
			}
		}

		static void metricUV( BufferGeometry g, Func<Vector3, double[]> fn )
		{
			var p = g.attributes[ "position" ];
			var uv = new float[ p.count * 2 ];
			var v = V( 0, 0, 0 );
			for ( int i = 0; i < p.count; i ++ )
			{
				v.fromBufferAttribute( p, i );
				var t = fn( v );
				uv[ i * 2 ] = ( float ) t[ 0 ]; uv[ i * 2 + 1 ] = ( float ) t[ 1 ];
			}

			g.setAttribute( "uv", new BufferAttribute( uv, 2 ) );
		}

		// ------------------------------------------------------------------ console, helm, instruments

		public sealed class PanelFrame
		{
			public Vector3 along, normal; public Func<double, double, Vector3> at; public double length, angle;
		}

		// Slope of the instrument panel (from the knee to the dash top).
		public static PanelFrame panelFrame()
		{
			var B = new Vector2( HOUSE.dash.zFace, HOUSE.dash.yKnee ); var C = new Vector2( HOUSE.dash.zTop, HOUSE.dash.yTop );
			var dir = C.clone().sub( B ).normalize(); // (dz, dy)
			var along = V( 0, dir.y, dir.x ); // up the panel
			var normal = V( 0, dir.x, -dir.y ); // toward the helmsman (up and aft)
			return new PanelFrame { along = along, normal = normal, at = ( x, f ) => V( x, lerp( B.y, C.y, f ), lerp( B.x, C.x, f ) ), length = B.distanceTo( C ), angle = Math.Atan2( dir.x, dir.y ) };
		}

		static Matrix4 panelBasis( PanelFrame pf, Vector3 position ) => new Matrix4().makeBasis( V( -1, 0, 0 ), pf.along, pf.normal ).setPosition( position );

		static void buildConsole( GeoKit kit, HullLines L, WheelhouseParts parts )
		{
			// console body; its ends follow the hull lining where the flared hull narrows toward the bow
			double[][] corners = { new[] { HOUSE.dash.zFace, L.deckY }, new[] { HOUSE.dash.zFace, HOUSE.dash.yKnee }, new[] { HOUSE.dash.zTop, HOUSE.dash.yTop }, new[] { HOUSE.dash.zBack, HOUSE.dash.yTop }, new[] { HOUSE.dash.zBack, L.deckY } };
			var outline = new List<double[]>();
			for ( int i = 0; i < corners.Length; i ++ )
			{
				var a = corners[ i ]; var b = corners[ ( i + 1 ) % corners.Length ];
				for ( int k = 0; k < 6; k ++ ) outline.Add( new[] { lerp( a[ 0 ], b[ 0 ], k / 6.0 ), lerp( a[ 1 ], b[ 1 ], k / 6.0 ) } );
			}

			Func<double, double, double> endX = ( z, y ) => Math.Min( HOUSE.dash.halfW, L.halfBreadth( L.tAtSheerZ( z ), Math.Min( y, L.sheerY( L.tAtSheerZ( z ) ) ) ) - L.shell - 0.004 );
			var body = GK.slab( outline.ToArray(), new double[ 0 ][][], ( u, v, side ) => V( ( side != 0 ? -1 : 1 ) * endX( u, v ), v, u ) );
			GK.auxVertices( body, ( p, i ) => new[] { 0.5, 0, 2, 0 } );
			kit.add( "gelcoat", body, new Opts { color = Palette.gelcoat } );

			// anti-glare dash top and instrument panel
			var top = GK.box( HOUSE.dash.halfW * 2 - 0.01, 0.01, HOUSE.dash.zBack - HOUSE.dash.zTop );
			top.translate( 0, HOUSE.dash.yTop + 0.005, ( HOUSE.dash.zBack + HOUSE.dash.zTop ) / 2 );
			kit.add( "gelcoat", top, new Opts { color = 0x2a2c2f, rough = 0.85 } );

			var pf = panelFrame();
			var panel = GK.box( HOUSE.dash.halfW * 2 - 0.04, pf.length - 0.02, 0.012 );
			panel.applyMatrix4( GK.mat4( 0, 0, 0, pf.angle, 0, 0 ) );
			var pp = pf.at( 0, 0.5 ).addScaledVector( pf.normal, 0.004 );
			panel.translate( pp.x, pp.y, pp.z );
			kit.add( "fittings", panel, new Opts { color = 0x1d1f22, rough = 0.7, pattern = 4 } );

			// teak fiddle rail along the dash top edge
			var fr = GK.box( HOUSE.dash.halfW * 2 - 0.02, 0.03, 0.022 );
			fr.translate( 0, HOUSE.dash.yTop + 0.02, HOUSE.dash.zTop + 0.011 );
			kit.add( "wood", fr, new Opts { rough = 0.3 } );

			// gauges (bezel + backlit dial)
			foreach ( var xf in new[] { new[] { -0.76, 0.72 }, new[] { -0.34, 0.72 }, new[] { -0.55, 0.9 }, new[] { -0.12, 0.5 }, new[] { 0.02, 0.5 } } )
			{
				var c = pf.at( xf[ 0 ], xf[ 1 ] ).addScaledVector( pf.normal, 0.012 );
				var m = panelBasis( pf, c );
				var bezel = GK.torus( 0.043, 0.006, 4, 16 );
				bezel.applyMatrix4( m );
				kit.add( "fittings", bezel, STAINLESS );
				var dial = Geo.Circle( 0.042, 20 );
				// 3 mm proud of the panel face (was coplanar with it: z-fighting)
				dial.applyMatrix4( panelBasis( pf, c.clone().addScaledVector( pf.normal, 0.001 ) ) );
				kit.add( "glow", dial, new Opts { color = 0xffffff, rough = 0.2, pattern = 3 } );
			}

			// switch panel with rocker switches
			var sw = pf.at( 0.55, 0.45 ).addScaledVector( pf.normal, 0.012 );
			var swm = panelBasis( pf, sw );
			var swPlate = GK.box( 0.34, 0.1, 0.008 );
			swPlate.applyMatrix4( swm );
			kit.add( "fittings", swPlate, new Opts { color = 0x2d3036, rough = 0.5, pattern = 4 } );
			var tape = GK.box( 0.3, 0.014, 0.002 );
			tape.applyMatrix4( panelBasis( pf, sw.clone().addScaledVector( pf.along, -0.038 ).addScaledVector( pf.normal, 0.005 ) ) );
			kit.add( "fittings", tape, new Opts { color = 0xffffff, rough = 0.5, pattern = 8 } );
			for ( int i = 0; i < 6; i ++ )
			{
				var r = GK.box( 0.03, 0.045, 0.015 );
				r.applyMatrix4( panelBasis( pf, sw.clone().add( V( -0.13 + i * 0.052, 0, 0 ) ).addScaledVector( pf.normal, 0.008 ) ) );
				kit.add( "fittings", r, new Opts { color = 0x111214, rough = 0.4 } );
				var led = GK.box( 0.008, 0.008, 0.004 );
				led.applyMatrix4( panelBasis( pf, sw.clone().add( V( -0.13 + i * 0.052, 0, 0 ) ).addScaledVector( pf.along, 0.035 ).addScaledVector( pf.normal, 0.006 ) ) );
				kit.add( "glow", led, new Opts { color = i % 3 == 0 ? 0x33ff66 : 0xff5522, rough = 0.3, pattern = 6 } );
			}

			// helm: wheel shaft (static) and the wheel pivot (animated part built in BoatModel)
			var hub = pf.at( HOUSE.helmX, 0.45 );
			var center = hub.clone().addScaledVector( pf.normal, 0.14 );
			kit.add( "fittings", GK.rod( hub.clone().addScaledVector( pf.normal, -0.01 ), center.clone().addScaledVector( pf.normal, -0.03 ), 0.02, 10 ), STAINLESS );
			var helmBoss = GK.cylinder( 0.05, 0.055, 0.025, 16 );
			helmBoss.applyMatrix4( GK.alignY( hub.clone().addScaledVector( pf.normal, 0.008 ), pf.normal ) );
			kit.add( "fittings", helmBoss, new Opts { color = 0x2b2d30, rough = 0.5 } );
			parts.wheelCenter = center;
			parts.wheelAxis = pf.normal.clone().negate(); // local +Z of the wheel points into the dash

			// throttle / shift control on the dash top, starboard of the wheel
			double tx = -0.93, tz = 1.24;
			var tb = GK.roundedBox( 0.1, 0.075, 0.15, 0.015, 1 );
			tb.translate( tx, HOUSE.dash.yTop + 0.01 + 0.0375, tz );
			kit.add( "fittings", tb, BLACK_PLASTIC );
			var tp = GK.box( 0.085, 0.004, 0.13 );
			tp.translate( tx, HOUSE.dash.yTop + 0.01 + 0.077, tz );
			kit.add( "fittings", tp, STAINLESS );
			parts.throttlePivot = V( tx, HOUSE.dash.yTop + 0.09, tz );

			// compass binnacle with a glass dome
			double cx = HOUSE.helmX, cz = 1.3;
			var cb = GK.lathe( new[] { new[] { 0.0, 0 }, new[] { 0.07, 0 }, new[] { 0.072, 0.02 }, new[] { 0.062, 0.045 }, new[] { 0.0, 0.045 } }, 20 );
			cb.translate( cx, HOUSE.dash.yTop + 0.01, cz );
			kit.add( "fittings", cb, BLACK_PLASTIC );
			var card = GK.cylinder( 0.05, 0.05, 0.01, 20 );
			card.translate( cx, HOUSE.dash.yTop + 0.06, cz );
			kit.add( "fittings", card, new Opts { color = 0x2c2c2a, rough = 0.5 } );
			var lubber = GK.box( 0.004, 0.02, 0.008 );
			lubber.translate( cx, HOUSE.dash.yTop + 0.07, cz + 0.045 );
			kit.add( "fittings", lubber, new Opts { color = 0xd0402a, rough = 0.5 } );
			var dome = GK.sphere( 0.058, 20, 8, 0, Math.PI * 2, 0, Math.PI / 2 );
			dome.translate( cx, HOUSE.dash.yTop + 0.053, cz );
			kit.add( "glass", dome );

			// electronics: radar display (centre) and chart plotter (port)
			foreach ( var xm in new[] { new[] { -0.06, 1 }, new[] { 0.38, 2 } } )
			{
				double x = xm[ 0 ], mode = xm[ 1 ];
				double tilt = 0.3; // lean back so the screen faces the helmsman
				double w = 0.36, h = 0.27;
				var bodyM = GK.mat4( x, HOUSE.dash.yTop + 0.01 + h / 2 + 0.03, 1.29, tilt, 0, 0 );
				var bodyG = GK.roundedBox( w, h, 0.07, 0.018, 1 );
				bodyG.applyMatrix4( bodyM );
				kit.add( "fittings", bodyG, BLACK_PLASTIC );
				var mount = GK.box( 0.1, 0.05, 0.08 );
				mount.translate( x, HOUSE.dash.yTop + 0.035, 1.3 );
				kit.add( "fittings", mount, BLACK_PLASTIC );
				var screen = Geo.Plane( w - 0.05, h - 0.06 );
				screen.applyMatrix4( new Matrix4().makeRotationY( Math.PI ) );
				screen.applyMatrix4( GK.mat4( 0, 0.012, -0.037 ) ); // 2 mm proud of the bezel face
				screen.applyMatrix4( bodyM );
				// PlaneGeometry uvs are flipped horizontally after the Y rotation; restore them
				var uv = screen.attributes[ "uv" ];
				for ( int i = 0; i < uv.count; i ++ ) uv.setX( i, 1 - uv.getX( i ) );
				kit.add( "glow", screen, new Opts { color = 0xffffff, rough = 0.15, pattern = mode } );
			}

			// overhead console with VHF radio above the windshield
			double oz = 1.02, oy = HOUSE.roofUnderY - 0.07;
			var oc = GK.roundedBox( 0.9, 0.13, 0.3, 0.02, 1 );
			oc.applyMatrix4( GK.mat4( -0.15, oy, oz, -0.25, 0, 0 ) );
			kit.add( "fittings", oc, new Opts { color = 0x24262a, rough = 0.6 } );
			var face = GK.mat4( -0.15, oy, oz, -0.25, 0, 0 ); // aft face tilted down toward the helm
			var vhf = GK.box( 0.2, 0.06, 0.02 );
			vhf.applyMatrix4( new Matrix4().makeTranslation( -0.18, 0, -0.155 ) );
			vhf.applyMatrix4( face );
			kit.add( "fittings", vhf, new Opts { color = 0x111214, rough = 0.4 } );
			var lcd = Geo.Plane( 0.09, 0.03 );
			lcd.applyMatrix4( new Matrix4().makeRotationY( Math.PI ) );
			lcd.applyMatrix4( new Matrix4().makeTranslation( -0.2, 0.005, -0.168 ) ); // 3 mm proud of the radio face
			lcd.applyMatrix4( face );
			kit.add( "glow", lcd, new Opts { color = 0x7dff9a, rough = 0.2, pattern = 6 } );
			foreach ( double k in new[] { -0.06, 0.1, 0.26 } )
			{
				var knob = GK.cylinder( 0.012, 0.012, 0.02, 10 );
				knob.applyMatrix4( new Matrix4().makeRotationX( Math.PI / 2 ) );
				knob.applyMatrix4( new Matrix4().makeTranslation( k, 0, -0.16 ) );
				knob.applyMatrix4( face );
				kit.add( "fittings", knob, STAINLESS );
			}

			// microphone hanging from the VHF
			var mic = GK.roundedBox( 0.05, 0.08, 0.03, 0.01, 1 );
			mic.translate( -0.02, oy - 0.2, oz - 0.12 );
			kit.add( "fittings", mic, BLACK_PLASTIC );
			kit.add( "fittings", GK.tube( new List<Vector3> { V( -0.05, oy - 0.04, oz - 0.15 ), V( -0.08, oy - 0.1, oz - 0.14 ), V( -0.04, oy - 0.14, oz - 0.13 ), V( -0.02, oy - 0.16, oz - 0.12 ) }, 0.004, 16, 4 ), BLACK_PLASTIC );

			// cabin dome light
			var dl = GK.cylinder( 0.08, 0.085, 0.012, 20 );
			dl.translate( 0, HOUSE.roofUnderY - 0.006, 0.42 );
			kit.add( "glow", dl, new Opts { color = 0xffe6b8, rough = 0.4, pattern = 5 } );

			// cuddy door in the console face (port side)
			var door = GK.box( 0.5, 0.62, 0.01 );
			door.translate( 0.55, 0.72, HOUSE.dash.zFace - 0.004 );
			kit.add( "fittings", door, new Opts { color = 0x121315, rough = 0.9 } );
			foreach ( var f in new[] { new[] { 0.56, 0.03, 0.55, 1.045 }, new[] { 0.56, 0.03, 0.55, 0.395 }, new[] { 0.03, 0.68, 0.285, 0.72 }, new[] { 0.03, 0.68, 0.815, 0.72 } } )
			{
				var b = GK.box( f[ 0 ], f[ 1 ], 0.022 );
				b.translate( f[ 2 ], f[ 3 ], HOUSE.dash.zFace - 0.011 );
				kit.add( "wood", b, new Opts { rough = 0.3 } );
			}
		}

		// ------------------------------------------------------------------ seating

		static void buildSeating( GeoKit kit, HullLines L )
		{
			double x = HOUSE.helmX, z = HOUSE.seatZ, y0 = L.deckY;
			var baseP = GK.cylinder( 0.18, 0.19, 0.012, 20 );
			baseP.translate( x, y0 + 0.006, z );
			kit.add( "fittings", baseP, STAINLESS );
			kit.add( "fittings", GK.rod( V( x, y0, z ), V( x, 0.92, z ), 0.042, 14 ), STAINLESS );
			var ring = GK.torus( 0.17, 0.011, 6, 28 );
			ring.applyMatrix4( GK.mat4( x, 0.62, z, Math.PI / 2, 0, 0 ) );
			kit.add( "fittings", ring, STAINLESS );
			for ( int i = 0; i < 3; i ++ )
			{
				double a = i * Math.PI * 2 / 3 + 0.5;
				kit.add( "fittings", GK.rod( V( x + Math.Cos( a ) * 0.04, 0.62, z + Math.Sin( a ) * 0.04 ), V( x + Math.Cos( a ) * 0.165, 0.62, z + Math.Sin( a ) * 0.165 ), 0.008, 6 ), STAINLESS );
			}

			var pan = GK.roundedBox( 0.44, 0.045, 0.4, 0.015, 1 );
			pan.translate( x, 0.94, z );
			kit.add( "fittings", pan, new Opts { color = 0x2b2e33, rough = 0.5 } );
			var cushion = GK.roundedBox( 0.46, 0.085, 0.42, 0.035, 1 );
			cushion.translate( x, 1.0, z );
			kit.add( "fittings", cushion, VINYL );
			var back = GK.roundedBox( 0.44, 0.3, 0.075, 0.03, 1 );
			back.applyMatrix4( GK.mat4( x, 1.22, z - 0.2, -0.17, 0, 0 ) );
			kit.add( "fittings", back, VINYL );
			kit.add( "fittings", GK.rod( V( x, 0.96, z - 0.17 ), V( x, 1.12, z - 0.2 ), 0.018, 8 ), STAINLESS );

			// companion bench on the port side
			var bench = GK.roundedBox( 0.42, 0.4, 0.75, 0.02, 1 );
			bench.translate( 0.86, y0 + 0.2, 0.45 );
			kit.add( "gelcoat", bench, new Opts { color = Palette.gelcoat, rough = 0.35 } );
			var bc = GK.roundedBox( 0.42, 0.07, 0.73, 0.03, 1 );
			bc.translate( 0.86, y0 + 0.435, 0.45 );
			kit.add( "fittings", bc, VINYL );
		}

		// inner face of the wheelhouse side wall (s = 1 port, -1 starboard) and the inward normal
		static double wallIn( HullLines L, double s, double z, double y ) => s * ( wallX( L, z, y ) - HOUSE.wallT );
	}
}
