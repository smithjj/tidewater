using System;
using System.Collections.Generic;
using System.Linq;
using Tidewater.Engine;

// Port of src/world/boat/GeoKit.js: geometry helpers for the procedural boat. Every geometry is normalized to the same attribute layout
// so parts can be merged per material:
//   position, normal, uv (meters where it matters), color (linear RGB), aux = (roughness, metalness, pattern id, animation weight)
namespace Tidewater.World.Boat
{
	// per-geometry options of prepare / GeoKit.add
	public sealed class Opts
	{
		public double color = 0xffffff;      // sRGB hex
		public Color colorRgb;               // or a linear colour
		public double rough = 0.5, metal = 0, pattern = 0, anim = 0;
		public Matrix4 matrix;

		// a copy with some fields replaced: the JS `{ ...OPTS, rough: 0.3 }`
		public Opts with( double? color = null, double? rough = null, double? metal = null, double? pattern = null, double? anim = null )
		{
			var o = new Opts { color = this.color, colorRgb = colorRgb, rough = this.rough, metal = this.metal, pattern = this.pattern, anim = this.anim, matrix = matrix };
			if ( color.HasValue ) { o.color = color.Value; o.colorRgb = null; }
			if ( rough.HasValue ) o.rough = rough.Value;
			if ( metal.HasValue ) o.metal = metal.Value;
			if ( pattern.HasValue ) o.pattern = pattern.Value;
			if ( anim.HasValue ) o.anim = anim.Value;
			return o;
		}
	}

	public sealed class GridOpts
	{
		public bool flip, closeJ;
		public Func<Vector3, int, int, double, double, double[]> uvFn;
	}

	public sealed class SlabOpts
	{
		public bool edges = true, back = true, front = true;
	}

	public static class GK
	{
		static readonly HashSet<string> KEEP = new HashSet<string> { "position", "normal", "uv", "color", "aux" };
		static readonly Vector3 _v = new Vector3();

		public static Matrix4 mat4( double x = 0, double y = 0, double z = 0, double rx = 0, double ry = 0, double rz = 0, double sx = 1, double? sy = null, double? sz = null, string order = "XYZ" )
		{
			var e = new Euler().set( rx, ry, rz, order );
			var q = new Quaternion().setFromEuler( e );
			return new Matrix4().compose( new Vector3( x, y, z ), q, new Vector3( sx, sy ?? sx, sz ?? sx ) );
		}

		// Matrix placing the local +Y axis along `dir` at `pos`.
		public static Matrix4 alignY( Vector3 pos, Vector3 dir, double roll = 0 )
		{
			var d = dir.clone().normalize();
			var q = new Quaternion().setFromUnitVectors( new Vector3( 0, 1, 0 ), d );
			if ( roll != 0 ) q.multiply( new Quaternion().setFromAxisAngle( new Vector3( 0, 1, 0 ), roll ) );
			return new Matrix4().compose( pos, q, new Vector3( 1, 1, 1 ) );
		}

		public static Color linearColor( Opts o ) => o.colorRgb ?? new Color().set( o.color );
		public static Color linearColor( double hex ) => new Color().set( hex );

