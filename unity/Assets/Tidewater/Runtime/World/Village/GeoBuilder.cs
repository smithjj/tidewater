using System;
using System.Collections.Generic;
using System.Globalization;
using Tidewater.Engine;

// Port of src/world/village/GeoBuilder.js: the procedural geometry toolkit for the village.
//
// Every primitive is generated as a small local-space "Part" (positions, normals, meter-scaled uvs, indices). Parts are transformed
// and appended to a per-material Batch, which becomes one merged mesh (one draw per material).
//
// Vertex attributes of every batch:
//   position, normal, uv (meters: u runs along the grain / main axis)
//   tint  (vec3) : base color (paint color, wood tone, rope color ...)
//   vdata (vec4) : material specific parameters (seed, wear, pattern, ...)
//
// Numerics as in JS: Part arrays are Float32Array (float[] here), a Batch accumulates JS arrays of doubles, and only the built
// attribute is rounded to float. The Part cache keys use toFixed like the JS, so two sizes within its rounding share one Part.
namespace Tidewater.World.Village
{
	// A per-vertex value that is either a fixed array or a function of the vertex ( x, y, z, i ) in part space
	public delegate double[] AttrFn( double x, double y, double z, int i );

	public sealed class Attr
	{
		public readonly double[] a;
		public readonly AttrFn f;
		public Attr( double[] a ) { this.a = a; }
		public Attr( AttrFn f ) { this.f = f; }
		public static implicit operator Attr( double[] a ) => a == null ? null : new Attr( a );
		public static implicit operator Attr( Color c ) => c == null ? null : new Attr( new[] { c.r, c.g, c.b } );
		public static Attr Of( AttrFn f ) => new Attr( f );
	}

	// the options object of the Builder emitters ( { grain, skip, rx, ry, rz, sx, sy, sz, tint, data, segs, ... } )
	public sealed class O
	{
		public int? grain, skip, segs, radial, tubular;
		public double? rx, ry, rz, sx, sy, sz, arc, rRef, roll, extend;
		public bool? capTop, capBot, swapUV;
		public Attr tint, data;
		public Vector3 up, uDir;
	}

	public sealed class Part
	{
		public readonly float[] p, n, uv;
		public readonly int[] idx;

		public Part( float[] p, float[] n, float[] uv, int[] idx ) { this.p = p; this.n = n; this.uv = uv; this.idx = idx; }
		public Part( List<double> p, List<double> n, List<double> uv, List<int> idx ) { this.p = F32( p ); this.n = F32( n ); this.uv = F32( uv ); this.idx = idx.ToArray(); }
		public int vertexCount => p.Length / 3;

		static float[] F32( List<double> l ) { var a = new float[ l.Count ]; for ( int i = 0; i < a.Length; i ++ ) a[ i ] = ( float ) l[ i ]; return a; }
	}

	public struct GridVertex
	{
		public double px, py, pz, nx, ny, nz, u, v;
		public GridVertex( double[] p, double[] n, double[] uv ) { px = p[ 0 ]; py = p[ 1 ]; pz = p[ 2 ]; nx = n[ 0 ]; ny = n[ 1 ]; nz = n[ 2 ]; u = uv[ 0 ]; v = uv[ 1 ]; }
		public GridVertex( double px, double py, double pz, double nx, double ny, double nz, double u, double v ) { this.px = px; this.py = py; this.pz = pz; this.nx = nx; this.ny = ny; this.nz = nz; this.u = u; this.v = v; }
	}

	public static class GeoBuilder
	{
		static readonly Matrix4 _m4 = new Matrix4();
		static readonly Matrix3 _m3 = new Matrix3();
		static readonly Quaternion _q = new Quaternion();
		static readonly Euler _e = new Euler( 0, 0, 0, "YXZ" );
		static readonly Vector3 _p = new Vector3();
		static readonly Vector3 _s = new Vector3( 1, 1, 1 );
		static readonly Vector3 _a = new Vector3(), _b = new Vector3(), _c = new Vector3(), _d = new Vector3();
		static readonly Vector3 _X = new Vector3( 1, 0, 0 );
		static readonly Vector3 _Y = new Vector3( 0, 1, 0 );

		// UV conventions shared with the village materials:
		//   box faces perpendicular to the grain (end grain) get u += END_GRAIN_U
		//   cylinder caps get u += CAP_U (centered cap coordinates)
		public const double END_GRAIN_U = 1000;
		public const double CAP_U = 2000;

		// Texture period (m) around the circumference per material, so round parts wrap seamlessly.
		public static double WrapPeriod( string key ) => key == "wood" || key == "hard" || key == "thatch" ? 1.0 : key == "stone" ? 2.0 : key == "roofMetal" ? 0.84 : key == "roofMetalSwap" ? 1.68 : 0;

		// scale factor for the around-coordinate so a circumference C maps onto whole texture periods
		static double wrapScale( double C, double P ) => P > 0 ? Math.Max( 1, JS.Round( C / P ) ) * P / C : 1;

		// ---------------------------------------------------------------------------
		// Primitive generators (local space)

		static readonly Dictionary<string, Part> partCache = new Dictionary<string, Part>();

		static Part cached( string key, Func<Part> fn )
		{
			if ( ! partCache.TryGetValue( key, out var p ) ) { p = fn(); partCache[ key ] = p; }
			return p;
		}

		// Number.prototype.toFixed
		public static string toFixed( double x, int digits )
		{
			if ( x == 0 ) x = 0; // -0 -> 0
			return x.ToString( "F" + digits, CultureInfo.InvariantCulture );
		}

		static double[] V( int n ) => new double[ n ];

