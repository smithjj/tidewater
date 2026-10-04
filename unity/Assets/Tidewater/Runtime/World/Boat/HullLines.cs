using System;
using System.Collections.Generic;
using Tidewater.Engine;

// Port of src/world/boat/HullLines.js: lines plan of an ~8.2 m Downeast lobster boat, in the boat's local frame: +Z forward (bow),
// +Y up, +X port. y = 0 is the design waterline, x = 0 the centerline and z = 0 the middle of the waterline.
//
// The hull surface is parameterized by t (0 = transom .. 1 = stem head) and a section index j running from the keel (j = 0) up to the
// sheer. Each section is a centripetal Catmull-Rom spline through seven control points (keel, garboard, bilge start, bilge, bilge end,
// topsides, sheer). Aft sections are planar (constant z); forward of t = 0.55 they tilt progressively so that the last one (t = 1)
// traces the raked stem with a rounded forefoot.
namespace Tidewater.World.Boat
{
	public struct HullSample { public Vector3 position; public double area, depth, bottomY; }

	public sealed class HullLines
	{
		public static double clamp01( double x ) => Math.Min( 1, Math.Max( 0, x ) );
		public static double lerp( double a, double b, double t ) => a + ( b - a ) * t;
		public static double sstep( double a, double b, double x ) { double t = clamp01( ( x - a ) / ( b - a ) ); return t * t * ( 3 - 2 * t ); }

		// Intervals per spline span (K-G, G-B1, B1-C, C-B2, B2-M, M-S) at density 1.
		static readonly int[] SPANS = { 4, 4, 3, 3, 8, 10 };
		public const double RHO_SEAWATER = 1025;

		public double zAft = -3.9, zBow = 4.3, length;
		public double deckY = 0.35;      // cockpit sole
		public double shell = 0.07;      // outer skin to inner lining at the sheer
		public double houseBack = -0.35; // aft end of the wheelhouse side walls
		public double houseFront = 1.45; // wheelhouse front (base of the windshield)

		public struct StemParams { public double k, R, yF, zW, yc, zc, yT; }
		public StemParams stem;

		readonly Dictionary<int, double[]> _setback = new Dictionary<int, double[]>();
		readonly Dictionary<string, double[]> _sectionCache = new Dictionary<string, double[]>();

		// analyze() results
		public double wlStart, wlEnd, waterplaneArea, canoeVolume, centerOfFlotationZ;
		public Vector3 centerOfBuoyancy;
		double[] _beamTable; double _tableZ0, _tableDz;
		float[] fBottom; int fNx, fNz; double fCell, fX0, fZ0;

		public HullLines()
		{
			length = zBow - zAft;

			// Stem: straight rake above the forefoot, circular forefoot below.
			double k = 0.2, R = 0.45;
			double yF = keelY( 1 ), yTip = sheerY( 1 );
			double zW = zBow - k * yTip;
			double q = Math.Sqrt( 1 + k * k );
			double yc = yF + R;
			stem = new StemParams { k = k, R = R, yF = yF, zW = zW, yc = yc, zc = zW + k * yc - R * q, yT = yc - R * k / q };

			analyze();
		}

		// ---------------------------------------------------------------- design curves

		public double sheerZ( double t ) => zAft + length * t;
		public double tAtSheerZ( double z ) => clamp01( ( z - zAft ) / length );

		public double sheerY( double t )
		{
			double a = Math.Max( 0, 1 - t / 0.2 );
			return 0.98 + 0.62 * Math.Pow( t, 2.2 ) + 0.04 * a * a;
		}

		public double sheerX( double t )
		{
			if ( t <= 0.42 ) { double a = 1 - t / 0.42; return 1.45 - 0.17 * a * a; }
			double s = ( t - 0.42 ) / 0.58;
			return 1.45 * ( 1 - Math.Pow( s, 2.6 ) );
		}

