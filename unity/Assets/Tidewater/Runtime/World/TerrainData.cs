using System;
using System.Collections.Generic;
using System.Diagnostics;
using Tidewater.Util;
using Tidewater.World.Terrain;
using UnityEngine;
using static Tidewater.Util.MathX;
using static Tidewater.World.Terrain.TerrainNoise;
using static Tidewater.World.Terrain.IslandShape;

// Port of src/world/TerrainData.js (same structure, names and constants; the JS comments are kept).
// Numerics: all arithmetic is in doubles as in JS, grids are float[] / byte[] like the Float32Array /
// Uint8Array they replace, so every store rounds exactly where the JS rounds.
namespace Tidewater.World
{
	public sealed class RockSite
	{
		public double x, z, r, h;
		public string kind;
	}

	public sealed class Pad
	{
		public double x, z, radius, height;
	}

	// CPU-side procedural island heightmap plus bilinear queries.
	//
	// A volcanic high island: a hand-placed ridge skeleton (summit massif with rock plugs, spurs
	// framing the village valley and the two headlands) carved by slope-aligned erosion noise into
	// branching gullies and knife-edge ridges; sea cliffs, wave-cut platforms and sea stacks on the
	// rocky coast; the bay keeps its tuned beach / surf-zone profile (only cusps, a zero-mean bar and
	// dunes behind the beach are added).
	//
	// Generation runs on three grids:
	//   A (8 m)  large, smooth fields: coast warp, deep-sea variation, the ridge envelope of the massif
	//   B (2 m)  coast distance, beach / seabed profile (unchanged in the bay), coastal cliffs, the massif
	//            carved by slope-aligned erosion noise, volcanic plugs
	//   C (1 m)  Catmull-Rom upsample + fine relief (outcrops, crags, layered cliffs), sea stacks, rock
	//            platforms, beach cusps / bar / berm, dune ridges, footpaths, seabed seagrass and
	//            rubble, then the rock / sand / path / gully masks
	public sealed class TerrainData
	{
		// large-scale fields at a point (JS: the _F object)
		public sealed class Fields
		{
			public double wx, wz, f170, und, deep, E, gx, gz;
		}

		// grid B profile at a point (JS: the _out object)
		public sealed class BaseOut
		{
			public double h, rock, d, bz, carve;
		}

		public struct MinMaxLevel
		{
			public int n;
			public float[] min, max;
		}

		// volcanic plugs on the summit ridge: [x, z, radius, height above the envelope]
		static readonly double[][] PLUGS = { new[] { - 42.0, - 505, 72, 62 }, new[] { 152.0, - 466, 46, 38 }, new[] { - 238.0, - 458, 40, 26 } };
		const double VILLAGE_X = WorldLayout.Village.x, VILLAGE_Z = WorldLayout.Village.z;
		const int RES = 2048; // 1 m texels over the 2048 m domain

		static double Smin( double a, double b, double k )
		{
			double h = Clamp( 0.5 + 0.5 * ( b - a ) / k, 0, 1 );
			return Lerp( b, a, h ) - k * h * ( 1 - h );
		}

		static double EllipseDist( double x, double z, double cx, double cz, double rx, double rz )
		{
			double dx = ( x - cx ) / rx, dz = ( z - cz ) / rz;
			return ( Math.Sqrt( dx * dx + dz * dz ) - 1 ) * Math.Min( rx, rz );
		}

		// central bay: the beach and its underwater profile stay smooth (the surf is tuned for it)
		static double BeachZoneAt( double x, double z ) => ( 1 - Smoothstep( 120, 205, Math.Abs( x - 10 ) ) ) * ( 1 - Smoothstep( 40, 140, z + 40 ) ) * Smoothstep( - 260, - 120, z );

		public readonly double size;
		public readonly int res;
		public readonly double texel;
		public readonly double origin;
		readonly Noise2D noise, noise2, noise3;
		public float[] heights;
		public float[] rock; // rockiness mask (0 sand/soil .. 1 bare rock)
		public readonly byte[] sand; // loose sand cover (beach, dunes, seabed)
		public readonly byte[] path; // worn footpaths
		public readonly byte[] gully; // drainage lines carved by the erosion noise
		public readonly byte[] seagrass; // seagrass meadows on the shallow seabed (1.5 - 12 m deep)
		public readonly byte[] rubble; // dark coral rubble / rock heads on the seabed
		public readonly byte[] scarp; // face of the eroded embankment behind the bay beach
		public readonly List<RockSite> rockSites = new List<RockSite>(); // outcrops / stacks for the rock scatter
		public readonly List<Pad> pads = new List<Pad>(); // building pads flattened by the village (trampled ground in the splat map)
		public readonly FootPath[] paths = PATHS;
		public readonly Dictionary<string, long> timings = new Dictionary<string, long>();
		readonly Fields _F = new Fields();
		readonly BaseOut _out = new BaseOut();

		public int mmTile, mmN;
		public float[] mmMin, mmMax;
		public List<MinMaxLevel> mmLevels;

		public TerrainData( int seed = 7 )
		{
			size = WorldLayout.TerrainSize;
			res = RES;
			texel = size / res;
			origin = - size / 2;
			noise = new Noise2D( seed );
			noise2 = new Noise2D( seed * 31 + 5 );
			noise3 = new Noise2D( seed * 131 + 17 );
			int n = res * res;
			heights = new float[ n ];
			rock = new float[ n ];
			sand = new byte[ n ];
			path = new byte[ n ];
			gully = new byte[ n ];
			seagrass = new byte[ n ];
			rubble = new byte[ n ];
			scarp = new byte[ n ];
			Generate();
			BuildMinMax();
		}

		// signed coast distance (m): > 0 water, < 0 land
		public (double d, double beachZone) CoastDistance( double x, double z )
		{
			var F = _fields( x, z, false );
			double beachZone = BeachZoneAt( x, z );
			return ( _coast( x, z, F, beachZone ), beachZone );
		}

		// height before the 1 m detail pass (analytic, slow path of the grid pipeline)
		public (double h, double rock) HeightFn( double x, double z )
		{
			var F = _fields( x, z, true );
			var o = _base( x, z, F, _out );
			return ( o.h, o.rock );
		}

		// ------------------------------------------------------------------ generation