		// Axis aligned box centered at the origin.
		// grain: 0 = x, 1 = y, 2 = z, -1 = longest axis. u runs along the grain axis on faces containing it.
		// skip: bitmask of faces to omit (1:+x 2:-x 4:+y 8:-y 16:+z 32:-z)
		public static Part boxPart( double sx, double sy, double sz, int grain = -1, int skip = 0 )
		{
			string key = $"b{ toFixed( sx, 4 ) },{ toFixed( sy, 4 ) },{ toFixed( sz, 4 ) },{ grain },{ skip }";
			return cached( key, () =>
			{
				var s = new[] { sx, sy, sz };
				int g = grain;
				if ( g < 0 ) g = sx >= sy && sx >= sz ? 0 : ( sy >= sz ? 1 : 2 );
				var p = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
				var corners = new[] { new[] { -1, -1 }, new[] { 1, -1 }, new[] { 1, 1 }, new[] { -1, 1 } };

				for ( int k = 0; k < 3; k ++ )
				{
					for ( int si = 0; si < 2; si ++ )
					{
						int sign = si == 0 ? 1 : -1;
						if ( ( skip & ( 1 << ( k * 2 + si ) ) ) != 0 ) continue;
						int a = ( k + 1 ) % 3, b = ( k + 2 ) % 3;
						int ua = a, va = b;
						if ( g == b ) { ua = b; va = a; }

						int bas = p.Count / 3;
						// faces perpendicular to the grain are end grain: flagged by u + 1000
						double endOff = k == g ? END_GRAIN_U : 0;
						foreach ( var cc in corners )
						{
							var v = V( 3 );
							v[ k ] = sign * s[ k ] / 2;
							v[ a ] = cc[ 0 ] * s[ a ] / 2;
							v[ b ] = cc[ 1 ] * s[ b ] / 2;
							p.Add( v[ 0 ] ); p.Add( v[ 1 ] ); p.Add( v[ 2 ] );
							var nn = V( 3 );
							nn[ k ] = sign;
							n.Add( nn[ 0 ] ); n.Add( nn[ 1 ] ); n.Add( nn[ 2 ] );
							uv.Add( v[ ua ] + s[ ua ] / 2 + endOff ); uv.Add( v[ va ] + s[ va ] / 2 );
						}

						if ( sign > 0 ) idx.AddRange( new[] { bas, bas + 1, bas + 2, bas, bas + 2, bas + 3 } );
						else idx.AddRange( new[] { bas, bas + 2, bas + 1, bas, bas + 3, bas + 2 } );
					}
				}

				return new Part( p, n, uv, idx );
			} );
		}

		// Generic parametric grid surface. fn( i, j ) -> { p, n, uv }.
		// Triangle winding is chosen per quad so faces agree with the supplied normals.
		public static Part gridPart( int cols, int rows, Func<int, int, GridVertex> fn )
		{
			var p = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
			for ( int j = 0; j <= rows; j ++ )
			{
				for ( int i = 0; i <= cols; i ++ )
				{
					var v = fn( i, j );
					p.Add( v.px ); p.Add( v.py ); p.Add( v.pz );
					n.Add( v.nx ); n.Add( v.ny ); n.Add( v.nz );
					uv.Add( v.u ); uv.Add( v.v );
				}
			}

			int W = cols + 1;
			for ( int j = 0; j < rows; j ++ )
			{
				for ( int i = 0; i < cols; i ++ )
				{
					int a = j * W + i, b = a + 1, c = a + W, d = c + 1;
					// geometric normal of triangle (a, b, d)
					_a.set( p[ a * 3 ], p[ a * 3 + 1 ], p[ a * 3 + 2 ] ); _b.set( p[ b * 3 ], p[ b * 3 + 1 ], p[ b * 3 + 2 ] );
					_c.set( p[ d * 3 ], p[ d * 3 + 1 ], p[ d * 3 + 2 ] ); _d.set( p[ c * 3 ], p[ c * 3 + 1 ], p[ c * 3 + 2 ] );
					var e1 = _b.clone().sub( _a ); var e2 = _c.clone().sub( _a ); var e3 = _d.clone().sub( _a );
					var fn1 = e1.clone().cross( e2 ).add( e2.clone().cross( e3 ) );
					double ns = n[ a * 3 ] + n[ b * 3 ] + n[ c * 3 ] + n[ d * 3 ];
					double nsy = n[ a * 3 + 1 ] + n[ b * 3 + 1 ] + n[ c * 3 + 1 ] + n[ d * 3 + 1 ];
					double nsz = n[ a * 3 + 2 ] + n[ b * 3 + 2 ] + n[ c * 3 + 2 ] + n[ d * 3 + 2 ];
					double dot = fn1.x * ns + fn1.y * nsy + fn1.z * nsz;
					if ( dot >= 0 ) idx.AddRange( new[] { a, b, d, a, d, c } );
					else idx.AddRange( new[] { a, d, b, a, c, d } );
				}
			}

			return new Part( p, n, uv, idx );
		}

