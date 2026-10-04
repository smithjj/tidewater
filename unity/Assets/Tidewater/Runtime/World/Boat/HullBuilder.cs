using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;
using static Tidewater.World.Boat.HullLines;

// Port of src/world/boat/HullBuilder.js: the hull shell, keel, lining, decks, gunwale caps and rubrails, built into the GeoKit buckets.
namespace Tidewater.World.Boat
{
	public static class Palette
	{
		// Colors (sRGB hex; GeoKit converts to linear)
		public const double gelcoat = 0xf1eee6, lining = 0xe9e6dc, deck = 0xe2ddcf, antifouling = 0x7a1d15, stainless = 0xd0d3d6;
	}

	public static class HullBuilder
	{
		static Vector3 V( double x, double y, double z ) => new Vector3( x, y, z );

		public static void buildHull( GeoKit kit, HullLines L )
		{
			buildShell( kit, L );
			buildKeel( kit, L );
			buildLining( kit, L );
			buildDecks( kit, L );
			buildGunwale( kit, L );
			buildRubrail( kit, L );
		}

		// ------------------------------------------------------------------ outer shell + transom

		static void buildShell( GeoKit kit, HullLines L )
		{
			var ts = L.stationParams( 60 );
			var rows = ts.Select( t => L.station( t, 1 ) ).ToList();
			Func<Vector3, int, int, double, double, double[]> uvFn = ( p, i, j, u, v ) => new[] { p.z, p.y };
			kit.add( "hull", GK.gridSurface( rows, new GridOpts { uvFn = uvFn } ) );
			var mirrored = rows.Select( r => r.Select( p => V( -p.x, p.y, p.z ) ).ToList() ).ToList();
			kit.add( "hull", GK.gridSurface( mirrored, new GridOpts { flip = true, uvFn = uvFn } ) );

			// transom: strips between the port and starboard halves of the t = 0 section
			var sec = rows[ 0 ];
			var pos = new List<double>(); var idx = new List<int>(); var uvs = new List<double>();
			foreach ( var p in sec )
			{
				pos.AddRange( new[] { p.x, p.y, p.z, -p.x, p.y, p.z } );
				uvs.AddRange( new[] { p.x, p.y, -p.x, p.y } );
			}

			for ( int j = 0; j < sec.Count - 1; j ++ )
			{
				int a = 2 * j, b = 2 * j + 1, c = 2 * j + 2, d = 2 * j + 3;
				if ( sec[ j ].x > 1e-5 ) { idx.Add( a ); idx.Add( b ); idx.Add( c ); }
				idx.Add( b ); idx.Add( d ); idx.Add( c );
			}

			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
			g.setIndex( idx );
			GK.orientTowards( g, V( 0, 0, -1 ) );
			g.computeVertexNormals();
			fixUnusedNormals( g, V( 0, 0, -1 ) );
			kit.add( "hull", g );
		}

