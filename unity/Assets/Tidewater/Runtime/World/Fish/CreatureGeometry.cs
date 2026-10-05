using System;
using System.Collections.Generic;
using Tidewater.Engine;

// Port of src/world/fish/CreatureGeometry.js. Rays and the green sea turtle, in the fish frame (nose +z, back +y, total length 1 from the
// snout at z = +0.5) so they share the fish shader and swimming data:
//  - rays: a flat disc (Part.DISC) whose margins undulate (stingray) or flap (eagle ray) in the vertex shader, eyes and spiracles on top, a
//    whip tail (Part.WHIP, swings sideways). aData: x = position along the body, z = distance from the midline (0 .. 1 at the wing tip),
//    w = 1 on the back, -1 on the belly.
//  - turtle: domed carapace (Part.CARAPACE, pattern coordinates in z / w), plastron, head and neck (Part.SKIN), flippers (Part.FLIPPER:
//    z = distance from the shoulder, w = flipper id 0 / 1 front left / right, 2 / 3 hind) that stroke about their shoulders.
namespace Tidewater.World.Fish
{
	public static class CreatureGeometry
	{
		const double TAU = Math.PI * 2;

		sealed class Acc
		{
			public readonly List<double> pos = new List<double>(), dat = new List<double>();
			public readonly List<int> idx = new List<int>();

			public int v( double x, double y, double z, double a, double b, double c, double d )
			{
				pos.Add( x ); pos.Add( y ); pos.Add( z );
				dat.Add( a ); dat.Add( b ); dat.Add( c ); dat.Add( d );
				return pos.Count / 3 - 1;
			}

			public BufferGeometry build()
			{
				var g = new BufferGeometry();
				g.setAttribute( "position", new BufferAttribute( pos, 3 ) );
				g.setAttribute( "aData", new BufferAttribute( dat, 4 ) );
				g.setIndex( idx );
				g.computeVertexNormals();
				g.computeBoundingSphere();
				return g;
			}
		}

		// Tapered tube along points (x, y, z, radius[, squash y]); part and aData z / w per point from fn( i, t ).
		static void tube( Acc A, double[][] pts, int sides, double part, Func<int, double, double[]> dataFn, bool caps = true )
		{
			var rows = new List<int[]>();
			for ( int i = 0; i < pts.Length; i ++ )
			{
				var p = pts[ i ]; var q = pts[ Math.Min( i + 1, pts.Length - 1 ) ]; var o = pts[ Math.Max( i - 1, 0 ) ];
				var d = new Vector3( q[ 0 ] - o[ 0 ], q[ 1 ] - o[ 1 ], q[ 2 ] - o[ 2 ] ).normalize();
				var s1 = new Vector3( 0, 1, 0 ).cross( d );
				if ( s1.lengthSq() < 1e-6 ) s1.set( 1, 0, 0 );
				s1.normalize();
				var s2 = new Vector3().crossVectors( d, s1 );
				var row = new int[ sides ];
				double t = ( double ) i / ( pts.Length - 1 );
				var dd = dataFn( i, t );
				for ( int j = 0; j < sides; j ++ )
				{
					double a = ( double ) j / sides * TAU;
					double r = p[ 3 ];
					row[ j ] = A.v(
						p[ 0 ] + ( s1.x * Math.Cos( a ) + s2.x * Math.Sin( a ) ) * r,
						p[ 1 ] + ( s1.y * Math.Cos( a ) + s2.y * Math.Sin( a ) ) * r * ( p.Length > 4 ? p[ 4 ] : 1 ),
						p[ 2 ] + ( s1.z * Math.Cos( a ) + s2.z * Math.Sin( a ) ) * r,
						0.5 - p[ 2 ], part, dd[ 0 ], dd[ 1 ] );
				}

				rows.Add( row );
			}

			for ( int i = 0; i < rows.Count - 1; i ++ ) for ( int j = 0; j < sides; j ++ )
			{
				int a = rows[ i ][ j ], b = rows[ i ][ ( j + 1 ) % sides ], c = rows[ i + 1 ][ ( j + 1 ) % sides ], d = rows[ i + 1 ][ j ];
				A.idx.AddRange( new[] { a, d, b, b, d, c } );
			}

			if ( caps )
			{
				var last = rows[ rows.Count - 1 ];
				var p = pts[ pts.Length - 1 ];
				var dd = dataFn( pts.Length - 1, 1 );
				int c = A.v( p[ 0 ], p[ 1 ], p[ 2 ], 0.5 - p[ 2 ], part, dd[ 0 ], dd[ 1 ] );
				for ( int j = 0; j < sides; j ++ ) A.idx.AddRange( new[] { last[ j ], c, last[ ( j + 1 ) % sides ] } );
			}
		}

