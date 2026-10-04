using System;
using Tidewater.Core;
using Tidewater.Ocean;
using Tidewater.Util;
using Tidewater.World;
using UnityEngine;
using UnityEngine.Rendering;

// Port of src/fx/Spray.js: GPU spray particles: drops, ligaments, dense spray and mist.
//
// One ring of particle slots. The first part is written by GPU emitters (breaking waves, Breakers) through an atomic head; the tail is
// owned by the CPU emit API (boat bow spray, splashes), whose requests the update kernel (Shaders/Fx/Spray.compute) expands into the
// slots the CPU reserved. Particles fall under gravity with drag toward the wind and die when they fall back into the water, where
// they leave foam (ShoreSim deposit). Rendered as soft camera-facing sprites (Shaders/Fx/Spray.shader), drawn procedurally from the
// particle buffers. All positions, velocities and the body pose are in SIM space (Util/Sim.cs).
//
// GPU emitters: Spray.Bind( cs, kernel ) binds the buffers of Shaders/Fx/SprayEmit.hlsl to their kernel.
namespace Tidewater.Fx
{
	public static class SprayKind
	{
		public const float DROPLET = 0, MIST = 1, LIGAMENT = 2, SPRAY = 3, SHEET = 4;
	}

	public sealed class SprayEmitOptions
	{
		public float spread = 0.6f;       // velocity jitter, m/s
		public float jitter = 0.05f;      // position jitter, m
		public float life = -1;           // s (default by kind)
		public Vector3? to;               // emit along the segment position -> to
		public float sizeJitter = 0.5f;   // 0..1
	}

	public sealed class Spray : IDisposable
	{
		const int MAX_REQUESTS = 32;
		public readonly int NG, NC, N;

		readonly ComputeShader cs;
		readonly int kernel;
		readonly Material material;
		readonly OceanFFT fft;
		readonly TerrainGPU terrain;
		readonly ShoreWaves shore;
		readonly ShoreSim shoreSim;

		ComputeBuffer pos, vel, info, head, req, dummyCrest;
		readonly float[] reqData = new float[ MAX_REQUESTS * 16 ];
		int nReq, nReqParticles, cpuHead;
		uint frame;
		Texture2D puff, dots;
		readonly MaterialPropertyBlock props = new MaterialPropertyBlock();

		// tuning
		public float intensity = 1f, maxDistance = 320f;

		// the body the particles collide with (SetBody / SetBodyShape), in sim space
		bool bodyOn;
		Matrix4x4 bodyMat = Matrix4x4.identity;
		Vector3 bodyVel;
		Vector4 bodyHull = new Vector4( 0, 0, 1, 0 ), bodyHullWL = new Vector4( 0, 1, 0, 0 ), bodySheer = new Vector4( 0, 0, -1, 0 );
		Vector3 bodyBoxMin, bodyBoxMax;

		public ComputeBuffer positionBuffer => pos;

		public Spray( ComputeShader shader, Shader renderShader, OceanFFT fft, TerrainGPU terrain, ShoreWaves shore, ShoreSim shoreSim, int gpuCapacity = 32768, int cpuCapacity = 8192 )
		{
			cs = shader; kernel = cs.FindKernel( "SprayUpdate" );
			this.fft = fft; this.terrain = terrain; this.shore = shore; this.shoreSim = shoreSim;
			NG = gpuCapacity; NC = cpuCapacity; N = gpuCapacity + cpuCapacity;
			pos = Zeroed( N ); vel = Zeroed( N ); info = Zeroed( N );
			head = new ComputeBuffer( 1, 4 ); head.SetData( new uint[ 1 ] );
			req = new ComputeBuffer( MAX_REQUESTS * 4, 16 ); req.SetData( new Vector4[ MAX_REQUESTS * 4 ] );
			material = new Material( renderShader ) { name = "Spray", hideFlags = HideFlags.HideAndDontSave };
			puff = MakePuffTexture();
			dots = MakeDotsTexture();

			// no breaking waves yet: the shader's crest hooks read an empty table
			dummyCrest = new ComputeBuffer( 3, 16 ); dummyCrest.SetData( new Vector4[ 3 ] );
			Shader.SetGlobalBuffer( "_BrkCrest", dummyCrest );
			Shader.SetGlobalVector( "_BrkParams", Vector4.zero );
		}

