using System;
using Tidewater.Core;
using JS = Tidewater.Engine.JS;
using Tidewater.Player;
using Tidewater.Util;
using Tidewater.World;
using UnityEngine;
using JMath = Tidewater.Engine.MathUtils;

// Port of src/ocean/WakeSim.js (host side; the kernels are Shaders/Ocean/WakeSim.compute, the readers Shaders/Ocean/Wake.hlsl):
// the interactive boat wake, a linear free-surface wave simulation with exact dispersion on a 512^2 grid (0.4 m cells) in a window that
// follows the boat. See the compute shader's header for the method. This class bakes the hull and pier-pile maps, runs the three kernels
// each frame (nothing while the boat has been still for 40 s: the output fades out and the simulation sleeps) and publishes the
// parameters and textures the water reads (globals; compute users bind with SetCompute).
//
// Output (display texture, ARGBHalf): (h, dh/dx, dh/dz, foam), plus the aeration and the boat's near-field template.
namespace Tidewater.Ocean
{
	public sealed class WakeSim : IDisposable
	{
		public const int N = 512;
		public const int HALF = N / 2;
		public const double CELL = 0.4;      // m
		public const int TW = 32, TH = 88;   // near-field template, 0.1 m texels
		const double HULL_X1 = 1.9;
		const double GRAVITY = 9.81;

		public static WakeSim current { get; private set; }

		// tuning
		public float amplitude = 1f;
		public float foamGain = 0.35f;       // thick churn at the transom, patchy lace behind
		public float aerGain = 1f;
		public float aerOut = 0.3f;          // output aeration at saturation (0..1)
		public double dynamicPressure = 0.35; // K
		public double sourceGain = 0.72;     // near-field waves ~0.2-0.4 m at cruise
		public double settleTime = 40;       // s of calm before the simulation goes to sleep

		readonly ComputeShader cs;
		readonly int kRows, kCols, kInv;
		readonly TerrainGPU terrain;
		readonly BoatController boat;
		readonly World.Boat.HullLines lines;
		readonly Texture2D hullTex, pileTex;
		readonly RenderTexture display, aerTex, nearTex;
		readonly ComputeBuffer state, scratch, spec, aerA, aerB, nearBuf;
		Vector4 pileBox;
		double hullZ0, hullZ1;

		// the window (cells, integer centre), the kernels' parameters
		double centerX, centerZ;
		bool hasWindow;
		public bool sleeping = true;
		double idleTime = 1e9;
		public int stepCount;
		bool primed;
		float amount, reset = 1, dt = 1f / 60f, speed, source, wash, bow, washW = 0.6f, boil = 1.2f, visc = 0.006f, dyn, hollow = 1, hollowK, nearRate = 1;
		Vector2 origin, prev, center, boatPos, boatRot = new Vector2( 1, 0 );
		Vector3 trim;
		Vector2 dead = new Vector2( 0.03f, 0.08f );

		static readonly Tidewater.Engine.Vector3 _fwd = new Tidewater.Engine.Vector3();

		public WakeSim( ComputeShader shader, TerrainGPU terrain, BoatController boat, World.Boat.HullLines lines, World.Colliders colliders = null )
		{
			cs = shader; this.terrain = terrain; this.boat = boat; this.lines = lines;
			kRows = cs.FindKernel( "WakeRows" ); kCols = cs.FindKernel( "WakeColumns" ); kInv = cs.FindKernel( "WakeInverse" );

			state = new ComputeBuffer( N * N, 16 ); scratch = new ComputeBuffer( N * N, 16 ); spec = new ComputeBuffer( N * N, 16 );
			aerA = new ComputeBuffer( N * N, 4 ); aerB = new ComputeBuffer( N * N, 4 );
			state.SetData( new Vector4[ N * N ] ); aerA.SetData( new float[ N * N ] );
			// (h, dh/dx, dh/dz, foam), sampled linear / repeat
			display = Make( "wakeDisplay", N, N, FilterMode.Bilinear, TextureWrapMode.Repeat );
			// read with Load: no sampler
			aerTex = Make( "wakeAeration", N, N, FilterMode.Point, TextureWrapMode.Repeat );

			// The boat must not feel its own steady wave pattern through the water queries (the controller models its hydrodynamics
			// already, and the 1-3 frame query latency turns that self-coupling into porpoising). A boat-frame running mean of the wake
			// height under the hull is subtracted inside the footprint: waves moving relative to the hull still get through.
			nearBuf = new ComputeBuffer( TW * TH, 4 );
			nearBuf.SetData( new float[ TW * TH ] );
			nearTex = Make( "wakeNearField", TW, TH, FilterMode.Point, TextureWrapMode.Clamp );

			hullTex = BakeHull( lines, out hullZ0, out hullZ1 );
			pileTex = BakePiles( colliders, out pileBox );
			current = this;
			SetGlobals();
		}