		// Closed low-poly hull envelope: both shell halves, transom and a flat lid at the sheer.
		public static BufferGeometry buildHullVolume( HullLines L )
		{
			var ts = L.stationParams( 28 );
			var rows = ts.Select( t => { var st = L.station( t, 1 ); return st.Where( ( _, j ) => j % 2 == 0 || j == st.Count - 1 ).ToList(); } ).ToList();
			int nj = rows[ 0 ].Count;
			var pos = new List<double>(); var idx = new List<int>();
			Func<Vector3, int> vert = p => { pos.Add( p.x ); pos.Add( p.y ); pos.Add( p.z ); return pos.Count / 3 - 1; };

			var port = rows.Select( r => r.Select( vert ).ToList() ).ToList();
			var star = rows.Select( r => r.Select( p => vert( V( -p.x, p.y, p.z ) ) ).ToList() ).ToList();
			for ( int i = 0; i < rows.Count - 1; i ++ )
			{
				for ( int j = 0; j < nj - 1; j ++ )
				{
					int a = port[ i ][ j ], b = port[ i + 1 ][ j ], c = port[ i + 1 ][ j + 1 ], d = port[ i ][ j + 1 ];
					idx.AddRange( new[] { a, d, b, d, c, b } );
					int a2 = star[ i ][ j ], b2 = star[ i + 1 ][ j ], c2 = star[ i + 1 ][ j + 1 ], d2 = star[ i ][ j + 1 ];
					idx.AddRange( new[] { a2, b2, d2, b2, c2, d2 } );
				}
			}

			// transom
			for ( int j = 0; j < nj - 1; j ++ ) idx.AddRange( new[] { port[ 0 ][ j ], star[ 0 ][ j ], port[ 0 ][ j + 1 ], star[ 0 ][ j ], star[ 0 ][ j + 1 ], port[ 0 ][ j + 1 ] } );
			// lid: strip between the port and starboard sheer lines
			int top = nj - 1;
			for ( int i = 0; i < rows.Count - 1; i ++ ) idx.AddRange( new[] { port[ i ][ top ], star[ i ][ top ], port[ i + 1 ][ top ], star[ i ][ top ], star[ i + 1 ][ top ], port[ i + 1 ][ top ] } );

			// drop zero-area triangles where the halves meet (keel line, stem head)
			Func<int, int, bool> same = ( i, k ) => Math.Abs( pos[ 3 * i ] - pos[ 3 * k ] ) + Math.Abs( pos[ 3 * i + 1 ] - pos[ 3 * k + 1 ] ) + Math.Abs( pos[ 3 * i + 2 ] - pos[ 3 * k + 2 ] ) < 1e-7;
			var clean = new List<int>();
			for ( int i = 0; i < idx.Count; i += 3 )
			{
				int a = idx[ i ], b = idx[ i + 1 ], c = idx[ i + 2 ];
				if ( ! same( a, b ) && ! same( b, c ) && ! same( a, c ) ) { clean.Add( a ); clean.Add( b ); clean.Add( c ); }
			}

			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setIndex( clean );
			g.computeVertexNormals();
			g.computeBoundingBox();
			g.computeBoundingSphere();
			return g;
		}

		// ------------------------------------------------------------------ keel, shoe, shaft log

		// Keel bottom profile (z, y): shallow at the forefoot, deepest at the prop aperture.
		static readonly double[][] KEEL_PROFILE = { new[] { 3.74, -0.093 }, new[] { 3.3, -0.2 }, new[] { 3.0, -0.265 }, new[] { 1.5, -0.5 }, new[] { 0.0, -0.62 }, new[] { -1.5, -0.7 }, new[] { -3.05, -0.77 } };

		public static double keelBottomAt( double z )
		{
			var P = KEEL_PROFILE;
			if ( z >= P[ 0 ][ 0 ] ) return P[ 0 ][ 1 ];
			if ( z <= P[ P.Length - 1 ][ 0 ] ) return P[ P.Length - 1 ][ 1 ];
			int i = 0;
			while ( i < P.Length - 2 && z < P[ i + 1 ][ 0 ] ) i ++;
			var p0 = P[ Math.Max( 0, i - 1 ) ]; var p1 = P[ i ]; var p2 = P[ i + 1 ]; var p3 = P[ Math.Min( P.Length - 1, i + 2 ) ];
			double f = ( p1[ 0 ] - z ) / ( p1[ 0 ] - p2[ 0 ] );
			// uniform Catmull-Rom on y
			double f2 = f * f, f3 = f2 * f;
			return 0.5 * ( 2 * p1[ 1 ] + ( -p0[ 1 ] + p2[ 1 ] ) * f + ( 2 * p0[ 1 ] - 5 * p1[ 1 ] + 4 * p2[ 1 ] - p3[ 1 ] ) * f2 + ( -p0[ 1 ] + 3 * p1[ 1 ] - 3 * p2[ 1 ] + p3[ 1 ] ) * f3 );
		}

		public static class KEEL { public const double zFront = 3.74, zAft = -3.05, shoeAft = -3.68, bottom = -0.77, shoeTop = -0.74; }