		static ComputeBuffer Zeroed( int n )
		{
			var b = new ComputeBuffer( n, 16 );
			b.SetData( new Vector4[ n ] );
			return b;
		}

		// ------------------------------------------------------------------ CPU API

		// Emit `count` particles at `position` with base `velocity` (m/s). size: radius (m), kind: SprayKind. Up to 32 calls and 8192
		// particles per frame; nothing is allocated.
		public void Emit( Vector3 position, Vector3 velocity, int count, float size = 0.04f, float kind = SprayKind.DROPLET, SprayEmitOptions opts = null )
		{
			if ( nReq >= MAX_REQUESTS || count <= 0 ) return;
			count = Math.Min( count, NC - nReqParticles );
			if ( count <= 0 ) return;
			opts = opts ?? new SprayEmitOptions();
			float life = opts.life >= 0 ? opts.life : ( kind == SprayKind.MIST ? 2.5f : 1.6f );
			// requests are expanded on the GPU by the update kernel
			var b = opts.to ?? position;
			int o = nReq * 16;
			nReqParticles += count;
			reqData[ o ] = position.x; reqData[ o + 1 ] = position.y; reqData[ o + 2 ] = position.z; reqData[ o + 3 ] = nReqParticles;
			reqData[ o + 4 ] = b.x; reqData[ o + 5 ] = b.y; reqData[ o + 6 ] = b.z; reqData[ o + 7 ] = size;
			reqData[ o + 8 ] = velocity.x; reqData[ o + 9 ] = velocity.y; reqData[ o + 10 ] = velocity.z; reqData[ o + 11 ] = kind;
			reqData[ o + 12 ] = opts.spread; reqData[ o + 13 ] = opts.jitter; reqData[ o + 14 ] = life; reqData[ o + 15 ] = opts.sizeJitter;
			nReq ++;
		}

		// Emit along a polyline: countPerSegment particles spread over each segment. velocities: one for all, or one per point (each
		// segment uses the velocity of its start point).
		public void EmitAlongPoints( Vector3[] points, Vector3[] velocities, int countPerSegment, float size = 0.04f, float kind = SprayKind.DROPLET, SprayEmitOptions opts = null )
		{
			opts = opts ?? new SprayEmitOptions();
			for ( int i = 0; i + 1 < points.Length; i ++ )
			{
				opts.to = points[ i + 1 ];
				Emit( points[ i ], velocities.Length > 1 ? velocities[ i ] : velocities[ 0 ], countPerSegment, size, kind, opts );
			}

			opts.to = null;
		}

		// Collision shape of the body, in its own frame (+Z forward, +Y up): the hull's plan outline (full half beam aft of the
		// shoulder, an elliptic bow to the stem), given at the waterline (y = 0: wl*) and at the sheer, blended with height (flare,
		// raked stem), from yBottom up to the sheer (linear from ySheerAft to ySheerStem); and one box (e.g. the wheelhouse).
		public void SetBodyShape( float zAft, float zShoulder, float zStem, float halfBeam, float wlShoulder, float wlStem, float wlHalfBeam,
			float ySheerAft, float ySheerStem, float yBottom, Vector3 boxMin, Vector3 boxMax )
		{
			bodyHull = new Vector4( zAft, zShoulder, zStem, halfBeam );
			bodyHullWL = new Vector4( wlShoulder, wlStem, wlHalfBeam, 0 );
			bodySheer = new Vector4( ySheerAft, ySheerStem, yBottom, 0 );
			bodyBoxMin = boxMin; bodyBoxMax = boxMax;
		}

		// The body's pose (body -> sim world) and velocity this frame; null disables the collisions.
		public void SetBody( Matrix4x4? matrixWorld, Vector3 velocity )
		{
			bodyOn = matrixWorld.HasValue;
			if ( ! bodyOn ) return;
			bodyMat = matrixWorld.Value;
			bodyVel = velocity;
		}

		// ------------------------------------------------------------------ simulation

