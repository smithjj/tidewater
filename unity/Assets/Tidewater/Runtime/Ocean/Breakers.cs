using System;
using System.Collections.Generic;
using Tidewater.Core;
using Tidewater.Fx;
using Tidewater.World;
using UnityEngine;
using UnityEngine.Rendering;
using TerrainData = Tidewater.World.TerrainData;

// Port of src/ocean/Breakers.js: plunging breakers along the main beach.
//
// Stations are laid out every ~0.6 m along the shoreline (CPU, once). Each frame Breakers.compute marches every station's transect
// through the surf zone, finds the crests of the breaking waves (crossings of the wave phase with integers: the same analytic field
// that drives the water surface) and stores for every crest the lip root, the breaking progress and the wave height in the crest
// table, and emits spray (Spray.cs) where a real breaker makes it. The thrown lip is a separate ribbon mesh extruded along the crest
// (BreakersLip.shader), drawn procedurally from the crest table.
namespace Tidewater.Ocean
{
	public sealed class Breakers : IDisposable
	{
		const int NV = 20; // profile vertices across the lip (2 on the back of the crest + 18 along the curtain)

		readonly ComputeShader cs;
		readonly int kernel;
		readonly Material lipMaterial;
		readonly OceanFFT fft;
		readonly TerrainGPU terrain;
		readonly ShoreWaves shore;
		readonly Spray spray;
		readonly MaterialPropertyBlock props = new MaterialPropertyBlock();

		public readonly int NS;            // stations
		public readonly float spacing;     // m between stations
		ComputeBuffer stations, crest;

		// tuning
		public float sprayAmount = 1f;     // emission multiplier
		public float sheet = 1f;           // lip opacity multiplier
		public float emitRange = 240f;     // no emission beyond this camera distance
		float budget = 0.3f;               // emission scale that keeps the spray ring from wrapping (see Budget)

		// the ring head, read back a few frames late, steers the emission budget
		struct HeadSample { public uint head; public double t; }
		readonly List<HeadSample> hist = new List<HeadSample>();
		bool pending;
		double issuedAt;

		public ComputeBuffer crestBuffer => crest;

		public Breakers( ComputeShader shader, Shader lipShader, OceanFFT fft, TerrainGPU terrainGpu, ShoreWaves shore, TerrainData terrainData, Spray spray )
		{
			cs = shader; kernel = cs.FindKernel( "SurfCrests" );
			this.fft = fft; terrain = terrainGpu; this.shore = shore; this.spray = spray;

			var st = BuildStations( terrainData );
			NS = st.count; spacing = st.spacing;
			var stationData = new Vector4[ Math.Max( 1, NS ) ];
			for ( int i = 0; i < NS; i ++ ) stationData[ i ] = new Vector4( st.data[ i * 4 ], st.data[ i * 4 + 1 ], st.data[ i * 4 + 2 ], st.data[ i * 4 + 3 ] );
			stations = new ComputeBuffer( Math.Max( 1, NS ), 16 ); stations.SetData( stationData );
			// per station and slot (wave parity): 3 x float4
			//   (root.xyz, b) (back.xyz, H) (dir.xz, trough y, wave id 1..1024 or 0 = none)
			crest = new ComputeBuffer( Math.Max( 1, NS * 6 ), 16 ); crest.SetData( new Vector4[ Math.Max( 1, NS * 6 ) ] );

			lipMaterial = new Material( lipShader ) { name = "BreakerLip", hideFlags = HideFlags.HideAndDontSave };
			if ( NS <= 1 ) Debug.LogWarning( "Breakers: no beach stations found" );

			// the spray shader asks the crests for the wave's shadow on the particles they made
			Shader.SetGlobalBuffer( "_BrkCrest", crest );
			Shader.SetGlobalVector( "_BrkParams", new Vector4( NS * 2, 0, 0, 0 ) );
		}

		// ------------------------------------------------------------------ crest finder + emitters

		public void Update( Vector3 cameraSim, float amplitude, float dt, float time )
		{
			if ( NS <= 1 ) return;
			cs.SetVector( "_BrkP0", new Vector4( cameraSim.x, cameraSim.y, cameraSim.z, amplitude ) );
			cs.SetVector( "_BrkP1", new Vector4( sprayAmount, sheet, emitRange, budget ) );
			cs.SetVector( "_BrkP2", new Vector4( G.seaLevel, dt, G.windDir.x, G.windDir.y ) );
			cs.SetVector( "_BrkP3", new Vector4( G.windSpeed, NS, spacing, 0 ) );
			cs.SetBuffer( kernel, "_BrkStations", stations );
			cs.SetBuffer( kernel, "_BrkCrestW", crest );
			spray.Bind( cs, kernel );
			WaterQuery.SetHeightInputs( cs, kernel, fft, terrain, shore, amplitude );
			cs.Dispatch( kernel, ( NS + 63 ) / 64, 1, 1 );
			UpdateBudget( time );
		}

