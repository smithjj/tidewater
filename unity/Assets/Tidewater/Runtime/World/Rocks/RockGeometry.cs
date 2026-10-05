using System;
using System.Collections.Generic;
using Tidewater.Engine;
using Tidewater.World.Terrain;

// Port of src/world/terrain/RockGeometry.js: procedural rock meshes. An icosphere carved by a handful of random planes (fracture facets), blended with an ellipsoid and roughened by 3D noise. The same shape
// function is evaluated on every LOD so silhouettes stay consistent. A per-vertex cavity term is baked into the ao attribute. The numbers are the JS's, down to the Float32 stores (positions, radii,
// cavity sums, normals).
namespace Tidewater.World.Rocks
{
	public sealed class RockStyle
	{
		public string name; public double[] scale; public int planes; public double[] cut; public double soft, rough;
	}

	public sealed class RockMesh
	{
		public float[] pos, nor, ao;
		public int[] index;
		public int vertexCount => pos.Length / 3;
		public int triangles => index.Length / 3;
	}

	public static class RockGeometry
	{
		// Rock styles: ellipsoid proportions, number of fracture planes, facet sharpness, roughness
		public static readonly RockStyle[] STYLES =
		{
			new RockStyle { name = "boulder", scale = new[] { 1.0, 0.8, 0.9 }, planes = 10, cut = new[] { 0.62, 0.88 }, soft = 0.08, rough = 0.06 },
			new RockStyle { name = "block", scale = new[] { 1.1, 0.9, 0.95 }, planes = 13, cut = new[] { 0.58, 0.8 }, soft = 0.04, rough = 0.045 },
			new RockStyle { name = "slab", scale = new[] { 1.35, 0.55, 1.0 }, planes = 11, cut = new[] { 0.6, 0.86 }, soft = 0.05, rough = 0.045 },
			new RockStyle { name = "spire", scale = new[] { 0.75, 1.45, 0.8 }, planes = 12, cut = new[] { 0.58, 0.84 }, soft = 0.05, rough = 0.05 },
		};

		// small 3D gradient noise ( value hashed on the integer lattice )
		static double Hash3( int i, int j, int k, int s ) { unchecked { return TerrainNoise.Hash2( i * 73 + k * 19349663, j * 31 + k * 83492791, s ); } }

		static double Noise3( double x, double y, double z, int s )
		{
			int xi = ( int ) Math.Floor( x ), yi = ( int ) Math.Floor( y ), zi = ( int ) Math.Floor( z );
			double xf = x - xi, yf = y - yi, zf = z - zi;
			double u = xf * xf * ( 3 - 2 * xf ), v = yf * yf * ( 3 - 2 * yf ), w = zf * zf * ( 3 - 2 * zf );
			double c000 = Hash3( xi, yi, zi, s ), c100 = Hash3( xi + 1, yi, zi, s ), c010 = Hash3( xi, yi + 1, zi, s ), c110 = Hash3( xi + 1, yi + 1, zi, s );
			double c001 = Hash3( xi, yi, zi + 1, s ), c101 = Hash3( xi + 1, yi, zi + 1, s ), c011 = Hash3( xi, yi + 1, zi + 1, s ), c111 = Hash3( xi + 1, yi + 1, zi + 1, s );
			double x00 = c000 + ( c100 - c000 ) * u;
			double x10 = c010 + ( c110 - c010 ) * u;
			double x01 = c001 + ( c101 - c001 ) * u;
			double x11 = c011 + ( c111 - c011 ) * u;
			double y0 = x00 + ( x10 - x00 ) * v, y1 = x01 + ( x11 - x01 ) * v;
			return ( y0 + ( y1 - y0 ) * w ) * 2 - 1;
		}

		static double Fbm3( double x, double y, double z, int oct, int s )
		{
			double a = 1, f = 1, sum = 0, n = 0;
			for ( int o = 0; o < oct; o ++ )
			{
				sum += Noise3( x * f, y * f, z * f, s + o * 7 ) * a;
				n += a;
				a *= 0.5;
				f *= 2.03;
			}

			return sum / n;
		}

		// the icosphere ( indexed, shared vertices ); the vertices are doubles until the end, then stored as Float32
		static void Icosphere( int subdiv, out float[] vertsOut, out int[] facesOut )
		{
			double t = ( 1 + Math.Sqrt( 5 ) ) / 2;
			var verts = new List<double> { - 1, t, 0, 1, t, 0, - 1, - t, 0, 1, - t, 0, 0, - 1, t, 0, 1, t, 0, - 1, - t, 0, 1, - t, t, 0, - 1, t, 0, 1, - t, 0, - 1, - t, 0, 1 };
			var faces = new List<int> { 0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8, 3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1 };
			Action<int> norm = i =>
			{
				double l = Tidewater.Util.MathX.Hypot3( verts[ i * 3 ], verts[ i * 3 + 1 ], verts[ i * 3 + 2 ] );
				verts[ i * 3 ] /= l; verts[ i * 3 + 1 ] /= l; verts[ i * 3 + 2 ] /= l;
			};
			for ( int i = 0; i < verts.Count / 3; i ++ ) norm( i );
			for ( int s = 0; s < subdiv; s ++ )
			{
				var cache = new Dictionary<long, int>();
				Func<int, int, int> mid = ( a, b ) =>
				{
					long key = a < b ? a * 100000L + b : b * 100000L + a;
					if ( cache.TryGetValue( key, out int m ) ) return m;
					m = verts.Count / 3;
					verts.Add( ( verts[ a * 3 ] + verts[ b * 3 ] ) / 2 ); verts.Add( ( verts[ a * 3 + 1 ] + verts[ b * 3 + 1 ] ) / 2 ); verts.Add( ( verts[ a * 3 + 2 ] + verts[ b * 3 + 2 ] ) / 2 );
					norm( m );
					cache[ key ] = m;
					return m;
				};
				var nf = new List<int>();
				for ( int f = 0; f < faces.Count; f += 3 )
				{
					int a = faces[ f ], b = faces[ f + 1 ], c = faces[ f + 2 ];
					int ab = mid( a, b ), bc = mid( b, c ), ca = mid( c, a );
					nf.AddRange( new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca } );
				}

				faces = nf;
			}