		public double keelY( double t )
		{
			if ( t <= 0.45 ) return -0.23 - 0.22 * Math.Sin( Math.PI * 0.5 * t / 0.45 );
			return -0.45 + 0.33 * Math.Pow( sstep( 0.45, 1, t ), 1.6 );
		}

		public double chineX( double t )
		{
			if ( t <= 0.42 ) { double a = 1 - t / 0.42; return 1.26 - 0.1 * a * a; }
			double s = ( t - 0.42 ) / 0.58;
			return 1.26 * Math.Pow( Math.Max( 0, 1 - Math.Pow( s, 1.7 ) ), 1.15 );
		}

		public double chineY( double t ) => lerp( 0.01, -0.045, sstep( 0, 0.45, t ) ) + 0.33 * Math.Pow( sstep( 0.5, 1, t ), 1.3 );
		public double bilgeTangent( double t ) => lerp( 0.2, 0.24, sstep( 0, 0.4, t ) ) * ( 1 - 0.55 * sstep( 0.6, 1, t ) );
		public double bottomConvexity( double t ) => lerp( 0.025, 0.012, sstep( 0.55, 0.95, t ) );
		public double flare( double t ) => lerp( -0.012, 0.075, sstep( 0.3, 0.85, t ) ) * ( 1 - 0.3 * sstep( 0.9, 1, t ) );
		public double bowBlend( double t ) => t > 0.55 ? Math.Pow( ( t - 0.55 ) / 0.45, 2 ) : 0;

		public double stemZ( double y )
		{
			var s = stem;
			if ( y >= s.yT ) return s.zW + s.k * y;
			double dy = y - s.yc;
			return s.zc + Math.Sqrt( Math.Max( 0, s.R * s.R - dy * dy ) );
		}

		// ---------------------------------------------------------------- sections

		public double[][] controlPoints( double t )
		{
			double[] K = { 0, keelY( t ) };
			double[] C0 = { chineX( t ), chineY( t ) };
			double[] S = { sheerX( t ), sheerY( t ) };

			double bx = C0[ 0 ] - K[ 0 ], by = C0[ 1 ] - K[ 1 ];
			double lb = JS.Hypot( bx, by );
			double dbx = bx / lb, dby = by / lb;
			double tx = S[ 0 ] - C0[ 0 ], ty = S[ 1 ] - C0[ 1 ];
			double lt = JS.Hypot( tx, ty );
			double dtx = tx / lt, dty = ty / lt;

			// shape offsets fade out where the section collapses into the stem
			double w = sstep( 0, 0.3, C0[ 0 ] );
			double r = Math.Min( bilgeTangent( t ), Math.Min( 0.3 * lb, 0.3 * lt ) );

			double convex = bottomConvexity( t ) * lb * w;
			double[] G = { K[ 0 ] + dbx * lb * 0.5 + dby * convex, K[ 1 ] + dby * lb * 0.5 - dbx * convex };
			double[] B1 = { C0[ 0 ] - dbx * r, C0[ 1 ] - dby * r };
			double[] B2 = { C0[ 0 ] + dtx * r, C0[ 1 ] + dty * r };

			// midpoint of a circular fillet between bottom and topsides lines
			double psi = Math.Acos( Math.Min( 1, Math.Max( -1, dbx * dtx + dby * dty ) ) );
			double mx = dtx - dbx, my = dty - dby;
			double ml = JS.Hypot( mx, my );
			double e = ml > 1e-6 ? r * Math.Tan( psi / 4 ) / ml : 0;
			double[] Cm = { C0[ 0 ] + mx * e, C0[ 1 ] + my * e };

			double fl = flare( t ) * lt * w; // > 0: concave flare
			double[] M = { C0[ 0 ] + dtx * lt * 0.5 - dty * fl, C0[ 1 ] + dty * lt * 0.5 + dtx * fl };

			return new[] { K, G, B1, Cm, B2, M, S };
		}