		static void buildKeel( GeoKit kit, HullLines L )
		{
			int N = 32;
			var profiles = new List<List<Vector3>>();
			for ( int i = 0; i <= N; i ++ )
			{
				double f = ( double ) i / N;
				double z = lerp( KEEL.zAft, KEEL.zFront, Math.Pow( f, 0.9 ) );
				double yB = keelBottomAt( z );
				double canoe = -L.draftAt( z );
				double yTop = Math.Max( canoe + 0.06, yB + 0.02 );
				double fwd = sstep( 2.6, KEEL.zFront, z );
				double wt = lerp( 0.065, 0.012, fwd );
				double wb = lerp( 0.042, 0.008, fwd );
				double mid = lerp( yTop, yB, 0.55 );
				double[][] half = {
					new[] { wt, yTop }, new[] { lerp( wt, wb, 0.6 ), mid }, new[] { wb, yB + 0.035 }, new[] { wb * 0.75, yB + 0.012 }, new[] { wb * 0.35, yB + 0.002 },
				};
				var prof = new List<Vector3>();
				foreach ( var h in half ) prof.Add( V( h[ 0 ], h[ 1 ], z ) );
				prof.Add( V( 0, yB, z ) );
				for ( int k = half.Length - 1; k >= 0; k -- ) prof.Add( V( -half[ k ][ 0 ], half[ k ][ 1 ], z ) );
				profiles.Add( prof );
			}

			var g = GK.loft( profiles );
			orientOutward( g, p => V( p.x, 0, 0 ), V( 0, -1, 0 ) );
			kit.add( "hull", g );
			kit.add( "hull", GK.fanCap( profiles[ 0 ], V( 0, 0, -1 ) ) );
			kit.add( "hull", GK.fanCap( profiles[ N ], V( 0, 0, 1 ) ) );

			// shoe under the prop aperture carrying the rudder heel
			double shoeLen = KEEL.zAft - KEEL.shoeAft;
			var shoe = GK.box( 0.075, KEEL.shoeTop - KEEL.bottom, shoeLen );
			shoe.translate( 0, ( KEEL.shoeTop + KEEL.bottom ) / 2, ( KEEL.zAft + KEEL.shoeAft ) / 2 );
			kit.add( "hull", shoe );

			// heel bearing and shaft
			kit.add( "fittings", GK.rod( V( 0, KEEL.shoeTop, -3.58 ), V( 0, KEEL.shoeTop + 0.03, -3.58 ), 0.03, 10 ), new Opts { color = 0xb0764a, rough = 0.4, metal = 1 } );
			kit.add( "fittings", GK.rod( V( 0, -0.53, KEEL.zAft + 0.02 ), V( 0, -0.53, -3.27 ), 0.024, 10 ), new Opts { color = 0xc9ccd0, rough = 0.25, metal = 1 } );
			// stern tube boss where the shaft leaves the keel
			kit.add( "hull", GK.rod( V( 0, -0.53, KEEL.zAft + 0.06 ), V( 0, -0.53, KEEL.zAft - 0.03 ), 0.05, 12, 0.04 ) );
		}

		// Volume of the keel appendage and shoe below the canoe body (m^3).
		public static double keelVolume( HullLines L )
		{
			int N = 240;
			double dz = ( KEEL.zFront - KEEL.zAft ) / N;
			double v = 0;
			for ( int i = 0; i < N; i ++ )
			{
				double z = KEEL.zAft + ( i + 0.5 ) * dz;
				double h = Math.Max( 0, -L.draftAt( z ) - keelBottomAt( z ) );
				double fwd = sstep( 2.6, KEEL.zFront, z );
				v += ( lerp( 0.065, 0.012, fwd ) + lerp( 0.042, 0.008, fwd ) ) * h * dz;
			}

			return v + 0.075 * ( KEEL.shoeTop - KEEL.bottom ) * ( KEEL.zAft - KEEL.shoeAft );
		}

		// Vertices not referenced by any triangle get a sane normal.
		static void fixUnusedNormals( BufferGeometry g, Vector3 n )
		{
			var nrm = g.attributes[ "normal" ];
			for ( int i = 0; i < nrm.count; i ++ )
				if ( JS.Hypot( nrm.getX( i ), nrm.getY( i ), nrm.getZ( i ) ) < 0.5 ) nrm.setXYZ( i, n.x, n.y, n.z );
		}