		public static BufferGeometry prepare( BufferGeometry geo, Opts o = null )
		{
			o = o ?? new Opts();
			foreach ( var name in geo.attributeNames.ToList() ) if ( ! KEEP.Contains( name ) ) geo.deleteAttribute( name );

			int n = geo.attributes[ "position" ].count;
			if ( geo.index == null )
			{
				var idx = new int[ n ];
				for ( int i = 0; i < n; i ++ ) idx[ i ] = i;
				geo.setIndex( idx );
			}

			if ( ! geo.hasAttribute( "normal" ) ) geo.computeVertexNormals();
			if ( ! geo.hasAttribute( "uv" ) ) geo.setAttribute( "uv", new BufferAttribute( new float[ n * 2 ], 2 ) );

			if ( ! geo.hasAttribute( "color" ) )
			{
				var c = linearColor( o );
				var arr = new float[ n * 3 ];
				for ( int i = 0; i < n; i ++ ) { arr[ i * 3 ] = ( float ) c.r; arr[ i * 3 + 1 ] = ( float ) c.g; arr[ i * 3 + 2 ] = ( float ) c.b; }
				geo.setAttribute( "color", new BufferAttribute( arr, 3 ) );
			}

			if ( ! geo.hasAttribute( "aux" ) )
			{
				var arr = new float[ n * 4 ];
				for ( int i = 0; i < n; i ++ ) { arr[ i * 4 ] = ( float ) o.rough; arr[ i * 4 + 1 ] = ( float ) o.metal; arr[ i * 4 + 2 ] = ( float ) o.pattern; arr[ i * 4 + 3 ] = ( float ) o.anim; }
				geo.setAttribute( "aux", new BufferAttribute( arr, 4 ) );
			}

			if ( o.matrix != null ) geo.applyMatrix4( o.matrix );
			return geo;
		}

		// ------------------------------------------------------------------ primitives

		// Box with UVs in meters.
		public static BufferGeometry box( double w, double h, double d )
		{
			var g = Geo.Box( w, h, d );
			var uv = g.attributes[ "uv" ];
			// face order: px, nx, py, ny, pz, nz (4 vertices each)
			double[][] dims = { new[] { d, h }, new[] { d, h }, new[] { w, d }, new[] { w, d }, new[] { w, h }, new[] { w, h } };
			for ( int f = 0; f < 6; f ++ )
				for ( int i = 0; i < 4; i ++ )
				{
					int k = f * 4 + i;
					uv.setXY( k, uv.getX( k ) * dims[ f ][ 0 ], uv.getY( k ) * dims[ f ][ 1 ] );
				}

			return g;
		}

		public static BufferGeometry roundedBox( double w, double h, double d, double radius = 0.02, int segments = 2 )
		{
			var g = Geo.RoundedBox( w, h, d, segments, Math.Min( radius, Math.Min( w / 2 - 1e-4, Math.Min( h / 2 - 1e-4, d / 2 - 1e-4 ) ) ) );
			var uv = g.attributes[ "uv" ];
			for ( int i = 0; i < uv.count; i ++ ) uv.setXY( i, uv.getX( i ) * Math.Max( w, d ), uv.getY( i ) * h );
			return g;
		}

		public static BufferGeometry cylinder( double rTop, double rBottom, double h, int radial = 12, int heightSegs = 1, bool open = false, double thetaStart = 0, double thetaLength = Math.PI * 2 )
		{
			var g = Geo.Cylinder( rTop, rBottom, h, radial, heightSegs, open, thetaStart, thetaLength );
			var uv = g.attributes[ "uv" ];
			double circ = Math.Max( rTop, rBottom ) * thetaLength;
			for ( int i = 0; i < uv.count; i ++ ) uv.setXY( i, uv.getX( i ) * circ, uv.getY( i ) * h );
			return g;
		}

		// Cylinder between two points.
		public static BufferGeometry rod( Vector3 a, Vector3 b, double radius, int radial = 8, double? rEnd = null )
		{
			var dir = new Vector3().subVectors( b, a );
			double len = dir.length();
			var g = cylinder( rEnd ?? radius, radius, len, radial, 1, false );
			g.applyMatrix4( alignY( new Vector3().addVectors( a, b ).multiplyScalar( 0.5 ), dir ) );
			return g;
		}

		public static BufferGeometry sphere( double r, int w = 12, int h = 8, double phiStart = 0, double phiLength = Math.PI * 2, double thetaStart = 0, double thetaLength = Math.PI )
			=> Geo.Sphere( r, w, h, phiStart, phiLength, thetaStart, thetaLength );

		public static BufferGeometry torus( double R, double r, int radial = 8, int tubular = 24, double arc = Math.PI * 2 )
		{
			var g = Geo.Torus( R, r, radial, tubular, arc );
			var uv = g.attributes[ "uv" ];
			for ( int i = 0; i < uv.count; i ++ ) uv.setXY( i, uv.getX( i ) * R * arc, uv.getY( i ) * 2 * Math.PI * r );
			return g;
		}