		// Section points (x >= 0, y) from keel to sheer as a flat [x0, y0, x1, y1, ...] array.
		public double[] sectionPoints( double t, int density = 1 )
		{
			string key = JsNumber( t ) + ":" + density;
			if ( _sectionCache.TryGetValue( key, out var cached ) ) return cached;

			var cp = controlPoints( t );
			int n = cp.Length;
			var ext = new List<double[]>();
			ext.Add( new[] { 2 * cp[ 0 ][ 0 ] - cp[ 1 ][ 0 ], 2 * cp[ 0 ][ 1 ] - cp[ 1 ][ 1 ] } );
			ext.AddRange( cp );
			ext.Add( new[] { 2 * cp[ n - 1 ][ 0 ] - cp[ n - 2 ][ 0 ], 2 * cp[ n - 1 ][ 1 ] - cp[ n - 2 ][ 1 ] } );

			int total = 0; foreach ( int sp in SPANS ) total += sp;
			int count = total * density + 1;
			var o = new double[ count * 2 ];
			int k = 0;
			var p = new double[ 2 ];
			for ( int s = 0; s < SPANS.Length; s ++ )
			{
				int steps = SPANS[ s ] * density;
				for ( int i = 0; i < steps; i ++ )
				{
					catmullRom( ext[ s ], ext[ s + 1 ], ext[ s + 2 ], ext[ s + 3 ], ( double ) i / steps, p );
					o[ k ++ ] = Math.Max( 0, p[ 0 ] );
					o[ k ++ ] = p[ 1 ];
				}
			}

			o[ k ++ ] = cp[ n - 1 ][ 0 ];
			o[ k ++ ] = cp[ n - 1 ][ 1 ];

			if ( _sectionCache.Count > 4096 ) _sectionCache.Clear();
			_sectionCache[ key ] = o;
			return o;
		}

		// the cache key only has to be consistent
		static string JsNumber( double t ) => t.ToString( "R", System.Globalization.CultureInfo.InvariantCulture );

		// Longitudinal setback of each section point, so the t = 1 section traces the stem.
		public double[] setback( int density = 1 )
		{
			if ( _setback.TryGetValue( density, out var H ) ) return H;
			var sec = sectionPoints( 1, density );
			int n = sec.Length / 2;
			H = new double[ n ];
			for ( int j = 0; j < n; j ++ ) H[ j ] = zBow - stemZ( sec[ 2 * j + 1 ] );
			H[ n - 1 ] = 0;
			_setback[ density ] = H;
			return H;
		}

		public int sectionCount( int density = 1 ) { int total = 0; foreach ( int sp in SPANS ) total += sp; return total * density + 1; }

		// Surface points of station t as Vector3s (port side).
		public List<Vector3> station( double t, int density = 1 )
		{
			var sec = sectionPoints( t, density );
			var H = setback( density );
			double zs = sheerZ( t ), D = bowBlend( t );
			var pts = new List<Vector3>();
			for ( int j = 0; j < H.Length; j ++ ) pts.Add( new Vector3( sec[ 2 * j ], sec[ 2 * j + 1 ], zs - D * H[ j ] ) );
			return pts;
		}

		// Station parameters, denser toward the bow where the sections change quickly.
		public List<double> stationParams( int count )
		{
			Func<double, double> density = t => 1 + 2.2 * sstep( 0.62, 1, t ) + 0.5 * ( 1 - sstep( 0, 0.08, t ) );
			int N = 2000;
			var cum = new double[ N + 1 ];
			for ( int i = 1; i <= N; i ++ ) cum[ i ] = cum[ i - 1 ] + density( ( i - 0.5 ) / N ) / N;
			double total = cum[ N ];
			var ts = new List<double>();
			int k = 0;
			for ( int i = 0; i < count; i ++ )
			{
				double target = total * i / ( count - 1 );
				while ( k < N && cum[ k + 1 ] < target ) k ++;
				double f = ( target - cum[ k ] ) / Math.Max( 1e-12, cum[ k + 1 ] - cum[ k ] );
				ts.Add( i == count - 1 ? 1 : ( k + clamp01( f ) ) / N );
			}

			return ts;
		}