		// Flip a geometry so normals point along outward(p) on average.
		static BufferGeometry orientOutward( BufferGeometry g, Func<Vector3, Vector3> outwardFn, Vector3 fallback )
		{
			var p = g.attributes[ "position" ]; var n = g.attributes[ "normal" ];
			double dot = 0;
			var a = V( 0, 0, 0 ); var b = V( 0, 0, 0 );
			for ( int i = 0; i < p.count; i ++ )
			{
				a.fromBufferAttribute( p, i );
				b.fromBufferAttribute( n, i );
				var o = outwardFn( a );
				dot += o.lengthSq() > 1e-10 ? b.dot( o ) : b.dot( fallback );
			}

			if ( dot < 0 )
			{
				var idx = ( int[] ) g.index.array.Clone();
				for ( int i = 0; i < idx.Length; i += 3 ) { int t = idx[ i + 1 ]; idx[ i + 1 ] = idx[ i + 2 ]; idx[ i + 2 ] = t; }
				g.setIndex( idx );
				g.computeVertexNormals();
			}

			return g;
		}

		// ------------------------------------------------------------------ inner lining (bulwarks)

		static double innerX( HullLines L, double t, double y ) => L.halfBreadth( t, y ) - L.shell;

		static void buildLining( GeoKit kit, HullLines L )
		{
			double tA = L.shell / L.length;
			double tF = L.tAtSheerZ( L.houseFront );
			int NS = 40, NY = 7;
			var rows = new List<List<Vector3>>();
			for ( int i = 0; i <= NS; i ++ )
			{
				double t = lerp( tA, tF, ( double ) i / NS );
				double z = L.sheerZ( t );
				double yTop = L.sheerY( t );
				var row = new List<Vector3>();
				for ( int k = 0; k <= NY; k ++ )
				{
					double y = lerp( L.deckY, yTop, ( double ) k / NY );
					row.Add( V( innerX( L, t, y ), y, z ) );
				}

				rows.Add( row );
			}

			Func<Vector3, int, int, double, double, double[]> uvFn = ( p, i, j, u, v ) => new[] { p.z, p.y };
			// port lining faces -x (inboard)
			var port = GK.gridSurface( rows, new GridOpts { uvFn = uvFn, flip = true } );
			var star = GK.gridSurface( rows.Select( r => r.Select( p => V( -p.x, p.y, p.z ) ).ToList() ).ToList(), new GridOpts { uvFn = uvFn } );
			kit.add( "gelcoat", port, new Opts { color = Palette.lining, rough = 0.4 } );
			kit.add( "gelcoat", star, new Opts { color = Palette.lining, rough = 0.4 } );

			// inner face of the transom
			double zT = L.zAft + L.shell;
			var pos = new List<double>(); var uvs = new List<double>(); var idx = new List<int>();
			for ( int k = 0; k <= NY; k ++ )
			{
				double y = lerp( L.deckY, L.sheerY( tA ), ( double ) k / NY );
				double x = innerX( L, tA, y );
				pos.AddRange( new[] { x, y, zT, -x, y, zT } );
				uvs.AddRange( new[] { x, y, -x, y } );
			}

			for ( int k = 0; k < NY; k ++ )
			{
				int a = 2 * k, b = 2 * k + 1, c = 2 * k + 2, d = 2 * k + 3;
				idx.AddRange( new[] { a, c, b, b, c, d } );
			}

			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
			g.setIndex( idx );
			GK.orientTowards( g, V( 0, 0, 1 ) );
			g.computeVertexNormals();
			kit.add( "gelcoat", g, new Opts { color = Palette.lining, rough = 0.4 } );
		}

		// ------------------------------------------------------------------ decks

		public static double foredeckY( HullLines L, double t, double x )
		{
			double xe = Math.Max( 1e-3, L.sheerX( t ) - L.shell );
			double f = Math.Min( 1, Math.Abs( x ) / xe );
			return L.sheerY( t ) + 0.06 * ( 1 - f * f );
		}

		public static double houseHalfWidth( HullLines L, double z ) => L.sheerX( L.tAtSheerZ( z ) ) - 0.2;

