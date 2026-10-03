using System;
using UnityEngine;

// Port of src/ocean/OceanFFT.js: multi-cascade FFT ocean (Tessendorf) with a Horvath / JONSWAP spectrum.
// The kernels live in Shaders/Ocean/OceanFFT.compute; this class owns the parameters, the buffers and the two
// output textures and runs the per-frame dispatches.
//
// Output textures (2D arrays, one layer per cascade, 256 x 256 RGBA16F, repeat + trilinear, full mip chain):
//   displacementTexture  (Dx, Dy, Dz, foam)
//   derivativeTexture    (dDy/dx, dDy/dz, dDx/dx, dDz/dz)
// Published to the shaders by SetGlobals() as _TWOceanDisp / _TWOceanDeriv, with _TWOceanSizes[ c ].x the cascade
// tile size (m) and _TWOceanParams = ( choppiness, foamBias, cascades, 0 ).
namespace Tidewater.Ocean
{
	public sealed class WaveSystem
	{
		public double scale = 1;
		public double windSpeed = 8; // m/s
		public double windDirection = 20; // degrees
		public double fetch = 200; // km
		public double spreadBlend = 0.9;
		public double swell = 0.2;
		public double peakEnhancement = 3.3;
		public double shortWavesFade = 0.01;
	}

	public sealed class OceanFFT : IDisposable
	{
		public const int FFT_SIZE = 256;
		const int N = FFT_SIZE;
		public const double GRAVITY = 9.81;

		// Non-integer ratios between cascade sizes avoid visible repetition.
		public static readonly double[] DEFAULT_CASCADE_SIZES = { 733, 157, 33.3, 7.1 };

		public readonly int cascades;
		public double[] sizes;
		public double depth;

		public WaveSystem local, swell;

		public float choppiness;
		// foam starts where a cascade compresses the surface below this Jacobian (per-cascade J stays close to 1:
		// 0.85 gives ~0.3% whitecap cover at 7 m/s, 0.9 several % in fresh wind)
		public float foamBias = 0.58f;
		public float foamGain = 3.0f;
		public float foamDecay = 0.35f;
		public float foamAdd = 2.5f;
		public double time;
		public float timeScale = 1;
		// s: the wave field eases towards a newly computed spectrum instead of snapping to it
		public float smoothTau = 0.6f;
		uint seed = 1337;
		float h0Smooth = 1;

		public RenderTexture displacementTexture, derivativeTexture;

		readonly ComputeShader cs;
		ComputeBuffer h0, h0Target, waveData, tmp, foam;
		readonly Vector4[] vSizes = new Vector4[ 4 ], vCuts = new Vector4[ 4 ], vSysA = new Vector4[ 2 ], vSysB = new Vector4[ 2 ];
		readonly int kInit, kConj, kCopy, kCopyKeep, kSmooth, kRows, kCols;

		bool needsSpectrum, resetFoam, h0Ready, h0Smoothing;
		float h0SmoothT;