		// Outer half-breadth of the station t at height y (topsides, above the bilge).
		public double halfBreadth( double t, double y )
		{
			var sec = sectionPoints( t, 2 );
			int n = sec.Length / 2;
			if ( y >= sec[ 2 * n - 1 ] ) return sec[ 2 * n - 2 ];
			for ( int j = n - 2; j >= 0; j -- )
			{
				double y0 = sec[ 2 * j + 1 ], y1 = sec[ 2 * j + 3 ];
				if ( y >= y0 && y <= y1 )
				{
					double f = ( y - y0 ) / Math.Max( 1e-9, y1 - y0 );
					return lerp( sec[ 2 * j ], sec[ 2 * j + 2 ], f );
				}
			}

			return 0;
		}

		// Station parameter whose surface passes through longitudinal position z at height y.
		public double tAt( double z, double y )
		{
			double lo = 0, hi = 1;
			for ( int i = 0; i < 40; i ++ )
			{
				double mid = ( lo + hi ) * 0.5;
				if ( zOnStation( mid, y ) < z ) lo = mid; else hi = mid;
			}

			return ( lo + hi ) * 0.5;
		}

		// Longitudinal position of station t at height y (accounts for the tilted bow sections).
		public double zOnStation( double t, double y )
		{
			double D = bowBlend( t );
			if ( D == 0 ) return sheerZ( t );
			var sec = sectionPoints( t, 2 );
			var H = setback( 2 );
			int n = sec.Length / 2;
			double h = 0;
			if ( y <= sec[ 1 ] ) h = H[ 0 ];
			else if ( y >= sec[ 2 * n - 1 ] ) h = 0;
			else
			{
				for ( int j = 0; j < n - 1; j ++ )
				{
					double y0 = sec[ 2 * j + 1 ], y1 = sec[ 2 * j + 3 ];
					if ( y >= y0 && y <= y1 )
					{
						h = lerp( H[ j ], H[ j + 1 ], ( y - y0 ) / Math.Max( 1e-9, y1 - y0 ) );
						break;
					}
				}
			}

			return sheerZ( t ) - D * h;
		}

		// Outer hull half-breadth at an arbitrary (z, y) on the topsides.
		public double hullXAt( double z, double y ) => halfBreadth( tAt( z, y ), y );

		// ---------------------------------------------------------------- hydrostatics