		// large-scale fields at a point (grid A computes the same on an 8 m lattice)
		Fields _fields( double x, double z, bool withMassif, Fields F = null )
		{
			F = F ?? _F;
			var n = noise;
			double ox = n.Fbm( x / 300, z / 300, 3 ) * 60, oz = n.Fbm( x / 300 + 7.1, z / 300 - 3.3, 3 ) * 60;
			F.wx = x + ox; F.wz = z + oz;
			F.f170 = n.Fbm( F.wx / 170, F.wz / 170, 4 );
			F.und = n.Fbm( x / 260, z / 90, 2 );
			F.deep = n.Fbm( x / 220, z / 220, 4 );
			if ( withMassif )
			{
				F.E = _envelope( x, z );
				F.gx = ( _envelope( x + 8, z ) - _envelope( x - 8, z ) ) / 16;
				F.gz = ( _envelope( x, z + 8 ) - _envelope( x, z - 8 ) ) / 16;
			}

			return F;
		}

		double _envelope( double x, double z )
		{
			var n = noise; var n3 = noise3;
			double wx = x + n.Fbm( x / 300, z / 300, 3 ) * 60 + n3.Fbm( x / 130, z / 130, 2 ) * 28;
			double wz = z + n.Fbm( x / 300 + 7.1, z / 300 - 3.3, 3 ) * 60 + n3.Fbm( x / 130 + 5.2, z / 130 - 1.7, 2 ) * 28;
			return RidgeEnvelope( wx, wz );
		}

		double _coast( double x, double z, Fields F, double bz )
		{
			double dBody = EllipseDist( x, z, 0, - 442, 565, 400 );
			double dW = EllipseDist( x, z, - 272, - 25, 92, 205 );
			double dE = EllipseDist( x, z, 288, - 12, 108, 228 );
			double d = Smin( dBody, Smin( dW, dE, 40 ), 75 );
			d += F.f170 * 34 * ( 1 - 0.9 * bz );
			if ( bz < 1 ) d += noise.Fbm( x / 38, z / 38, 3 ) * 7 * ( 1 - bz );
			d += F.und * 5 * bz; // gentle beach undulation
			return d;
		}

		// grid B profile at a point
		BaseOut _base( double x, double z, Fields F, BaseOut outp )
		{
			var n = noise; var n2 = noise2;
			double bz = BeachZoneAt( x, z );
			double d = _coast( x, z, F, bz );

			// rocky headlands and outer coast
			double headland = Math.Max(
				1 - Smoothstep( 60, 140, Hypot( ( x + 272 ) * 0.9, ( z + 25 ) * 0.45 ) ),
				1 - Smoothstep( 60, 150, Hypot( ( x - 288 ) * 0.9, ( z + 12 ) * 0.45 ) ) );
			double rm = Math.Max( headland, 1 - bz * 1.4 );
			double rock = 0;
			if ( rm > 0.35 ) rock = Smoothstep( 0.35, 0.75, Clamp( rm * ( 0.55 + 0.45 * n2.Fbm( x / 60, z / 60, 3 ) ), 0, 1 ) );

			double h, carve = 0;

			if ( d >= 0 )
			{
				double depth;
				if ( d < 70 ) depth = d * 0.05;
				else if ( d < 260 ) depth = 3.5 + ( d - 70 ) * 0.06;
				else if ( d < 700 ) depth = 14.9 + ( d - 260 ) * 0.07;
				else depth = 45.7 + ( d - 700 ) * 0.05;
				depth = Math.Min( depth, 85 );
				depth *= Lerp( 1, 2.4, rock * ( 1 - Smoothstep( 150, 400, d ) ) );

				// seabed variation (ripples skipped far offshore where nobody can see them)
				double sandRipple = d < 420 ? n.Fbm( x / 45, z / 45, 3 ) * 0.6 * ( 1 - Smoothstep( 380, 420, d ) ) : 0;
				double deepVar = F.deep * 6 * Smoothstep( 60, 400, d );
				h = - depth + sandRipple * Smoothstep( 8, 40, d ) + deepVar;

				// rocky seabed near cliffs
				if ( rock > 0 && d < 300 ) h += rock * n2.Ridged( x / 25, z / 25, 4 ) * 5 * Smoothstep( 0, 20, d ) * ( 1 - Smoothstep( 120, 300, d ) );

				// coral reef platform
				double rd = Hypot( x - WorldLayout.Reef.x, z - WorldLayout.Reef.z );
				double reefMask = 1 - Smoothstep( WorldLayout.Reef.radius * 0.35, WorldLayout.Reef.radius, rd );
				if ( reefMask > 0 )
				{
					double bumps = n2.Ridged( x / 11, z / 11, 4 ) * 2.8 + n.Fbm( x / 5, z / 5, 2 ) * 0.5;
					h += reefMask * ( 1.2 + bumps );
					h = Math.Min( h, - 1.4 );
				}

				// keep the swim/boat channel along the pier sandy
				double px = WorldLayout.Pier.x;
				double pierMask = ( 1 - Smoothstep( 6, 16, Math.Abs( x - px - 4 ) ) ) * Smoothstep( - 60, - 20, z );
				if ( pierMask > 0 ) h = Lerp( h, - depth + sandRipple * 0.3, pierMask * 0.8 );
			}
			else
			{
				double e = - d;
				double beach;
				if ( e < 38 ) beach = e * 0.068;
				else if ( e < 70 ) beach = 2.584 + ( e - 38 ) * 0.03;
				else if ( e < 230 ) beach = 3.544 + ( e - 70 ) * 0.075;
				else beach = 15.544 + ( e - 230 ) * 0.04;

				double dunes = e > 25 && e < 240 ? Smoothstep( 25, 60, e ) * ( 1 - Smoothstep( 120, 240, e ) ) * n2.Fbm( x / 30, z / 30, 3 ) * 0.9 : 0;
				h = beach + dunes;

				// sea cliffs on the rocky coast
				double cliffZone = rock * ( 1 - Smoothstep( 90, 170, e ) );
				if ( cliffZone > 0 )
				{
					double cliff = Math.Min( e * 1.6, 18 + n2.Ridged( x / 70, z / 70, 4 ) * 22 ) + n2.Fbm( x / 12, z / 12, 3 ) * 2.5;
					h = Lerp( h, Math.Max( h, cliff ), cliffZone );
				}

				// the massif, carved by slope-aligned erosion noise; the village valley stays open
				// (an elongated valley reaching north into the massif, like the bays of Moorea)
				double bay = 1 - Smoothstep( 120, 230, Math.Abs( x - 22 - ( z + 100 ) * 0.08 + n.Noise( z / 120, 3.7 ) * 30 ) );
				double open = bay * ( 1 - Smoothstep( 70, 340, e + n.Noise( x / 80, z / 80 ) * 30 ) );
				if ( F.E > 0.01 && open < 1 )
				{
					double m = F.E;
					double slope = Math.Sqrt( F.gx * F.gx + F.gz * F.gz );
					if ( slope > 0.005 )
					{
						// meandering: warp the lookup a little so the gullies do not run dead straight
						double mx = x + n.Noise( x / 45, z / 45 ) * 14, mz = z + n.Noise( x / 45 + 9.1, z / 45 - 4.3 ) * 14;
						carve = ErosionNoise( mx, mz, F.gx, F.gz, 5, 160, Math.Min( 30, 0.16 * F.E ) );
						m += carve;
					}

					// volcanic plugs: sheer rock towers crowning the summit ridge
					foreach ( var plug in PLUGS )
					{
						double px = plug[ 0 ], pz = plug[ 1 ], R = plug[ 2 ], H = plug[ 3 ];
						double dx = x - px, dz = z - pz;
						double r2 = dx * dx + dz * dz;
						if ( r2 > R * R * 1.8 ) continue;
						double a = Math.Atan2( dz, dx );
						double ca = Math.Cos( a ), sa = Math.Sin( a );
						// lobed outline with vertical fluting (columnar jointing)
						double flute = Math.Abs( n2.Noise( ca * 4.5 + px * 0.1, sa * 4.5 ) ) * 0.14;
						double rr = R * ( 1 + 0.2 * n.Noise( ca * 1.3 + px, sa * 1.3 + pz ) + 0.08 * n2.Noise( x / 17, z / 17 ) - flute );
						double u = Math.Sqrt( r2 ) / rr;
						// sheer walls, a talus apron at the foot and a craggy crown
						double wall = Smoothstep( 1.0, 0.72, u );
						double apron = ( 1 - Smoothstep( 0.9, 1.3, u ) ) * 0.12;
						double crown = 1 - 0.35 * u * u + n2.Ridged( x / 19, z / 19, 3 ) * 0.35 - 0.15;
						m += H * ( wall * crown + apron );
					}

					// where the massif meets the sea it drops as a sea cliff (steep on the rocky coast,
					// gentle behind beaches) so heights stay continuous across the shoreline
					double S = 2.4 - 1.9 * bz;
					m *= 1 - open;
					m -= SoftRamp( m - e * S - n2.Fbm( x / 9, z / 9, 2 ) * 1.5 * ( 1 - bz ), 3 );
					h = h + SoftRamp( m - h, 6 );
				}
			}

			outp.h = h; outp.rock = rock; outp.d = d; outp.bz = bz; outp.carve = carve;
			return outp;
		}