		// Cylinder / cone along +y from y = 0 to y = h.
		// uv: u along the axis (m), v around the circumference (m). swapUV exchanges them.
		public static Part cylPart( double rTop, double rBot, double h, int segs = 8, bool capTop = true, bool capBot = false, bool swapUV = false, double period = 0 )
		{
			string key = $"c{ toFixed( rTop, 4 ) },{ toFixed( rBot, 4 ) },{ toFixed( h, 4 ) },{ segs },{ capTop },{ capBot },{ swapUV },{ period.ToString( CultureInfo.InvariantCulture ) }";
			return cached( key, () =>
			{
				var p = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
				double slope = ( rBot - rTop ) / h;
				double nl = JS.Hypot( 1, slope );
				double rAvg = ( rTop + rBot ) / 2;
				double ws = wrapScale( Math.PI * 2 * rAvg, period );
				for ( int i = 0; i <= segs; i ++ )
				{
					double t = ( double ) i / segs * Math.PI * 2;
					double c = Math.Cos( t ), s = Math.Sin( t );
					for ( int j = 0; j < 2; j ++ )
					{
						double r = j != 0 ? rTop : rBot, y = j != 0 ? h : 0;
						p.Add( c * r ); p.Add( y ); p.Add( s * r );
						n.Add( c / nl ); n.Add( slope / nl ); n.Add( s / nl );
						double U = y, Vv = t * rAvg * ws;
						if ( swapUV ) { uv.Add( Vv ); uv.Add( U ); }
						else { uv.Add( U ); uv.Add( Vv ); }
					}
				}

				for ( int i = 0; i < segs; i ++ )
				{
					int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
					idx.AddRange( new[] { a, b, c, b, d, c } );
				}

				void cap( double y, double r, bool up )
				{
					if ( r <= 0 ) return;
					int bas = p.Count / 3;
					p.Add( 0 ); p.Add( y ); p.Add( 0 ); n.Add( 0 ); n.Add( up ? 1 : -1 ); n.Add( 0 ); uv.Add( CAP_U ); uv.Add( 0 );
					for ( int i = 0; i <= segs; i ++ )
					{
						double t = ( double ) i / segs * Math.PI * 2;
						p.Add( Math.Cos( t ) * r ); p.Add( y ); p.Add( Math.Sin( t ) * r );
						n.Add( 0 ); n.Add( up ? 1 : -1 ); n.Add( 0 );
						uv.Add( Math.Cos( t ) * r + CAP_U ); uv.Add( Math.Sin( t ) * r );
					}

					for ( int i = 0; i < segs; i ++ )
					{
						if ( up ) idx.AddRange( new[] { bas, bas + 2 + i, bas + 1 + i } );
						else idx.AddRange( new[] { bas, bas + 1 + i, bas + 2 + i } );
					}
				}

				if ( capTop ) cap( h, rTop, true );
				if ( capBot ) cap( 0, rBot, false );
				return new Part( p, n, uv, idx );
			} );
		}

		// Surface of revolution around +y. profile: [[r, y], ...] bottom to top.
		// A repeated point creates a crease. uv: u along the profile (m), v around (m, at rRef).
		public static Part lathePart( double[][] profile, int segs = 12, double? rRef = null, double period = 0 )
		{
			var sb = new System.Text.StringBuilder( "l" );
			for ( int i = 0; i < profile.Length; i ++ ) { if ( i > 0 ) sb.Append( ',' ); sb.Append( toFixed( profile[ i ][ 0 ], 3 ) ).Append( ':' ).Append( toFixed( profile[ i ][ 1 ], 3 ) ); }
			sb.Append( '|' ).Append( segs ).Append( '|' ).Append( rRef.HasValue ? rRef.Value.ToString( "R", CultureInfo.InvariantCulture ) : "null" ).Append( '|' ).Append( period.ToString( CultureInfo.InvariantCulture ) );
			return cached( sb.ToString(), () =>
			{
				int m = profile.Length;
				double mx = double.NegativeInfinity;
				foreach ( var q in profile ) mx = Math.Max( mx, q[ 0 ] );
				double rr0 = rRef ?? mx;
				double rr = rr0 * wrapScale( Math.PI * 2 * rr0, period );
				// profile normals
				var pn = new List<double[]>();
				double[] segN( int i )
				{
					double dr = profile[ i + 1 ][ 0 ] - profile[ i ][ 0 ], dy = profile[ i + 1 ][ 1 ] - profile[ i ][ 1 ];
					double l = JS.Or( JS.Hypot( dr, dy ), 1 );
					return new[] { dy / l, -dr / l };
				}

				bool same( int a, int b ) => a >= 0 && b < m && profile[ a ][ 0 ] == profile[ b ][ 0 ] && profile[ a ][ 1 ] == profile[ b ][ 1 ];
				for ( int i = 0; i < m; i ++ )
				{
					double nr = 0, ny = 0;
					if ( same( i, i + 1 ) )
					{
						// crease start: previous segment only
						if ( i > 0 ) { var s = segN( i - 1 ); nr = s[ 0 ]; ny = s[ 1 ]; }
					}
					else if ( same( i - 1, i ) )
					{
						// crease end: next segment only
						if ( i < m - 1 ) { var s = segN( i ); nr = s[ 0 ]; ny = s[ 1 ]; }
					}
					else
					{
						if ( i < m - 1 ) { var s = segN( i ); nr += s[ 0 ]; ny += s[ 1 ]; }
						if ( i > 0 ) { var s = segN( i - 1 ); nr += s[ 0 ]; ny += s[ 1 ]; }
					}

					double l = JS.Or( JS.Hypot( nr, ny ), 1 );
					pn.Add( new[] { nr / l, ny / l } );
				}

				var len = new List<double> { 0 };
				for ( int i = 1; i < m; i ++ ) len.Add( len[ i - 1 ] + JS.Hypot( profile[ i ][ 0 ] - profile[ i - 1 ][ 0 ], profile[ i ][ 1 ] - profile[ i - 1 ][ 1 ] ) );

				return gridPart( m - 1, segs, ( i, j ) =>
				{
					double t = ( double ) j / segs * Math.PI * 2;
					double c = Math.Cos( t ), s = Math.Sin( t );
					double r = profile[ i ][ 0 ], y = profile[ i ][ 1 ];
					double nr = pn[ i ][ 0 ], ny = pn[ i ][ 1 ];
					return new GridVertex( c * r, y, s * r, c * nr, ny, s * nr, len[ i ], t * rr );
				} );
			} );
		}