		void analyze()
		{
			int density = 3;
			int NT = 480;
			var H = setback( density );
			int nj = H.Length;

			// Waterline (y = 0 crossing of every station).
			var wl = new List<double[]>();
			for ( int i = 0; i <= NT; i ++ )
			{
				double t = ( double ) i / NT;
				var sec = sectionPoints( t, density );
				double zs = sheerZ( t ), D = bowBlend( t );
				for ( int j = 0; j < nj - 1; j ++ )
				{
					double y0 = sec[ 2 * j + 1 ], y1 = sec[ 2 * j + 3 ];
					if ( y0 <= 0 && y1 > 0 )
					{
						double f = -y0 / ( y1 - y0 );
						wl.Add( new[] { zs - D * lerp( H[ j ], H[ j + 1 ], f ), lerp( sec[ 2 * j ], sec[ 2 * j + 2 ], f ) } );
						break;
					}
				}
			}

			// stable sort by z (JS Array.sort is stable)
			var sorted = new List<double[]>( wl );
			var order = new int[ sorted.Count ]; for ( int i = 0; i < order.Length; i ++ ) order[ i ] = i;
			Array.Sort( order, ( ia, ib ) => { int c = sorted[ ia ][ 0 ].CompareTo( sorted[ ib ][ 0 ] ); return c != 0 ? c : ia.CompareTo( ib ); } );
			wl = new List<double[]>(); foreach ( int i in order ) wl.Add( sorted[ i ] );
			wlStart = wl[ 0 ][ 0 ];
			wlEnd = wl[ wl.Count - 1 ][ 0 ];

			int TN = 512;
			_beamTable = new double[ TN + 1 ];
			_tableZ0 = wlStart;
			_tableDz = ( wlEnd - wlStart ) / TN;
			int kk0 = 0;
			for ( int i = 0; i <= TN; i ++ )
			{
				double z = wlStart + i * _tableDz;
				while ( kk0 < wl.Count - 2 && wl[ kk0 + 1 ][ 0 ] < z ) kk0 ++;
				var a = wl[ kk0 ]; var b = wl[ kk0 + 1 ];
				double f = clamp01( ( z - a[ 0 ] ) / Math.Max( 1e-9, b[ 0 ] - a[ 0 ] ) );
				_beamTable[ i ] = lerp( a[ 1 ], b[ 1 ], f );
			}

			// Rasterize the immersed canoe body (port half) into a bottom height field.
			double cell = 0.02;
			double x0 = 0, z0 = zAft - 0.02;
			int nx = ( int ) Math.Ceiling( 1.5 / cell ), nz = ( int ) Math.Ceiling( ( wlEnd + 0.05 - z0 ) / cell );
			var bottom = new float[ nx * nz ];
			for ( int i = 0; i < bottom.Length; i ++ ) bottom[ i ] = float.PositiveInfinity;
			var grid = new List<double[]>();
			for ( int i = 0; i <= NT; i ++ )
			{
				double t = ( double ) i / NT;
				var sec = sectionPoints( t, density );
				double zs = sheerZ( t ), D = bowBlend( t );
				var row = new double[ nj * 3 ];
				for ( int j = 0; j < nj; j ++ )
				{
					row[ 3 * j ] = sec[ 2 * j ];
					row[ 3 * j + 1 ] = sec[ 2 * j + 1 ];
					row[ 3 * j + 2 ] = zs - D * H[ j ];
				}

				grid.Add( row );
			}

			Action<double[], double[], double[]> tri = ( a, b, c ) =>
			{
				if ( Math.Min( a[ 1 ], Math.Min( b[ 1 ], c[ 1 ] ) ) > 0.01 ) return;
				double minX = Math.Min( a[ 0 ], Math.Min( b[ 0 ], c[ 0 ] ) ), maxX = Math.Max( a[ 0 ], Math.Max( b[ 0 ], c[ 0 ] ) );
				double minZ = Math.Min( a[ 2 ], Math.Min( b[ 2 ], c[ 2 ] ) ), maxZ = Math.Max( a[ 2 ], Math.Max( b[ 2 ], c[ 2 ] ) );
				int i0 = ( int ) Math.Max( 0, Math.Ceiling( ( minX - x0 ) / cell - 0.5 ) ), i1 = ( int ) Math.Min( nx - 1, Math.Floor( ( maxX - x0 ) / cell - 0.5 ) );
				int k0 = ( int ) Math.Max( 0, Math.Ceiling( ( minZ - z0 ) / cell - 0.5 ) ), k1 = ( int ) Math.Min( nz - 1, Math.Floor( ( maxZ - z0 ) / cell - 0.5 ) );
				if ( i0 > i1 || k0 > k1 ) return;
				double det = ( b[ 2 ] - c[ 2 ] ) * ( a[ 0 ] - c[ 0 ] ) + ( c[ 0 ] - b[ 0 ] ) * ( a[ 2 ] - c[ 2 ] );
				if ( Math.Abs( det ) < 1e-12 ) return;
				for ( int kk = k0; kk <= k1; kk ++ )
				{
					double pz = z0 + ( kk + 0.5 ) * cell;
					for ( int ii = i0; ii <= i1; ii ++ )
					{
						double px = x0 + ( ii + 0.5 ) * cell;
						double l1 = ( ( b[ 2 ] - c[ 2 ] ) * ( px - c[ 0 ] ) + ( c[ 0 ] - b[ 0 ] ) * ( pz - c[ 2 ] ) ) / det;
						double l2 = ( ( c[ 2 ] - a[ 2 ] ) * ( px - c[ 0 ] ) + ( a[ 0 ] - c[ 0 ] ) * ( pz - c[ 2 ] ) ) / det;
						double l3 = 1 - l1 - l2;
						if ( l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9 ) continue;
						double y = l1 * a[ 1 ] + l2 * b[ 1 ] + l3 * c[ 1 ];
						int idx = kk * nx + ii;
						if ( y < bottom[ idx ] ) bottom[ idx ] = ( float ) y;
					}
				}
			};

			var A = new double[ 3 ]; var B = new double[ 3 ]; var C = new double[ 3 ]; var Dp = new double[ 3 ];
			Func<double[], int, double[], double[]> get = ( row, j, o ) => { o[ 0 ] = row[ 3 * j ]; o[ 1 ] = row[ 3 * j + 1 ]; o[ 2 ] = row[ 3 * j + 2 ]; return o; };
			for ( int i = 0; i < NT; i ++ )
			{
				for ( int j = 0; j < nj - 1; j ++ )
				{
					get( grid[ i ], j, A ); get( grid[ i + 1 ], j, B ); get( grid[ i + 1 ], j + 1, C ); get( grid[ i ], j + 1, Dp );
					tri( A, B, C ); tri( A, C, Dp );
				}
			}

			fBottom = bottom; fNx = nx; fNz = nz; fCell = cell; fX0 = x0; fZ0 = z0;

			// Waterplane area, displaced volume, centers.
			double area = 0, volume = 0, mz = 0, vz = 0, vy = 0;
			for ( int kk = 0; kk < nz; kk ++ )
			{
				double pz = z0 + ( kk + 0.5 ) * cell;
				for ( int ii = 0; ii < nx; ii ++ )
				{
					double y = bottom[ kk * nx + ii ];
					if ( ! ( y < 0 ) ) continue;
					double dA = cell * cell * 2;
					area += dA;
					mz += dA * pz;
					volume += dA * -y;
					vz += dA * -y * pz;
					vy += dA * -y * ( y * 0.5 );
				}
			}

			waterplaneArea = area;
			canoeVolume = volume;
			centerOfFlotationZ = mz / area;
			centerOfBuoyancy = new Vector3( 0, vy / volume, vz / volume );
		}

