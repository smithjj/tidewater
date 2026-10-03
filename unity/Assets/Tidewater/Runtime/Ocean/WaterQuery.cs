using System;
using Tidewater.Core;
using Tidewater.World;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Port of src/ocean/WaterQuery.js: water surface queries on the GPU (Shaders/Ocean/WaterQuery.compute).
//   - slot 0 is always the camera (consumed the same frame by the waterline / underwater passes)
//   - other slots are gameplay points (boat hull samples, swimmer, particles...)
// Results are read back asynchronously for CPU physics (1-3 frames latency).
//
// Usage: Allocate( "boat", 24 ) hands out a block of slots; SetPoint( slot, x, z ) each frame (sim xz); Update() once per
// frame after the FFT; Get( slot ) returns the latest read-back { height, nx, nz, floor }.
namespace Tidewater.Ocean
{
	public static class QueryLimits
	{
		public const int MAX_QUERIES = 256; // the whole table: slot 0 is the camera, the rest are named blocks
		public const int QUERY_WORKGROUP = 64; // threads per workgroup in the query kernel

		// Workgroups to dispatch for `count` slots in use. The kernel must cover every slot: a slot past the last dispatched
		// workgroup is never computed and reads back as zeros, with no error anywhere.
		public static int QueryWorkgroups( int count ) => ( Math.Min( Math.Max( count, 1 ), MAX_QUERIES ) + QUERY_WORKGROUP - 1 ) / QUERY_WORKGROUP;
	}

	public struct WaterSample
	{
		public float height, nx, nz, floor;
	}

	public sealed class WaterQuery : IDisposable
	{
		const int MAX = QueryLimits.MAX_QUERIES;

		readonly ComputeShader cs;
		readonly int kernel;
		readonly OceanFFT fft;
		readonly TerrainGPU terrain; // optional
		readonly ShoreWaves shore; // optional (needs the terrain)
		readonly ComputeBuffer inputBuffer, results;
		readonly Vector4[] inputsGpu = new Vector4[ MAX ];

		public readonly float[] inputs = new float[ MAX * 4 ];
		public readonly float[] cpu = new float[ MAX * 4 ];
		public bool cpuValid;
		public int count = 1;

		// read-back bookkeeping: `version` increments with every new result, `resultTime` is the simulation time the result
		// was computed at (it arrives 1-3 frames later)
		public int version;
		public double resultTime;
		public readonly float[] resultInputs = new float[ MAX * 4 ]; // query points of the latest result
		readonly float[] issueInputs = new float[ MAX * 4 ];
		public float latency = 0.05f; // s, smoothed age of the results when they arrive
		readonly System.Collections.Generic.Dictionary<string, (int start, int n)> slots = new System.Collections.Generic.Dictionary<string, (int, int)>();
		bool pending;
		double issued;
		AsyncGPUReadbackRequest request;

		public float amplitude = 1;
		// test hook: use the exact integer hash for the per-wave random (Shaders/Ocean/ShoreWaves.hlsl, TW_SHORE_ORACLE)
		public bool oracleHash;
		// the results table, for shaders that read the same-frame camera state (slot 0): ( height, nx, nz, sea floor )
		public ComputeBuffer resultsBuffer => results;

		public WaterQuery( ComputeShader shader, OceanFFT fft, TerrainGPU terrain = null, ShoreWaves shore = null )
		{
			cs = shader; this.fft = fft; this.terrain = terrain; this.shore = terrain != null ? shore : null;
			kernel = cs.FindKernel( "WaterQueries" );
			inputBuffer = new ComputeBuffer( MAX, 16 );
			results = new ComputeBuffer( MAX, 16 );
			results.SetData( new Vector4[ MAX ] );
		}

		// ---------------------------------------------------------------- CPU API

		public void SetCamera( float x, float z ) { inputs[ 0 ] = x; inputs[ 1 ] = z; }

