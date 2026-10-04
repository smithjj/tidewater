using System;
using System.Collections.Generic;

// Port of src/engine/geometry/PrimitiveGeometries.js, RoundedBoxGeometry.js and TubeGeometry.js: three.js-identical vertex order,
// UV conventions and index winding. Each is a static factory returning a BufferGeometry with position, normal and uv.
namespace Tidewater.Engine
{
	public static class Geo
	{
		static void Finish( BufferGeometry g, List<int> indices, List<double> vertices, List<double> normals, List<double> uvs )
		{
			if ( indices != null ) g.setIndex( indices );
			g.setAttribute( "position", new BufferAttribute( vertices, 3 ) );
			g.setAttribute( "normal", new BufferAttribute( normals, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
		}

		public static BufferGeometry Plane( double width = 1, double height = 1, double widthSegments = 1, double heightSegments = 1 )
		{
			var g = new BufferGeometry { type = "PlaneGeometry" };
			double hw = width / 2, hh = height / 2;
			int gx = ( int ) Math.Floor( widthSegments ), gy = ( int ) Math.Floor( heightSegments );
			int gx1 = gx + 1, gy1 = gy + 1;
			double sw = width / gx, sh = height / gy;
			var indices = new List<int>(); var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>();
			for ( int iy = 0; iy < gy1; iy ++ )
			{
				double y = iy * sh - hh;
				for ( int ix = 0; ix < gx1; ix ++ )
				{
					vertices.Add( ix * sw - hw ); vertices.Add( -y ); vertices.Add( 0 );
					normals.Add( 0 ); normals.Add( 0 ); normals.Add( 1 );
					uvs.Add( ( double ) ix / gx ); uvs.Add( 1 - ( ( double ) iy / gy ) );
				}
			}

			for ( int iy = 0; iy < gy; iy ++ )
			{
				for ( int ix = 0; ix < gx; ix ++ )
				{
					int a = ix + gx1 * iy, b = ix + gx1 * ( iy + 1 ), c = ( ix + 1 ) + gx1 * ( iy + 1 ), d = ( ix + 1 ) + gx1 * iy;
					indices.Add( a ); indices.Add( b ); indices.Add( d ); indices.Add( b ); indices.Add( c ); indices.Add( d );
				}
			}

			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Box( double width = 1, double height = 1, double depth = 1, int widthSegments = 1, int heightSegments = 1, int depthSegments = 1 )
		{
			var g = new BufferGeometry { type = "BoxGeometry" };
			var indices = new List<int>(); var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>();
			int numberOfVertices = 0;
			var vec = new double[ 3 ];

			Action<int, int, int, double, double, double, double, double, int, int> plane = ( u, v, w, udir, vdir, pw, ph, pd, gx, gy ) =>
			{
				double sw = pw / gx, sh = ph / gy, hw = pw / 2, hh = ph / 2, hd = pd / 2;
				int gx1 = gx + 1, gy1 = gy + 1;
				int count = 0;
				for ( int iy = 0; iy < gy1; iy ++ )
				{
					double y = iy * sh - hh;
					for ( int ix = 0; ix < gx1; ix ++ )
					{
						double x = ix * sw - hw;
						vec[ u ] = x * udir; vec[ v ] = y * vdir; vec[ w ] = hd;
						vertices.Add( vec[ 0 ] ); vertices.Add( vec[ 1 ] ); vertices.Add( vec[ 2 ] );
						vec[ u ] = 0; vec[ v ] = 0; vec[ w ] = pd > 0 ? 1 : -1;
						normals.Add( vec[ 0 ] ); normals.Add( vec[ 1 ] ); normals.Add( vec[ 2 ] );
						uvs.Add( ( double ) ix / gx ); uvs.Add( 1 - ( ( double ) iy / gy ) );
						count ++;
					}
				}

				for ( int iy = 0; iy < gy; iy ++ )
				{
					for ( int ix = 0; ix < gx; ix ++ )
					{
						int n = numberOfVertices;
						int a = n + ix + gx1 * iy, b = n + ix + gx1 * ( iy + 1 ), c = n + ( ix + 1 ) + gx1 * ( iy + 1 ), d = n + ( ix + 1 ) + gx1 * iy;
						indices.Add( a ); indices.Add( b ); indices.Add( d ); indices.Add( b ); indices.Add( c ); indices.Add( d );
					}
				}

				numberOfVertices += count;
			};

			// axes: x = 0, y = 1, z = 2
			plane( 2, 1, 0, -1, -1, depth, height, width, depthSegments, heightSegments ); // px
			plane( 2, 1, 0, 1, -1, depth, height, -width, depthSegments, heightSegments ); // nx
			plane( 0, 2, 1, 1, 1, width, depth, height, widthSegments, depthSegments ); // py
			plane( 0, 2, 1, 1, -1, width, depth, -height, widthSegments, depthSegments ); // ny
			plane( 0, 1, 2, 1, -1, width, height, depth, widthSegments, heightSegments ); // pz
			plane( 0, 1, 2, -1, -1, width, height, -depth, widthSegments, heightSegments ); // nz
			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Sphere( double radius = 1, double widthSegments = 32, double heightSegments = 16, double phiStart = 0, double phiLength = Math.PI * 2, double thetaStart = 0, double thetaLength = Math.PI )
		{
			var g = new BufferGeometry { type = "SphereGeometry" };
			int ws = ( int ) Math.Max( 3, Math.Floor( widthSegments ) );
			int hs = ( int ) Math.Max( 2, Math.Floor( heightSegments ) );
			double thetaEnd = Math.Min( thetaStart + thetaLength, Math.PI );
			int index = 0;
			var grid = new List<int[]>(); var indices = new List<int>(); var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>();
			var v3 = new Vector3();
			for ( int iy = 0; iy <= hs; iy ++ )
			{
				var row = new int[ ws + 1 ]; double v = ( double ) iy / hs;
				double uOffset = 0;
				if ( iy == 0 && thetaStart == 0 ) uOffset = 0.5 / ws;
				else if ( iy == hs && thetaEnd == Math.PI ) uOffset = -0.5 / ws;
				for ( int ix = 0; ix <= ws; ix ++ )
				{
					double u = ( double ) ix / ws;
					double st = Math.Sin( thetaStart + v * thetaLength );
					v3.set( -radius * Math.Cos( phiStart + u * phiLength ) * st, radius * Math.Cos( thetaStart + v * thetaLength ), radius * Math.Sin( phiStart + u * phiLength ) * st );
					vertices.Add( v3.x ); vertices.Add( v3.y ); vertices.Add( v3.z );
					v3.normalize();
					normals.Add( v3.x ); normals.Add( v3.y ); normals.Add( v3.z );
					uvs.Add( u + uOffset ); uvs.Add( 1 - v );
					row[ ix ] = index ++;
				}

				grid.Add( row );
			}

			for ( int iy = 0; iy < hs; iy ++ )
			{
				for ( int ix = 0; ix < ws; ix ++ )
				{
					int a = grid[ iy ][ ix + 1 ], b = grid[ iy ][ ix ], c = grid[ iy + 1 ][ ix ], d = grid[ iy + 1 ][ ix + 1 ];
					if ( iy != 0 || thetaStart > 0 ) { indices.Add( a ); indices.Add( b ); indices.Add( d ); }
					if ( iy != hs - 1 || thetaEnd < Math.PI ) { indices.Add( b ); indices.Add( c ); indices.Add( d ); }
				}
			}

			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Cylinder( double radiusTop = 1, double radiusBottom = 1, double height = 1, int radialSegments = 32, int heightSegments = 1, bool openEnded = false, double thetaStart = 0, double thetaLength = Math.PI * 2 )
		{
			var g = new BufferGeometry { type = "CylinderGeometry" };
			var indices = new List<int>(); var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>();
			var indexArray = new List<int[]>(); double halfHeight = height / 2;
			int index = 0;
			var n = new Vector3();

			// torso
			{
				double slope = ( radiusBottom - radiusTop ) / height;
				for ( int y = 0; y <= heightSegments; y ++ )
				{
					var row = new int[ radialSegments + 1 ]; double v = ( double ) y / heightSegments;
					double radius = v * ( radiusBottom - radiusTop ) + radiusTop;
					for ( int x = 0; x <= radialSegments; x ++ )
					{
						double u = ( double ) x / radialSegments, theta = u * thetaLength + thetaStart;
						double s = Math.Sin( theta ), c = Math.Cos( theta );
						vertices.Add( radius * s ); vertices.Add( -v * height + halfHeight ); vertices.Add( radius * c );
						n.set( s, slope, c ).normalize();
						normals.Add( n.x ); normals.Add( n.y ); normals.Add( n.z );
						uvs.Add( u ); uvs.Add( 1 - v );
						row[ x ] = index ++;
					}

					indexArray.Add( row );
				}

				for ( int x = 0; x < radialSegments; x ++ )
				{
					for ( int y = 0; y < heightSegments; y ++ )
					{
						int a = indexArray[ y ][ x ], b = indexArray[ y + 1 ][ x ], c = indexArray[ y + 1 ][ x + 1 ], d = indexArray[ y ][ x + 1 ];
						if ( radiusTop > 0 || y != 0 ) { indices.Add( a ); indices.Add( b ); indices.Add( d ); }
						if ( radiusBottom > 0 || y != heightSegments - 1 ) { indices.Add( b ); indices.Add( c ); indices.Add( d ); }
					}
				}
			}

			Action<bool> cap = top =>
			{
				int centerStart = index;
				double radius = top ? radiusTop : radiusBottom, sign = top ? 1 : -1;
				for ( int x = 1; x <= radialSegments; x ++ )
				{
					vertices.Add( 0 ); vertices.Add( halfHeight * sign ); vertices.Add( 0 );
					normals.Add( 0 ); normals.Add( sign ); normals.Add( 0 );
					uvs.Add( 0.5 ); uvs.Add( 0.5 );
					index ++;
				}

				int centerEnd = index;
				for ( int x = 0; x <= radialSegments; x ++ )
				{
					double u = ( double ) x / radialSegments, theta = u * thetaLength + thetaStart;
					double c = Math.Cos( theta ), s = Math.Sin( theta );
					vertices.Add( radius * s ); vertices.Add( halfHeight * sign ); vertices.Add( radius * c );
					normals.Add( 0 ); normals.Add( sign ); normals.Add( 0 );
					uvs.Add( ( c * 0.5 ) + 0.5 ); uvs.Add( ( s * 0.5 * sign ) + 0.5 );
					index ++;
				}

				for ( int x = 0; x < radialSegments; x ++ )
				{
					int c = centerStart + x, i = centerEnd + x;
					if ( top ) { indices.Add( i ); indices.Add( i + 1 ); indices.Add( c ); }
					else { indices.Add( i + 1 ); indices.Add( i ); indices.Add( c ); }
				}
			};

			if ( ! openEnded )
			{
				if ( radiusTop > 0 ) cap( true );
				if ( radiusBottom > 0 ) cap( false );
			}

			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Cone( double radius = 1, double height = 1, int radialSegments = 32, int heightSegments = 1, bool openEnded = false, double thetaStart = 0, double thetaLength = Math.PI * 2 )
			=> Cylinder( 0, radius, height, radialSegments, heightSegments, openEnded, thetaStart, thetaLength );

		public static BufferGeometry Circle( double radius = 1, int segments = 32, double thetaStart = 0, double thetaLength = Math.PI * 2 )
		{
			var g = new BufferGeometry { type = "CircleGeometry" };
			segments = Math.Max( 3, segments );
			var indices = new List<int>(); var vertices = new List<double> { 0, 0, 0 }; var normals = new List<double> { 0, 0, 1 }; var uvs = new List<double> { 0.5, 0.5 };
			for ( int s = 0; s <= segments; s ++ )
			{
				double a = thetaStart + ( double ) s / segments * thetaLength;
				double x = radius * Math.Cos( a ), y = radius * Math.Sin( a );
				vertices.Add( x ); vertices.Add( y ); vertices.Add( 0 );
				normals.Add( 0 ); normals.Add( 0 ); normals.Add( 1 );
				uvs.Add( ( x / radius + 1 ) / 2 ); uvs.Add( ( y / radius + 1 ) / 2 );
			}

			for ( int i = 1; i <= segments; i ++ ) { indices.Add( i ); indices.Add( i + 1 ); indices.Add( 0 ); }
			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Torus( double radius = 1, double tube = 0.4, int radialSegments = 12, int tubularSegments = 48, double arc = Math.PI * 2, double thetaStart = 0, double thetaLength = Math.PI * 2 )
		{
			var g = new BufferGeometry { type = "TorusGeometry" };
			var indices = new List<int>(); var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>();
			var p = new Vector3(); var c = new Vector3();
			for ( int j = 0; j <= radialSegments; j ++ )
			{
				double v = thetaStart + ( ( double ) j / radialSegments ) * thetaLength;
				for ( int i = 0; i <= tubularSegments; i ++ )
				{
					double u = ( double ) i / tubularSegments * arc;
					p.set( ( radius + tube * Math.Cos( v ) ) * Math.Cos( u ), ( radius + tube * Math.Cos( v ) ) * Math.Sin( u ), tube * Math.Sin( v ) );
					vertices.Add( p.x ); vertices.Add( p.y ); vertices.Add( p.z );
					c.set( radius * Math.Cos( u ), radius * Math.Sin( u ), 0 );
					p.sub( c ).normalize();
					normals.Add( p.x ); normals.Add( p.y ); normals.Add( p.z );
					uvs.Add( ( double ) i / tubularSegments ); uvs.Add( ( double ) j / radialSegments );
				}
			}

			for ( int j = 1; j <= radialSegments; j ++ )
			{
				for ( int i = 1; i <= tubularSegments; i ++ )
				{
					int t1 = tubularSegments + 1;
					int a = t1 * j + i - 1, b = t1 * ( j - 1 ) + i - 1, cc = t1 * ( j - 1 ) + i, d = t1 * j + i;
					indices.Add( a ); indices.Add( b ); indices.Add( d ); indices.Add( b ); indices.Add( cc ); indices.Add( d );
				}
			}

			Finish( g, indices, vertices, normals, uvs );
			return g;
		}

		public static BufferGeometry Lathe( IList<Vector2> points, int segments = 12, double phiStart = 0, double phiLength = Math.PI * 2 )
		{
			var g = new BufferGeometry { type = "LatheGeometry" };
			phiLength = Math.Max( 0, Math.Min( Math.PI * 2, phiLength ) );
			var indices = new List<int>(); var vertices = new List<double>(); var uvs = new List<double>(); var initNormals = new List<double>(); var normals = new List<double>();
			double inv = 1.0 / segments;
			var normal = new Vector3(); var cur = new Vector3(); var prev = new Vector3();
			int n = points.Count;

			// profile normals: perpendicular of each edge, averaged at interior points
			for ( int j = 0; j <= n - 1; j ++ )
			{
				if ( j == 0 )
				{
					double dx = points[ 1 ].x - points[ 0 ].x, dy = points[ 1 ].y - points[ 0 ].y;
					normal.set( dy, -dx, 0 );
					prev.copy( normal );
					normal.normalize();
					initNormals.Add( normal.x ); initNormals.Add( normal.y ); initNormals.Add( normal.z );
				}
				else if ( j == n - 1 )
				{
					initNormals.Add( prev.x ); initNormals.Add( prev.y ); initNormals.Add( prev.z );
				}
				else
				{
					double dx = points[ j + 1 ].x - points[ j ].x, dy = points[ j + 1 ].y - points[ j ].y;
					normal.set( dy, -dx, 0 );
					cur.copy( normal );
					normal.add( prev ).normalize();
					initNormals.Add( normal.x ); initNormals.Add( normal.y ); initNormals.Add( normal.z );
					prev.copy( cur );
				}
			}

			for ( int i = 0; i <= segments; i ++ )
			{
				double phi = phiStart + i * inv * phiLength;
				double s = Math.Sin( phi ), c = Math.Cos( phi );
				for ( int j = 0; j <= n - 1; j ++ )
				{
					vertices.Add( points[ j ].x * s ); vertices.Add( points[ j ].y ); vertices.Add( points[ j ].x * c );
					uvs.Add( ( double ) i / segments ); uvs.Add( ( double ) j / ( n - 1 ) );
					normals.Add( initNormals[ 3 * j ] * s ); normals.Add( initNormals[ 3 * j + 1 ] ); normals.Add( initNormals[ 3 * j ] * c );
				}
			}

			for ( int i = 0; i < segments; i ++ )
			{
				for ( int j = 0; j < n - 1; j ++ )
				{
					int b0 = j + i * n;
					int a = b0, b = b0 + n, c = b0 + n + 1, d = b0 + 1;
					indices.Add( a ); indices.Add( b ); indices.Add( d ); indices.Add( c ); indices.Add( d ); indices.Add( b );
				}
			}

			// three.js order for lathes: position, uv, normal
			g.setIndex( indices );
			g.setAttribute( "position", new BufferAttribute( vertices, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
			g.setAttribute( "normal", new BufferAttribute( normals, 3 ) );
			return g;
		}

		// ------------------------------------------------------------------ tube

		public static BufferGeometry Tube( Curve path, int tubularSegments = 64, double radius = 1, int radialSegments = 8, bool closed = false )
		{
			var g = new BufferGeometry { type = "TubeGeometry" };
			var frames = path.computeFrenetFrames( tubularSegments, closed );
			var vertices = new List<double>(); var normals = new List<double>(); var uvs = new List<double>(); var indices = new List<int>();
			var P = new Vector3(); var n = new Vector3(); var v3 = new Vector3();

			Action<int> segment = i =>
			{
				path.getPointAt( ( double ) i / tubularSegments, P );
				var N = frames.normals[ i ]; var B = frames.binormals[ i ];
				for ( int j = 0; j <= radialSegments; j ++ )
				{
					double v = ( double ) j / radialSegments * Math.PI * 2;
					double s = Math.Sin( v ), c = -Math.Cos( v );
					n.set( c * N.x + s * B.x, c * N.y + s * B.y, c * N.z + s * B.z ).normalize();
					normals.Add( n.x ); normals.Add( n.y ); normals.Add( n.z );
					v3.copy( P ).addScaledVector( n, radius );
					vertices.Add( v3.x ); vertices.Add( v3.y ); vertices.Add( v3.z );
				}
			};

			for ( int i = 0; i < tubularSegments; i ++ ) segment( i );
			segment( closed == false ? tubularSegments : 0 );
			for ( int i = 0; i <= tubularSegments; i ++ ) for ( int j = 0; j <= radialSegments; j ++ ) { uvs.Add( ( double ) i / tubularSegments ); uvs.Add( ( double ) j / radialSegments ); }
			for ( int j = 1; j <= tubularSegments; j ++ )
			{
				for ( int i = 1; i <= radialSegments; i ++ )
				{
					int r1 = radialSegments + 1;
					int a = r1 * ( j - 1 ) + ( i - 1 ), b = r1 * j + ( i - 1 ), c = r1 * j + i, d = r1 * ( j - 1 ) + i;
					indices.Add( a ); indices.Add( b ); indices.Add( d ); indices.Add( b ); indices.Add( c ); indices.Add( d );
				}
			}

			g.setIndex( indices );
			g.setAttribute( "position", new BufferAttribute( vertices, 3 ) );
			g.setAttribute( "normal", new BufferAttribute( normals, 3 ) );
			g.setAttribute( "uv", new BufferAttribute( uvs, 2 ) );
			return g;
		}

		// ------------------------------------------------------------------ rounded box

		static readonly Vector3 _n = new Vector3();

		// UV coordinate along `uvAxis` for a face, arc-length parameterized so the rounded strip and the flat center share texel density.
		static double ArcUv( Vector3 faceDir, Vector3 normal, int uvAxis, int projectionAxis, double radius, double sideLength )
		{
			double totArc = 2 * Math.PI * radius / 4;
			double center = Math.Max( sideLength - 2 * radius, 0 );
			double halfArc = Math.PI / 4;
			_n.copy( normal );
			_n.setComponent( projectionAxis, 0 );
			_n.normalize();
			double arcUvRatio = 0.5 * totArc / ( totArc + center );
			double arcAngleRatio = 1.0 - ( _n.angleTo( faceDir ) / halfArc );
			if ( Math.Sign( _n.getComponent( uvAxis ) ) == 1 ) return arcAngleRatio * arcUvRatio;
			double lenUv = center / ( totArc + center );
			return lenUv + arcUvRatio + arcUvRatio * ( 1.0 - arcAngleRatio );
		}

		static double Sign( double v ) => v > 0 ? 1 : ( v < 0 ? -1 : 0 );

		// Box with rounded edges and corners: a non-indexed unit box with an odd segment count whose vertices are pushed onto an inner
		// box offset by `radius` along a smoothed normal.
		public static BufferGeometry RoundedBox( double width = 1, double height = 1, double depth = 1, int segments = 2, double radius = 0.1 )
		{
			segments = segments * 2 + 1;
			radius = Math.Min( Math.Min( width / 2, height / 2 ), Math.Min( depth / 2, radius ) );
			var g = Box( 1, 1, 1, segments, segments, segments );
			g.type = "RoundedBoxGeometry";
			if ( segments == 1 ) return g;
			var flat = g.toNonIndexed();
			g.index = null;
			g.setAttribute( "position", flat.attributes[ "position" ] );
			g.setAttribute( "normal", flat.attributes[ "normal" ] );
			g.setAttribute( "uv", flat.attributes[ "uv" ] );

			var position = new Vector3(); var normal = new Vector3(); var faceDir = new Vector3();
			var box = new Vector3( width, height, depth ).divideScalar( 2 ).subScalar( radius );
			var P = g.attributes[ "position" ].array; var N = g.attributes[ "normal" ].array; var U = g.attributes[ "uv" ].array;
			int perFace = P.Length / 3 / 6;
			double halfSeg = 0.5 / segments;
			for ( int i = 0, j = 0; i < P.Length / 3; i ++, j += 3 )
			{
				position.fromArray( P, j );
				normal.copy( position );
				normal.x -= Sign( normal.x ) * halfSeg;
				normal.y -= Sign( normal.y ) * halfSeg;
				normal.z -= Sign( normal.z ) * halfSeg;
				normal.normalize();
				P[ j ] = ( float ) ( box.x * Sign( position.x ) + normal.x * radius );
				P[ j + 1 ] = ( float ) ( box.y * Sign( position.y ) + normal.y * radius );
				P[ j + 2 ] = ( float ) ( box.z * Sign( position.z ) + normal.z * radius );
				N[ j ] = ( float ) normal.x; N[ j + 1 ] = ( float ) normal.y; N[ j + 2 ] = ( float ) normal.z;
				int k = i * 2;
				switch ( ( int ) Math.Floor( ( double ) i / perFace ) )
				{
					case 0: // +x
						faceDir.set( 1, 0, 0 );
						U[ k ] = ( float ) ArcUv( faceDir, normal, 2, 1, radius, depth );
						U[ k + 1 ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 1, 2, radius, height ) );
						break;
					case 1: // -x
						faceDir.set( -1, 0, 0 );
						U[ k ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 2, 1, radius, depth ) );
						U[ k + 1 ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 1, 2, radius, height ) );
						break;
					case 2: // +y
						faceDir.set( 0, 1, 0 );
						U[ k ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 0, 2, radius, width ) );
						U[ k + 1 ] = ( float ) ArcUv( faceDir, normal, 2, 0, radius, depth );
						break;
					case 3: // -y
						faceDir.set( 0, -1, 0 );
						U[ k ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 0, 2, radius, width ) );
						U[ k + 1 ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 2, 0, radius, depth ) );
						break;
					case 4: // +z
						faceDir.set( 0, 0, 1 );
						U[ k ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 0, 1, radius, width ) );
						U[ k + 1 ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 1, 0, radius, height ) );
						break;
					case 5: // -z
						faceDir.set( 0, 0, -1 );
						U[ k ] = ( float ) ArcUv( faceDir, normal, 0, 1, radius, width );
						U[ k + 1 ] = ( float ) ( 1.0 - ArcUv( faceDir, normal, 1, 0, radius, height ) );
						break;
				}
			}

			return g;
		}
	}
}