		// Lathe around +Y from [ [r, y], ... ] (bottom to top).
		public static BufferGeometry lathe( double[][] profile, int segments = 16 )
		{
			var pts = profile.Select( p => new Vector2( Math.Max( 0, p[ 0 ] ), p[ 1 ] ) ).ToList();
			return Geo.Lathe( pts, segments );
		}

		public static BufferGeometry tube( List<Vector3> points, double radius, int tubular = 32, int radial = 6, bool closed = false, double tension = 0.5 )
		{
			var curve = new CatmullRomCurve3( points, closed, "catmullrom", tension );
			var g = Geo.Tube( curve, tubular, radius, radial, closed );
			double len = curve.getLength();
			var uv = g.attributes[ "uv" ];
			for ( int i = 0; i < uv.count; i ++ ) uv.setXY( i, uv.getX( i ) * len, uv.getY( i ) );
			return g;
		}

		// Indexed surface from rows[i][j] (Vector3). `flip` reverses the winding. UVs: u = distance along i (first column), v = distance
		// along j (first row).
		public static BufferGeometry gridSurface( List<List<Vector3>> rows, GridOpts o = null )
		{
			o = o ?? new GridOpts();
			int ni = rows.Count, nj = rows[ 0 ].Count;
			var pos = new float[ ni * nj * 3 ];
			var uvs = new float[ ni * nj * 2 ];
			double u = 0;
			for ( int i = 0; i < ni; i ++ )
			{
				if ( i > 0 ) u += rows[ i ][ 0 ].distanceTo( rows[ i - 1 ][ 0 ] );
				double v = 0;
				for ( int j = 0; j < nj; j ++ )
				{
					var p = rows[ i ][ j ];
					if ( j > 0 ) v += p.distanceTo( rows[ i ][ j - 1 ] );
					int k = i * nj + j;
					pos[ k * 3 ] = ( float ) p.x; pos[ k * 3 + 1 ] = ( float ) p.y; pos[ k * 3 + 2 ] = ( float ) p.z;
					if ( o.uvFn != null )
					{
						var t = o.uvFn( p, i, j, u, v );
						uvs[ k * 2 ] = ( float ) t[ 0 ]; uvs[ k * 2 + 1 ] = ( float ) t[ 1 ];
					}
					else
					{
						uvs[ k * 2 ] = ( float ) u; uvs[ k * 2 + 1 ] = ( float ) v;
					}
				}
			}

			var idx = new List<int>();
			int jmax = o.closeJ ? nj : nj - 1;
			for ( int i = 0; i < ni - 1; i ++ )
			{
				for ( int j = 0; j < jmax; j ++ )
				{
					int j1 = ( j + 1 ) % nj;
					int a = i * nj + j, b = ( i + 1 ) * nj + j, c = ( i + 1 ) * nj + j1, d = i * nj + j1;
					if ( o.flip ) { idx.Add( a ); idx.Add( b ); idx.Add( d ); idx.Add( b ); idx.Add( c ); idx.Add( d ); }
					else { idx.Add( a ); idx.Add( d ); idx.Add( b ); idx.Add( d ); idx.Add( c ); idx.Add( b ); }
				}
			}

			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
			g.setIndex( idx );
			g.computeVertexNormals();
			return g;
		}

		// Flat polygon (Vector3 loop, assumed planar-ish and convex-ish) as a fan from its centroid.
		public static BufferGeometry fanCap( List<Vector3> loop, Vector3 normalHint )
		{
			var c = new Vector3();
			foreach ( var p in loop ) c.add( p );
			c.divideScalar( loop.Count );
			var pos = new List<double> { c.x, c.y, c.z };
			foreach ( var p in loop ) { pos.Add( p.x ); pos.Add( p.y ); pos.Add( p.z ); }
			var idx = new List<int>();
			for ( int i = 0; i < loop.Count; i ++ ) { idx.Add( 0 ); idx.Add( 1 + i ); idx.Add( 1 + ( ( i + 1 ) % loop.Count ) ); }
			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setIndex( idx );
			orientTowards( g, normalHint );
			g.computeVertexNormals();
			planarUV( g, normalHint );
			return g;
		}