		static readonly Func<int, double, double[]> zero = ( i, t ) => new double[] { 0, 0 };
		static readonly Func<int, double, double[]> tailData = ( i, t ) => new double[] { 0, 1 };

		// The rays are the modelled assets (assets/eagle-ray.glb and assets/stingray-family.glb, baked by tools/creatures/bake-eagle-ray.mjs and
		// bake-stingray-family.mjs into EagleRayData.js and StingrayData.js, here EagleRayData.cs and StingrayData.cs, four levels of detail each): the
		// sculpted disc (Part.DISC: the margins undulate or flap in the vertex shader), the straightened whip and the fins (Part.WHIP), with the game's
		// own eyes at the model's eye positions. Frame as the other creatures: nose +z, snout at z = 0.5, span 1, aData.x = 0.5 - z.
		static BufferGeometry bakedRayGeometry( string[] lods, double scale, double whipRoot, double whipK, double[][] eyes, int lod )
		{
			var bytes = Convert.FromBase64String( lods[ Math.Min( lod, lods.Length - 1 ) ] );
			int nV = ( int ) BitConverter.ToUInt32( bytes, 0 ), nT = ( int ) BitConverter.ToUInt32( bytes, 4 );
			var A = new Acc();
			int o = 8;
			for ( int i = 0; i < nV; i ++ )
			{
				double x = BitConverter.ToInt16( bytes, o ) / scale, y = BitConverter.ToInt16( bytes, o + 2 ) / scale, z = BitConverter.ToInt16( bytes, o + 4 ) / scale;
				int part = bytes[ o + 6 ]; int w = ( sbyte ) bytes[ o + 7 ];
				o += 8;
				bool disc = part == ( int ) Part.DISC;
				// the whip and fins: x measured from where they leave the disc (the wave's envelope grows with x: the root stays put)
				double u = disc ? 0.5 - z : Math.Max( 0, 0.5 - z - whipRoot ) * whipK;
				A.v( x, y, z, u, part, disc ? Math.Min( 1, Math.Abs( x ) / 0.5 ) : 0, disc ? w : 1 );
			}

			for ( int i = 0; i < nT * 3; i ++ ) A.idx.Add( BitConverter.ToUInt16( bytes, o + i * 2 ) );
			if ( lod == 0 )
				foreach ( var e in eyes )
					tube( A, new[] { new[] { e[ 0 ], e[ 1 ] + 0.012, e[ 2 ] + 0.012, 0.004 }, new[] { e[ 0 ], e[ 1 ] + 0.016, e[ 2 ], 0.013 }, new[] { e[ 0 ], e[ 1 ] + 0.012, e[ 2 ] - 0.012, 0.004 } }, 6, Part.EYE, zero );
			return A.build();
		}

		// The spotted eagle ray: the duckbill head, a whip three times the disc length, the dorsal and pelvic fins (8,000 triangles in the disc at
		// level 0, then 1,200, 350 and 140).
		public static BufferGeometry eagleRayGeometry( int lod = 0 ) => bakedRayGeometry( EagleRayData.lods, EagleRayData.scale, EagleRayData.whipRoot, EagleRayData.whipK, EagleRayData.eyes, lod );

		// The southern stingray: the blunt rhombic disc, a whip two and a quarter times its length, the pelvic fins (5,000 triangles in the disc at
		// level 0, then 900, 300 and 200). The three colourings of the glb are the fish material's.
		public static BufferGeometry stingrayGeometry( int lod = 0 ) => bakedRayGeometry( StingrayData.lods, StingrayData.scale, StingrayData.whipRoot, StingrayData.whipK, StingrayData.eyes, lod );