		public OceanFFT( ComputeShader shader, int cascades = 4, double[] sizes = null, double depth = 500, WaveSystem local = null, WaveSystem swell = null, float choppiness = 0.9f )
		{
			cs = shader;
			this.cascades = cascades;
			this.sizes = new double[ cascades ];
			Array.Copy( sizes ?? DEFAULT_CASCADE_SIZES, this.sizes, cascades );
			this.depth = depth;
			this.choppiness = choppiness;

			this.local = local ?? new WaveSystem { windSpeed = 7, windDirection = 25, fetch = 120, spreadBlend = 0.85, swell = 0.05 };
			this.swell = swell ?? new WaveSystem { scale = 0.48, windSpeed = 6, windDirection = 5, fetch = 1200, spreadBlend = 1.0, swell = 0.9, shortWavesFade = 0.1 };

			int total = N * N * cascades;
			h0 = new ComputeBuffer( total, 16 );
			// the spectrum the live field (h0) is heading for: a spectrum change then *bends* the amplitudes over a
			// fraction of a second rather than stepping them (see SmoothH0)
			h0Target = new ComputeBuffer( total, 16 );
			waveData = new ComputeBuffer( total, 16 );
			tmp = new ComputeBuffer( total * 2, 16 );
			foam = new ComputeBuffer( total, 4 );
			// WebGPU zero-fills new buffers and the JS relies on it (SmoothH0 mixes the old live field in); a Unity
			// ComputeBuffer is not guaranteed to start zeroed, and a NaN there survives a lerp
			h0.SetData( new Vector4[ total ] ); h0Target.SetData( new Vector4[ total ] ); waveData.SetData( new Vector4[ total ] );
			tmp.SetData( new Vector4[ total * 2 ] ); foam.SetData( new float[ total ] );
			displacementTexture = MakeTex( "oceanDisplacement", 1 ); // (Dx, Dy, Dz, foam)
			derivativeTexture = MakeTex( "oceanDerivatives", 4 ); // (dDy/dx, dDy/dz, dDx/dx, dDz/dz), anisotropy 4 like the JS fragment

			kInit = cs.FindKernel( "InitSpectrum" ); kConj = cs.FindKernel( "Conjugate" );
			kCopy = cs.FindKernel( "CopyH0" ); kCopyKeep = cs.FindKernel( "CopyH0KeepFoam" ); kSmooth = cs.FindKernel( "SmoothH0" );
			kRows = cs.FindKernel( "FFTRows" ); kCols = cs.FindKernel( "FFTColumns" );
			cs.SetBuffer( kInit, "_H0Target", h0Target ); cs.SetBuffer( kInit, "_WaveData", waveData );
			cs.SetBuffer( kConj, "_H0Target", h0Target ); cs.SetBuffer( kConj, "_Tmp", tmp );
			cs.SetBuffer( kCopy, "_H0Target", h0Target ); cs.SetBuffer( kCopy, "_Tmp", tmp ); cs.SetBuffer( kCopy, "_Foam", foam );
			cs.SetBuffer( kCopyKeep, "_H0Target", h0Target ); cs.SetBuffer( kCopyKeep, "_Tmp", tmp );
			cs.SetBuffer( kSmooth, "_H0", h0 ); cs.SetBuffer( kSmooth, "_H0Target", h0Target );
			cs.SetBuffer( kRows, "_H0", h0 ); cs.SetBuffer( kRows, "_WaveData", waveData ); cs.SetBuffer( kRows, "_Tmp", tmp );
			cs.SetBuffer( kCols, "_Tmp", tmp ); cs.SetBuffer( kCols, "_Foam", foam );
			cs.SetTexture( kCols, "_DispOut", displacementTexture ); cs.SetTexture( kCols, "_DerivOut", derivativeTexture );

			UpdateSpectrumUniforms();
			needsSpectrum = true;
		}

