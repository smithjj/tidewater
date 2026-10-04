using System;
using System.Collections.Generic;
using System.Linq;

// Port of src/engine/math/ShapeUtils.js: signed area, winding test and triangulation of a contour with holes (hole bridging + ear
// clipping). Index triples refer to contour.concat( ...holes ).
namespace Tidewater.Engine
{
	public static class ShapeUtils
	{
		public static double area( IList<Vector2> pts )
		{
			int n = pts.Count; double a = 0;
			for ( int p = n - 1, q = 0; q < n; p = q ++ ) a += pts[ p ].x * pts[ q ].y - pts[ q ].x * pts[ p ].y;
			return a * 0.5;
		}

		public static bool isClockWise( IList<Vector2> pts ) => area( pts ) < 0;

		static void RemoveDupEndPts( List<Vector2> pts )
		{
			int l = pts.Count;
			if ( l > 2 && pts[ l - 1 ].x == pts[ 0 ].x && pts[ l - 1 ].y == pts[ 0 ].y ) pts.RemoveAt( l - 1 );
		}

		static double Cross( Vector2 a, Vector2 b, Vector2 c ) => ( b.x - a.x ) * ( c.y - b.y ) - ( b.y - a.y ) * ( c.x - b.x );
		static bool Same( Vector2 a, Vector2 b ) => a.x == b.x && a.y == b.y;

		static bool InTri( Vector2 a, Vector2 b, Vector2 c, Vector2 p )
		{
			return ( c.x - p.x ) * ( a.y - p.y ) >= ( a.x - p.x ) * ( c.y - p.y ) &&
				( a.x - p.x ) * ( b.y - p.y ) >= ( b.x - p.x ) * ( a.y - p.y ) &&
				( b.x - p.x ) * ( c.y - p.y ) >= ( c.x - p.x ) * ( b.y - p.y );
		}

		static double PolyArea( List<int> idx, List<Vector2> P )
		{
			double a = 0;
			for ( int i = 0, j = idx.Count - 1; i < idx.Count; j = i ++ ) a += P[ idx[ j ] ].x * P[ idx[ i ] ].y - P[ idx[ i ] ].x * P[ idx[ j ] ].y;
			return a;
		}

		static bool InTriCCW( Vector2[] t, Vector2 p )
		{
			Vector2 a, b, c;
			if ( Cross( t[ 0 ], t[ 1 ], t[ 2 ] ) >= 0 ) { a = t[ 0 ]; b = t[ 1 ]; c = t[ 2 ]; } else { a = t[ 0 ]; b = t[ 2 ]; c = t[ 1 ]; }
			return InTri( a, b, c, p );
		}

		// Splice `hole` (CW) into `poly` (CCW) via a bridge from the hole's rightmost vertex to a visible outer vertex (ray cast toward +x).
		static List<int> Bridge( List<int> poly, List<int> hole, List<Vector2> P )
		{
			int hm = 0;
			for ( int i = 1; i < hole.Count; i ++ ) if ( P[ hole[ i ] ].x > P[ hole[ hm ] ].x ) hm = i;
			var M = P[ hole[ hm ] ];
			double best = double.PositiveInfinity; int bi = -1;
			int n = poly.Count;
			for ( int i = 0; i < n; i ++ )
			{
				var a = P[ poly[ i ] ]; var b = P[ poly[ ( i + 1 ) % n ] ];
				if ( ( a.y <= M.y && M.y <= b.y ) || ( b.y <= M.y && M.y <= a.y ) )
				{
					if ( a.y == b.y ) continue;
					double x = a.x + ( M.y - a.y ) * ( b.x - a.x ) / ( b.y - a.y );
					if ( x >= M.x && x < best )
					{
						best = x;
						bi = a.x > b.x ? i : ( i + 1 ) % n;
					}
				}
			}

			if ( bi < 0 ) return poly;
			// A reflex vertex inside triangle (M, I, P) may block the view; take the one with the smallest angle to the ray instead.
			var I = new Vector2( best, M.y );
			var Pv = P[ poly[ bi ] ];
			if ( best != Pv.x || M.y != Pv.y )
			{
				double tanMin = double.PositiveInfinity;
				var tri = Pv.y < M.y ? new[] { M, Pv, I } : new[] { M, I, Pv };
				int start = bi;
				for ( int k = 0; k < n; k ++ )
				{
					int i = ( start + k ) % n; var v = P[ poly[ i ] ];
					if ( v.x < M.x || i == start ) continue;
					var pv = P[ poly[ ( i + n - 1 ) % n ] ]; var nv = P[ poly[ ( i + 1 ) % n ] ];
					bool reflex = Cross( pv, v, nv ) < 0;
					if ( ! reflex && ! ( v.x == M.x && v.y == M.y ) ) continue;
					if ( ! InTriCCW( tri, v ) ) continue;
					double dx = v.x - M.x;
					double tan = Math.Abs( M.y - v.y ) / ( dx != 0 ? dx : 1e-12 );
					if ( tan < tanMin || ( tan == tanMin && v.x < Pv.x ) ) { tanMin = tan; bi = i; Pv = v; }
				}
			}

			// A vertex may occur several times (earlier bridges); connect through the occurrence whose interior wedge contains M.
			for ( int k = 0; k < n; k ++ )
			{
				if ( ! Same( P[ poly[ k ] ], Pv ) ) continue;
				var pv = P[ poly[ ( k + n - 1 ) % n ] ]; var nv = P[ poly[ ( k + 1 ) % n ] ];
				bool l1 = Cross( pv, Pv, M ) >= 0, l2 = Cross( Pv, nv, M ) >= 0;
				if ( Cross( pv, Pv, nv ) >= 0 ? ( l1 && l2 ) : ( l1 || l2 ) ) { bi = k; break; }
			}

			var rot = new List<int>( hole.Skip( hm ) ); rot.AddRange( hole.Take( hm ) );
			var res = new List<int>( poly.Take( bi + 1 ) );
			res.AddRange( rot ); res.Add( hole[ hm ] ); res.Add( poly[ bi ] ); res.AddRange( poly.Skip( bi + 1 ) );
			return res;
		}