		static RenderTexture Make( string name, int w, int h, FilterMode filter, TextureWrapMode wrap )
		{
			var rt = new RenderTexture( w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
				{ name = name, enableRandomWrite = true, filterMode = filter, wrapMode = wrap };
			rt.Create();
			return rt;
		}

		// ---------------------------------------------------------------- setup

		// Hull immersion at the design waterline over the boat frame (x = |port|, z = forward), blurred to the grid scale so the moving
		// pressure field does not excite grid noise.
		static Texture2D BakeHull( World.Boat.HullLines L, out double Z0, out double Z1 )
		{
			const int NX = 24, NZ = 112;
			const double X1 = HULL_X1;
			Z0 = L.zAft - 0.8; Z1 = L.wlEnd + 0.8;
			const double R = 0.55;
			var data = new ushort[ NX * NZ ];
			for ( int iz = 0; iz < NZ; iz ++ )
			{
				for ( int ix = 0; ix < NX; ix ++ )
				{
					double x = ( ix + 0.5 ) / NX * X1, z = Z0 + ( iz + 0.5 ) / NZ * ( Z1 - Z0 );
					double sum = 0, wsum = 0;
					for ( int u = -3; u <= 3; u ++ )
					{
						for ( int v = -3; v <= 3; v ++ )
						{
							double dx = u / 3.0 * R, dz = v / 3.0 * R;
							double wgt = Math.Exp( - ( dx * dx + dz * dz ) / ( R * R * 0.4 ) );
							double y = L.bottomAt( x + dx, z + dz );
							sum += wgt * ( double.IsFinite( y ) ? Math.Max( 0, - y ) : 0 );
							wsum += wgt;
						}
					}

					data[ iz * NX + ix ] = HalfFloat.ToHalf( ix == NX - 1 || iz == 0 || iz == NZ - 1 ? 0 : sum / wsum );
				}
			}

			return HalfTexture( "wakeHull", NX, NZ, data );
		}

		static Texture2D HalfTexture( string name, int w, int h, ushort[] data )
		{
			// linear filter, clamp
			var t = new Texture2D( w, h, TextureFormat.RHalf, false, true ) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
			t.SetPixelData( data, 0 );
			t.Apply( false, true );
			return t;
		}

		// Fraction of each grid cell blocked by the pier piles standing in the water (cells are made rigid). Land, cliffs and the sea
		// stacks come from the terrain.
		static Texture2D BakePiles( World.Colliders colliders, out Vector4 box )
		{
			var piles = new System.Collections.Generic.List<World.ColliderCylinder>();
			if ( colliders != null ) foreach ( var c in colliders.cylinders ) if ( c.tag == "pile" && c.yMin < -0.1 && c.yMax > 0.2 ) piles.Add( c );
			double x0 = 0, z0 = 0, x1 = 1, z1 = 1;
			if ( piles.Count > 0 )
			{
				x0 = double.PositiveInfinity; z0 = double.PositiveInfinity; x1 = double.NegativeInfinity; z1 = double.NegativeInfinity;
				foreach ( var c in piles )
				{
					x0 = Math.Min( x0, c.x - c.radius ); x1 = Math.Max( x1, c.x + c.radius );
					z0 = Math.Min( z0, c.z - c.radius ); z1 = Math.Max( z1, c.z + c.radius );
				}

				x0 -= 1; x1 += 1; z0 -= 1; z1 += 1;
			}

			double res = Math.Max( 0.1, Math.Max( ( x1 - x0 ) / 2048, ( z1 - z0 ) / 2048 ) );
			int W = Math.Max( 2, ( int ) Math.Ceiling( ( x1 - x0 ) / res ) ), H = Math.Max( 2, ( int ) Math.Ceiling( ( z1 - z0 ) / res ) );
			var cover = new float[ W * H ];
			const int sub = 4;
			foreach ( var c in piles )
			{
				int ia = ( int ) Math.Floor( ( c.x - c.radius - x0 ) / res ), ib = ( int ) Math.Ceiling( ( c.x + c.radius - x0 ) / res );
				int ja = ( int ) Math.Floor( ( c.z - c.radius - z0 ) / res ), jb = ( int ) Math.Ceiling( ( c.z + c.radius - z0 ) / res );
				for ( int j = Math.Max( 0, ja ); j <= Math.Min( H - 1, jb ); j ++ )
				{
					for ( int i = Math.Max( 0, ia ); i <= Math.Min( W - 1, ib ); i ++ )
					{
						int n = 0;
						for ( int a = 0; a < sub; a ++ ) for ( int b = 0; b < sub; b ++ )
						{
							double px = x0 + ( i + ( a + 0.5 ) / sub ) * res, pz = z0 + ( j + ( b + 0.5 ) / sub ) * res;
							if ( ( px - c.x ) * ( px - c.x ) + ( pz - c.z ) * ( pz - c.z ) < c.radius * c.radius ) n ++;
						}

						cover[ j * W + i ] = ( float ) Math.Min( 1, cover[ j * W + i ] + n / ( double ) ( sub * sub ) );
					}
				}
			}

			// box filter over one grid cell -> blocked fraction of a cell centred at each texel
			int k = Math.Max( 1, ( int ) Tidewater.Engine.JS.Round( CELL / res / 2 ) );
			var data = new ushort[ W * H ];
			for ( int j = 0; j < H; j ++ )
			{
				for ( int i = 0; i < W; i ++ )
				{
					double s = 0; int n = 0;
					for ( int b = - k; b < k; b ++ ) for ( int a = - k; a < k; a ++ )
					{
						int ii = i + a, jj = j + b;
						if ( ii >= 0 && jj >= 0 && ii < W && jj < H ) s += cover[ jj * W + ii ];
						n ++;
					}

					data[ j * W + i ] = HalfFloat.ToHalf( s / n );
				}
			}

			box = new Vector4( ( float ) x0, ( float ) z0, ( float ) ( x1 - x0 ), ( float ) ( z1 - z0 ) );
			return HalfTexture( "wakePiles", W, H, data );
		}

		// ---------------------------------------------------------------- readers

		// the globals of a world without a wake: amount 0 makes every reader return nothing
		public static void SetDisabledGlobals()
		{
			Shader.SetGlobalVector( "_TWWakeA", new Vector4( 0, 0, 0, 0 ) );
			Shader.SetGlobalVector( "_TWWakeB", new Vector4( 1, 0, 0.03f, 0.08f ) );
			Shader.SetGlobalVector( "_TWWakeC", new Vector4( 0, 1, 0.3f, 0 ) );
			Shader.SetGlobalTexture( "_TWWakeDisplay", Texture2D.blackTexture );
			Shader.SetGlobalTexture( "_TWWakeNear", Texture2D.blackTexture );
			Shader.SetGlobalTexture( "_TWWakeAer", Texture2D.blackTexture );
		}

		Vector4 ReaderA => new Vector4( center.x, center.y, boatPos.x, boatPos.y );
		Vector4 ReaderB => new Vector4( boatRot.x, boatRot.y, dead.x, dead.y );
		Vector4 ReaderC => new Vector4( amount, amplitude, aerOut, 0 );

		public void SetGlobals()
		{
			Shader.SetGlobalVector( "_TWWakeA", ReaderA );
			Shader.SetGlobalVector( "_TWWakeB", ReaderB );
			Shader.SetGlobalVector( "_TWWakeC", ReaderC );
			Shader.SetGlobalTexture( "_TWWakeDisplay", display );
			Shader.SetGlobalTexture( "_TWWakeNear", nearTex );
			Shader.SetGlobalTexture( "_TWWakeAer", aerTex );
		}

		// the reader's inputs for a compute kernel that includes WaterQueryHeight.hlsl (the water queries, spray, breakers)
		public static void SetCompute( ComputeShader c, int kernel )
		{
			var w = current;
			if ( w == null )
			{
				c.SetVector( "_TWWakeA", Vector4.zero ); c.SetVector( "_TWWakeB", new Vector4( 1, 0, 0.03f, 0.08f ) ); c.SetVector( "_TWWakeC", new Vector4( 0, 1, 0.3f, 0 ) );
				c.SetTexture( kernel, "_TWWakeDisplay", Texture2D.blackTexture ); c.SetTexture( kernel, "_TWWakeNear", Texture2D.blackTexture );
				c.SetTexture( kernel, "_TWWakeAer", Texture2D.blackTexture );
				return;
			}

			c.SetVector( "_TWWakeA", w.ReaderA ); c.SetVector( "_TWWakeB", w.ReaderB ); c.SetVector( "_TWWakeC", w.ReaderC );
			c.SetTexture( kernel, "_TWWakeDisplay", w.display ); c.SetTexture( kernel, "_TWWakeNear", w.nearTex ); c.SetTexture( kernel, "_TWWakeAer", w.aerTex );
		}

		// ---------------------------------------------------------------- per frame

		void Dispatch()
		{
			// kernel parameters
			cs.SetVector( "_WKA", new Vector4( origin.x, origin.y, prev.x, prev.y ) );
			cs.SetVector( "_WKC", new Vector4( dt, reset, G.seaLevel, G.time ) );
			cs.SetVector( "_WKD", new Vector4( source, wash, bow, speed ) );
			cs.SetVector( "_WKE", new Vector4( washW, boil, visc, dyn ) );
			cs.SetVector( "_WKF", new Vector4( hollow, hollowK, foamGain, aerGain ) );
			cs.SetVector( "_WKG", new Vector4( nearRate, trim.x, trim.y, trim.z ) );
			cs.SetVector( "_WKH", pileBox );
			cs.SetVector( "_WKI", new Vector4( ( float ) HULL_X1, ( float ) hullZ0, ( float ) ( hullZ1 - hullZ0 ), ( float ) lines.zAft ) );
			cs.SetVector( "_WKJ", new Vector4( ( float ) ( lines.zAft + 0.25 ), 0, 0, 0 ) );
			cs.SetVector( "_TWWakeA", ReaderA ); cs.SetVector( "_TWWakeB", ReaderB ); cs.SetVector( "_TWWakeC", ReaderC );
			cs.SetVector( "_TWTerrainParams", new Vector4( ( float ) terrain.origin, ( float ) terrain.size, terrain.res, 0 ) );
			cs.SetVector( "_TWShoreParams", new Vector4( terrain.shoreRes, 0, 0, 0 ) );

			foreach ( int k in new[] { kRows, kCols, kInv } )
			{
				cs.SetBuffer( k, "_WakeState", state ); cs.SetBuffer( k, "_WakeScratch", scratch ); cs.SetBuffer( k, "_WakeSpec", spec );
				cs.SetBuffer( k, "_WakeAerA", aerA ); cs.SetBuffer( k, "_WakeAerB", aerB ); cs.SetBuffer( k, "_WakeNearBuf", nearBuf );
				cs.SetTexture( k, "_WakeHull", hullTex ); cs.SetTexture( k, "_WakePile", pileTex );
				cs.SetTexture( k, "_TWHeightTex", terrain.heightTexture ); cs.SetTexture( k, "_TWShoreTex", terrain.shoreTexture );
				cs.SetTexture( k, "_TWNormalTex", terrain.normalTexture );
			}

			cs.SetTexture( kRows, "_WakeDisplayOut", display ); cs.SetTexture( kRows, "_WakeAerOut", aerTex );
			cs.SetTexture( kCols, "_TWWakeDisplay", display ); cs.SetTexture( kCols, "_WakeNearOut", nearTex );
			cs.Dispatch( kRows, N, 1, 1 );
			cs.Dispatch( kCols, N, 1, 1 );
			cs.Dispatch( kInv, N, 1, 1 );
		}

		public void Update( double dtFrame )
		{
			var b = boat;
			double spd = Tidewater.Engine.JS.Hypot( b.velocity.x, b.velocity.z );
			bool moving = spd > 0.5 || ( b.driven && Math.Abs( b.throttle ) > 0.04 );
			idleTime = moving ? 0 : idleTime + dtFrame;

			if ( idleTime > settleTime )
			{
				// asleep: nothing is dispatched; the (faded out) output is ignored by the shaders
				sleeping = true;
				amount = 0;
				// one step on the first frame compiles the pipelines behind the loading screen, not when the player first opens the throttle
				if ( ! primed ) Prime();
				SetGlobals();
				return;
			}

			if ( sleeping )
			{
				sleeping = false;
				hasWindow = false;
				reset = 1;
			}

			amount = ( float ) Math.Min( 1, ( settleTime - idleTime ) / 4 );
			double h = Math.Min( Math.Max( dtFrame, 1.0 / 240 ), 1.0 / 30 );
			dt = ( float ) h;

			// ---- window: keep the boat inside a box around the centre (moves by whole cells)
			double bx = b.position.x / CELL, bz = b.position.z / CELL;
			const double box = 170; // the boat runs up to 68 m off centre: ~170 m of track behind it, ~25 m ahead
			if ( ! hasWindow || Math.Abs( bx - centerX ) > N || Math.Abs( bz - centerZ ) > N )
			{
				centerX = Tidewater.Engine.JS.Round( bx ); centerZ = JS.Round( bz );
				hasWindow = true;
				reset = 1;
			}

			if ( bx - centerX > box ) centerX = Math.Ceiling( bx - box );
			if ( centerX - bx > box ) centerX = Math.Floor( bx + box );
			if ( bz - centerZ > box ) centerZ = Math.Ceiling( bz - box );
			if ( centerZ - bz > box ) centerZ = Math.Floor( bz + box );
			prev = origin;
			origin = new Vector2( ( float ) ( centerX - HALF ), ( float ) ( centerZ - HALF ) );
			if ( reset > 0.5f ) prev = origin;
			center = new Vector2( ( float ) ( centerX * CELL ), ( float ) ( centerZ * CELL ) );

			// ---- boat
			var fw = b.forward( _fwd );
			double yaw = Math.Atan2( fw.x, fw.z );
			boatPos = new Vector2( ( float ) b.position.x, ( float ) b.position.z );
			boatRot = new Vector2( ( float ) Math.Cos( yaw ), ( float ) Math.Sin( yaw ) );
			ScheduleTrim( spd );
			speed = ( float ) spd;
			dyn = ( float ) ( dynamicPressure * spd * spd / ( 2 * GRAVITY ) );
			double plane = JMath.smoothstep( spd, 2, 9 );
			hollow = ( float ) ( 0.35 + 0.015 * spd * spd );
			hollowK = ( float ) ( 0.3 * plane );
			source = ( float ) ( sourceGain * ( 1 + 0.2 * plane ) );
			double prop = b.driven ? Math.Abs( b.throttle ) * b.rpm : 0;
			wash = ( float ) ( prop * 1.0 + JMath.smoothstep( spd, 1.5, 7 ) * 0.5 );
			washW = ( float ) ( 0.7 + 0.6 * JMath.smoothstep( spd, 2, 9 ) );
			bow = ( float ) JMath.smoothstep( spd, 3.5, 9 );
			nearRate = reset > 0.5f ? 1 : ( float ) ( 1 - Math.Exp( - h / 0.25 ) );

			SetGlobals();
			Dispatch();
			primed = true;
			reset = 0;
			stepCount ++;
		}

		void Prime()
		{
			primed = true;
			reset = 1;
			prev = origin;
			Dispatch();
		}

		// Immersion change of the hull relative to its design waterline, c + a x + b z (m), scheduled from speed rather than fitted to the
		// boat's pose: the boat feels its own pressure field through the water queries 1-3 frames late, and any dependence of the source
		// on the boat's heave / pitch / roll closes a delayed feedback loop (porpoising). Coming onto the plane the hull rises and the bow
		// lifts out, so the pressure footprint moves aft.
		void ScheduleTrim( double spd )
		{
			double plane = JMath.smoothstep( spd, 3, 10 );
			trim = new Vector3( ( float ) ( - 0.08 * plane ), 0, ( float ) ( - 0.035 * plane ) );
		}

		public void Dispose()
		{
			if ( current == this ) { current = null; SetDisabledGlobals(); }
			foreach ( var b in new[] { state, scratch, spec, aerA, aerB, nearBuf } ) if ( b != null ) b.Release();
			foreach ( var r in new[] { display, aerTex, nearTex } ) if ( r != null ) { r.Release(); UnityEngine.Object.DestroyImmediate( r ); }
			if ( hullTex != null ) UnityEngine.Object.DestroyImmediate( hullTex );
			if ( pileTex != null ) UnityEngine.Object.DestroyImmediate( pileTex );
		}
	}
}