		// Torus lying in the XZ plane (axis +y). arc < 2PI gives an open ring.
		public static Part torusPart( double R, double r, int radial = 6, int tubular = 16, double arc = Math.PI * 2 )
		{
			string key = $"t{ toFixed( R, 4 ) },{ toFixed( r, 4 ) },{ radial },{ tubular },{ toFixed( arc, 3 ) }";
			return cached( key, () => gridPart( tubular, radial, ( i, j ) =>
			{
				double phi = ( double ) i / tubular * arc, psi = ( double ) j / radial * Math.PI * 2;
				double cp = Math.Cos( phi ), sp = Math.Sin( phi ), cs = Math.Cos( psi ), ss = Math.Sin( psi );
				return new GridVertex( ( R + r * cs ) * cp, r * ss, ( R + r * cs ) * sp, cs * cp, ss, cs * sp, phi * R, psi * r );
			} ) );
		}

		// Tube along a polyline (array of Vector3). uv: u along the length, v around.
		public static Part tubePart( IList<Vector3> points, double radius, int radial = 5 )
		{
			int m = points.Count;
			var T = new List<Vector3>(); var N = new List<Vector3>(); var Bn = new List<Vector3>();
			for ( int i = 0; i < m; i ++ )
			{
				var a = points[ Math.Max( 0, i - 1 ) ]; var b = points[ Math.Min( m - 1, i + 1 ) ];
				T.Add( b.clone().sub( a ).normalize() );
			}

			// initial normal
			var n0 = Math.Abs( T[ 0 ].y ) < 0.9 ? new Vector3( 0, 1, 0 ) : new Vector3( 1, 0, 0 );
			n0 = n0.sub( T[ 0 ].clone().multiplyScalar( n0.dot( T[ 0 ] ) ) ).normalize();
			N.Add( n0 );
			Bn.Add( T[ 0 ].clone().cross( n0 ) );
			for ( int i = 1; i < m; i ++ )
			{
				var prev = N[ i - 1 ];
				var nn = prev.clone().sub( T[ i ].clone().multiplyScalar( prev.dot( T[ i ] ) ) );
				if ( nn.lengthSq() < 1e-8 ) nn.copy( prev );
				nn.normalize();
				N.Add( nn );
				Bn.Add( T[ i ].clone().cross( nn ) );
			}

			var len = new List<double> { 0 };
			for ( int i = 1; i < m; i ++ ) len.Add( len[ i - 1 ] + points[ i ].distanceTo( points[ i - 1 ] ) );

			return gridPart( radial, m - 1, ( i, j ) =>
			{
				double t = ( double ) i / radial * Math.PI * 2;
				double c = Math.Cos( t ), s = Math.Sin( t );
				double nx = N[ j ].x * c + Bn[ j ].x * s, ny = N[ j ].y * c + Bn[ j ].y * s, nz = N[ j ].z * c + Bn[ j ].z * s;
				var P = points[ j ];
				return new GridVertex( P.x + nx * radius, P.y + ny * radius, P.z + nz * radius, nx, ny, nz, len[ j ], t * radius );
			} );
		}

		// Planar subdivided rectangle in the XY plane facing +z, centered horizontally, from y = 0 down to y = -h
		// when hang = true (useful for cloth / nets), otherwise centered. uv in meters.
		public static Part planePart( double w, double h, int sw = 1, int sh = 1, bool hang = false )
		{
			string key = $"p{ toFixed( w, 3 ) },{ toFixed( h, 3 ) },{ sw },{ sh },{ hang }";
			return cached( key, () => gridPart( sw, sh, ( i, j ) =>
			{
				double x = ( ( double ) i / sw - 0.5 ) * w;
				double y = hang ? -( double ) j / sh * h : ( ( double ) j / sh - 0.5 ) * h;
				return new GridVertex( x, y, 0, 0, 0, 1, x + w / 2, hang ? h + y : y + h / 2 );
			} ) );
		}

		// Single quad in the XY plane facing +z, centered, with normalized 0..1 uvs (window panes).
		public static Part quad01Part( double w, double h )
		{
			string key = $"q{ toFixed( w, 3 ) },{ toFixed( h, 3 ) }";
			return cached( key, () => new Part(
				new[] { ( float ) ( -w / 2 ), ( float ) ( -h / 2 ), 0, ( float ) ( w / 2 ), ( float ) ( -h / 2 ), 0, ( float ) ( w / 2 ), ( float ) ( h / 2 ), 0, ( float ) ( -w / 2 ), ( float ) ( h / 2 ), 0 },
				new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
				new float[] { 0, 0, 1, 0, 1, 1, 0, 1 },
				new[] { 0, 1, 2, 0, 2, 3 } ) );
		}