		// Green sea turtle (carapace length ~0.72 of the total length 1, head forward).
		public static BufferGeometry turtleGeometry( int lod = 0 )
		{
			var A = new Acc();
			double L = 0.72, W = 0.56, Hc = 0.2, zc = - 0.02;
			int nA = lod != 0 ? 14 : 30, nR = lod != 0 ? 3 : 7;
			// carapace: heart-shaped dome; plastron: flat below
			Func<double, double> rim = ( a ) =>
			{
				double s = Math.Sin( a ), c = Math.Cos( a );
				double r = 1 / Math.Sqrt( ( s * s ) / ( W * W / 4 ) + ( c * c ) / ( L * L / 4 ) );
				return r * ( 1 - 0.12 * Math.Pow( Math.Max( 0, - c ), 3 ) ) * ( 1 + 0.04 * Math.Max( 0, c ) );
			};

			foreach ( bool top in new[] { true, false } )
			{
				var rows = new List<int[]>();
				int centre = A.v( 0, top ? Hc : - 0.07, zc, 0.5 - zc, top ? Part.CARAPACE : Part.SKIN, 0, top ? 0 : 2 );
				for ( int k = 1; k <= nR; k ++ )
				{
					double s = ( double ) k / nR;
					var row = new int[ nA ];
					for ( int j = 0; j < nA; j ++ )
					{
						double a = ( double ) j / nA * TAU;
						double r = rim( a ) * s;
						double x = Math.Sin( a ) * r, z = zc + Math.Cos( a ) * r;
						double y = top ? Hc * Math.Pow( 1 - s * s, 0.6 ) * ( 1 - 0.1 * Math.Cos( a ) ) - 0.02 * s * s : - 0.07 * ( 1 - s * s * s ) - 0.015;
						row[ j ] = A.v( x, y, z, 0.5 - z, top ? Part.CARAPACE : Part.SKIN, x / ( W / 2 ), top ? ( z - zc ) / ( L / 2 ) : 2 );
					}

					rows.Add( row );
				}

				for ( int j = 0; j < nA; j ++ )
				{
					int j1 = ( j + 1 ) % nA;
					if ( top ) A.idx.AddRange( new[] { centre, rows[ 0 ][ j ], rows[ 0 ][ j1 ] } );
					else A.idx.AddRange( new[] { centre, rows[ 0 ][ j1 ], rows[ 0 ][ j ] } );
					for ( int k = 0; k < nR - 1; k ++ )
					{
						int a = rows[ k ][ j ], b = rows[ k ][ j1 ], c = rows[ k + 1 ][ j1 ], d = rows[ k + 1 ][ j ];
						if ( top ) A.idx.AddRange( new[] { a, d, b, b, d, c } );
						else A.idx.AddRange( new[] { a, b, d, b, c, d } );
					}
				}
			}

			// head and neck (w = 1: head skin)
			int sides = lod != 0 ? 5 : 9;
			tube( A, new[] {
				new[] { 0, 0.0, zc + L / 2 - 0.08, 0.075, 0.75 }, new[] { 0, 0.01, zc + L / 2 + 0.02, 0.065, 0.8 }, new[] { 0, 0.02, zc + L / 2 + 0.1, 0.07, 0.85 },
				new[] { 0, 0.02, zc + L / 2 + 0.16, 0.06, 0.8 }, new[] { 0, 0.0, zc + L / 2 + 0.2, 0.03, 0.7 },
			}, sides, Part.SKIN, tailData );
			// eyes
			if ( lod == 0 ) foreach ( int s in new[] { 1, - 1 } )
			{
				double ex = s * 0.052, ez = zc + L / 2 + 0.13;
				tube( A, new[] { new[] { ex, 0.035, ez + 0.012, 0.004 }, new[] { ex * 1.08, 0.037, ez, 0.013 }, new[] { ex, 0.035, ez - 0.012, 0.004 } }, 6, Part.EYE, zero );
			}

			// flippers: flattened tapered tubes from the shoulders (w = flipper id, z = distance along)
			Action<int, double, double, double[], double, double> flipper = ( id, x0, z0, dir, len, width ) =>
			{
				var pts = new List<double[]>();
				int n = lod != 0 ? 4 : 8;
				for ( int i = 0; i <= n; i ++ )
				{
					double t = ( double ) i / n;
					// paddle: widest a third of the way out, curving back toward the tip
					double w = width * ( 0.55 + 0.9 * Math.Sin( Math.PI * Math.Min( 1, 0.15 + t * 0.95 ) ) ) * ( 1 - 0.7 * t * t );
					double x = x0 + dir[ 0 ] * len * t, z = z0 + dir[ 2 ] * len * t - len * 0.25 * t * t;
					pts.Add( new[] { x, - 0.03 - t * 0.02, z, w, 0.22 } );
				}

				tube( A, pts.ToArray(), lod != 0 ? 4 : 7, Part.FLIPPER, ( i, t ) => new double[] { t, id } );
			};

			double s2 = Math.Sqrt( 0.5 ); // Math.SQRT1_2
			flipper( 0, 0.2, zc + 0.2, new[] { s2 * 1.2, 0, s2 * 0.2 }, 0.48, 0.07 );
			flipper( 1, - 0.2, zc + 0.2, new[] { - s2 * 1.2, 0, s2 * 0.2 }, 0.48, 0.07 );
			flipper( 2, 0.16, zc - 0.28, new[] { 0.8, 0, - 0.6 }, 0.2, 0.06 );
			flipper( 3, - 0.16, zc - 0.28, new[] { - 0.8, 0, - 0.6 }, 0.2, 0.06 );
			// short tail
			tube( A, new[] { new[] { 0, - 0.02, zc - L / 2 + 0.02, 0.035, 0.6 }, new[] { 0, - 0.03, zc - L / 2 - 0.08, 0.004, 0.6 } }, lod != 0 ? 3 : 5, Part.SKIN, tailData );
			return A.build();
		}
	}
}
