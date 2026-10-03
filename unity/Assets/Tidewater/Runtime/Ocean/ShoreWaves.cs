using System;
using Tidewater.World;
using UnityEngine;

// Port of src/ocean/ShoreWaves.js (the parameters, the wave clock and the direction texture; the model itself is
// Shaders/Ocean/ShoreWaves.hlsl): depth-aware shoreline waves. Wave phase comes from the precomputed travel-time field
// (ShoreField), each wave has its own height, height grows in shallow water until it breaks, plunges and runs up the beach
// as a swash sheet.
//
// Published to shaders by SetGlobals (and to compute shaders by SetCompute):
//   _TWShoreA = ( period, phase, amplitude, variation )     _TWShoreB = ( gamma, breakSpan, curl, runup )
//   _TWShoreC = ( enabled, turbidity, dirOn, dirSize )      _TWShoreD = ( dirMin.xy ), _TWShoreE = ( time, sea level )
//   _TWShoreDirTex = the cheap wave direction lookup (BuildDirTexture)
namespace Tidewater.Ocean
{
	public sealed class ShoreWaves
	{
		public float period = 9.0f;
		// Wave phase in periods, accumulated as dt / period rather than computed as time / period. Dividing an absolute clock
		// by the period looks equivalent and is not: the period changes with the weather, and (t / period) then jumps by
		// t * d(1/period), a phase discontinuity that grows the longer you play, jittering the surf and the swash. Integrating
		// the rate keeps the phase continuous however the period moves.
		public double phase = 0;
		public float amplitude = 0.34f; // offshore amplitude (H/2)
		public float variation = 0.55f;
		public float gamma = 0.78f;
		// fraction of the break depth over which the lip plunges. Also sets how fast the breaking state changes along a crest
		// whose height varies: wide enough that a broken section joins the clean face through a shoulder where the lip is
		// still falling (peeling), not a vertical cut
		public float breakSpan = 0.13f;
		public float curl = 1.0f;
		public float runup = 1.0f;
		public float enabled = 1.0f;
		public float turbidity = 0.16f; // sediment + bubble scattering in the surf zone (1/m)

		readonly TerrainGPU terrain;
		readonly ShoreFieldData field;
		Vector2 dirMin;
		float dirSize = 1, dirOn = 0;
		Texture2D dirTexture;
		readonly Texture2D dirPlaceholder;

		public ShoreWaves( TerrainGPU terrain, ShoreFieldData field )
		{
			this.terrain = terrain;
			this.field = field;
			// placeholder until BuildDirTexture() (a binding needs a texture; dirOn selects the fallback)
			dirPlaceholder = new Texture2D( 1, 1, TextureFormat.RGBAFloat, false, true ) { name = "shoreDirPlaceholder", filterMode = FilterMode.Point };
			dirPlaceholder.SetPixelData( new float[] { 1, 0, 0, 1 }, 0 );
			dirPlaceholder.Apply( false, true );
		}

		// The wave clock: integrate the phase rate, so a change to the period bends the phase instead of stepping it (see
		// `phase`). Called once a frame.
		public void Update( float dt )
		{
			phase += dt / Math.Max( 0.2, period );
		}

		Vector4 A => new Vector4( period, ( float ) phase, amplitude, variation );
		Vector4 B => new Vector4( gamma, breakSpan, curl, runup );
		Vector4 C => new Vector4( enabled, turbidity, dirOn, dirSize );
		Vector4 D => new Vector4( dirMin.x, dirMin.y, 0, 0 );

		public void SetGlobals( float time, float seaLevel )
		{
			Shader.SetGlobalVector( "_TWShoreA", A ); Shader.SetGlobalVector( "_TWShoreB", B );
			Shader.SetGlobalVector( "_TWShoreC", C ); Shader.SetGlobalVector( "_TWShoreD", D );
			Shader.SetGlobalVector( "_TWShoreE", new Vector4( time, seaLevel, 0, 0 ) );
			Shader.SetGlobalTexture( "_TWShoreDirTex", dirTexture != null ? dirTexture : dirPlaceholder );
		}