		void Generate()
		{
			var sw = Stopwatch.StartNew();
			long last = 0;
			void tick( string name )
			{
				long now = sw.ElapsedMilliseconds;
				timings[ name ] = now - last;
				last = now;
			}

			double o = origin;
			var n = noise; var n3 = noise3;

			// ---- grid A: 8 m
			const int NA = 256;
			double TA = size / NA;
			var Awx = new float[ NA * NA ]; var Awz = new float[ NA * NA ];
			var Af170 = new float[ NA * NA ]; var Aund = new float[ NA * NA ]; var Adeep = new float[ NA * NA ];
			var AE = new float[ NA * NA ];
			for ( int j = 0; j < NA; j ++ )
			{
				double z = o + ( j + 0.5 ) * TA;
				for ( int i = 0; i < NA; i ++ )
				{
					double x = o + ( i + 0.5 ) * TA;
					int k = j * NA + i;
					double ox = n.Fbm( x / 300, z / 300, 3 ) * 60, oz = n.Fbm( x / 300 + 7.1, z / 300 - 3.3, 3 ) * 60;
					double wx = x + ox, wz = z + oz;
					Awx[ k ] = ( float ) ox; Awz[ k ] = ( float ) oz;
					Af170[ k ] = ( float ) n.Fbm( wx / 170, wz / 170, 4 );
					Aund[ k ] = ( float ) n.Fbm( x / 260, z / 90, 2 );
					Adeep[ k ] = ( float ) n.Fbm( x / 220, z / 220, 4 );
					// massif envelope only over the island body
					if ( EllipseDist( x, z, 0, - 442, 640, 470 ) < 0 || EllipseDist( x, z, - 272, - 25, 160, 280 ) < 0 || EllipseDist( x, z, 288, - 12, 170, 300 ) < 0 )
					{
						AE[ k ] = ( float ) RidgeEnvelope( wx + n3.Fbm( x / 130, z / 130, 2 ) * 28, wz + n3.Fbm( x / 130 + 5.2, z / 130 - 1.7, 2 ) * 28 );
					}
				}
			}

			// envelope gradient
			var Agx = new float[ NA * NA ]; var Agz = new float[ NA * NA ];
			for ( int j = 1; j < NA - 1; j ++ ) for ( int i = 1; i < NA - 1; i ++ )
			{
				int k = j * NA + i;
				Agx[ k ] = ( float ) ( ( ( double ) AE[ k + 1 ] - AE[ k - 1 ] ) / ( 2 * TA ) );
				Agz[ k ] = ( float ) ( ( ( double ) AE[ k + NA ] - AE[ k - NA ] ) / ( 2 * TA ) );
			}

			tick( "gridA" );

			// ---- grid B: 2 m
			int NB = RES / 2;
			double TB = size / NB;
			var Bh = new float[ NB * NB ]; var Brock = new float[ NB * NB ]; var Bd = new float[ NB * NB ];
			var Bbz = new float[ NB * NB ]; var Bcarve = new float[ NB * NB ];
			var F = new Fields();
			var outp = new BaseOut();
			for ( int j = 0; j < NB; j ++ )
			{
				double z = o + ( j + 0.5 ) * TB;
				for ( int i = 0; i < NB; i ++ )
				{
					double x = o + ( i + 0.5 ) * TB;
					F.wx = x + SampleGrid( Awx, NA, o, TA, x, z );
					F.wz = z + SampleGrid( Awz, NA, o, TA, x, z );
					F.f170 = SampleGrid( Af170, NA, o, TA, x, z );
					F.und = SampleGrid( Aund, NA, o, TA, x, z );
					F.deep = SampleGrid( Adeep, NA, o, TA, x, z );
					F.E = SampleGrid( AE, NA, o, TA, x, z );
					if ( F.E > 0.01 )
					{
						F.gx = SampleGrid( Agx, NA, o, TA, x, z );
						F.gz = SampleGrid( Agz, NA, o, TA, x, z );
					}

					_base( x, z, F, outp );
					int k = j * NB + i;
					Bh[ k ] = ( float ) outp.h; Brock[ k ] = ( float ) outp.rock; Bd[ k ] = ( float ) outp.d; Bbz[ k ] = ( float ) outp.bz; Bcarve[ k ] = ( float ) outp.carve;
				}
			}

			tick( "gridB" );

			// ---- grid C: 1 m
			var H = Upsample2( Bh, NB, true );
			var D = Upsample2( Bd, NB, false );
			var R0 = Upsample2( Brock, NB, false );
			var BZ = Upsample2( Bbz, NB, false );
			var CV = Upsample2( Bcarve, NB, false );
			heights = H;
			tick( "upsample" );

			_detail( H, D, R0, BZ );
			tick( "detail" );

			_features( H, D, R0, BZ );
			tick( "features" );

			_scarp( H, D, R0, BZ );
			tick( "scarp" );

			_seabed( H, R0 );
			tick( "seabed" );

			_masks( H, D, R0, BZ, CV );
			// soften the 1 m masks so material transitions never show the texel grid
			rock = BoxBlur( rock, RES, RES, 1 );
			tick( "masks" );

			// fade to deep ocean floor at the domain border
			int rs = RES;
			for ( int j = 0; j < rs; j ++ )
			{
				if ( Math.Min( j, rs - 1 - j ) > 121 )
				{
					for ( int i = 0; i < 122; i ++ ) _fadeBorder( H, i, j );
					for ( int i = rs - 122; i < rs; i ++ ) _fadeBorder( H, i, j );
				}
				else
				{
					for ( int i = 0; i < rs; i ++ ) _fadeBorder( H, i, j );
				}
			}

			tick( "border" );
			long total = 0;
			foreach ( var v in timings.Values ) total += v;
			timings[ "total" ] = total;
		}

