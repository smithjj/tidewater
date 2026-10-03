using System;
using Tidewater.Ocean;

// Port of src/world/ShoreField.js: offline wave-propagation field for shoreline waves.
//
// Solves the Eikonal equation |grad T| = 1 / c(x) with the Fast Marching Method, where c = sqrt(g * depth) is the
// shallow-water wave speed (capped offshore). Sources are the domain borders initialized with a plane wave travelling
// along `swellDir`, so wave fronts refract naturally around headlands and align with the depth contours near the beach.
//
// Output (RGBA float, res x res over the terrain domain):
//   r = arrival time T (s), g,b = propagation direction * exposure (length = exposure 0..1),
//   a = arrival time at the nearest shoreline (extended onto land for swash timing)
//
// Numerics as in TerrainData: doubles for all arithmetic, float[] where the JS has Float32Array.
namespace Tidewater.World
{
	public sealed class ShoreFieldData
	{
		public float[] data;
		public int res;
		public double cellSize, origin, size;
		public float[] depth;
	}

	public static class ShoreField
	{
		sealed class MinHeap
		{
			readonly double[] keys;
			readonly int[] vals;
			public int size;

			public MinHeap( int cap ) { keys = new double[ cap ]; vals = new int[ cap ]; }

			public void Push( double k, int v )
			{
				int i = size ++;
				while ( i > 0 )
				{
					int p = ( i - 1 ) >> 1;
					if ( keys[ p ] <= k ) break;
					keys[ i ] = keys[ p ]; vals[ i ] = vals[ p ];
					i = p;
				}

				keys[ i ] = k; vals[ i ] = v;
			}

			public int Pop()
			{
				int top = vals[ 0 ];
				double k = keys[ -- size ]; int v = vals[ size ];
				int i = 0;
				int n = size;
				while ( true )
				{
					int c = 2 * i + 1;
					if ( c >= n ) break;
					if ( c + 1 < n && keys[ c + 1 ] < keys[ c ] ) c ++;
					if ( keys[ c ] >= k ) break;
					keys[ i ] = keys[ c ]; vals[ i ] = vals[ c ];
					i = c;
				}

				keys[ i ] = k; vals[ i ] = v;
				return top;
			}
		}

