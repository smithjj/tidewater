using System;
using Tidewater.Core;
using Tidewater.Util;
using UnityEngine;

// Port of src/ocean/SeaDetail.js: large-scale variation of the sea surface in world space (never repeats with the FFT tiles):
//  - gusts ("cat's paws"): patches of rougher water drifting downwind. Rough water reflects less of the bright horizon sky, so
//    from a low viewpoint gusts read as irregular dark patches.
//  - slicks: long calm bands along the wind (surfactant films) where capillary waves are damped; mirror-like and bright, mostly
//    in light to moderate wind.
//  - windrows: thin wavy foam lines along the wind in fresh wind (Langmuir circulation).
// The lookup is Shaders/Ocean/SeaDetail.hlsl; this class owns the noise texture and the wind drift.
//   _TWSeaDetailNoise: RGBA16F, 256^2, repeat, bilinear, no mips (smooth, low-frequency fbm in four channels)
//   _TWSeaDetail = ( offset.x, offset.z, gustAmount, slickAmount ), _TWSeaDetail2 = ( streakAmount, 0, 0, 0 )
namespace Tidewater.Ocean
{
	public sealed class SeaDetail : IDisposable
	{
		public Texture2D texture;
		public readonly int size;
		public Vector2 offset; // accumulated wind drift (m)
		public float gustAmount = 1, slickAmount = 1, streakAmount = 0.3f;

		public SeaDetail( int size = 256 )
		{
			this.size = size;
			texture = MakeNoiseTexture( size );
		}

		// gust patterns travel with the wind at roughly its speed near the surface
		public void Update( float dt )
		{
			float s = G.windSpeed * 0.7f * dt;
			offset.x += G.windDir.x * s;
			offset.y += G.windDir.y * s;
		}

		public void SetGlobals()
		{
			Shader.SetGlobalTexture( "_TWSeaDetailNoise", texture );
			Shader.SetGlobalVector( "_TWSeaDetail", new Vector4( offset.x, offset.y, gustAmount, slickAmount ) );
			Shader.SetGlobalVector( "_TWSeaDetail2", new Vector4( streakAmount, 0, 0, 0 ) );
		}

		public static void SetDisabledGlobals()
		{
			// a flat noise field: no gusts, no slicks, no windrows (the noise value 0.5 everywhere)
			Shader.SetGlobalVector( "_TWSeaDetail", new Vector4( 0, 0, 0, 0 ) );
			Shader.SetGlobalVector( "_TWSeaDetail2", Vector4.zero );
		}

		public void Dispose()
		{
			if ( texture != null ) UnityEngine.Object.DestroyImmediate( texture );
			texture = null;
		}

		// Tileable smooth fbm in 4 channels (different seeds / base frequencies).
		public static ushort[] NoiseData( int size )
		{
			var data = new ushort[ size * size * 4 ];
			var channels = new[] { ( seed: 11, freq: 4, oct: 4 ), ( seed: 23, freq: 5, oct: 4 ), ( seed: 37, freq: 4, oct: 3 ), ( seed: 53, freq: 6, oct: 3 ) };

			for ( int c = 0; c < 4; c ++ )
			{
				var (seed, freq, oct) = channels[ c ];
				var rand = new Mulberry32( ( uint ) seed );
				// gradient tables per octave (periodic lattice)
				var tn = new int[ oct ]; var tg = new float[ oct ][];
				for ( int o = 0; o < oct; o ++ )
				{
					int n = freq << o;
					var g = new float[ n * n * 2 ];
					for ( int i = 0; i < n * n; i ++ )
					{
						double a = rand.Next() * Math.PI * 2;
						g[ i * 2 ] = ( float ) Math.Cos( a );
						g[ i * 2 + 1 ] = ( float ) Math.Sin( a );
					}

					tn[ o ] = n; tg[ o ] = g;
				}

				double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
				var vals = new float[ size * size ];
				for ( int y = 0; y < size; y ++ ) for ( int x = 0; x < size; x ++ )
				{
					double v = 0, amp = 1, norm = 0;
					for ( int o = 0; o < oct; o ++ )
					{
						int n = tn[ o ]; var g = tg[ o ];
						double fx = ( double ) x / size * n, fy = ( double ) y / size * n;
						double xiD = Math.Floor( fx ), yiD = Math.Floor( fy );
						int xi = ( int ) xiD, yi = ( int ) yiD;
						double xf = fx - xiD, yf = fy - yiD;
						double Grad( int ix, int iy, double dx, double dy )
						{
							int k = ( ( ( iy % n ) + n ) % n ) * n + ( ( ( ix % n ) + n ) % n );
							return g[ k * 2 ] * dx + g[ k * 2 + 1 ] * dy;
						}

						double u = xf * xf * xf * ( xf * ( xf * 6 - 15 ) + 10 );
						double w = yf * yf * yf * ( yf * ( yf * 6 - 15 ) + 10 );
						double a0 = Grad( xi, yi, xf, yf ), a1 = Grad( xi + 1, yi, xf - 1, yf );
						double b0 = Grad( xi, yi + 1, xf, yf - 1 ), b1 = Grad( xi + 1, yi + 1, xf - 1, yf - 1 );
						double nv = ( a0 + ( a1 - a0 ) * u ) + ( ( b0 + ( b1 - b0 ) * u ) - ( a0 + ( a1 - a0 ) * u ) ) * w;
						v += nv * amp;
						norm += amp;
						amp *= 0.5;
					}

					v /= norm;
					vals[ y * size + x ] = ( float ) v;
					mn = Math.Min( mn, vals[ y * size + x ] );
					mx = Math.Max( mx, vals[ y * size + x ] );
				}

				for ( int i = 0; i < size * size; i ++ ) data[ i * 4 + c ] = HalfFloat.ToHalf( ( vals[ i ] - mn ) / ( mx - mn ) );
			}

			return data;
		}

		static Texture2D MakeNoiseTexture( int size )
		{
			// no mips: sampled at level 0 with a linear repeat sampler
			var tex = new Texture2D( size, size, TextureFormat.RGBAHalf, false, true ) { name = "seaDetailNoise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
			tex.SetPixelData( NoiseData( size ), 0 );
			tex.Apply( false, true );
			return tex;
		}
	}
}