		// the globals of a world without shore waves: disabled (enabled = 0), with a sane period so no shader divides by zero
		public static void SetDisabledGlobals()
		{
			Shader.SetGlobalVector( "_TWShoreA", new Vector4( 9, 0, 0.34f, 0.55f ) ); Shader.SetGlobalVector( "_TWShoreB", new Vector4( 0.78f, 0.13f, 1, 1 ) );
			Shader.SetGlobalVector( "_TWShoreC", new Vector4( 0, 0.16f, 0, 1 ) ); Shader.SetGlobalVector( "_TWShoreD", Vector4.zero );
			Shader.SetGlobalVector( "_TWShoreE", Vector4.zero );
		}

		public void SetCompute( ComputeShader cs, int kernel, float time, float seaLevel )
		{
			cs.SetVector( "_TWShoreA", A ); cs.SetVector( "_TWShoreB", B );
			cs.SetVector( "_TWShoreC", C ); cs.SetVector( "_TWShoreD", D );
			cs.SetVector( "_TWShoreE", new Vector4( time, seaLevel, 0, 0 ) );
			cs.SetTexture( kernel, "_TWShoreDirTex", dirTexture != null ? dirTexture : dirPlaceholder );
		}

		// Low-resolution texture of the wave direction and exposure over a region (one bilinear fetch from 4 loads instead of
		// the 4 exact loads of the shore field for each of 4 corners) for per-pixel effects near the beach. region: min (xz)
		// and size. HLSL: ShoreDirAt( xz ) -> float3( dir.x, dir.z, exposure ).
		public Texture2D BuildDirTexture( Vector2 min, float size, int res = 128 )
		{
			var data = field.data;
			int fres = field.res;
			double origin = terrain.origin, tsize = terrain.size;
			var outp = new float[ res * res * 4 ];
			for ( int j = 0; j < res; j ++ ) for ( int i = 0; i < res; i ++ )
			{
				double x = min.x + ( i + 0.5 ) / res * size, z = min.y + ( j + 0.5 ) / res * size;
				double fx = Math.Min( Math.Max( ( x - origin ) / tsize * fres - 0.5, 0 ), fres - 1.001 );
				double fz = Math.Min( Math.Max( ( z - origin ) / tsize * fres - 0.5, 0 ), fres - 1.001 );
				int i0 = ( int ) Math.Floor( fx ), j0 = ( int ) Math.Floor( fz );
				double tx = fx - i0, tz = fz - j0;
				double At( int ch )
				{
					double a = data[ ( j0 * fres + i0 ) * 4 + ch ], b = data[ ( j0 * fres + i0 + 1 ) * 4 + ch ];
					double cc = data[ ( ( j0 + 1 ) * fres + i0 ) * 4 + ch ], d = data[ ( ( j0 + 1 ) * fres + i0 + 1 ) * 4 + ch ];
					return ( a * ( 1 - tx ) + b * tx ) * ( 1 - tz ) + ( cc * ( 1 - tx ) + d * tx ) * tz;
				}

				double dx = At( 1 ), dz = At( 2 );
				double e = Math.Sqrt( dx * dx + dz * dz );
				if ( e == 0 ) e = 1e-4;
				int k = ( j * res + i ) * 4;
				outp[ k ] = ( float ) ( dx / e );
				outp[ k + 1 ] = ( float ) ( dz / e );
				outp[ k + 2 ] = ( float ) Math.Min( 1, e * 1.4 );
				outp[ k + 3 ] = 1;
			}

			// float data, read with exact loads: no sampler
			if ( dirTexture != null ) UnityEngine.Object.DestroyImmediate( dirTexture );
			dirTexture = new Texture2D( res, res, TextureFormat.RGBAFloat, false, true ) { name = "shoreDir", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
			dirTexture.SetPixelData( outp, 0 );
			dirTexture.Apply( false, true );
			dirMin = min;
			dirSize = size;
			dirOn = 1;
			return dirTexture;
		}

		public void Destroy()
		{
			if ( dirTexture != null ) UnityEngine.Object.DestroyImmediate( dirTexture );
			if ( dirPlaceholder != null ) UnityEngine.Object.DestroyImmediate( dirPlaceholder );
		}
	}
}