		// Flip all triangles if their average normal disagrees with `dir`.
		public static BufferGeometry orientTowards( BufferGeometry g, Vector3 dir )
		{
			var p = g.attributes[ "position" ]; var idx = g.index.array;
			var a = new Vector3(); var b = new Vector3(); var c = new Vector3(); var n = new Vector3(); var acc = new Vector3();
			for ( int i = 0; i < idx.Length; i += 3 )
			{
				a.fromBufferAttribute( p, idx[ i ] ); b.fromBufferAttribute( p, idx[ i + 1 ] ); c.fromBufferAttribute( p, idx[ i + 2 ] );
				n.subVectors( b, a ).cross( c.sub( a ) );
				acc.add( n );
			}

			if ( acc.dot( dir ) < 0 )
			{
				var arr = ( int[] ) idx.Clone();
				for ( int i = 0; i < arr.Length; i += 3 ) { int t = arr[ i + 1 ]; arr[ i + 1 ] = arr[ i + 2 ]; arr[ i + 2 ] = t; }
				g.setIndex( arr );
			}

			return g;
		}

		// Planar projection UVs (meters) perpendicular to `normal`.
		public static BufferGeometry planarUV( BufferGeometry g, Vector3 normal )
		{
			var n = normal.clone().normalize();
			var t = Math.Abs( n.y ) > 0.9 ? new Vector3( 1, 0, 0 ) : new Vector3( 0, 1, 0 ).cross( n ).normalize();
			var b = new Vector3().crossVectors( n, t );
			var p = g.attributes[ "position" ];
			var uv = new float[ p.count * 2 ];
			var v = new Vector3();
			for ( int i = 0; i < p.count; i ++ )
			{
				v.fromBufferAttribute( p, i );
				uv[ i * 2 ] = ( float ) v.dot( t ); uv[ i * 2 + 1 ] = ( float ) v.dot( b );
			}

			g.setAttribute( "uv", new BufferAttribute( uv, 2 ) );
			return g;
		}