		void _fadeBorder( float[] H, int i, int j )
		{
			int rs = RES;
			double e = Math.Min( Math.Min( i, j ), Math.Min( rs - 1 - i, rs - 1 - j ) ) * texel;
			double t = Smoothstep( 0, 120, e );
			int k = j * rs + i;
			H[ k ] = ( float ) Lerp( - 90, H[ k ], t );
		}

		// 1 m relief on land: micro undulation and rock outcrops breaking through the slopes
		void _detail( float[] H, float[] D, float[] R0, float[] BZ )
		{
			int rs = RES; double o = origin; var n2 = noise2; var n3 = noise3;
			for ( int j = 1; j < rs - 1; j ++ )
			{
				double z = o + j + 0.5;
				if ( z > 320 || z < - 900 ) continue;
				for ( int i = 1; i < rs - 1; i ++ )
				{
					int k = j * rs + i;
					double h = H[ k ];
					if ( h < - 6 ) continue;
					double x = o + i + 0.5;
					double d = D[ k ];
					double e = - d;
					double bz = BZ[ k ];
					// keep the bay beach, its dunes and the village smooth
					double vd = Hypot( x - VILLAGE_X, z - VILLAGE_Z );
					double calm = Math.Max( bz * ( 1 - Smoothstep( 70, 130, e ) ), 1 - Smoothstep( 105, 150, vd ) );
					double wild = 1 - calm;
					if ( wild <= 0.01 ) continue;
					double gx = ( double ) H[ k + 1 ] - H[ k - 1 ], gz = ( double ) H[ k + rs ] - H[ k - rs ];
					double slope = Math.Sqrt( gx * gx + gz * gz ) * 0.5;

					// soft undulation on land (creep, root mounds)
					double add = 0;
					if ( e > 4 ) add += n3.Fbm( x / 17, z / 17, 2 ) * 0.28 * Smoothstep( 4, 30, e );

					double r0 = R0[ k ];

					// crags: broken, sharp-crested rock along the rocky waterline
					if ( r0 > 0.35 && e > - 25 && e < 40 )
					{
						double crag = ( n2.Ridged( x / 11 + 1.3, z / 11 - 4.4, 3 ) - 0.45 ) * 1.6 + ( n3.Ridged( x / 4.3 + 8.8, z / 4.3, 2 ) - 0.45 ) * 0.5;
						add += crag * Smoothstep( 0.35, 0.7, r0 ) * Smoothstep( - 3, 1.5, h ) * ( 1 - Smoothstep( 15, 38, e ) );
					}

					// layered cliffs: steep rocky ground steps into ledges and risers (lava flows)
					// (sea cliffs and headlands only; patchy so the bands never run for long)
					double layered = Smoothstep( 0.45, 0.9, slope ) * r0 * ( 1 - Smoothstep( 60, 140, e ) ) * Smoothstep( 1, 5, h ) *
						Smoothstep( - 0.1, 0.35, n3.Noise( x / 53 + 4.1, z / 53 ) );
					if ( layered > 0.02 )
					{
						// big, irregular ledges (6-11 m lifts) rather than fine steps that read as noise
						double warp = n2.Noise( x / 41, z / 41 ) * 5 + n3.Noise( x / 17, z / 17 ) * 1.5;
						double step = 6 + 5 * ( 0.5 + 0.5 * n3.Noise( x / 97, z / 97 ) );
						double t = ( h + warp ) / step;
						double f = Math.Floor( t ), fr = t - f;
						double ledge = ( f + Smoothstep( 0.25, 0.9, fr ) ) * step - warp;
						add += ( ledge - h ) * 0.3 * layered;
					}

					// rock outcrops: blocky bumps on steeper ground and around the rocky coast
					double want = Math.Max( Smoothstep( 0.35, 0.8, slope ), r0 * 0.8 );
					if ( want > 0.05 )
					{
						// steep walls, a gently domed top and joints splitting the outcrop into blocks
						double rid = n2.Fbm( x / 24 + 3.1, z / 24 - 7.7, 3 ) + n3.Noise( x / 6.3, z / 6.3 ) * 0.08;
						double wall = Smoothstep( 0.3, 0.36, rid );
						if ( wall > 0 )
						{
							double dome = Smoothstep( 0.36, 0.6, rid );
							double joint = 1 - Smoothstep( 0.03, 0.09, Math.Abs( n3.Noise( x / 4.6 + 7.3, z / 4.6 - 1.9 ) ) );
							add += want * ( 1.5 + 1.5 * r0 ) * ( wall * 0.7 + dome * 0.45 ) * ( 1 - 0.3 * joint * wall );
						}
					}

					H[ k ] = ( float ) ( h + add * wild );
				}
			}
		}

