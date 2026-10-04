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

		// southern stingray: rhombic disc that undulates; spotted eagle ray: pointed, swept wings that flap, a protruding head and a very long tail;
		// the eagle's outline is the planform of the modelled ray (eagleRayGeometry), which it replaces close up: this one is the far level of detail
		public static BufferGeometry rayGeometry( int lod = 0, bool eagle = false )
		{
			var A = new Acc();
			int nA = lod != 0 ? 20 : 44, nR = lod != 0 ? 4 : 9;
			// disc outline (right half, x >= 0, from the snout clockwise to the tail), radius at angle a (0 = straight ahead, PI / 2 = the right wing
			// tip) found by intersecting the polygon
			double[][] half = eagle ?
				new[] { new[] { 0, 0.27 }, new[] { 0.06, 0.265 }, new[] { 0.078, 0.22 }, new[] { 0.094, 0.17 }, new[] { 0.151, 0.12 }, new[] { 0.238, 0.07 }, new[] { 0.308, 0.02 }, new[] { 0.371, - 0.03 }, new[] { 0.433, - 0.08 }, new[] { 0.487, - 0.13 }, new[] { 0.5, - 0.171 }, new[] { 0.475, - 0.23 }, new[] { 0.083, - 0.28 }, new[] { 0, - 0.29 } } :
				new[] { new[] { 0, 0.43 }, new[] { 0.08, 0.37 }, new[] { 0.3, 0.17 }, new[] { 0.47, 0.02 }, new[] { 0.5, - 0.03 }, new[] { 0.44, - 0.12 }, new[] { 0.26, - 0.3 }, new[] { 0.12, - 0.4 }, new[] { 0, - 0.42 } };
			var poly = new List<double[]>( half );
			// half.slice( 1, -1 ).reverse().map( mirror )
			for ( int i = half.Length - 2; i >= 1; i -- ) poly.Add( new[] { - half[ i ][ 0 ], half[ i ][ 1 ] } );
			Func<double, double> outline = ( a ) =>
			{
				double dx = Math.Sin( a ), dz = Math.Cos( a );
				double best = 0;
				for ( int i = 0; i < poly.Count; i ++ )
				{
					var p = poly[ i ]; var q = poly[ ( i + 1 ) % poly.Count ];
					// ray (0,0) + t (dx, dz) against the segment p q
					double ex = q[ 0 ] - p[ 0 ], ez = q[ 1 ] - p[ 1 ];
					double den = dx * ez - dz * ex;
					if ( Math.Abs( den ) < 1e-9 ) continue;
					double t = ( p[ 0 ] * ez - p[ 1 ] * ex ) / den;
					double u = ( p[ 0 ] * dz - p[ 1 ] * dx ) / den;
					if ( t > 0 && u >= 0 && u <= 1 ) best = Math.Max( best, t );
				}

				return best;
			};

			double cz = eagle ? 0.23 : 0.07; // disc centre (the disc sits at the front of the total length)
			double thick = eagle ? 0.07 : 0.06;
			foreach ( int side in new[] { 1, - 1 } )
			{
				var rows = new List<int[]>();
				int centre = A.v( 0, side * thick * ( side > 0 ? 1 : 0.4 ), cz, 0.5 - cz, Part.DISC, 0, side );
				for ( int k = 1; k <= nR; k ++ )
				{
					double s = ( double ) k / nR;
					var row = new int[ nA ];
					for ( int j = 0; j < nA; j ++ )
					{
						double a = ( double ) j / nA * TAU;
						double r = outline( a ) * s;
						double x = Math.Sin( a ) * r, z = cz + Math.Cos( a ) * r * ( eagle ? 1 : 1 );
						// body dome over the middle, thin margins
						double body = Math.Exp( - ( x * x ) / ( eagle ? 0.012 : 0.02 ) - Math.Pow( ( z - cz ) / 0.28, 2 ) );
						double y = side * ( thick * body * ( side > 0 ? 1 : 0.45 ) + 0.004 * ( 1 - s ) );
						row[ j ] = A.v( x, y, z, 0.5 - z, Part.DISC, Math.Abs( x ) / 0.5, side );
					}

					rows.Add( row );
				}

				for ( int j = 0; j < nA; j ++ )
				{
					int j1 = ( j + 1 ) % nA;
					if ( side > 0 ) A.idx.AddRange( new[] { centre, rows[ 0 ][ j ], rows[ 0 ][ j1 ] } );
					else A.idx.AddRange( new[] { centre, rows[ 0 ][ j1 ], rows[ 0 ][ j ] } );
					for ( int k = 0; k < nR - 1; k ++ )
					{
						int a = rows[ k ][ j ], b = rows[ k ][ j1 ], c = rows[ k + 1 ][ j1 ], d = rows[ k + 1 ][ j ];
						if ( side > 0 ) A.idx.AddRange( new[] { a, d, b, b, d, c } );
						else A.idx.AddRange( new[] { a, b, d, b, c, d } );
					}
				}
			}

			// eyes on top
			if ( lod == 0 )
			{
				foreach ( int s in new[] { 1, - 1 } )
				{
					double ex = s * ( eagle ? 0.09 : 0.06 ), ez = cz + ( eagle ? 0.13 : 0.16 );
					tube( A, new[] { new[] { ex, thick * 0.7, ez + 0.012, 0.004 }, new[] { ex, thick * 0.85, ez, 0.013 }, new[] { ex, thick * 0.9, ez - 0.012, 0.004 } }, 6, Part.EYE, zero );
				}
			}

			// tail: long whip (the eagle ray's is three times the disc length)
			double tl = eagle ? 1.0 : 0.62;
			double tz0 = cz - ( eagle ? 0.29 : 0.4 );
			var tp = new List<double[]>();
			int nT = lod != 0 ? 4 : 10;
			for ( int i = 0; i <= nT; i ++ )
			{
				double t = ( double ) i / nT;
				tp.Add( new[] { 0, 0.01 - t * 0.01, tz0 - t * tl, ( eagle ? 0.014 : 0.026 ) * ( 1 - t ) + 0.002, 0.8 } );
			}

			tube( A, tp.ToArray(), lod != 0 ? 3 : 5, Part.WHIP, tailData );
			return A.build();
		}

		// The spotted eagle ray from the modelled asset (assets/eagle-ray.glb baked by tools/creatures/bake-eagle-ray.mjs into EagleRayData.js, here
		// EagleRayData.cs): the sculpted disc with its duckbill head (Part.DISC: the margins flap in the vertex shader), the straightened whip, the
		// dorsal and pelvic fins (Part.WHIP), with the game's own eyes at the model's eye positions. Two levels of detail (0: 8,000 triangles in the
		// disc, 1: 1,200); the procedural rayGeometry( lod, true ) is the far level. Frame as the other rays: nose +z, snout at z = 0.5, span 1.
		public static BufferGeometry eagleRayGeometry( int lod = 0 )
		{
			var bytes = Convert.FromBase64String( EagleRayData.lods[ Math.Min( lod, EagleRayData.lods.Length - 1 ) ] );
			int nV = ( int ) BitConverter.ToUInt32( bytes, 0 ), nT = ( int ) BitConverter.ToUInt32( bytes, 4 );
			var A = new Acc();
			int o = 8;
			for ( int i = 0; i < nV; i ++ )
			{
				double x = BitConverter.ToInt16( bytes, o ) / EagleRayData.scale, y = BitConverter.ToInt16( bytes, o + 2 ) / EagleRayData.scale, z = BitConverter.ToInt16( bytes, o + 4 ) / EagleRayData.scale;
				int part = bytes[ o + 6 ]; int w = ( sbyte ) bytes[ o + 7 ];
				o += 8;
				bool disc = part == ( int ) Part.DISC;
				// the whip and fins: x measured from where they leave the disc (the wave's envelope grows with x: the root stays put)
				double u = disc ? 0.5 - z : Math.Max( 0, 0.5 - z - EagleRayData.whipRoot ) * EagleRayData.whipK;
				A.v( x, y, z, u, part, disc ? Math.Min( 1, Math.Abs( x ) / 0.5 ) : 0, disc ? w : 1 );
			}

			for ( int i = 0; i < nT * 3; i ++ ) A.idx.Add( BitConverter.ToUInt16( bytes, o + i * 2 ) );
			if ( lod == 0 )
				foreach ( var e in EagleRayData.eyes )
					tube( A, new[] { new[] { e[ 0 ], e[ 1 ] + 0.012, e[ 2 ] + 0.012, 0.004 }, new[] { e[ 0 ], e[ 1 ] + 0.016, e[ 2 ], 0.013 }, new[] { e[ 0 ], e[ 1 ] + 0.012, e[ 2 ] - 0.012, 0.004 } }, 6, Part.EYE, zero );
			return A.build();
		}

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