		// Solid panel from a 2D outline (u, v in meters) with optional holes. map( u, v, side ) gives the point on the front (side 0) or
		// back (side 1) face. Faces are oriented automatically (front faces away from the back face, edges outward).
		public static BufferGeometry slab( double[][] outline, double[][][] holes, Func<double, double, int, Vector3> map, SlabOpts so = null )
		{
			so = so ?? new SlabOpts();
			var contour = outline.Select( p => new Vector2( p[ 0 ], p[ 1 ] ) ).ToList();
			var holeVs = holes.Select( h => h.Select( p => new Vector2( p[ 0 ], p[ 1 ] ) ).ToList() ).ToList();
			if ( ShapeUtils.isClockWise( contour ) ) contour.Reverse();
			foreach ( var h in holeVs ) if ( ! ShapeUtils.isClockWise( h ) ) h.Reverse();
			var tris = ShapeUtils.triangulateShape( contour, holeVs );
			var all = new List<Vector2>( contour ); foreach ( var h in holeVs ) all.AddRange( h );

			var parts = new List<BufferGeometry>();
			Func<int, BufferGeometry> faceGeo = side =>
			{
				var pos = new List<double>(); var uvs = new List<double>();
				foreach ( var p in all )
				{
					var q = map( p.x, p.y, side );
					pos.Add( q.x ); pos.Add( q.y ); pos.Add( q.z );
					uvs.Add( p.x ); uvs.Add( p.y );
				}

				// drop zero-area triangles (earcut emits them for collinear outline points)
				var idx = new List<int>();
				foreach ( var t in tris )
				{
					var a = all[ t[ 0 ] ]; var b = all[ t[ 1 ] ]; var c = all[ t[ 2 ] ];
					if ( Math.Abs( ( b.x - a.x ) * ( c.y - a.y ) - ( c.x - a.x ) * ( b.y - a.y ) ) > 1e-10 ) { idx.Add( t[ 0 ] ); idx.Add( t[ 1 ] ); idx.Add( t[ 2 ] ); }
				}

				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
				g.setIndex( idx );
				// orient: front normal points from back face toward front face
				var c0 = contour[ 0 ];
				var dir = map( c0.x, c0.y, side ).sub( map( c0.x, c0.y, 1 - side ) );
				orientTowards( g, dir );
				g.computeVertexNormals();
				// vertices left without triangles get the face direction
				var nrm = g.attributes[ "normal" ];
				dir.normalize();
				for ( int i = 0; i < nrm.count; i ++ )
				{
					if ( JS.Hypot( nrm.getX( i ), nrm.getY( i ), nrm.getZ( i ) ) < 0.5 ) nrm.setXYZ( i, dir.x, dir.y, dir.z );
				}

				return g;
			};

			if ( so.front ) parts.Add( faceGeo( 0 ) );
			if ( so.back ) parts.Add( faceGeo( 1 ) );

			if ( so.edges )
			{
				var pos = new List<double>(); var uvs = new List<double>(); var idx = new List<int>();
				var loops = new List<List<Vector2>> { contour }; loops.AddRange( holeVs );
				var a0 = new Vector3(); var a1 = new Vector3(); var b0 = new Vector3(); var b1 = new Vector3();
				var n = new Vector3(); var outv = new Vector3();
				foreach ( var loop in loops )
				{
					double len = 0;
					for ( int i = 0; i < loop.Count; i ++ )
					{
						var p = loop[ i ]; var q = loop[ ( i + 1 ) % loop.Count ];
						double el = p.distanceTo( q );
						if ( el < 1e-6 ) continue;
						a0.copy( map( p.x, p.y, 0 ) ); a1.copy( map( p.x, p.y, 1 ) );
						b0.copy( map( q.x, q.y, 0 ) ); b1.copy( map( q.x, q.y, 1 ) );
						double th = a0.distanceTo( a1 );
						int bas = pos.Count / 3;
						pos.AddRange( new[] { a0.x, a0.y, a0.z, b0.x, b0.y, b0.z, b1.x, b1.y, b1.z, a1.x, a1.y, a1.z } );
						uvs.AddRange( new[] { len, 0, len + el, 0, len + el, th, len, th } );
						len += el;
						// outward direction: right-hand side of the edge for CCW outline / CW holes
						double mx = ( p.x + q.x ) / 2, my = ( p.y + q.y ) / 2;
						double ox = ( q.y - p.y ) / el * 0.01, oy = -( q.x - p.x ) / el * 0.01;
						outv.copy( map( mx + ox, my + oy, 0 ) ).sub( map( mx, my, 0 ) );
						n.subVectors( b0, a0 ).cross( _v.subVectors( a1, a0 ) );
						if ( n.dot( outv ) >= 0 ) idx.AddRange( new[] { bas, bas + 1, bas + 2, bas, bas + 2, bas + 3 } );
						else idx.AddRange( new[] { bas, bas + 2, bas + 1, bas, bas + 3, bas + 2 } );
					}
				}

				if ( pos.Count > 0 )
				{
					var g = new BufferGeometry();
					g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
					g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
					g.setIndex( idx );
					g.computeVertexNormals();
					parts.Add( g );
				}
			}

			return parts.Count == 1 ? parts[ 0 ] : GeometryUtils.mergeGeometries( parts );
		}