		static void buildDecks( GeoKit kit, HullLines L )
		{
			double tA = L.shell / L.length;
			double tF = L.tAtSheerZ( L.houseFront );
			var grip = new Opts { color = Palette.deck, rough = 0.5, pattern = 1 };
			Func<Vector3, int, int, double, double, double[]> uvFn = ( p, i, j, u, v ) => new[] { p.x, p.z };

			// cockpit / wheelhouse sole (y = deckY), edges follow the lining
			int NS = 40, NX = 8;
			var sole = new List<List<Vector3>>();
			for ( int i = 0; i <= NS; i ++ )
			{
				double t = lerp( tA, tF, ( double ) i / NS );
				double z = L.sheerZ( t );
				double xe = innerX( L, t, L.deckY );
				var row = new List<Vector3>();
				for ( int k = 0; k <= NX; k ++ ) row.Add( V( lerp( xe, -xe, ( double ) k / NX ), L.deckY, z ) );
				sole.Add( row );
			}

			var gs = GK.gridSurface( sole, new GridOpts { uvFn = uvFn } );
			GK.orientTowards( gs, V( 0, 1, 0 ) );
			gs.computeVertexNormals();
			kit.add( "gelcoat", gs, grip );

			// cambered foredeck from the wheelhouse front to the stem head
			int NF = 30, NFX = 12;
			double tDeckEnd = 1;
			while ( L.sheerX( tDeckEnd ) - L.shell < 0.012 ) tDeckEnd -= 0.0005;
			var fore = new List<List<Vector3>>();
			for ( int i = 0; i <= NF; i ++ )
			{
				double t = lerp( tF, tDeckEnd, 1 - Math.Pow( 1 - ( double ) i / NF, 1.3 ) );
				double z = L.sheerZ( t );
				double xe = Math.Max( 0.012, L.sheerX( t ) - L.shell );
				var row = new List<Vector3>();
				for ( int k = 0; k <= NFX; k ++ )
				{
					double x = lerp( xe, -xe, ( double ) k / NFX );
					row.Add( V( x, foredeckY( L, t, x ), z ) );
				}

				fore.Add( row );
			}

			var gf = GK.gridSurface( fore, new GridOpts { uvFn = uvFn } );
			GK.orientTowards( gf, V( 0, 1, 0 ) );
			gf.computeVertexNormals();
			kit.add( "gelcoat", gf, grip );

			// side decks (washboards) alongside the wheelhouse
			foreach ( double s in new double[] { 1, -1 } )
			{
				var outline = new List<double[]>();
				int NZ = 12;
				double z0 = L.houseBack, z1 = L.houseFront;
				for ( int i = 0; i <= NZ; i ++ )
				{
					double z = lerp( z0, z1, ( double ) i / NZ );
					outline.Add( new[] { z, houseHalfWidth( L, z ) - 0.045 } );
				}

				for ( int i = NZ; i >= 0; i -- )
				{
					double z = lerp( z0, z1, ( double ) i / NZ );
					outline.Add( new[] { z, L.sheerX( L.tAtSheerZ( z ) ) - L.shell + 0.01 } );
				}

				var g = GK.slab( outline.ToArray(), new double[ 0 ][][], ( u, v, side ) =>
				{
					double t = L.tAtSheerZ( u );
					return V( s * v, L.sheerY( t ) - ( side != 0 ? 0.04 : 0 ), u );
				} );
				GK.auxVertices( g, ( p, i ) => new[] { 0.5, 0, p.y > L.sheerY( L.tAtSheerZ( p.z ) ) - 0.02 ? 1.0 : 0.0, 0.0 } );
				kit.add( "gelcoat", g, new Opts { color = Palette.deck } );
			}

			// deck hatch frame
			double hy = L.deckY + 0.008;
			double[][] frames = { new[] { 0.74, 0.03, 0, -1.25 }, new[] { 0.74, 0.03, 0, -1.95 }, new[] { 0.03, 0.73, 0.355, -1.6 }, new[] { 0.03, 0.73, -0.355, -1.6 } };
			foreach ( var f in frames )
			{
				var b = GK.box( f[ 0 ], 0.016, f[ 1 ] );
				b.translate( f[ 2 ], hy, f[ 3 ] );
				kit.add( "gelcoat", b, new Opts { color = 0xd6d1c4, rough = 0.45 } );
			}

			// flush hatch lifting ring
			kit.add( "fittings", GK.rod( V( -0.05, L.deckY + 0.004, -1.45 ), V( 0.05, L.deckY + 0.004, -1.45 ), 0.006, 6 ), new Opts { color = Palette.stainless, rough = 0.3, metal = 1 } );
		}