		// discrete features: sea stacks, wave-cut rock platforms, beach cusps / bars, dune ridges, paths
		void _features( float[] H, float[] D, float[] R0, float[] BZ )
		{
			int rs = RES; double o = origin; var n = noise; var n2 = noise2; var n3 = noise3;

			// ---- sea stacks: steep columns with a rubble skirt
			foreach ( var st in SEA_STACKS )
			{
				double sx = st[ 0 ], sz = st[ 1 ], r = st[ 2 ], hgt = st[ 3 ];
				double R = r * 2.4;
				int i0 = ( int ) Math.Max( 1, Math.Floor( sx - R - o ) ), i1 = ( int ) Math.Min( rs - 2, Math.Ceiling( sx + R - o ) );
				int j0 = ( int ) Math.Max( 1, Math.Floor( sz - R - o ) ), j1 = ( int ) Math.Min( rs - 2, Math.Ceiling( sz + R - o ) );
				for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
				{
					double x = o + i + 0.5, z = o + j + 0.5;
					double dx = x - sx, dz = z - sz;
					double a = Math.Atan2( dz, dx );
					double rr = r * ( 1 + 0.22 * n3.Noise( Math.Cos( a ) * 1.7 + sx, Math.Sin( a ) * 1.7 + sz ) + 0.08 * n2.Noise( x / 2.5, z / 2.5 ) );
					double u = Hypot( dx, dz ) / rr;
					int k = j * rs + i;
					// column: near vertical faces, rounded shoulder, craggy top
					double col = Smoothstep( 1.0, 0.82, u );
					double top = hgt * ( 1 - 0.25 * u * u ) + n2.Ridged( x / 6, z / 6, 3 ) * 2.2;
					double skirt = ( 1 - Smoothstep( 0.9, 2.4, u ) ) * ( 1.6 + n2.Ridged( x / 4, z / 4, 2 ) * 1.4 );
					double bs = H[ k ];
					double h = bs + skirt;
					h = Lerp( h, Math.Max( h, top ), col );
					H[ k ] = ( float ) h;
					R0[ k ] = ( float ) Math.Max( R0[ k ], 1 - Smoothstep( 1.6, 2.4, u ) );
				}

				rockSites.Add( new RockSite { x = sx, z = sz, r = r, h = hgt, kind = "stack" } );
			}

			// ---- rocky coast: wave-cut platform just below the cliffs with tide pools
			for ( int j = 1; j < rs - 1; j ++ )
			{
				double z = o + j + 0.5;
				if ( z > 330 || z < - 880 ) continue;
				for ( int i = 1; i < rs - 1; i ++ )
				{
					int k = j * rs + i;
					double d = D[ k ];
					if ( d < - 4 || d > 26 ) continue;
					double r0 = R0[ k ];
					if ( r0 < 0.3 ) continue;
					double x = o + i + 0.5;
					double width = 9 + 12 * ( 0.5 + 0.5 * n3.Fbm( x / 45, z / 45, 2 ) );
					double inside = ( 1 - Smoothstep( width * 0.7, width, d ) ) * Smoothstep( 0.3, 0.6, r0 );
					if ( inside <= 0 ) continue;
					double pool = Smoothstep( 0.2, 0.45, n2.Noise( x / 3.1, z / 3.1 ) ) * 0.35;
					double shelf = - 0.35 + 0.35 * n.Fbm( x / 7, z / 7, 2 ) + n2.Ridged( x / 3, z / 3, 2 ) * 0.25 - pool;
					double h = H[ k ];
					if ( shelf > h )
					{
						H[ k ] = ( float ) Lerp( h, shelf, inside );
						R0[ k ] = ( float ) Math.Max( R0[ k ], inside );
					}
				}
			}

			// ---- the bay beach: cusps in the swash zone, a longshore bar, a berm crest and dune ridges
			{
				int i0 = ( int ) Math.Floor( - 200 - o ), i1 = ( int ) Math.Ceiling( 230 - o );
				int j0 = ( int ) Math.Floor( - 170 - o ), j1 = ( int ) Math.Ceiling( 160 - o );
				for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
				{
					int k = j * rs + i;
					double bz = BZ[ k ];
					if ( bz < 0.05 ) continue;
					double x = o + i + 0.5, z = o + j + 0.5;
					double d = D[ k ];
					double h = H[ k ];
					double add = 0;
					if ( d < 0 )
					{
						double e = - d;
						// beach cusps: horns and embayments ~24 m apart in the upper swash
						double cusp = Math.Sin( ( x + n.Noise( x / 60, 3.3 ) * 9 ) * ( 2 * Math.PI / 24 ) );
						add += cusp * 0.09 * Smoothstep( 4, 12, e ) * ( 1 - Smoothstep( 20, 32, e ) );
						// berm crest at the top of the swash
						add += 0.14 * Math.Exp( - Math.Pow( ( e - 38 ) / 5, 2 ) );
						// dune ridges behind the beach, away from the village and the pier foot
						double vd = Hypot( ( x - VILLAGE_X ) * 0.8, z - VILLAGE_Z );
						double away = Smoothstep( 85, 125, vd ) * Smoothstep( 14, 30, Math.Abs( x - WorldLayout.Pier.x ) );
						if ( away > 0 && e > 40 && e < 130 )
						{
							double rid = 1 - Math.Abs( n2.Noise( x / 34, e / 15 + 11.3 ) );
							double hummock = n3.Fbm( x / 9, z / 9, 2 );
							add += away * Smoothstep( 44, 60, e ) * ( 1 - Smoothstep( 95, 128, e ) ) * ( rid * rid * 1.3 + hummock * 0.35 );
						}
					}
					else
					{
						// longshore bar and trough (roughly zero mean over the profile)
						double bar = Math.Exp( - Math.Pow( ( d - 44 ) / 10, 2 ) ) * 0.28 - Math.Exp( - Math.Pow( ( d - 26 ) / 9, 2 ) ) * 0.31;
						add += bar * ( 0.7 + 0.3 * n.Noise( x / 50, 7.7 ) ) * Smoothstep( 5, 15, d );
					}

					H[ k ] = ( float ) ( h + add * bz );
				}
			}

			// ---- footpaths: worn, slightly sunken
			foreach ( var p in PATHS )
			{
				double x0 = double.PositiveInfinity, x1 = double.NegativeInfinity, z0 = double.PositiveInfinity, z1 = double.NegativeInfinity;
				foreach ( var pt in p.pts )
				{
					x0 = Math.Min( x0, pt[ 0 ] ); x1 = Math.Max( x1, pt[ 0 ] ); z0 = Math.Min( z0, pt[ 1 ] ); z1 = Math.Max( z1, pt[ 1 ] );
				}

				double R = p.w * 2 + 2;
				int i0 = ( int ) Math.Floor( x0 - R - o ), i1 = ( int ) Math.Ceiling( x1 + R - o );
				int j0 = ( int ) Math.Floor( z0 - R - o ), j1 = ( int ) Math.Ceiling( z1 + R - o );
				for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
				{
					double x = o + i + 0.5, z = o + j + 0.5;
					double dist = PolylineDistance( p.pts, x, z ).distance;
					double w = p.w * ( 1 + 0.25 * n2.Noise( x / 6, z / 6 ) );
					double m = 1 - Smoothstep( w * 0.6, w * 1.9, dist );
					if ( m <= 0 ) continue;
					int k = j * rs + i;
					int v = ( int ) Round( m * 255 );
					if ( v > path[ k ] ) path[ k ] = ( byte ) v;
					// sink up to 8 cm (none on the beach where it would disturb the swash)
					double sink = 0.08 * ( 1 - Smoothstep( w * 0.4, w * 1.2, dist ) ) * Smoothstep( 1.5, 3, H[ k ] );
					H[ k ] = ( float ) ( H[ k ] - sink );
				}
			}
		}