		static List<int[]> EarClip( List<int> poly, List<Vector2> P, List<int[]> o )
		{
			var idx = new List<int>( poly );
			int guard = 0;
			while ( idx.Count > 3 && guard ++ < 100000 )
			{
				int n = idx.Count; bool clipped = false;
				for ( int pass = 0; pass < 2 && ! clipped; pass ++ )
				{
					for ( int i = 0; i < n; i ++ )
					{
						int ia = idx[ ( i + n - 1 ) % n ], ib = idx[ i ], ic = idx[ ( i + 1 ) % n ];
						var a = P[ ia ]; var b = P[ ib ]; var c = P[ ic ];
						double cr = Cross( a, b, c );
						if ( pass == 0 ? cr <= 0 : cr < 0 ) continue;
						bool ear = true;
						if ( cr > 0 )
						{
							for ( int k = 0; k < n; k ++ )
							{
								var v = P[ idx[ k ] ];
								if ( Same( v, a ) || Same( v, b ) || Same( v, c ) ) continue;
								if ( InTri( a, b, c, v ) ) { ear = false; break; }
							}
						}

						if ( ear )
						{
							o.Add( new[] { ia, ib, ic } );
							idx.RemoveAt( i );
							clipped = true;
							break;
						}
					}
				}

				if ( ! clipped )
				{
					// self-intersecting or degenerate input: cut the least reflex vertex
					int bi = 0; double bc = double.NegativeInfinity;
					for ( int i = 0; i < n; i ++ )
					{
						double c = Cross( P[ idx[ ( i + n - 1 ) % n ] ], P[ idx[ i ] ], P[ idx[ ( i + 1 ) % n ] ] );
						if ( c > bc ) { bc = c; bi = i; }
					}

					o.Add( new[] { idx[ ( bi + n - 1 ) % n ], idx[ bi ], idx[ ( bi + 1 ) % n ] } );
					idx.RemoveAt( bi );
				}
			}

			if ( idx.Count == 3 ) o.Add( new[] { idx[ 0 ], idx[ 1 ], idx[ 2 ] } );
			return o;
		}

		// Returns index triples into contour.concat( ...holes ). Like three.js, a duplicated closing point is removed from each input
		// list in place.
		public static List<int[]> triangulateShape( List<Vector2> contour, List<List<Vector2>> holes )
		{
			RemoveDupEndPts( contour );
			foreach ( var h in holes ) RemoveDupEndPts( h );
			var P = new List<Vector2>( contour );
			foreach ( var h in holes ) P.AddRange( h );
			var poly = new List<int>(); for ( int i = 0; i < contour.Count; i ++ ) poly.Add( i );
			bool outerCW = PolyArea( poly, P ) < 0;
			if ( outerCW ) poly.Reverse();
			int off = contour.Count;
			var hs = new List<List<int>>();
			foreach ( var h in holes )
			{
				var idx = new List<int>(); for ( int i = 0; i < h.Count; i ++ ) idx.Add( off + i );
				off += h.Count;
				if ( PolyArea( idx, P ) > 0 ) idx.Reverse();
				if ( idx.Count >= 3 ) hs.Add( idx );
			}

			// stable sort by descending rightmost x (JS Array.sort is stable)
			hs = hs.Select( ( h, i ) => ( h, i, m: h.Max( k => P[ k ].x ) ) ).OrderByDescending( t => t.m ).ThenBy( t => t.i ).Select( t => t.h ).ToList();
			foreach ( var h in hs ) poly = Bridge( poly, h, P );
			var tris = EarClip( poly, P, new List<int[]>() );
			if ( outerCW ) foreach ( var t in tris ) { int s = t[ 0 ]; t[ 0 ] = t[ 2 ]; t[ 2 ] = s; }
			return tris;
		}
	}
}