		// Convex planar polygon (array of Vector3, counter-clockwise seen from the outside / top)
		// extruded by `thickness` against its normal. uDir: in-plane direction for u.
		public static Part slabPart( Vector3[] pts, double thickness, Vector3 uDir = null, Vector3 upHint = null )
		{
			int m = pts.Length;
			var nrm = new Vector3();
			for ( int i = 0; i < m; i ++ )
			{
				var a = pts[ i ]; var b = pts[ ( i + 1 ) % m ];
				nrm.x += ( a.y - b.y ) * ( a.z + b.z );
				nrm.y += ( a.z - b.z ) * ( a.x + b.x );
				nrm.z += ( a.x - b.x ) * ( a.y + b.y );
			}

			nrm.normalize();
			if ( upHint != null && nrm.dot( upHint ) < 0 )
			{
				pts = ( Vector3[] ) pts.Clone();
				Array.Reverse( pts );
				nrm.negate();
			}

			var u = uDir != null ? uDir.clone() : pts[ 1 ].clone().sub( pts[ 0 ] );
			u.sub( nrm.clone().multiplyScalar( u.dot( nrm ) ) ).normalize();
			var v = nrm.clone().cross( u );
			var off = nrm.clone().multiplyScalar( -thickness );
			var p = new List<double>(); var n = new List<double>(); var uv = new List<double>(); var idx = new List<int>();
			// uv origin at the minimum projection so u = distance from the lowest edge along uDir
			double uMin = double.PositiveInfinity, vMin = double.PositiveInfinity;
			foreach ( var q in pts ) { uMin = Math.Min( uMin, q.dot( u ) ); vMin = Math.Min( vMin, q.dot( v ) ); }

			var o = u.clone().multiplyScalar( uMin ).add( v.clone().multiplyScalar( vMin ) );

			// top
			int bas = 0;
			foreach ( var q in pts )
			{
				p.Add( q.x ); p.Add( q.y ); p.Add( q.z ); n.Add( nrm.x ); n.Add( nrm.y ); n.Add( nrm.z );
				_p.copy( q ).sub( o );
				uv.Add( _p.dot( u ) ); uv.Add( _p.dot( v ) );
			}

			for ( int i = 1; i < m - 1; i ++ ) { idx.Add( bas ); idx.Add( bas + i ); idx.Add( bas + i + 1 ); }

			// bottom (omitted for zero-thickness sheets, which are rendered double sided)
			if ( thickness > 0 )
			{
				bas = p.Count / 3;
				foreach ( var q in pts )
				{
					p.Add( q.x + off.x ); p.Add( q.y + off.y ); p.Add( q.z + off.z ); n.Add( -nrm.x ); n.Add( -nrm.y ); n.Add( -nrm.z );
					_p.copy( q ).sub( o );
					uv.Add( _p.dot( u ) ); uv.Add( _p.dot( v ) );
				}

				for ( int i = 1; i < m - 1; i ++ ) { idx.Add( bas ); idx.Add( bas + i + 1 ); idx.Add( bas + i ); }
			}

			// sides
			if ( thickness > 0 )
			{
				for ( int i = 0; i < m; i ++ )
				{
					var a = pts[ i ]; var b = pts[ ( i + 1 ) % m ];
					var e = b.clone().sub( a );
					double L = e.length();
					var sn = e.clone().cross( nrm ).normalize();
					bas = p.Count / 3;
					p.AddRange( new[] { a.x, a.y, a.z, b.x, b.y, b.z, b.x + off.x, b.y + off.y, b.z + off.z, a.x + off.x, a.y + off.y, a.z + off.z } );
					for ( int k = 0; k < 4; k ++ ) { n.Add( sn.x ); n.Add( sn.y ); n.Add( sn.z ); }
					uv.AddRange( new[] { 0, 0, 0, L, thickness, L, thickness, 0.0 } );
					idx.AddRange( new[] { bas, bas + 2, bas + 1, bas, bas + 3, bas + 2 } );
				}
			}

			var part = new Part( p, n, uv, idx );
			// fix winding of every triangle against its vertex normals
			fixWinding( part );
			return part;
		}

		// Flip triangles whose geometric normal disagrees with the average vertex normal.
		public static Part fixWinding( Part part )
		{
			var p = part.p; var n = part.n; var idx = part.idx;
			for ( int t = 0; t < idx.Length; t += 3 )
			{
				int i0 = idx[ t ], i1 = idx[ t + 1 ], i2 = idx[ t + 2 ];
				_a.set( p[ i0 * 3 ], p[ i0 * 3 + 1 ], p[ i0 * 3 + 2 ] ); _b.set( p[ i1 * 3 ], p[ i1 * 3 + 1 ], p[ i1 * 3 + 2 ] ); _c.set( p[ i2 * 3 ], p[ i2 * 3 + 1 ], p[ i2 * 3 + 2 ] );
				_b.sub( _a ); _c.sub( _a );
				_b.cross( _c );
				double nx = ( double ) n[ i0 * 3 ] + n[ i1 * 3 ] + n[ i2 * 3 ];
				double ny = ( double ) n[ i0 * 3 + 1 ] + n[ i1 * 3 + 1 ] + n[ i2 * 3 + 1 ];
				double nz = ( double ) n[ i0 * 3 + 2 ] + n[ i1 * 3 + 2 ] + n[ i2 * 3 + 2 ];
				if ( _b.x * nx + _b.y * ny + _b.z * nz < 0 ) { idx[ t + 1 ] = i2; idx[ t + 2 ] = i1; }
			}

			return part;
		}

		// Triangular prism: triangle (-w/2,0) (w/2,0) (0,h) in XY, extruded along z by depth (centered).
		public static Part gablePart( double w, double h, double depth )
		{
			string key = $"g{ toFixed( w, 3 ) },{ toFixed( h, 3 ) },{ toFixed( depth, 3 ) }";
			return cached( key, () =>
			{
				double d = depth / 2;
				var pts = new[] { new Vector3( -w / 2, 0, d ), new Vector3( w / 2, 0, d ), new Vector3( 0, h, d ) };
				return slabPart( pts, depth, new Vector3( 1, 0, 0 ) );
			} );
		}

		// ---------------------------------------------------------------------------
		// transforms

		public static Matrix4 mat4( double x = 0, double y = 0, double z = 0, double ry = 0, double rx = 0, double rz = 0, Matrix4 @out = null )
		{
			_e.set( rx, ry, rz, "YXZ" );
			_q.setFromEuler( _e );
			_p.set( x, y, z );
			return ( @out ?? new Matrix4() ).compose( _p, _q, _s );
		}

		// transform from an options object { ry, rx, rz, sx, sy, sz }
		internal static Matrix4 optMat( double x, double y, double z, O o )
		{
			_e.set( o.rx ?? 0, o.ry ?? 0, o.rz ?? 0, "YXZ" );
			_q.setFromEuler( _e );
			_p.set( x, y, z );
			_d.set( o.sx ?? 1, o.sy ?? 1, o.sz ?? 1 );
			return new Matrix4().compose( _p, _q, _d );
		}