		// ------------------------------------------------------------------ gunwale caps (wood)

		const double CAP_H = 0.045;

		static List<Vector3> capProfile( double xi, double xo, double y0, double z, double sign )
		{
			double xm = ( xi + xo ) / 2;
			double[][] pts = {
				new[] { xi, y0 - 0.012 }, new[] { xi, y0 + CAP_H - 0.012 }, new[] { xi + 0.01, y0 + CAP_H - 0.002 }, new[] { xm, y0 + CAP_H + 0.004 },
				new[] { xo - 0.01, y0 + CAP_H - 0.002 }, new[] { xo, y0 + CAP_H - 0.012 }, new[] { xo, y0 - 0.026 }, new[] { xo - 0.018, y0 - 0.03 },
			};
			return pts.Select( p => V( sign * Math.Max( 0, p[ 0 ] ), p[ 1 ], z ) ).ToList();
		}

		static void buildGunwale( GeoKit kit, HullLines L )
		{
			double tStart = ( L.shell + 0.03 ) / L.length;
			int N = 60;
			foreach ( double s in new double[] { 1, -1 } )
			{
				var profiles = new List<List<Vector3>>();
				for ( int i = 0; i <= N; i ++ )
				{
					double t = lerp( tStart, 1, 1 - Math.Pow( 1 - ( double ) i / N, 1.25 ) );
					double xs = L.sheerX( t );
					double z = L.sheerZ( t );
					double xo = xs + 0.022;
					if ( i == N ) { xo = 0; z += 0.03; }
					profiles.Add( capProfile( Math.Max( 0, xs - L.shell - 0.03 ), xo, L.sheerY( t ), z, s ) );
				}

				var g = GK.loft( profiles );
				orientOutward( g, p => V( p.x - s * ( L.sheerX( L.tAtSheerZ( p.z ) ) - 0.03 ), p.y - L.sheerY( L.tAtSheerZ( p.z ) ) - 0.01, 0 ), V( 0, 1, 0 ) );
				woodUV( g, profiles );
				kit.add( "wood", g, new Opts { rough = 0.32 } );
				kit.add( "wood", GK.fanCap( profiles[ 0 ], V( 0, 0, -1 ) ), new Opts { rough = 0.32 } );
			}

			// transom cap
			double y0 = L.sheerY( 0 );
			double xo2 = L.sheerX( 0 ) + 0.022;
			double zi = L.zAft + L.shell + 0.03, zo = L.zAft - 0.022;
			Func<double, List<Vector3>> prof = x => capProfile( 0, zi - zo, 0, 0, 1 ).Select( p => V( x, y0 + p.y, zi - p.x ) ).ToList();
			var tprofiles = new List<List<Vector3>>();
			for ( int i = 0; i <= 8; i ++ ) tprofiles.Add( prof( lerp( -xo2, xo2, ( double ) i / 8 ) ) );
			var tg = GK.loft( tprofiles );
			orientOutward( tg, p => V( 0, p.y - y0 - 0.01, p.z - ( zi + zo ) / 2 ), V( 0, 1, 0 ) );
			woodUV( tg, tprofiles );
			kit.add( "wood", tg, new Opts { rough = 0.32 } );
			kit.add( "wood", GK.fanCap( tprofiles[ 0 ], V( -1, 0, 0 ) ), new Opts { rough = 0.32 } );
			kit.add( "wood", GK.fanCap( tprofiles[ 8 ], V( 1, 0, 0 ) ), new Opts { rough = 0.32 } );
		}