		// Eroded embankment behind the bay beach: storms cut the foredune back into a low scarp (0.4 -
		// 1.2 m) just above the berm crest and the storm line, where the grass-bound ground meets the
		// open beach. Its position, height and steepness wander along the shore; slumped stretches are
		// lower and gentler, fallen chunks lie at the toe, and the raised lip settles back to the old
		// ground within ~20 - 30 m. Gaps where the village core, the pier foot, the footpaths and rock
		// come down to the beach. The toe stays above the berm (e > 41 m), so the swash and surf never
		// reach it. this.scarp marks the face (and the chunks) for the terrain material.
		void _scarp( float[] H, float[] D, float[] R0, float[] BZ )
		{
			int rs = RES; double o = origin; var n = noise; var n2 = noise2; var n3 = noise3;
			int i0 = ( int ) Math.Floor( - 200 - o ), i1 = ( int ) Math.Ceiling( 230 - o );
			int j0 = ( int ) Math.Floor( - 190 - o ), j1 = ( int ) Math.Ceiling( 0 - o );
			for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
			{
				int k = j * rs + i;
				double bz = BZ[ k ];
				if ( bz < 0.3 ) continue;
				double e = - ( double ) D[ k ];
				if ( e < 36 || e > 95 ) continue;
				double x = o + i + 0.5, z = o + j + 0.5;

				// gaps: village core, pier foot, footpaths, rock
				double vd = Hypot( ( x - VILLAGE_X ) * 0.8, z - VILLAGE_Z );
				double gap = Smoothstep( 45, 70, vd ) * Smoothstep( 10, 22, Math.Abs( x - WorldLayout.Pier.x ) ) * ( 1 - Smoothstep( 0.15, 0.4, R0[ k ] ) );
				foreach ( var p in PATHS )
				{
					double dist = PolylineDistance( p.pts, x, z ).distance;
					gap *= Smoothstep( p.w + 1.5, p.w + 6, dist );
				}

				double Hs = ( 0.5 + 0.7 * Smoothstep( - 0.4, 0.4, n3.Noise( x / 38, 4.1 ) ) ) * gap * Smoothstep( 0.3, 0.7, bz );
				if ( Hs < 0.02 ) continue;
				// slumped stretches: lower, wider, gentler faces
				double slump = Smoothstep( 0.15, 0.6, n.Noise( x / 23, 9.1 ) );
				double w = 1.1 + slump * 1.8;
				double hs = Hs * ( 1 - slump * 0.35 );
				double eS = Math.Max( 44 + w * 0.5, 49 + n.Noise( x / 45, 1.7 ) * 5 + n2.Noise( x / 11, z / 11 ) * 1.5 );
				double t = e - eS;
				double face = Smoothstep( - w * 0.5, w * 0.5, t );
				double back = 1 - Smoothstep( 6, 24 + n2.Noise( x / 30, 3.3 ) * 6, t );
				// fallen chunks and slumped sand at the toe
				double toe = Smoothstep( - 4.5, - 2.5, t ) * ( 1 - Smoothstep( - w * 0.5 - 0.4, - w * 0.5 + 0.3, t ) );
				double chunks = toe * Math.Max( 0, n3.Noise( x / 1.4, z / 1.4 ) + n2.Noise( x / 3.1, z / 3.1 ) * 0.5 ) * 0.22 * hs;
				H[ k ] = ( float ) ( H[ k ] + hs * face * back + chunks );

				double faceM = Smoothstep( - w * 0.5 - 0.35, - w * 0.5 + 0.25, t ) * ( 1 - Smoothstep( w * 0.5 - 0.1, w * 0.5 + 0.7, t ) );
				// the mask climbs from ~0.55 at the toe to 1 at the lip (the material puts the root mat and
				// humus under the lip and an undercut shadow at the foot)
				double up = 0.55 + 0.45 * Smoothstep( - w * 0.5, w * 0.5, t );
				double m = Math.Max( faceM * up * Smoothstep( 0.12, 0.35, hs ), toe * Smoothstep( 0.02, 0.1, chunks ) * 0.3 );
				scarp[ k ] = ( byte ) Math.Max( scarp[ k ], ( int ) Round( Clamp( m, 0, 1 ) * 255 ) );
			}
		}