		// Catmull-Rom sampled sagging rope between two points (catenary approximation).
		public static List<Vector3> sagPoints( double[] a, double[] b, double sag, int n = 8 )
		{
			var pts = new List<Vector3>();
			for ( int i = 0; i <= n; i ++ )
			{
				double t = ( double ) i / n;
				pts.Add( new Vector3(
					a[ 0 ] + ( b[ 0 ] - a[ 0 ] ) * t,
					a[ 1 ] + ( b[ 1 ] - a[ 1 ] ) * t - sag * 4 * t * ( 1 - t ),
					a[ 2 ] + ( b[ 2 ] - a[ 2 ] ) * t ) );
			}

			return pts;
		}

		internal static Matrix3 m3 => _m3;
		internal static Matrix4 m4 => _m4;
		internal static Vector3 sa => _a;
		internal static Vector3 sb => _b;
		internal static Vector3 sc => _c;
		internal static Vector3 vX => _X;
		internal static Vector3 vY => _Y;
		internal static Vector3 vS => _s;
		internal static Vector3 vP => _p;
		internal static Quaternion vQ => _q;
	}

	// ---------------------------------------------------------------------------
	// Batch: merged geometry for one material

	public sealed class Batch
	{
		public readonly List<double> pos = new List<double>(), nrm = new List<double>(), uv = new List<double>(), tint = new List<double>(), data = new List<double>();
		public readonly List<int> idx = new List<int>();
		public int vcount;

		public int triangles => idx.Count / 3;

		public void add( Part part, Matrix4 m, Attr tint, Attr data )
		{
			var m3 = GeoBuilder.m3;
			m3.getNormalMatrix( m );
			var e = m.elements; var ne = m3.elements;
			var P = part.p; var N = part.n; var U = part.uv;
			int bas = vcount;
			int nv = P.Length / 3;
			for ( int i = 0; i < nv; i ++ )
			{
				double x = P[ i * 3 ], y = P[ i * 3 + 1 ], z = P[ i * 3 + 2 ];
				pos.Add( e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z + e[ 12 ] );
				pos.Add( e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z + e[ 13 ] );
				pos.Add( e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z + e[ 14 ] );
				double nx = N[ i * 3 ], ny = N[ i * 3 + 1 ], nz = N[ i * 3 + 2 ];
				double tx = ne[ 0 ] * nx + ne[ 3 ] * ny + ne[ 6 ] * nz;
				double ty = ne[ 1 ] * nx + ne[ 4 ] * ny + ne[ 7 ] * nz;
				double tz = ne[ 2 ] * nx + ne[ 5 ] * ny + ne[ 8 ] * nz;
				double l = JS.Or( JS.Hypot( tx, ty, tz ), 1 );
				nrm.Add( tx / l ); nrm.Add( ty / l ); nrm.Add( tz / l );
				uv.Add( U[ i * 2 ] ); uv.Add( U[ i * 2 + 1 ] );
				var t = tint.f != null ? tint.f( x, y, z, i ) : tint.a;
				this.tint.Add( t[ 0 ] ); this.tint.Add( t[ 1 ] ); this.tint.Add( t[ 2 ] );
				var d = data.f != null ? data.f( x, y, z, i ) : data.a;
				this.data.Add( d[ 0 ] ); this.data.Add( d[ 1 ] ); this.data.Add( d[ 2 ] ); this.data.Add( d[ 3 ] );
			}

			bool flip = m.determinant() < 0;
			var I = part.idx;
			for ( int k = 0; k < I.Length; k += 3 )
			{
				if ( flip ) { idx.Add( bas + I[ k ] ); idx.Add( bas + I[ k + 2 ] ); idx.Add( bas + I[ k + 1 ] ); }
				else { idx.Add( bas + I[ k ] ); idx.Add( bas + I[ k + 1 ] ); idx.Add( bas + I[ k + 2 ] ); }
			}

			vcount += nv;
		}

		// Stamp another batch (a prop prototype) into this one: positions / normals transformed by m,
		// tint multiplied by tintMul, the per-vertex seed (vdata.x) offset so every copy looks different.
		public void addBatch( Batch src, Matrix4 m, double[] tintMul = null, double seedOffset = 0 )
		{
			var m3 = GeoBuilder.m3;
			m3.getNormalMatrix( m );
			var e = m.elements; var ne = m3.elements;
			int bas = vcount;
			var P = src.pos; var N = src.nrm;
			int nv = src.vcount;
			for ( int i = 0; i < nv; i ++ )
			{
				double x = P[ i * 3 ], y = P[ i * 3 + 1 ], z = P[ i * 3 + 2 ];
				pos.Add( e[ 0 ] * x + e[ 4 ] * y + e[ 8 ] * z + e[ 12 ] );
				pos.Add( e[ 1 ] * x + e[ 5 ] * y + e[ 9 ] * z + e[ 13 ] );
				pos.Add( e[ 2 ] * x + e[ 6 ] * y + e[ 10 ] * z + e[ 14 ] );
				double nx = N[ i * 3 ], ny = N[ i * 3 + 1 ], nz = N[ i * 3 + 2 ];
				double tx = ne[ 0 ] * nx + ne[ 3 ] * ny + ne[ 6 ] * nz;
				double ty = ne[ 1 ] * nx + ne[ 4 ] * ny + ne[ 7 ] * nz;
				double tz = ne[ 2 ] * nx + ne[ 5 ] * ny + ne[ 8 ] * nz;
				double l = JS.Or( JS.Hypot( tx, ty, tz ), 1 );
				nrm.Add( tx / l ); nrm.Add( ty / l ); nrm.Add( tz / l );
				uv.Add( src.uv[ i * 2 ] ); uv.Add( src.uv[ i * 2 + 1 ] );
				var t = src.tint;
				if ( tintMul != null ) { tint.Add( t[ i * 3 ] * tintMul[ 0 ] ); tint.Add( t[ i * 3 + 1 ] * tintMul[ 1 ] ); tint.Add( t[ i * 3 + 2 ] * tintMul[ 2 ] ); }
				else { tint.Add( t[ i * 3 ] ); tint.Add( t[ i * 3 + 1 ] ); tint.Add( t[ i * 3 + 2 ] ); }
				var d = src.data;
				data.Add( d[ i * 4 ] + seedOffset ); data.Add( d[ i * 4 + 1 ] ); data.Add( d[ i * 4 + 2 ] ); data.Add( d[ i * 4 + 3 ] );
			}

			bool flip = m.determinant() < 0;
			var I = src.idx;
			for ( int k = 0; k < I.Count; k += 3 )
			{
				if ( flip ) { idx.Add( bas + I[ k ] ); idx.Add( bas + I[ k + 2 ] ); idx.Add( bas + I[ k + 1 ] ); }
				else { idx.Add( bas + I[ k ] ); idx.Add( bas + I[ k + 1 ] ); idx.Add( bas + I[ k + 2 ] ); }
			}

			vcount += nv;
		}

