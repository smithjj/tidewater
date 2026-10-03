using System;

// Port of src/world/terrain/IslandShape.js: hand-authored large-scale structure of the island: the
// ridge skeleton of the volcanic massif, sea stacks off the headlands and the footpaths around the village.
namespace Tidewater.World.Terrain
{
	public sealed class FootPath
	{
		public double w;
		public double[][] pts;
	}

	public static class IslandShape
	{
		// Ridge skeleton: polylines of [x, z, crest height] (optional 4th value: half width, defaults
		// to 2.5 * height + 50 m). Each polyline is the max of its segments; polylines are combined
		// with a p-norm so junctions and saddles round off naturally. The cross profile is concave
		// (sharp crest, flanks easing out toward the valley floors).
		public static readonly double[][][] RIDGES = BuildRidges();

		static double[][][] BuildRidges()
		{
			double[][][] r =
			{
				// main east-west divide with the summit north of the bay
				new[] { P( - 480, - 360, 40 ), P( - 380, - 410, 110 ), P( - 250, - 460, 150 ), P( - 120, - 495, 178 ), P( - 35, - 510, 215 ), P( 50, - 495, 176 ), P( 150, - 470, 168 ), P( 270, - 430, 124 ), P( 400, - 360, 60 ), P( 470, - 320, 25 ) },
				// west headland spur
				new[] { P( - 250, - 460, 140 ), P( - 262, - 340, 100 ), P( - 272, - 200, 66 ), P( - 278, - 70, 48, 120 ), P( - 274, 60, 36, 100 ), P( - 264, 165, 20, 70 ) },
				// east headland spur
				new[] { P( 150, - 470, 155 ), P( 240, - 340, 108 ), P( 284, - 190, 70 ), P( 294, - 50, 50, 125 ), P( 294, 80, 36, 105 ), P( 290, 190, 18, 70 ) },
				// central spur behind the village
				new[] { P( - 35, - 505, 195 ), P( - 15, - 410, 138 ), P( 8, - 330, 80, 180 ), P( 22, - 275, 38, 110 ) },
				// minor spurs framing the valley
				new[] { P( - 150, - 485, 160 ), P( - 135, - 370, 104 ), P( - 118, - 280, 44, 120 ) },
				new[] { P( 105, - 485, 162 ), P( 140, - 370, 110 ), P( 162, - 275, 42, 120 ) },
				// north spurs
				new[] { P( - 110, - 500, 170 ), P( - 160, - 640, 110 ), P( - 200, - 770, 38 ) },
				new[] { P( 50, - 495, 168 ), P( 115, - 630, 112 ), P( 165, - 765, 38 ) },
				new[] { P( - 380, - 410, 100 ), P( - 440, - 560, 56 ) },
				new[] { P( 270, - 430, 115 ), P( 390, - 550, 52 ) },
			};

			// default half width: 2.5 * crest height + 50
			foreach ( var poly in r ) for ( int i = 0; i < poly.Length; i ++ )
				if ( poly[ i ].Length < 4 ) poly[ i ] = new[] { poly[ i ][ 0 ], poly[ i ][ 1 ], poly[ i ][ 2 ], 2.5 * poly[ i ][ 2 ] + 50 };
			return r;
		}

		static double[] P( double x, double z, double h ) => new[] { x, z, h };
		static double[] P( double x, double z, double h, double w ) => new[] { x, z, h, w };

		const double Pn = 6;
		const double CREST = 0.07;
		static readonly double CREST_N = Math.Sqrt( 1 + CREST * CREST ) - CREST;

		// smooth envelope height of the ridge skeleton at (x, z)
		public static double RidgeEnvelope( double x, double z )
		{
			double sum = 0;
			for ( int r = 0; r < RIDGES.Length; r ++ )
			{
				var pts = RIDGES[ r ];
				double best = 0;
				for ( int k = 0; k < pts.Length - 1; k ++ )
				{
					var a = pts[ k ]; var b = pts[ k + 1 ];
					double abx = b[ 0 ] - a[ 0 ], abz = b[ 1 ] - a[ 1 ];
					double t = ( ( x - a[ 0 ] ) * abx + ( z - a[ 1 ] ) * abz ) / ( abx * abx + abz * abz );
					t = t < 0 ? 0 : t > 1 ? 1 : t;
					double dx = x - ( a[ 0 ] + abx * t ), dz = z - ( a[ 1 ] + abz * t );
					double Wd = a[ 3 ] + ( b[ 3 ] - a[ 3 ] ) * t;
					double u0 = Math.Sqrt( dx * dx + dz * dz ) / Wd;
					if ( u0 >= 1 ) continue;
					// rounded crest (soft |u|), renormalised so u = 1 at the foot
					double u = ( Math.Sqrt( u0 * u0 + CREST * CREST ) - CREST ) / CREST_N;
					double q = 1 - u;
					double v = ( a[ 2 ] + ( b[ 2 ] - a[ 2 ] ) * t ) * q * q * ( 1 + 0.5 * u );
					if ( v > best ) best = v;
				}

				if ( best > 0 )
				{
					double b2 = best * best;
					sum += b2 * b2 * b2;
				}
			}

			return sum > 0 ? Math.Pow( sum, 1 / Pn ) : 0;
		}

