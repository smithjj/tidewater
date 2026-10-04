using System;
using Tidewater.Core;
using Tidewater.World;
using UnityEngine;

// Port of src/ocean/ShoreSim.js: Eulerian state over the main beach, updated every frame on the GPU (Shaders/Ocean/
// ShoreSim.compute; ShoreSim.hlsl documents the four channels and the lookups):
//   r foam carried by the water, g sand wetness, b foam stranded on the sand, a flow speed along the local wave direction.
// Two RGBA16F textures: A is read by materials, B written by the sim and copied to A after each step.
// Published: _TWShoreSimState, _TWShoreSimLace (nearest), _TWShoreSimParams = ( min.x, min.z, size, enabled ).
namespace Tidewater.Ocean
{
	public sealed class ShoreSim : IDisposable
	{
		public const int RES = 768;

		public readonly Vector2 center;
		public readonly float size;
		public float dryTime = 28f;
		public float foamLife = 4.5f; // on the thin swash sheet
		public float surfFoamLife = 2.6f; // in the turbulent surf zone
		public float residueLife = 5.0f;
		public float foamGen = 1.0f;
		public float depositGain = 0.02f; // foam per drop

		public readonly LaceTexture.Lace lace;
		public RenderTexture stateA, stateB;
		// foam deposited by spray falling back into the water (drops per texel; the spray update adds, this consumes and clears)
		public ComputeBuffer deposit;

		readonly ComputeShader cs;
		readonly int kernel;
		readonly TerrainGPU terrain;
		readonly ShoreWaves shore;

		public ShoreSim( ComputeShader shader, TerrainGPU terrain, ShoreWaves shore, Vector2? center = null, float size = 380 )
		{
			cs = shader; this.terrain = terrain; this.shore = shore;
			this.center = center ?? new Vector2( 10, -25 );
			this.size = size;
			kernel = cs.FindKernel( "ShoreSimStep" );

			// tileable lace (bubble strands, bubbles, mottling, per-cell random), generated once on the CPU
			lace = LaceTexture.Make();
			// cheap filtered wave direction over the region (lace motion, surf zone turbidity)
			shore.BuildDirTexture( new Vector2( this.center.x - size / 2, this.center.y - size / 2 ), size );

			// nearest + read with loads everywhere (manual bilinear): no sampler binding in any material
			stateA = Make( "shoreStateA" );
			stateB = Make( "shoreStateB" );
			deposit = new ComputeBuffer( RES * RES, 4 );
			deposit.SetData( new uint[ RES * RES ] );
		}

		static RenderTexture Make( string name )
		{
			var rt = new RenderTexture( RES, RES, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
				{ name = name, enableRandomWrite = true, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
			rt.Create();
			return rt;
		}

		Vector4 Params => new Vector4( center.x - size / 2, center.y - size / 2, size, 1 );

		// the globals of a world without the sim: a region far away, so every lookup is outside it (no foam, no wetness)
		public static void SetDisabledGlobals()
		{
			Shader.SetGlobalVector( "_TWShoreSimParams", new Vector4( 1e6f, 1e6f, 1, 0 ) );
			Shader.SetGlobalTexture( "_TWShoreSimState", Texture2D.blackTexture );
			Shader.SetGlobalTexture( "_TWShoreSimLace", Texture2D.blackTexture );
		}

		public void SetGlobals()
		{
			Shader.SetGlobalVector( "_TWShoreSimParams", Params );
			Shader.SetGlobalTexture( "_TWShoreSimState", stateA );
			Shader.SetGlobalTexture( "_TWShoreSimLace", lace.nearest );
			Shader.SetGlobalTexture( "_TWSurfLace", lace.texture );
		}

		// one step: B from A, then A := B
		public void Update( float dt, float time, float seaLevel )
		{
			shore.SetCompute( cs, kernel, time, seaLevel );
			cs.SetTexture( kernel, "_TWHeightTex", terrain.heightTexture );
			cs.SetTexture( kernel, "_TWShoreTex", terrain.shoreTexture );
			cs.SetVector( "_TWTerrainParams", new Vector4( ( float ) terrain.origin, ( float ) terrain.size, terrain.res, 0 ) );
			cs.SetVector( "_TWShoreParams", new Vector4( terrain.shoreRes, 0, 0, 0 ) );
			cs.SetVector( "_TWShoreSimParams", Params );
			cs.SetVector( "_SSLife", new Vector4( dryTime, foamLife, surfFoamLife, residueLife ) );
			cs.SetVector( "_SSGen", new Vector4( foamGen, depositGain, dt, seaLevel ) );
			cs.SetTexture( kernel, "_TWShoreSimState", stateA );
			cs.SetTexture( kernel, "_TWShoreSimLace", lace.nearest );
			cs.SetTexture( kernel, "_ShoreSimLaceMips", lace.texture );
			cs.SetTexture( kernel, "_ShoreSimOut", stateB );
			cs.SetBuffer( kernel, "_ShoreSimDeposit", deposit );
			cs.Dispatch( kernel, ( RES + 7 ) / 8, ( RES + 7 ) / 8, 1 );
			Graphics.CopyTexture( stateB, stateA );
		}

		public void Dispose()
		{
			if ( stateA != null ) { stateA.Release(); UnityEngine.Object.DestroyImmediate( stateA ); }
			if ( stateB != null ) { stateB.Release(); UnityEngine.Object.DestroyImmediate( stateB ); }
			deposit?.Release();
			stateA = stateB = null; deposit = null;
		}
	}
}