		// Append another batch verbatim, optionally remapping each vertex's vdata (used to merge
		// several emitter keys into one material / draw call).
		public void append( Batch src, Func<double[], double[]> dataFn = null )
		{
			int bas = vcount;
			pos.AddRange( src.pos ); nrm.AddRange( src.nrm ); uv.AddRange( src.uv ); tint.AddRange( src.tint );
			for ( int i = 0; i < src.vcount; i ++ )
			{
				var d = new[] { src.data[ i * 4 ], src.data[ i * 4 + 1 ], src.data[ i * 4 + 2 ], src.data[ i * 4 + 3 ] };
				var o = dataFn != null ? dataFn( d ) : d;
				data.Add( o[ 0 ] ); data.Add( o[ 1 ] ); data.Add( o[ 2 ] ); data.Add( o[ 3 ] );
			}

			foreach ( int i in src.idx ) idx.Add( bas + i );
			vcount += src.vcount;
		}

		// The merged attributes as float arrays ( Float32BufferAttribute ) and the index buffer.
		public BuiltGeometry build()
		{
			var g = new BuiltGeometry
			{
				position = F32( pos ), normal = F32( nrm ), uv = F32( uv ), tint = F32( tint ), vdata = F32( data ), index = idx.ToArray(), vertexCount = vcount,
			};
			g.computeBounds();
			return g;
		}

		static float[] F32( List<double> l ) { var a = new float[ l.Count ]; for ( int i = 0; i < a.Length; i ++ ) a[ i ] = ( float ) l[ i ]; return a; }
	}

	public sealed class BuiltGeometry
	{
		public float[] position, normal, uv, tint, vdata;
		public int[] index;
		public int vertexCount;
		public Vector3 boundsMin, boundsMax;

		public void computeBounds()
		{
			boundsMin = new Vector3( double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity );
			boundsMax = new Vector3( double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity );
			for ( int i = 0; i < position.Length; i += 3 )
			{
				boundsMin.x = Math.Min( boundsMin.x, position[ i ] ); boundsMin.y = Math.Min( boundsMin.y, position[ i + 1 ] ); boundsMin.z = Math.Min( boundsMin.z, position[ i + 2 ] );
				boundsMax.x = Math.Max( boundsMax.x, position[ i ] ); boundsMax.y = Math.Max( boundsMax.y, position[ i + 1 ] ); boundsMax.z = Math.Max( boundsMax.z, position[ i + 2 ] );
			}
		}

		// geometry.translate( x, y, z )
		public void translate( double x, double y, double z )
		{
			for ( int i = 0; i < position.Length; i += 3 ) { position[ i ] = ( float ) ( position[ i ] + x ); position[ i + 1 ] = ( float ) ( position[ i + 1 ] + y ); position[ i + 2 ] = ( float ) ( position[ i + 2 ] + z ); }
			computeBounds();
		}
	}

	// ---------------------------------------------------------------------------
	// Builder: transform stack + convenience emitters for all material batches

	public sealed class Builder
	{
		public readonly Dictionary<string, Batch> batches = new Dictionary<string, Batch>();
		public Matrix4 frame = new Matrix4();
		readonly List<Matrix4> stack = new List<Matrix4>();
		public Tidewater.World.Fish.FishProps fishProps; // fish, ice, leaves and lobsters placed by Props (drawn by FishPropsView)

		public Batch batch( string key )
		{
			if ( ! batches.TryGetValue( key, out var b ) ) { b = new Batch(); batches[ key ] = b; }
			return b;
		}

		public bool has( string key ) => batches.ContainsKey( key );

		public Builder push( Matrix4 m )
		{
			stack.Add( frame );
			frame = frame.clone().multiply( m );
			return this;
		}

		public Builder pushAt( double x, double y, double z, double ry = 0, double rx = 0, double rz = 0 ) => push( GeoBuilder.mat4( x, y, z, ry, rx, rz ) );

		public Builder pop()
		{
			frame = stack[ stack.Count - 1 ];
			stack.RemoveAt( stack.Count - 1 );
			return this;
		}

		// local -> world point
		public Vector3 toWorld( double x, double y, double z ) => new Vector3( x, y, z ).applyMatrix4( frame );

		static readonly Attr white = new Attr( new double[] { 1, 1, 1 } );
		static readonly Attr zero4 = new Attr( new double[] { 0, 0, 0, 0 } );

		public void add( string key, Part part, Matrix4 local, Attr tint, Attr data )
		{
			GeoBuilder.m4.multiplyMatrices( frame, local );
			batch( key ).add( part, GeoBuilder.m4, tint ?? white, data ?? zero4 );
		}