		// allocate named slots (e.g. 'boat' -> 24 points). Returns the first index.
		public int Allocate( string name, int n )
		{
			if ( slots.TryGetValue( name, out var s ) ) return s.start;
			int start = count;
			if ( start + n > MAX ) throw new Exception( "WaterQuery: out of slots" );
			slots[ name ] = ( start, n );
			count += n;
			return start;
		}

		public void SetPoint( int i, float x, float z ) { inputs[ i * 4 ] = x; inputs[ i * 4 + 1 ] = z; }

		// last read-back results
		public WaterSample Get( int i )
		{
			return new WaterSample { height = cpu[ i * 4 ], nx = cpu[ i * 4 + 1 ], nz = cpu[ i * 4 + 2 ], floor = cpu[ i * 4 + 3 ] };
		}

		// Dispatch the queries for the points set so far and start a read-back if none is in flight.
		public void Update()
		{
			for ( int i = 0; i < MAX; i ++ ) inputsGpu[ i ] = new Vector4( inputs[ i * 4 ], inputs[ i * 4 + 1 ], inputs[ i * 4 + 2 ], inputs[ i * 4 + 3 ] );
			inputBuffer.SetData( inputsGpu );

			cs.SetTexture( kernel, "_TWOceanDisp", fft.displacementTexture );
			var sizes = new Vector4[ 4 ];
			for ( int c = 0; c < 4; c ++ ) sizes[ c ] = new Vector4( c < fft.cascades ? ( float ) fft.sizes[ c ] : 1, 0, 0, 0 );
			cs.SetVectorArray( "_TWOceanSizes", sizes );
			cs.SetVector( "_QAmp", new Vector4( amplitude, G.seaLevel, terrain != null ? 1 : 0, shore != null ? 1 : 0 ) );
			if ( terrain != null )
			{
				cs.SetTexture( kernel, "_TWHeightTex", terrain.heightTexture );
				cs.SetVector( "_TWTerrainParams", new Vector4( ( float ) terrain.origin, ( float ) terrain.size, terrain.res, 0 ) );
				cs.SetTexture( kernel, "_TWShoreTex", terrain.shoreTexture );
				cs.SetVector( "_TWShoreParams", new Vector4( terrain.shoreRes, 0, 0, 0 ) );
			}
			else
			{
				// the kernel never reads them without terrain, but the binding has to exist
				cs.SetTexture( kernel, "_TWHeightTex", Texture2D.blackTexture );
				cs.SetTexture( kernel, "_TWShoreTex", Texture2D.blackTexture );
			}

			if ( shore != null ) shore.SetCompute( cs, kernel, G.time, G.seaLevel );
			else cs.SetTexture( kernel, "_TWShoreDirTex", Texture2D.blackTexture );

			cs.SetBuffer( kernel, "_QueryInputs", inputBuffer );
			cs.SetBuffer( kernel, "_QueryResults", results );
			if ( oracleHash ) cs.EnableKeyword( "TW_SHORE_ORACLE" ); else cs.DisableKeyword( "TW_SHORE_ORACLE" );
			cs.Dispatch( kernel, QueryLimits.QueryWorkgroups( count ), 1, 1 );

			if ( ! pending )
			{
				issued = G.time;
				Array.Copy( inputs, issueInputs, inputs.Length );
				pending = true;
				request = AsyncGPUReadback.Request( results, OnReadback );
			}
		}

		// Block until the read-back in flight has arrived (tests, tools; the game reads the latest result instead).
		public void Flush()
		{
			if ( pending ) { request.WaitForCompletion(); }
		}

		void OnReadback( AsyncGPUReadbackRequest req )
		{
			pending = false;
			if ( req.hasError ) return;
			var data = req.GetData<float>();
			for ( int i = 0; i < data.Length && i < cpu.Length; i ++ ) cpu[ i ] = data[ i ];
			cpuValid = true;
			latency += ( ( float ) Math.Min( G.time - issued, 0.25 ) - latency ) * 0.2f;
			resultTime = issued;
			Array.Copy( issueInputs, resultInputs, issueInputs.Length );
			version ++;
		}

		public void Dispose()
		{
			inputBuffer?.Release(); results?.Release();
		}
	}
}