		// Waterplane half-beam at longitudinal position z (0 outside the waterline).
		public double halfBeamAt( double z )
		{
			if ( z < wlStart || z > wlEnd ) return 0;
			double f = ( z - _tableZ0 ) / _tableDz;
			int i = ( int ) Math.Min( _beamTable.Length - 2, Math.Max( 0, Math.Floor( f ) ) );
			return lerp( _beamTable[ i ], _beamTable[ i + 1 ], clamp01( f - i ) );
		}

		// Canoe-body depth below the waterline (at the centerline) at z, 0 outside.
		public double draftAt( double z )
		{
			double f = ( z - fZ0 ) / fCell - 0.5;
			if ( f < -0.5 || f > fNz - 0.5 ) return 0;
			int k = ( int ) Math.Min( fNz - 2, Math.Max( 0, Math.Floor( f ) ) );
			double a = fBottom[ k * fNx ], b = fBottom[ ( k + 1 ) * fNx ];
			double da = a < 0 ? -a : 0, db = b < 0 ? -b : 0;
			return lerp( da, db, clamp01( f - k ) );
		}

		// Hull bottom height (canoe body) at (x, z); +Infinity outside the immersed footprint.
		public double bottomAt( double x, double z )
		{
			int i = ( int ) Math.Floor( ( Math.Abs( x ) - fX0 ) / fCell ), k = ( int ) Math.Floor( ( z - fZ0 ) / fCell );
			if ( i < 0 || i >= fNx || k < 0 || k >= fNz ) return double.PositiveInfinity;
			return fBottom[ k * fNx + i ];
		}