		RenderTexture MakeTex( string name, int aniso )
		{
			var rt = new RenderTexture( N, N, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
			{
				name = name, dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray, volumeDepth = cascades,
				enableRandomWrite = true, useMipMap = true, autoGenerateMips = false,
				wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = aniso,
			};
			rt.Create();
			return rt;
		}

		public void SetCascadeSizes( double[] s )
		{
			sizes = new double[ cascades ];
			Array.Copy( s, sizes, cascades );
			UpdateSpectrumUniforms();
		}

		// Rebuild the wave spectrum on the next Update. The dispatch itself is nothing (three small passes); the reason
		// to think about it is resetFoam: the H0 copy also wipes the accumulated foam, which is what you want when the
		// sea *jumps* (a preset click, a load, the first frame) and what you do not want when it drifts, or the
		// weather's slow walk would keep scrubbing the foam off the water. resetFoam: false rebuilds the spectrum and
		// leaves the foam where it is.
		public void UpdateSpectrumUniforms( bool resetFoam = true )
		{
			int C = cascades;
			const double TWO_PI = Math.PI * 2;
			for ( int i = 0; i < 4; i ++ )
			{
				double size = i < C ? sizes[ i ] : 1;
				if ( i < C )
				{
					double low = i == 0 ? 0.0001 : ( TWO_PI / sizes[ i ] ) * 6;
					double high = i == C - 1 ? 9999 : ( TWO_PI / sizes[ i + 1 ] ) * 6;
					vCuts[ i ] = new Vector4( ( float ) low, ( float ) high, 0, 0 );
				}
				else vCuts[ i ] = Vector4.zero;
				vSizes[ i ] = new Vector4( ( float ) size, 0, 0, 0 );
			}

			var sys = new[] { local, swell };
			for ( int i = 0; i < 2; i ++ )
			{
				var s = sys[ i ];
				double fetchM = Math.Max( 1, s.fetch ) * 1000;
				double U = Math.Max( 0.1, s.windSpeed );
				double alpha = 0.076 * Math.Pow( GRAVITY * fetchM / ( U * U ), - 0.22 );
				double peakOmega = 22 * Math.Pow( U * fetchM / ( GRAVITY * GRAVITY ), - 0.33 );
				vSysA[ i ] = new Vector4( ( float ) s.scale, ( float ) ( s.windDirection * Math.PI / 180 ), ( float ) s.spreadBlend, ( float ) s.swell );
				vSysB[ i ] = new Vector4( ( float ) alpha, ( float ) peakOmega, ( float ) s.peakEnhancement, ( float ) s.shortWavesFade );
			}

			needsSpectrum = true;
			this.resetFoam = resetFoam;
		}

		void SetParams( float dt )
		{
			cs.SetVectorArray( "_Sizes", vSizes ); cs.SetVectorArray( "_Cuts", vCuts );
			cs.SetVectorArray( "_SysA", vSysA ); cs.SetVectorArray( "_SysB", vSysB );
			cs.SetFloat( "_Choppiness", choppiness );
			cs.SetFloat( "_FoamBias", foamBias ); cs.SetFloat( "_FoamGain", foamGain );
			cs.SetFloat( "_FoamDecay", foamDecay ); cs.SetFloat( "_FoamAdd", foamAdd );
			cs.SetFloat( "_OceanTime", ( float ) time );
			cs.SetFloat( "_OceanDepth", ( float ) depth );
			cs.SetInt( "_Seed", ( int ) seed );
			cs.SetFloat( "_H0Smooth", h0Smooth );
			cs.SetFloat( "_Dt", dt );
		}

		// Advance by dt: the same dispatch sequence as OceanFFT.update. (dt is also frame.dt, the foam's time step.)
		public void Update( float dt )
		{
			int C = cascades;

			if ( needsSpectrum )
			{
				needsSpectrum = false;
				// the live field now eases towards this target (a fraction per frame), unless this is the first
				// spectrum of the session, where it is simply installed
				h0Smooth = h0Ready ? 1 - ( float ) Math.Exp( - dt / Math.Max( 1e-3, smoothTau ) ) : 1;
				SetParams( dt );
				cs.Dispatch( kInit, N / 16, N / 16, C );
				cs.Dispatch( kConj, N / 16, N / 16, C );
				// clearing the foam is a jump-only affair: see UpdateSpectrumUniforms
				cs.Dispatch( resetFoam ? kCopy : kCopyKeep, N / 16, N / 16, C );
				h0Smoothing = true;
				h0SmoothT = 0;
			}

			if ( h0Smoothing )
			{
				SetParams( dt );
				cs.Dispatch( kSmooth, N / 16, N / 16, C );
				h0Ready = true;
				// keep easing for a few time constants after the last install, then stop spending the pass
				h0SmoothT += dt;
				if ( h0SmoothT > 6 * smoothTau ) h0Smoothing = false;
			}

			time += dt * timeScale;
			SetParams( dt );
			cs.Dispatch( kRows, N, C, 1 );
			cs.Dispatch( kCols, N, C, 1 );
			displacementTexture.GenerateMips();
			derivativeTexture.GenerateMips();
		}

		// publish the textures and per-cascade constants to every shader
		public void SetGlobals()
		{
			Shader.SetGlobalTexture( "_TWOceanDisp", displacementTexture );
			Shader.SetGlobalTexture( "_TWOceanDeriv", derivativeTexture );
			Shader.SetGlobalVectorArray( "_TWOceanSizes", vSizes );
			Shader.SetGlobalVector( "_TWOceanParams", new Vector4( choppiness, foamBias, cascades, 0 ) );
		}

		public void Dispose()
		{
			h0?.Release(); h0Target?.Release(); waveData?.Release(); tmp?.Release(); foam?.Release();
			if ( displacementTexture != null ) { displacementTexture.Release(); UnityEngine.Object.DestroyImmediate( displacementTexture ); }
			if ( derivativeTexture != null ) { derivativeTexture.Release(); UnityEngine.Object.DestroyImmediate( derivativeTexture ); }
			h0 = h0Target = waveData = tmp = foam = null;
		}
	}
}