		// box centered at (x, y, z)
		public void box( string key, double x, double y, double z, double sx, double sy, double sz, O o = null )
		{
			o = o ?? new O();
			var part = GeoBuilder.boxPart( sx, sy, sz, o.grain ?? -1, o.skip ?? 0 );
			add( key, part, GeoBuilder.optMat( x, y, z, o ), o.tint, o.data );
		}

		// cylinder standing on (x, y, z)
		public void cyl( string key, double x, double y, double z, double rTop, double rBot, double h, O o = null )
		{
			o = o ?? new O();
			bool swap = o.swapUV ?? false;
			double period = GeoBuilder.WrapPeriod( key == "roofMetal" && swap ? "roofMetalSwap" : key );
			var part = GeoBuilder.cylPart( rTop, rBot, h, o.segs ?? 8, o.capTop ?? true, o.capBot ?? false, swap, period );
			add( key, part, GeoBuilder.optMat( x, y, z, o ), o.tint, o.data );
		}

		// lathe standing on (x, y, z)
		public void lathe( string key, double x, double y, double z, double[][] profile, O o = null )
		{
			o = o ?? new O();
			var part = GeoBuilder.lathePart( profile, o.segs ?? 12, o.rRef, GeoBuilder.WrapPeriod( key ) );
			add( key, part, GeoBuilder.optMat( x, y, z, o ), o.tint, o.data );
		}

		// rope vdata carries the rope radius in .y so the shader can normalise its uvs to strands
		static Attr ropeData( Attr d, double r )
		{
			var a = d != null && d.a != null ? d.a : new double[] { 0, 0, 0, 0 };
			return new Attr( new[] { a[ 0 ], r, a[ 2 ], a[ 3 ] } );
		}

		public void torus( string key, double x, double y, double z, double R, double r, O o = null )
		{
			o = o ?? new O();
			var part = GeoBuilder.torusPart( R, r, o.radial ?? 6, o.tubular ?? 16, o.arc ?? Math.PI * 2 );
			add( key, part, GeoBuilder.optMat( x, y, z, o ), o.tint, key == "rope" ? ropeData( o.data, r ) : o.data );
		}

		public void part( string key, Part part, double x, double y, double z, O o = null )
		{
			o = o ?? new O();
			add( key, part, GeoBuilder.optMat( x, y, z, o ), o.tint, o.data );
		}

		// rectangular beam between two local points; w = width (horizontal), h = height (vertical-ish)
		public void beam( string key, double[] p0, double[] p1, double w, double h, O o = null )
		{
			o = o ?? new O();
			Vector3 _a = GeoBuilder.sa, _b = GeoBuilder.sb, _c = GeoBuilder.sc;
			_a.set( p1[ 0 ] - p0[ 0 ], p1[ 1 ] - p0[ 1 ], p1[ 2 ] - p0[ 2 ] );
			double L = _a.length();
			if ( L < 1e-5 ) return;
			_a.divideScalar( L );
			var up = Math.Abs( _a.y ) > 0.98 ? GeoBuilder.vX : GeoBuilder.vY;
			// local x -> dir, local y -> up-ish, local z -> x cross y
			_c.crossVectors( _a, up ).normalize(); // z axis
			_b.crossVectors( _c, _a ).normalize(); // y axis
			if ( o.roll.HasValue && o.roll.Value != 0 )
			{
				double cr = Math.Cos( o.roll.Value ), sr = Math.Sin( o.roll.Value );
				var by = _b.clone(); var bz = _c.clone();
				_b.copy( by ).multiplyScalar( cr ).addScaledVector( bz, sr );
				_c.copy( bz ).multiplyScalar( cr ).addScaledVector( by, -sr );
			}

			var m = new Matrix4().makeBasis( _a, _b, _c );
			m.setPosition( ( p0[ 0 ] + p1[ 0 ] ) / 2, ( p0[ 1 ] + p1[ 1 ] ) / 2, ( p0[ 2 ] + p1[ 2 ] ) / 2 );
			var part = GeoBuilder.boxPart( L + ( o.extend ?? 0 ), h, w, 0, o.skip ?? 0 );
			add( key, part, m, o.tint, o.data );
		}

		// round rod between two local points
		public void rod( string key, double[] p0, double[] p1, double r0, double? r1 = null, O o = null )
		{
			o = o ?? new O();
			Vector3 _a = GeoBuilder.sa;
			_a.set( p1[ 0 ] - p0[ 0 ], p1[ 1 ] - p0[ 1 ], p1[ 2 ] - p0[ 2 ] );
			double L = _a.length();
			if ( L < 1e-5 ) return;
			_a.divideScalar( L );
			var q = GeoBuilder.vQ;
			q.setFromUnitVectors( GeoBuilder.vY, _a );
			var m = new Matrix4().compose( GeoBuilder.vP.set( p0[ 0 ], p0[ 1 ], p0[ 2 ] ), q, GeoBuilder.vS );
			var part = GeoBuilder.cylPart( r1 ?? r0, r0, L, o.segs ?? 6, o.capTop ?? true, o.capBot ?? false, false, GeoBuilder.WrapPeriod( key ) );
			add( key, part, m, o.tint, o.data );
		}

		// tube (rope) along local points
		public void tube( string key, IList<Vector3> points, double radius, O o = null )
		{
			o = o ?? new O();
			var part = GeoBuilder.tubePart( points, radius, o.radial ?? 5 );
			add( key, part, new Matrix4(), o.tint, key == "rope" ? ropeData( o.data, radius ) : o.data );
		}

		public void slab( string key, Vector3[] pts, double thickness, O o = null )
		{
			o = o ?? new O();
			var part = GeoBuilder.slabPart( pts, thickness, o.uDir, o.up );
			add( key, part, new Matrix4(), o.tint, o.data );
		}

		public int triangles { get { int t = 0; foreach ( var b in batches.Values ) t += b.triangles; return t; } }
	}
}