		// Loft of profile loops/strips: profiles[i] = list of Vector3 (same length). `closed` joins the last profile point to the first
		// (duplicating the seam so UVs stay continuous; seam normals are averaged).
		public static BufferGeometry loft( List<List<Vector3>> profiles, bool closed = false, bool flip = false )
		{
			var rows = closed ? profiles.Select( r => { var l = new List<Vector3>( r ); l.Add( r[ 0 ].clone() ); return l; } ).ToList() : profiles;
			var g = gridSurface( rows, new GridOpts { flip = flip } );
			if ( closed )
			{
				int nj = rows[ 0 ].Count;
				var nrm = g.attributes[ "normal" ];
				var a = new Vector3(); var b = new Vector3();
				for ( int i = 0; i < rows.Count; i ++ )
				{
					int i0 = i * nj, i1 = i * nj + nj - 1;
					a.fromBufferAttribute( nrm, i0 ); b.fromBufferAttribute( nrm, i1 );
					a.add( b ).normalize();
					nrm.setXYZ( i0, a.x, a.y, a.z ); nrm.setXYZ( i1, a.x, a.y, a.z );
				}
			}

			return g;
		}

		// Assign per-vertex colors with a function of the vertex position.
		public static BufferGeometry paintVertices( BufferGeometry g, Func<Vector3, int, Color> fn )
		{
			var p = g.attributes[ "position" ];
			var arr = new float[ p.count * 3 ];
			var v = new Vector3();
			for ( int i = 0; i < p.count; i ++ )
			{
				v.fromBufferAttribute( p, i );
				var c = fn( v, i );
				arr[ i * 3 ] = ( float ) c.r; arr[ i * 3 + 1 ] = ( float ) c.g; arr[ i * 3 + 2 ] = ( float ) c.b;
			}

			g.setAttribute( "color", new BufferAttribute( arr, 3 ) );
			return g;
		}

		public static BufferGeometry paintVertices( BufferGeometry g, Func<Vector3, int, double> hexFn )
			=> paintVertices( g, ( v, i ) => linearColor( hexFn( v, i ) ) );

		// Per-vertex aux attribute with a function returning [rough, metal, pattern, anim].
		public static BufferGeometry auxVertices( BufferGeometry g, Func<Vector3, int, double[]> fn )
		{
			var p = g.attributes[ "position" ];
			var arr = new float[ p.count * 4 ];
			var v = new Vector3();
			for ( int i = 0; i < p.count; i ++ )
			{
				v.fromBufferAttribute( p, i );
				var a = fn( v, i );
				arr[ i * 4 ] = ( float ) a[ 0 ]; arr[ i * 4 + 1 ] = ( float ) a[ 1 ]; arr[ i * 4 + 2 ] = ( float ) a[ 2 ]; arr[ i * 4 + 3 ] = ( float ) a[ 3 ];
			}

			g.setAttribute( "aux", new BufferAttribute( arr, 4 ) );
			return g;
		}

		public static double triangleCount( BufferGeometry g ) => g.index != null ? g.index.count / 3.0 : g.attributes[ "position" ].count / 3.0;
	}

	// Collects geometries per material bucket and merges them.
	public sealed class GeoKit
	{
		readonly Dictionary<string, List<BufferGeometry>> buckets = new Dictionary<string, List<BufferGeometry>>();
		public static Action<string, BufferGeometry> onAdd;   // test hook: called with every prepared geometry

		public BufferGeometry add( string bucket, BufferGeometry geometry, Opts o = null )
		{
			var g = GK.prepare( geometry, o );
			if ( ! buckets.ContainsKey( bucket ) ) buckets[ bucket ] = new List<BufferGeometry>();
			buckets[ bucket ].Add( g );
			if ( onAdd != null ) onAdd( bucket, g );
			return g;
		}

		public BufferGeometry merged( string bucket )
		{
			if ( ! buckets.TryGetValue( bucket, out var list ) || list.Count == 0 ) return null;
			var g = list.Count == 1 ? list[ 0 ] : GeometryUtils.mergeGeometries( list );
			if ( g == null ) throw new Exception( "BoatModel: failed to merge bucket " + bucket );
			g.computeBoundingBox();
			g.computeBoundingSphere();
			return g;
		}

		public static BufferGeometry mergePrepared( List<BufferGeometry> list )
		{
			var g = list.Count == 1 ? list[ 0 ] : GeometryUtils.mergeGeometries( list );
			g.computeBoundingBox();
			g.computeBoundingSphere();
			return g;
		}
	}
}