		// Bind the particle buffers (Shaders/Fx/SprayEmit.hlsl) to a GPU emitter's kernel
		public void Bind( ComputeShader emitter, int emitterKernel )
		{
			emitter.SetBuffer( emitterKernel, "_SprayPos", pos );
			emitter.SetBuffer( emitterKernel, "_SprayVel", vel );
			emitter.SetBuffer( emitterKernel, "_SprayInfo", info );
			emitter.SetBuffer( emitterKernel, "_SprayHead", head );
			emitter.SetInts( "_SprayCap", NG, NC, N, unchecked( ( int ) frame ) );
		}

		public void Update( float dt, float time, float amplitude )
		{
			// CPU requests -> ring slots
			int n = nReqParticles;
			int cpuStart = cpuHead;
			cpuHead = ( cpuHead + n ) % NC;
			if ( nReq > 0 ) req.SetData( reqData, 0, 0, nReq * 16 );
			frame ++;

			Bind( cs, kernel );
			cs.SetBuffer( kernel, "_SprayReq", req );
			cs.SetInts( "_SprayRing", cpuStart, n, nReq, shoreSim != null ? 1 : 0 );
			cs.SetVector( "_SprayFrame", new Vector4( time, dt, G.windDir.x, G.windDir.y ) );
			cs.SetVector( "_SprayWindSpeed", new Vector4( G.windSpeed, 0, 0, 0 ) );
			cs.SetMatrix( "_SprayBodyMat", bodyMat );
			cs.SetMatrix( "_SprayBodyInv", bodyMat.inverse );
			cs.SetVector( "_SprayBodyVel", new Vector4( bodyVel.x, bodyVel.y, bodyVel.z, bodyOn ? 1 : 0 ) );
			cs.SetVector( "_SprayBodyHull", bodyHull );
			cs.SetVector( "_SprayBodyHullWL", bodyHullWL );
			cs.SetVector( "_SprayBodySheer", bodySheer );
			cs.SetVector( "_SprayBodyBoxMin", bodyBoxMin );
			cs.SetVector( "_SprayBodyBoxMax", bodyBoxMax );

			WaterQuery.SetHeightInputs( cs, kernel, fft, terrain, shore, amplitude );
			if ( shoreSim != null )
			{
				cs.SetBuffer( kernel, "_ShoreSimDeposit", shoreSim.deposit );
				cs.SetVector( "_TWShoreSimParams", new Vector4( shoreSim.center.x - shoreSim.size / 2, shoreSim.center.y - shoreSim.size / 2, shoreSim.size, 1 ) );
			}
			else
			{
				cs.SetBuffer( kernel, "_ShoreSimDeposit", dummyCrest );
				cs.SetVector( "_TWShoreSimParams", Vector4.zero );
			}

			// the shader reads these textures only when it samples the shore sim state; bind something valid
			cs.SetTexture( kernel, "_TWShoreSimState", shoreSim != null ? shoreSim.stateA : ( Texture ) Texture2D.blackTexture );
			cs.SetTexture( kernel, "_TWShoreSimLace", shoreSim != null ? shoreSim.lace.nearest : ( Texture ) Texture2D.blackTexture );

			cs.Dispatch( kernel, ( N + 63 ) / 64, 1, 1 );
			nReq = 0;
			nReqParticles = 0;
		}

		// ------------------------------------------------------------------ rendering

		// one camera-facing quad per particle slot (dead ones collapse off-screen)
		public void Draw( Camera cam )
		{
			props.SetBuffer( "_SprayPosR", pos );
			props.SetBuffer( "_SprayVelR", vel );
			props.SetBuffer( "_SprayInfoR", info );
			props.SetTexture( "_SprayPuff", puff );
			props.SetTexture( "_SprayDots", dots );
			props.SetVector( "_SprayMat", new Vector4( intensity, maxDistance, 0, 0 ) );
			var rp = new RenderParams( material )
			{
				camera = cam,
				worldBounds = new Bounds( Vector3.zero, Vector3.one * 1e7f ),
				matProps = props,
				shadowCastingMode = ShadowCastingMode.Off,
				receiveShadows = false,
			};
			Graphics.RenderPrimitives( rp, MeshTopology.Triangles, 6, N );
		}

		// ------------------------------------------------------------------ textures

		static Texture2D MipTexture( byte[] data, int size, string name )
		{
			var t = new Texture2D( size, size, TextureFormat.RGBA32, true, true ) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
			t.SetPixelData( data, 0 );
			t.Apply( true, false );
			return t;
		}