		// Emission budget. The emitters write into the spray's GPU ring (NG slots): emitting faster than NG per particle lifetime
		// overwrites particles a few frames after they're born (big surf made ~8k a frame into a 32k ring: every sprite popped out
		// again within ~60 ms). The ring head is read back (a few frames late) and the emission scale steered so the ring holds ~2 s
		// of spray.
		void UpdateBudget( float time )
		{
			if ( pending ) return;
			pending = true;
			issuedAt = time;
			AsyncGPUReadback.Request( spray.headBuffer, req =>
			{
				pending = false;
				if ( req.hasError ) return;
				uint head = req.GetData<uint>()[ 0 ];
				double now = issuedAt;
				if ( hist.Count == 0 || hist[ hist.Count - 1 ].t != now ) hist.Add( new HeadSample { head = head, t = now } );
				while ( hist.Count > 2 && now - hist[ 0 ].t > 2.5 ) hist.RemoveAt( 0 );
				if ( hist.Count > 1 )
				{
					var a = hist[ 0 ]; var b = hist[ hist.Count - 1 ];
					double dtt = b.t - a.t;
					if ( dtt > 1.0 )
					{
						double rate = unchecked( b.head - a.head ) / dtt; // particles / s (uint wrap safe)
						double target = spray.NG / 2.0;
						double want = rate > 1 ? budget * Math.Sqrt( target / rate ) : 1;
						budget = ( float ) Math.Min( 1, Math.Max( 0.05, budget + ( want - budget ) * 0.04 ) );
					}
				}
			} );
		}

		// ------------------------------------------------------------------ lip sheet

		public void Draw( Camera cam )
		{
			if ( NS <= 1 ) return;
			props.SetBuffer( "_BrkCrest", crest );
			props.SetTexture( "_BrkLace", LaceTexture.Make().texture );
			props.SetVector( "_BrkLip", new Vector4( sheet, spacing, 0, 0 ) );
			var rp = new RenderParams( lipMaterial )
			{
				camera = cam,
				worldBounds = new Bounds( Vector3.zero, Vector3.one * 1e7f ),
				matProps = props,
				shadowCastingMode = ShadowCastingMode.Off,
				receiveShadows = false,
			};
			int strips = ( NS - 1 ) * 2;
			Graphics.RenderPrimitives( rp, MeshTopology.Triangles, strips * ( NV - 1 ) * 6 );
		}

		// ------------------------------------------------------------------ stations

		public struct Stations { public float[] data; public int count; public float spacing; }

		// Shoreline stations along the main beach: evenly spaced along the (smoothed) shoreline, each with the unit direction toward
		// the sea. data: n * ( x, z, nx, nz ).
		public static Stations BuildStations( TerrainData terrain, double x0 = -175, double x1 = 195, double spacing = 0.6 )
		{
			// shoreline z(x): first land found marching north from the water
			var pts = new List<double[]>();
			for ( double x = x0; x <= x1; x += 0.5 )
			{
				if ( terrain.HeightAt( x, 12 ) > -0.3 ) continue; // not open water in front
				double z = 12; double? zs = null;
				for ( ; z > -110; z -= 0.5 )
				{
					if ( terrain.HeightAt( x, z ) > 0 )
					{
						double lo = z, hi = z + 0.5; // land at lo, water at hi
						for ( int k = 0; k < 16; k ++ )
						{
							double mid = ( lo + hi ) * 0.5;
							if ( terrain.HeightAt( x, mid ) > 0 ) lo = mid; else hi = mid;
						}

						zs = ( lo + hi ) * 0.5;
						break;
					}
				}

				if ( zs.HasValue ) pts.Add( new[] { x, zs.Value } );
			}

			// break into continuous runs, keep the longest (the main beach)
			var best = new List<double[]>(); var run = new List<double[]>();
			for ( int k = 0; k < pts.Count; k ++ )
			{
				if ( run.Count > 0 && ( Math.Abs( pts[ k ][ 1 ] - run[ run.Count - 1 ][ 1 ] ) > 3 || pts[ k ][ 0 ] - run[ run.Count - 1 ][ 0 ] > 1.01 ) )
				{
					if ( run.Count > best.Count ) best = run;
					run = new List<double[]>();
				}

				run.Add( pts[ k ] );
			}

			if ( run.Count > best.Count ) best = run;

			// smooth the polyline (the transects should not follow every wiggle of the waterline)
			var sm = new List<double[]>();
			foreach ( var p in best ) sm.Add( new[] { p[ 0 ], p[ 1 ] } );
			for ( int it = 0; it < 30; it ++ )
			{
				var nx = new List<double[]>( sm.Count );
				for ( int k = 0; k < sm.Count; k ++ )
				{
					if ( k == 0 || k == sm.Count - 1 ) nx.Add( sm[ k ] );
					else nx.Add( new[] { sm[ k ][ 0 ], ( sm[ k - 1 ][ 1 ] + 2 * sm[ k ][ 1 ] + sm[ k + 1 ][ 1 ] ) * 0.25 } );
				}

				sm = nx;
			}

			// resample by arc length
			var outp = new List<float>();
			double acc = 0, next = 0;
			for ( int k = 1; k < sm.Count; k ++ )
			{
				double ax = sm[ k - 1 ][ 0 ], az = sm[ k - 1 ][ 1 ], bx = sm[ k ][ 0 ], bz = sm[ k ][ 1 ];
				double seg = Math.Sqrt( ( bx - ax ) * ( bx - ax ) + ( bz - az ) * ( bz - az ) );
				while ( next <= acc + seg )
				{
					double t = ( next - acc ) / seg;
					double x = ax + ( bx - ax ) * t, z = az + ( bz - az ) * t;
					// normal toward the sea (+z side of a west->east polyline)
					double tx = ( bx - ax ) / seg, tz = ( bz - az ) / seg;
					outp.Add( ( float ) x ); outp.Add( ( float ) z ); outp.Add( ( float ) -tz ); outp.Add( ( float ) tx );
					next += spacing;
				}

				acc += seg;
			}

			return new Stations { data = outp.ToArray(), count = outp.Count / 4, spacing = ( float ) spacing };
		}

		public void Dispose()
		{
			stations?.Release(); crest?.Release();
			stations = crest = null;
			if ( lipMaterial != null ) UnityEngine.Object.DestroyImmediate( lipMaterial );
		}
	}
}