			vertsOut = new float[ verts.Count ];
			for ( int i = 0; i < verts.Count; i ++ ) vertsOut[ i ] = ( float ) verts[ i ];
			facesOut = faces.ToArray();
		}

		// the radius of the rock surface along the unit direction ( x, y, z ), as the position on the surface
		static Func<double, double, double, double[]> MakeShape( RockStyle style, int seed )
		{
			var planes = new List<double[]>();
			for ( int i = 0; i < style.planes; i ++ )
			{
				// fracture planes: random normals, biased toward the sides ( rocks break in slabs )
				double a = TerrainNoise.Hash2( seed, i, 1 ) * Math.PI * 2;
				double y = ( TerrainNoise.Hash2( seed, i, 2 ) * 2 - 1 ) * 0.85;
				double r = Math.Sqrt( 1 - y * y );
				double d = style.cut[ 0 ] + ( style.cut[ 1 ] - style.cut[ 0 ] ) * TerrainNoise.Hash2( seed, i, 3 );
				planes.Add( new[] { Math.Cos( a ) * r, y, Math.Sin( a ) * r, d } );
			}

			// a flat-ish base so the rock sits on the ground
			planes.Add( new[] { 0, - 1, 0, 0.62 + 0.15 * TerrainNoise.Hash2( seed, 99, 4 ) } );
			double sx = style.scale[ 0 ], sy = style.scale[ 1 ], sz = style.scale[ 2 ];
			return ( x, y, z ) =>
			{
				double rp = 1.6;
				foreach ( var p in planes )
				{
					double c = x * p[ 0 ] + y * p[ 1 ] + z * p[ 2 ];
					if ( c > 1e-3 ) rp = Math.Min( rp, p[ 3 ] / c );
				}

				// smooth-min with the unit sphere ( rounded, weathered edges )
				double k = style.soft;
				double h = Math.Max( k - Math.Abs( 1 - rp ), 0 ) / k;
				double rr = Math.Min( 1, rp ) - h * h * k * 0.25;
				rr += Fbm3( x * 2.1 + seed, y * 2.1, z * 2.1, 4, seed ) * style.rough;
				rr -= Math.Abs( Noise3( x * 5.3, y * 5.3 + seed, z * 5.3, seed + 3 ) ) * style.rough * 0.35;
				// ellipsoid proportions
				return new[] { x * rr * sx, y * rr * sy, z * rr * sz };
			};
		}

		public static RockMesh Build( int styleIndex, int seed, int subdiv )
		{
			var style = STYLES[ styleIndex ];
			var shape = MakeShape( style, seed );
			Icosphere( subdiv, out var verts, out var faces );
			int n = verts.Length / 3;
			var pos = new float[ n * 3 ];
			var rad = new float[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				var p = shape( verts[ i * 3 ], verts[ i * 3 + 1 ], verts[ i * 3 + 2 ] );
				pos[ i * 3 ] = ( float ) p[ 0 ]; pos[ i * 3 + 1 ] = ( float ) p[ 1 ]; pos[ i * 3 + 2 ] = ( float ) p[ 2 ];
				rad[ i ] = ( float ) Tidewater.Util.MathX.Hypot3( p[ 0 ], p[ 1 ], p[ 2 ] );
			}

			// cavity: vertices below the average of their neighbours are occluded
			var sum = new float[ n ]; var cnt = new ushort[ n ];
			for ( int f = 0; f < faces.Length; f += 3 )
				for ( int e = 0; e < 3; e ++ )
				{
					int a = faces[ f + e ], b = faces[ f + ( e + 1 ) % 3 ];
					sum[ a ] += rad[ b ]; cnt[ a ] ++;
					sum[ b ] += rad[ a ]; cnt[ b ] ++;
				}

			var ao = new float[ n ];
			for ( int i = 0; i < n; i ++ )
			{
				double d = ( double ) sum[ i ] / cnt[ i ] - rad[ i ];
				ao[ i ] = ( float ) Math.Max( 0.35, Math.Min( 1, 1 - d * 9 ) );
			}

			var g = new BufferGeometry();
			g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
			g.setIndex( faces );
			g.computeVertexNormals();
			return new RockMesh { pos = pos, nor = g.getAttribute( "normal" ).array, ao = ao, index = faces };
		}
	}
}