		// Small tileable value-noise texture for mist puffs (generated once on the CPU).
		static Texture2D MakePuffTexture( int size = 64 )
		{
			var data = new byte[ size * size * 4 ];
			Func<int, int, int, double> rnd = ( i, j, o ) =>
			{
				double s = Math.Sin( ( i % o ) * 127.1 + ( j % o ) * 311.7 + o * 17.3 ) * 43758.5453;
				return s - Math.Floor( s );
			};

			Func<double, double, int, double> vnoise = ( x, y, o ) =>
			{
				int i = ( int ) Math.Floor( x ), j = ( int ) Math.Floor( y );
				double fx = x - i, fy = y - j;
				double ux = fx * fx * ( 3 - 2 * fx ), uy = fy * fy * ( 3 - 2 * fy );
				double a = rnd( i, j, o ), b = rnd( i + 1, j, o ), c = rnd( i, j + 1, o ), d = rnd( i + 1, j + 1, o );
				return ( a * ( 1 - ux ) + b * ux ) * ( 1 - uy ) + ( c * ( 1 - ux ) + d * ux ) * uy;
			};

			for ( int j = 0; j < size; j ++ ) for ( int i = 0; i < size; i ++ )
			{
				double s = 0, a = 0.5, n = 0;
				for ( int o = 4; o <= 32; o *= 2 )
				{
					s += vnoise( ( double ) i / size * o, ( double ) j / size * o, o ) * a;
					n += a;
					a *= 0.55;
				}

				double v = Math.Max( 0, Math.Min( 1, ( s / n - 0.2 ) * 1.6 ) );
				int k = ( j * size + i ) * 4;
				data[ k ] = data[ k + 1 ] = data[ k + 2 ] = ( byte ) MathX.Round( v * 255 );
				data[ k + 3 ] = 255;
			}

			return MipTexture( data, size, "sprayPuff" );
		}

		// Tileable texture of scattered drops (r: coverage), for clusters of drops. Heavy-tailed radii: many tiny drops, a few large ones.
		static Texture2D MakeDotsTexture( int size = 128, int count = 150 )
		{
			var data = new byte[ size * size * 4 ];
			ulong seed = 12345;
			Func<double> rnd = () => { seed = ( seed * 1664525UL + 1013904223UL ) & 0xFFFFFFFFUL; return seed / 4294967296.0; };
			var cov = new float[ size * size ];
			for ( int k = 0; k < count; k ++ )
			{
				double cx = rnd() * size, cy = rnd() * size;
				double r = Math.Min( 0.9 * Math.Pow( 1 - rnd() * 0.97, -0.55 ), 6 );
				int R = ( int ) Math.Ceiling( r + 1.5 );
				for ( int dy = -R; dy <= R; dy ++ ) for ( int dx = -R; dx <= R; dx ++ )
				{
					int x = ( ( ( int ) Math.Floor( cx ) + dx ) % size + size ) % size, y = ( ( ( int ) Math.Floor( cy ) + dy ) % size + size ) % size;
					double d = Math.Sqrt( Math.Pow( Math.Floor( cx ) + dx + 0.5 - cx, 2 ) + Math.Pow( Math.Floor( cy ) + dy + 0.5 - cy, 2 ) );
					double c = Math.Max( 0, Math.Min( 1, r + 0.5 - d ) );
					cov[ y * size + x ] = ( float ) Math.Max( cov[ y * size + x ], c );
				}
			}

			for ( int i = 0; i < size * size; i ++ )
			{
				byte v = ( byte ) MathX.Round( cov[ i ] * 255.0 );
				data[ i * 4 ] = data[ i * 4 + 1 ] = data[ i * 4 + 2 ] = v;
				data[ i * 4 + 3 ] = 255;
			}

			return MipTexture( data, size, "sprayDots" );
		}

		public void Dispose()
		{
			pos?.Release(); vel?.Release(); info?.Release(); head?.Release(); req?.Release(); dummyCrest?.Release();
			pos = vel = info = head = req = dummyCrest = null;
			if ( material != null ) UnityEngine.Object.DestroyImmediate( material );
			if ( puff != null ) UnityEngine.Object.DestroyImmediate( puff );
			if ( dots != null ) UnityEngine.Object.DestroyImmediate( dots );
		}
	}
}