		// Buoyancy samples: `slices` longitudinal slices x 4 lateral strips (two per side). Each sample carries the waterplane area of its
		// patch; its y is the mean hull depth of the patch, so sum(area * -y) equals the displaced canoe-body volume, and x sits at the
		// patch's radius of gyration so roll stiffness is preserved.
		public List<HullSample> buildHullSamples( int slices = 8 )
		{
			double L = wlEnd - wlStart;
			var acc = new double[ slices ][][]; // [slice][strip] -> a, x2, z, v
			for ( int s = 0; s < slices; s ++ ) acc[ s ] = new[] { new double[ 4 ], new double[ 4 ] };

			for ( int kk = 0; kk < fNz; kk ++ )
			{
				double pz = fZ0 + ( kk + 0.5 ) * fCell;
				int s = ( int ) Math.Min( slices - 1, Math.Max( 0, Math.Floor( ( pz - wlStart ) / L * slices ) ) );
				double hb = halfBeamAt( pz );
				for ( int ii = 0; ii < fNx; ii ++ )
				{
					double y = fBottom[ kk * fNx + ii ];
					if ( ! ( y < 0 ) ) continue;
					double px = fX0 + ( ii + 0.5 ) * fCell;
					int strip = hb > 0 && px > hb * 0.5 ? 1 : 0;
					double dA = fCell * fCell;
					var c = acc[ s ][ strip ];
					c[ 0 ] += dA; c[ 1 ] += dA * px * px; c[ 2 ] += dA * pz; c[ 3 ] += dA * -y;
				}
			}

			var samples = new List<HullSample>();
			for ( int s = 0; s < slices; s ++ )
			{
				for ( int strip = 0; strip < 2; strip ++ )
				{
					var c = acc[ s ][ strip ];
					if ( c[ 0 ] <= 0 ) continue;
					double x = Math.Sqrt( c[ 1 ] / c[ 0 ] ), z = c[ 2 ] / c[ 0 ], y = -c[ 3 ] / c[ 0 ];
					double bottomY = Math.Min( 0, bottomAt( x, z ) );
					samples.Add( new HullSample { position = new Vector3( x, y, z ), area = c[ 0 ], depth = -y, bottomY = bottomY } );
					samples.Add( new HullSample { position = new Vector3( -x, y, z ), area = c[ 0 ], depth = -y, bottomY = bottomY } );
				}
			}

			return samples;
		}

		// Centripetal Catmull-Rom between p1 and p2.
		static double[] catmullRom( double[] p0, double[] p1, double[] p2, double[] p3, double u, double[] o )
		{
			double d01 = Math.Max( 1e-5, Math.Sqrt( JS.Hypot( p1[ 0 ] - p0[ 0 ], p1[ 1 ] - p0[ 1 ] ) ) );
			double d12 = Math.Max( 1e-5, Math.Sqrt( JS.Hypot( p2[ 0 ] - p1[ 0 ], p2[ 1 ] - p1[ 1 ] ) ) );
			double d23 = Math.Max( 1e-5, Math.Sqrt( JS.Hypot( p3[ 0 ] - p2[ 0 ], p3[ 1 ] - p2[ 1 ] ) ) );
			double t0 = 0, t1 = d01, t2 = t1 + d12, t3 = t2 + d23;
			double t = t1 + ( t2 - t1 ) * u;

			for ( int c = 0; c < 2; c ++ )
			{
				double a1 = ( ( t1 - t ) * p0[ c ] + ( t - t0 ) * p1[ c ] ) / ( t1 - t0 );
				double a2 = ( ( t2 - t ) * p1[ c ] + ( t - t1 ) * p2[ c ] ) / ( t2 - t1 );
				double a3 = ( ( t3 - t ) * p2[ c ] + ( t - t2 ) * p3[ c ] ) / ( t3 - t2 );
				double b1 = ( ( t2 - t ) * a1 + ( t - t0 ) * a2 ) / ( t2 - t0 );
				double b2 = ( ( t3 - t ) * a2 + ( t - t1 ) * a3 ) / ( t3 - t1 );
				o[ c ] = ( ( t2 - t ) * b1 + ( t - t1 ) * b2 ) / ( t2 - t1 );
			}

			return o;
		}
	}
}