		// u along the sweep (meters), v around the profile (meters) so grain runs lengthwise.
		static void woodUV( BufferGeometry g, List<List<Vector3>> profiles )
		{
			int nj = profiles[ 0 ].Count;
			var uv = g.attributes[ "uv" ];
			double u = 0;
			for ( int i = 0; i < profiles.Count; i ++ )
			{
				if ( i > 0 ) u += profiles[ i ][ 3 ].distanceTo( profiles[ i - 1 ][ 3 ] );
				double v = 0;
				for ( int j = 0; j < nj; j ++ )
				{
					if ( j > 0 ) v += profiles[ i ][ j ].distanceTo( profiles[ i ][ j - 1 ] );
					uv.setXY( i * nj + j, u, v );
				}
			}
		}

		// ------------------------------------------------------------------ rubrail (wood + stainless strip)

		static void buildRubrail( GeoKit kit, HullLines L )
		{
			int N = 50;
			double[] ANG = new double[] { -90, -60, -30, 0, 30, 60, 90 }.Select( a => a * Math.PI / 180 ).ToArray();
			foreach ( double s in new double[] { 1, -1 } )
			{
				var centers = new List<Vector3>();
				for ( int i = 0; i <= N; i ++ )
				{
					double t = lerp( 0.0, 0.992, 1 - Math.Pow( 1 - ( double ) i / N, 1.25 ) );
					double y = L.sheerY( t ) - 0.07;
					double x = L.halfBreadth( t, y );
					centers.Add( V( x, y, L.zOnStation( t, y ) ) );
				}

				var profiles = new List<List<Vector3>>();
				var strip = new List<Vector3>();
				for ( int i = 0; i <= N; i ++ )
				{
					var c = centers[ i ];
					var a = centers[ Math.Max( 0, i - 1 ) ]; var b = centers[ Math.Min( N, i + 1 ) ];
					// horizontal outward normal of the rail line
					double dz = b.z - a.z, dx = b.x - a.x;
					double hl = JS.Hypot( dx, dz ); double len = hl != 0 ? hl : 1;
					double nx = dz / len, nz = -dx / len;
					var prof = ANG.Select( ang =>
					{
						double o = 0.036 * Math.Cos( ang ) - 0.006;
						return V( s * ( c.x + nx * o ), c.y + 0.03 * Math.Sin( ang ), c.z + nz * o );
					} ).ToList();
					profiles.Add( prof );
					strip.Add( V( s * ( c.x + nx * 0.034 ), c.y, c.z + nz * 0.034 ) );
				}

				var g = GK.loft( profiles );
				orientOutward( g, p => V( s, 0, 0 ), V( s, 0, 0 ) );
				woodUV( g, profiles );
				kit.add( "wood", g, new Opts { rough = 0.35 } );
				kit.add( "wood", GK.fanCap( profiles[ 0 ], V( 0, 0, -1 ) ), new Opts { rough = 0.35 } );
				kit.add( "wood", GK.fanCap( profiles[ N ], V( 0, 0, 1 ) ), new Opts { rough = 0.35 } );
				kit.add( "fittings", GK.tube( strip, 0.0065, 64, 4 ), new Opts { color = Palette.stainless, rough = 0.22, metal = 1 } );
			}

			// across the transom
			double yy = L.sheerY( 0 ) - 0.07;
			double xo = L.sheerX( 0 ) + 0.015;
			var tprof = new List<List<Vector3>>();
			for ( int i = 0; i <= 6; i ++ )
			{
				double x = lerp( -xo, xo, ( double ) i / 6 );
				tprof.Add( ANG.Select( ang => V( x, yy + 0.03 * Math.Sin( ang ), L.zAft - ( 0.036 * Math.Cos( ang ) - 0.006 ) ) ).ToList() );
			}

			var tg = GK.loft( tprof );
			orientOutward( tg, p => V( 0, 0, p.z - L.zAft + 0.001 ), V( 0, 0, -1 ) );
			woodUV( tg, tprof );
			kit.add( "wood", tg, new Opts { rough = 0.35 } );
			kit.add( "wood", GK.fanCap( tprof[ 0 ], V( -1, 0, 0 ) ), new Opts { rough = 0.35 } );
			kit.add( "wood", GK.fanCap( tprof[ 6 ], V( 1, 0, 0 ) ), new Opts { rough = 0.35 } );
		}
	}
}