		// Sea stacks off the headland tips: [x, z, radius, height]
		public static readonly double[][] SEA_STACKS =
		{
			new[] { - 272.0, 216, 9, 17 }, new[] { - 300.0, 196, 6, 11 }, new[] { - 244.0, 240, 5.5, 8 }, new[] { - 316.0, 152, 7, 13 }, new[] { - 286.0, 238, 4, 6 },
			new[] { 298.0, 252, 10, 19 }, new[] { 262.0, 246, 6, 10 }, new[] { 328.0, 228, 7, 12 }, new[] { 322.0, 186, 5, 9 }, new[] { 280.0, 272, 4.5, 7 },
		};

		static double[] XZ( double x, double z ) => new[] { x, z };

		// Footpaths (x, z polylines) with half widths. Worn into the ground (darkened + slightly sunken).
		public static readonly FootPath[] PATHS =
		{
			// beach at the spawn up to the front row and on to the plaza
			new FootPath { w = 1.1, pts = new[] { XZ( 21, - 56 ), XZ( 20, - 68 ), XZ( 18.5, - 80 ), XZ( 16.5, - 92 ), XZ( 15.5, - 100 ), XZ( 20, - 102.5 ), XZ( 28, - 104.5 ), XZ( 35.5, - 108.5 ) } },
			// plaza north up the valley and onto the central spur (hiking trail)
			new FootPath { w = 0.6, pts = new[] { XZ( 42, - 116.5 ), XZ( 45.5, - 126 ), XZ( 48, - 138 ), XZ( 49, - 150 ), XZ( 49.5, - 162 ), XZ( 47, - 175 ), XZ( 41, - 188 ), XZ( 36, - 199 ), XZ( 31, - 212 ), XZ( 33, - 224 ), XZ( 26, - 236 ), XZ( 20, - 247 ), XZ( 24, - 258 ), XZ( 16, - 270 ), XZ( 11, - 283 ), XZ( 15, - 296 ), XZ( 8, - 309 ), XZ( 4, - 322 ) } },
			// east: boathouse up to the boardwalk
			new FootPath { w = 0.9, pts = new[] { XZ( 88, - 62 ), XZ( 80, - 72 ), XZ( 70, - 80 ), XZ( 60, - 86 ), XZ( 54.5, - 90 ) } },
			// west: along the back of the beach to the western grove
			new FootPath { w = 0.8, pts = new[] { XZ( 18, - 62 ), XZ( 5, - 68 ), XZ( - 12, - 73 ), XZ( - 30, - 77 ), XZ( - 50, - 80 ), XZ( - 72, - 84 ) } },
		};

		// distance from (x, z) to a polyline; returns the distance and the arc length position of the closest point
		public static (double distance, double arc) PolylineDistance( double[][] pts, double x, double z )
		{
			double best = double.PositiveInfinity, acc = 0, bestArc = 0;
			for ( int k = 0; k < pts.Length - 1; k ++ )
			{
				var a = pts[ k ]; var b = pts[ k + 1 ];
				double abx = b[ 0 ] - a[ 0 ], abz = b[ 1 ] - a[ 1 ];
				double len2 = abx * abx + abz * abz;
				double t = ( ( x - a[ 0 ] ) * abx + ( z - a[ 1 ] ) * abz ) / len2;
				t = t < 0 ? 0 : t > 1 ? 1 : t;
				double dx = x - ( a[ 0 ] + abx * t ), dz = z - ( a[ 1 ] + abz * t );
				double d = dx * dx + dz * dz;
				double len = Math.Sqrt( len2 );
				if ( d < best )
				{
					best = d;
					bestArc = acc + t * len;
				}

				acc += len;
			}

			return ( Math.Sqrt( best ), bestArc );
		}
	}
}