		// Shallow seabed biomes: seagrass meadows (irregular, ragged, with sand blowouts) and dark
		// rubble / rock heads, 1.5 - 12 m deep. Nothing in the swash zone, the first ~1.2 m of depth,
		// the sandy channel along the pier or the reef core (the reef system dresses that). Meadows
		// sit a little proud of the sand (sediment trapped by the blades), rubble heads are knobbly.
		void _seabed( float[] H, float[] R0 )
		{
			int rs = RES; double o = origin; var n = noise; var n2 = noise2; var n3 = noise3;
			double rcx = WorldLayout.Reef.x, rcz = WorldLayout.Reef.z, rr = WorldLayout.Reef.radius;
			double px = WorldLayout.Pier.x;
			for ( int j = 1; j < rs - 1; j ++ )
			{
				double z = o + j + 0.5;
				if ( z < - 900 || z > 420 ) continue;
				for ( int i = 1; i < rs - 1; i ++ )
				{
					int k = j * rs + i;
					double h = H[ k ];
					if ( h > - 1.2 || h < - 14 ) continue;
					double x = o + i + 0.5;
					double depth = - h;
					double r0 = R0[ k ];

					// exclusions (with ragged edges): reef core, the sandy swim / boat channel along the pier
					double rag = n2.Noise( x / 9, z / 9 ) * 5 + n3.Noise( x / 3.1, z / 3.1 ) * 1.5;
					double reef = 1 - Smoothstep( rr * 0.6, rr * 0.8, Hypot( x - rcx, z - rcz ) + rag * 2 );
					double channel = ( 1 - Smoothstep( 9, 13, Math.Abs( x - px - 4 ) + rag ) ) * Smoothstep( - 70, - 55, z ) * ( 1 - Smoothstep( 45, 60, z + rag * 2 ) );
					double keep = ( 1 - reef ) * ( 1 - channel );
					if ( keep <= 0 ) continue;

					// seagrass: warped large-scale field with ragged margins, favouring 3 - 8 m; the depth
					// limits wander so the meadows never trace the (straight) isobaths of the bay
					double wx = x + n3.Fbm( x / 60, z / 60, 2 ) * 26, wz = z + n3.Fbm( x / 60 + 3.7, z / 60 - 8.1, 2 ) * 26;
					double dj = depth + n.Noise( x / 23 + 5.5, z / 23 ) * 1.1 + n3.Noise( x / 7, z / 7 ) * 0.3;
					double win = Smoothstep( 1.7, 2.8, dj ) * ( 1 - Smoothstep( 8.5, 12.5, dj ) ) * Smoothstep( 1.2, 1.6, depth );
					double f = n.Fbm( wx / 85, wz / 85, 3 ) + n2.Noise( x / 17, z / 17 ) * 0.22 + n3.Noise( x / 6, z / 6 ) * 0.08
						+ 0.12 * ( 1 - Math.Abs( depth - 5.5 ) / 4 ) - 0.2 * r0;
					double g = Smoothstep( 0.1, 0.3, f ) * win * keep;
					if ( g > 0 )
					{
						// blowouts: sand holes scoured inside the meadows
						double hole = Smoothstep( 0.42, 0.62, n2.Noise( x / 7.5 + 4.4, z / 7.5 ) + n3.Noise( x / 2.7, z / 2.7 ) * 0.15 );
						g *= 1 - hole * 0.95;
						seagrass[ k ] = ( byte ) Round( g * 255 );
						H[ k ] = ( float ) ( h + 0.12 * g * Smoothstep( 2.2, 3.2, depth ) );
					}

					// rubble heads: sparse knobbly patches, more of them toward the rocky coasts
					double rw = Smoothstep( 1.8, 3.0, depth ) * ( 1 - Smoothstep( 11, 14, depth ) ) * keep;
					if ( rw > 0 )
					{
						double b = n3.Noise( x / 13 + 9.1, z / 13 - 2.2 ) + n.Noise( x / 4.3, z / 4.3 ) * 0.3 + r0 * 0.35;
						double m = Smoothstep( 0.52, 0.66, b ) * rw * ( 1 - g * 0.6 );
						if ( m > 0 )
						{
							rubble[ k ] = ( byte ) Round( m * 255 );
							double knob = 0.5 + 0.5 * n2.Ridged( x / 2.3, z / 2.3, 2 );
							H[ k ] = ( float ) ( H[ k ] + 0.38 * m * knob * Smoothstep( 2.4, 3.4, depth ) );
						}
					}
				}
			}
		}

		// rock / sand / gully masks from the final shape
		void _masks( float[] H, float[] D, float[] R0, float[] BZ, float[] CV )
		{
			int rs = RES; double o = origin; var n2 = noise2; var n3 = noise3;
			for ( int j = 1; j < rs - 1; j ++ )
			{
				double z = o + j + 0.5;
				for ( int i = 1; i < rs - 1; i ++ )
				{
					int k = j * rs + i;
					double h = H[ k ];
					double d = D[ k ];
					double r0 = R0[ k ];
					if ( h < - 14 )
					{
						rock[ k ] = ( float ) ( r0 * ( 1 - Smoothstep( 80, 260, d ) ) );
						sand[ k ] = 255;
						continue;
					}

					double x = o + i + 0.5;
					double gx = ( double ) H[ k + 1 ] - H[ k - 1 ], gz = ( double ) H[ k + rs ] - H[ k - rs ];
					double slope = Math.Sqrt( gx * gx + gz * gz ) * 0.5;
					double bz = BZ[ k ];
					double nz = n2.Noise( x / 23, z / 23 ) * 0.5 + n3.Noise( x / 7, z / 7 ) * 0.25;

					// bare rock: steep faces, the rocky coast band, the seabed below cliffs
					// near the rocky shore even moderately steep ground is bare (sea cliffs, spray)
					double seaCliff = d < 0 ? r0 * ( 1 - Smoothstep( 6, 38, - d ) ) : 0;
					double s0 = 1.45 - 0.75 * seaCliff + nz * 0.35;
					double steep = Smoothstep( s0, s0 + 0.35, slope );
					// gully floors keep their soil and plants; spurs and faces between them go bare
					if ( steep > 0 && j > 5 && i > 5 && j < rs - 6 && i < rs - 6 )
					{
						double cav = ( ( double ) H[ k + 5 ] + H[ k - 5 ] + H[ k + 5 * rs ] + H[ k - 5 * rs ] - 4 * h ) / 25;
						steep *= 1 - 0.85 * Smoothstep( 0.01, 0.08, cav );
					}

					double coast = d < 0 ? r0 * ( 1 - Smoothstep( 2, 16 + nz * 12, - d ) ) : r0 * ( 1 - Smoothstep( 80, 260, d ) );
					double rk = Math.Max( steep, coast * ( 0.75 + nz * 0.5 ) );
					// rounded outcrops (detail pass bumps): convex and steep-ish
					if ( j > 3 && i > 3 && j < rs - 4 && i < rs - 4 )
					{
						double lap = ( ( double ) H[ k + 3 ] + H[ k - 3 ] + H[ k + 3 * rs ] + H[ k - 3 * rs ] - 4 * h ) / 9;
						rk = Math.Max( rk, Smoothstep( 0.55, 0.9, slope ) * Smoothstep( - 0.12, - 0.3, lap ) * ( 1 - bz ) * 0.8 );
					}

					rock[ k ] = ( float ) Clamp( rk * ( 1 - scarp[ k ] / 255.0 ), 0, 1 );

					// loose sand: the bay beach and dunes, small coves, the seabed
					double sd;
					if ( d >= 0 ) sd = 1 - Smoothstep( 0.4, 0.8, rk );
					else
					{
						double e = - d;
						// the beach proper runs just past the berm crest; behind it sandy soil grades
						// into the village lawn, while the dunes away from the village stay sandy
						double beach = bz * ( 1 - Smoothstep( 36 + nz * 10, 50 + nz * 14, e ) );
						double backBeach = bz * 0.45 * ( 1 - Smoothstep( 60 + nz * 20, 100 + nz * 20, e ) );
						double vd = Hypot( ( x - VILLAGE_X ) * 0.8, z - VILLAGE_Z );
						double away = Smoothstep( 80, 120, vd ) * Smoothstep( 14, 30, Math.Abs( x - WorldLayout.Pier.x ) );
						double dune = bz * away * ( 1 - Smoothstep( 4.5 + nz * 2, 7 + nz * 2, h ) ) * ( 1 - Smoothstep( 110, 140, e ) );
						double cove = ( 1 - Smoothstep( 6, 16, e ) ) * ( 1 - Smoothstep( 0.25, 0.5, rk ) ) * ( 1 - Smoothstep( 1.8, 3.2, h ) ) * ( 1 - Smoothstep( 0.12, 0.3, slope ) );
						sd = Math.Max( Math.Max( beach, backBeach ), Math.Max( dune, cove ) ) * ( 1 - Smoothstep( 0.3, 0.6, slope ) );
					}

					sand[ k ] = ( byte ) Round( Clamp( sd, 0, 1 ) * 255 );
					gully[ k ] = ( byte ) Round( Smoothstep( 4, 18, - ( double ) CV[ k ] ) * 255 );
				}
			}
		}