		public static ShoreFieldData Compute( TerrainData terrain, int res = 512, double sdx = 0, double sdz = -1, double seaLevel = 0, double maxDepth = 25, double minDepth = 0.25 )
		{
			double size = terrain.size;
			double origin = terrain.origin;
			double h = size / res;
			int N = res * res;

			var depth = new float[ N ];
			var speed = new float[ N ];
			for ( int j = 0; j < res; j ++ )
			{
				double z = origin + ( j + 0.5 ) * h;
				for ( int i = 0; i < res; i ++ )
				{
					double x = origin + ( i + 0.5 ) * h;
					double d = seaLevel - terrain.HeightAt( x, z );
					depth[ j * res + i ] = ( float ) d;
					speed[ j * res + i ] = ( float ) ( d > 0 ? Math.Sqrt( OceanFFT.GRAVITY * Math.Min( Math.Max( d, minDepth ), maxDepth ) ) : 0 );
				}
			}

			var T = new float[ N ];
			for ( int i = 0; i < N; i ++ ) T[ i ] = float.PositiveInfinity;
			var state = new byte[ N ]; // 0 far, 1 trial, 2 known
			var heap = new MinHeap( N * 4 );
			double c0 = Math.Sqrt( OceanFFT.GRAVITY * maxDepth );

			// plane-wave initial condition on the border water cells
			for ( int j = 0; j < res; j ++ ) for ( int i = 0; i < res; i ++ )
			{
				if ( i != 0 && j != 0 && i != res - 1 && j != res - 1 ) continue;
				int k = j * res + i;
				if ( speed[ k ] <= 0 ) continue;
				double x = origin + ( i + 0.5 ) * h, z = origin + ( j + 0.5 ) * h;
				T[ k ] = ( float ) ( ( x * sdx + z * sdz ) / c0 + size ); // offset keeps T positive
				state[ k ] = 1;
				heap.Push( T[ k ], k );
			}

			double Solve( int i, int j )
			{
				int k = j * res + i;
				double c = speed[ k ];
				if ( c <= 0 ) return double.PositiveInfinity;
				double f = h / c;
				double tx = Math.Min(
					i > 0 && state[ k - 1 ] == 2 ? T[ k - 1 ] : double.PositiveInfinity,
					i < res - 1 && state[ k + 1 ] == 2 ? T[ k + 1 ] : double.PositiveInfinity );
				double tz = Math.Min(
					j > 0 && state[ k - res ] == 2 ? T[ k - res ] : double.PositiveInfinity,
					j < res - 1 && state[ k + res ] == 2 ? T[ k + res ] : double.PositiveInfinity );
				double a = Math.Min( tx, tz ), b = Math.Max( tx, tz );
				if ( double.IsInfinity( b ) || b - a >= f ) return a + f;
				return 0.5 * ( a + b + Math.Sqrt( 2 * f * f - ( a - b ) * ( a - b ) ) );
			}

			var di = new[] { -1, 1, 0, 0 };
			var dj = new[] { 0, 0, -1, 1 };
			while ( heap.size > 0 )
			{
				int k = heap.Pop();
				if ( state[ k ] == 2 ) continue;
				state[ k ] = 2;
				int i = k % res, j = k / res;
				for ( int q = 0; q < 4; q ++ )
				{
					int ni = i + di[ q ], nj = j + dj[ q ];
					if ( ni < 0 || nj < 0 || ni >= res || nj >= res ) continue;
					int nk = nj * res + ni;
					if ( state[ nk ] == 2 || speed[ nk ] <= 0 ) continue;
					double t = Solve( ni, nj );
					if ( t < T[ nk ] )
					{
						T[ nk ] = ( float ) t;
						state[ nk ] = 1;
						heap.Push( t, nk );
					}
				}
			}

			// Extend a field onto land one ring of cells per pass (average of the known neighbours + inc). Each pass reads
			// the previous pass only: filling in place while scanning would let values from far away (e.g. the other side
			// of the island) sweep across the land in a single pass.
			void Extend( float[] F, int passes, double inc )
			{
				var prev = new float[ N ];
				for ( int pass = 0; pass < passes; pass ++ )
				{
					Array.Copy( F, prev, N );
					bool changed = false;
					for ( int j = 0; j < res; j ++ ) for ( int i = 0; i < res; i ++ )
					{
						int k = j * res + i;
						if ( float.IsFinite( prev[ k ] ) ) continue;
						double s = 0; int n = 0;
						if ( i > 0 && float.IsFinite( prev[ k - 1 ] ) ) { s += prev[ k - 1 ]; n ++; }
						if ( i < res - 1 && float.IsFinite( prev[ k + 1 ] ) ) { s += prev[ k + 1 ]; n ++; }
						if ( j > 0 && float.IsFinite( prev[ k - res ] ) ) { s += prev[ k - res ]; n ++; }
						if ( j < res - 1 && float.IsFinite( prev[ k + res ] ) ) { s += prev[ k + res ]; n ++; }
						if ( n > 0 ) { F[ k ] = ( float ) ( s / n + inc ); changed = true; }
					}

					if ( ! changed ) break;
				}
			}

			// arrival time at the nearest shoreline, extended unchanged onto land (swash timing)
			var Tshore = ( float[] ) T.Clone();
			Extend( Tshore, 40, 0 );

			// extend T onto land (so the swash zone has a continuous phase), continuing slowly up the beach
			var Tfilled = ( float[] ) T.Clone();
			Extend( Tfilled, 24, h / 1.5 );

			// smooth to remove first-order FMM kinks (keeps phase monotonic)
			var Ts = Tfilled;
			for ( int it = 0; it < 3; it ++ )
			{
				var outp = new float[ N ];
				for ( int j = 0; j < res; j ++ ) for ( int i = 0; i < res; i ++ )
				{
					int k = j * res + i;
					if ( ! float.IsFinite( Ts[ k ] ) ) { outp[ k ] = Ts[ k ]; continue; }
					double s = Ts[ k ] * 4.0; int w = 4;
					for ( int q = 0; q < 4; q ++ )
					{
						int ni = i + di[ q ], nj = j + dj[ q ];
						if ( ni < 0 || nj < 0 || ni >= res || nj >= res ) continue;
						float v = Ts[ nj * res + ni ];
						if ( float.IsFinite( v ) ) { s += v; w ++; }
					}

					outp[ k ] = ( float ) ( s / w );
				}

				Ts = outp;
			}

			// directions + exposure
			var data = new float[ N * 4 ];
			double sl = Math.Sqrt( sdx * sdx + sdz * sdz );
			double G( float a, float b ) => ( float.IsFinite( a ) && float.IsFinite( b ) ) ? ( ( double ) a - b ) : 0;
			for ( int j = 0; j < res; j ++ ) for ( int i = 0; i < res; i ++ )
			{
				int k = j * res + i;
				float t = Ts[ k ];
				float tl = i > 0 ? Ts[ k - 1 ] : t, tr = i < res - 1 ? Ts[ k + 1 ] : t;
				float td = j > 0 ? Ts[ k - res ] : t, tu = j < res - 1 ? Ts[ k + res ] : t;
				double gx = G( tr, tl ), gz = G( tu, td );
				if ( gx == 0 && float.IsFinite( tr ) && float.IsFinite( t ) ) gx = ( double ) tr - t;
				if ( gz == 0 && float.IsFinite( tu ) && float.IsFinite( t ) ) gz = ( double ) tu - t;
				double len = Math.Sqrt( gx * gx + gz * gz );
				if ( len == 0 ) len = 1;
				double dx = gx / len, dz = gz / len;
				// exposure: how directly the local wave direction faces the incoming swell
				double align = ( dx * sdx + dz * sdz ) / sl;
				double exposure = Math.Min( 1, Math.Max( 0.02, align * 1.4 + 0.1 ) );
				// direction scaled by exposure (length = exposure), alpha = shoreline arrival time
				data[ k * 4 ] = float.IsFinite( t ) ? t : 1e5f;
				data[ k * 4 + 1 ] = ( float ) ( dx * exposure );
				data[ k * 4 + 2 ] = ( float ) ( dz * exposure );
				data[ k * 4 + 3 ] = float.IsFinite( Tshore[ k ] ) ? Tshore[ k ] : ( float.IsFinite( t ) ? t : 1e5f );
			}

			return new ShoreFieldData { data = data, res = res, cellSize = h, origin = origin, size = size, depth = depth };
		}
	}
}