		// ------------------------------------------------------------------ edits / queries

		// flatten a circular pad (for building foundations)
		public void Flatten( double x, double z, double radius, double height, double falloff = 4 )
		{
			double r = radius + falloff;
			pads.Add( new Pad { x = x, z = z, radius = radius, height = height } );
			int i0 = ( int ) Math.Max( 0, Math.Floor( ( x - r - origin ) / texel ) ), i1 = ( int ) Math.Min( res - 1, Math.Ceiling( ( x + r - origin ) / texel ) );
			int j0 = ( int ) Math.Max( 0, Math.Floor( ( z - r - origin ) / texel ) ), j1 = ( int ) Math.Min( res - 1, Math.Ceiling( ( z + r - origin ) / texel ) );
			for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
			{
				double px = origin + ( i + 0.5 ) * texel, pz = origin + ( j + 0.5 ) * texel;
				double d = Hypot( px - x, pz - z );
				double t = 1 - Smoothstep( radius, r, d );
				int k = j * res + i;
				heights[ k ] = ( float ) Lerp( heights[ k ], height, t );
				rock[ k ] = ( float ) ( rock[ k ] * ( 1 - t ) );
			}
		}

		public double HeightAt( double x, double z )
		{
			double fx = ( x - origin ) / texel - 0.5, fz = ( z - origin ) / texel - 0.5;
			if ( fx < 0 || fz < 0 || fx >= res - 1 || fz >= res - 1 ) return - 90;
			int i = ( int ) Math.Floor( fx ), j = ( int ) Math.Floor( fz );
			double tx = fx - i, tz = fz - j;
			int k = j * res + i;
			double a = heights[ k ], b = heights[ k + 1 ], c = heights[ k + res ], d = heights[ k + res + 1 ];
			return ( a * ( 1 - tx ) + b * tx ) * ( 1 - tz ) + ( c * ( 1 - tx ) + d * tx ) * tz;
		}

		public Vector3 NormalAt( double x, double z )
		{
			double e = texel;
			double hx = HeightAt( x + e, z ) - HeightAt( x - e, z );
			double hz = HeightAt( x, z + e ) - HeightAt( x, z - e );
			return new Vector3( ( float ) - hx, ( float ) ( 2 * e ), ( float ) - hz ).normalized;
		}

		// distance (m) from (x, z) to the nearest footpath edge (negative inside a path)
		public double PathDistance( double x, double z )
		{
			double best = double.PositiveInfinity;
			foreach ( var p in PATHS ) best = Math.Min( best, PolylineDistance( p.pts, x, z ).distance - p.w );
			return best;
		}

		// min/max pyramid for CDLOD culling bounds
		void BuildMinMax()
		{
			const int tile = 8; // texels per tile at the finest level
			int n = res / tile;
			mmTile = tile;
			mmN = n;
			var mn0 = new float[ n * n ]; var mx0 = new float[ n * n ];
			var H = heights;
			for ( int tj = 0; tj < n; tj ++ ) for ( int ti = 0; ti < n; ti ++ )
			{
				float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
				int jEnd = Math.Min( res - 1, ( tj + 1 ) * tile ), iEnd = Math.Min( res - 1, ( ti + 1 ) * tile );
				for ( int j = tj * tile; j <= jEnd; j ++ )
				{
					int row = j * res;
					for ( int i = ti * tile; i <= iEnd; i ++ )
					{
						float h = H[ row + i ];
						if ( h < mn ) mn = h;
						if ( h > mx ) mx = h;
					}
				}

				mn0[ tj * n + ti ] = mn;
				mx0[ tj * n + ti ] = mx;
			}

			mmMin = mn0;
			mmMax = mx0;
			// coarser levels: level l has n >> l tiles per side
			mmLevels = new List<MinMaxLevel> { new MinMaxLevel { n = n, min = mn0, max = mx0 } };
			var cur = mmLevels[ 0 ];
			while ( cur.n > 1 )
			{
				int m = cur.n >> 1;
				var mn = new float[ m * m ]; var mx = new float[ m * m ];
				for ( int j = 0; j < m; j ++ ) for ( int i = 0; i < m; i ++ )
				{
					int a = ( 2 * j ) * cur.n + 2 * i, b = a + cur.n;
					mn[ j * m + i ] = Math.Min( Math.Min( cur.min[ a ], cur.min[ a + 1 ] ), Math.Min( cur.min[ b ], cur.min[ b + 1 ] ) );
					mx[ j * m + i ] = Math.Max( Math.Max( cur.max[ a ], cur.max[ a + 1 ] ), Math.Max( cur.max[ b ], cur.max[ b + 1 ] ) );
				}

				cur = new MinMaxLevel { n = m, min = mn, max = mx };
				mmLevels.Add( cur );
			}
		}

		public (double min, double max) BoundsFor( double x0, double z0, double x1, double z1 )
		{
			double span = Math.Max( x1 - x0, z1 - z0 ) / ( texel * mmTile );
			// JS: ceil( log2( max( 1, span / 2 ) ) ), computed exactly as the smallest l with 2^l >= span / 2
			double sp = Math.Max( 1, span / 2 );
			int lg = 0;
			while ( ( double ) ( 1L << lg ) < sp ) lg ++;
			int l = Math.Max( 0, Math.Min( mmLevels.Count - 1, lg ) );
			var L = mmLevels[ l ];
			double ts = texel * mmTile * ( 1 << l );
			int i0 = ( int ) Math.Floor( ( x0 - origin ) / ts ), i1 = ( int ) Math.Floor( ( x1 - origin ) / ts );
			int j0 = ( int ) Math.Floor( ( z0 - origin ) / ts ), j1 = ( int ) Math.Floor( ( z1 - origin ) / ts );
			double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
			bool outside = false;
			for ( int j = j0; j <= j1; j ++ ) for ( int i = i0; i <= i1; i ++ )
			{
				if ( i < 0 || j < 0 || i >= L.n || j >= L.n )
				{
					outside = true;
					continue;
				}

				int k = j * L.n + i;
				if ( L.min[ k ] < mn ) mn = L.min[ k ];
				if ( L.max[ k ] > mx ) mx = L.max[ k ];
			}

			if ( outside )
			{
				mn = Math.Min( mn, - 90 );
				mx = Math.Max( mx, - 90 );
			}

			return ( mn - 1, mx + 2 );
		}
	}
}
