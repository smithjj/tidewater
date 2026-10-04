using System;
using Tidewater.Core;
using Tidewater.World;
using UnityEngine;

// Port of the bake side of installUnderwaterLighting (src/ocean/UnderwaterLighting.js). The wave terms the underwater lighting needs
// at a point depend on its xz only; they are baked once per frame into two camera-centred maps (0.25 m texels over 128 m, 2 m over
// 1 km, snapped to their texels so they don't crawl), and the lighting pass (UnderwaterLighting.shader, via
// UnderwaterLightingPass) reads them. Beyond the far map: a flat sea.
//   A = ( height - sea level, slope.xy, foam ),  B = ( mean level - sea level, caustic detail k )   RGBA16F 512^2
// Published: _TWUwWaves0/1, _TWUwLevel0/1, _TWUwOrigins = ( origin0, origin1 ), _TWUwParams = ( reach, sea level ).
namespace Tidewater.Ocean
{
	public sealed class UnderwaterLighting : IDisposable
	{
		public const int MAP_N = 512;
		static readonly float[] EXTENTS = { 128f, 1024f };

		sealed class Level
		{
			public float extent, texel;
			public RenderTexture A, B;
			public Vector2 origin;
		}

		readonly ComputeShader cs;
		readonly int kernel;
		readonly Level[] levels = new Level[ 2 ];

		// the baked maps and where they are centred (for the oracles and debug tools)
		public RenderTexture WavesMap( int level ) => levels[ level ].A;
		public RenderTexture LevelMap( int level ) => levels[ level ].B;
		public Vector2 Origin( int level ) => levels[ level ].origin;
		public float Texel( int level ) => levels[ level ].texel;

		public UnderwaterLighting( ComputeShader shader )
		{
			cs = shader;
			kernel = cs.FindKernel( "BakeMaps" );
			for ( int l = 0; l < 2; l ++ )
			{
				levels[ l ] = new Level { extent = EXTENTS[ l ], texel = EXTENTS[ l ] / MAP_N, A = Make( "uwWaves" + l ), B = Make( "uwLevel" + l ) };
			}
		}

		static RenderTexture Make( string name )
		{
			var rt = new RenderTexture( MAP_N, MAP_N, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear )
				{ name = name, enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
			rt.Create();
			return rt;
		}

		// the wave terms depend on the whole ocean state: bake both maps around the (Unity world) camera position
		public void Update( Vector3 cameraUnity, OceanFFT fft, TerrainGPU terrain, ShoreWaves shore, ShoreSim sim, SeaDetail detail, float time, float seaLevel )
		{
			float camX = cameraUnity.x, camZ = -cameraUnity.z; // sim
			cs.SetTexture( kernel, "_TWOceanDisp", fft.displacementTexture );
			cs.SetTexture( kernel, "_TWOceanDeriv", fft.derivativeTexture );
			var sizes = new Vector4[ 4 ];
			for ( int c = 0; c < 4; c ++ ) sizes[ c ] = new Vector4( c < fft.cascades ? ( float ) fft.sizes[ c ] : 1, 0, 0, 0 );
			cs.SetVectorArray( "_TWOceanSizes", sizes );
			cs.SetTexture( kernel, "_TWHeightTex", terrain.heightTexture );
			cs.SetTexture( kernel, "_TWNormalTex", terrain.normalTexture );
			cs.SetTexture( kernel, "_TWShoreTex", terrain.shoreTexture );
			cs.SetVector( "_TWTerrainParams", new Vector4( ( float ) terrain.origin, ( float ) terrain.size, terrain.res, 0 ) );
			cs.SetVector( "_TWShoreParams", new Vector4( terrain.shoreRes, 0, 0, 0 ) );
			shore.SetCompute( cs, kernel, time, seaLevel );
			cs.SetTexture( kernel, "_TWShoreSimState", sim.stateA );
			cs.SetTexture( kernel, "_TWShoreSimLace", sim.lace.nearest );
			cs.SetVector( "_TWShoreSimParams", new Vector4( sim.center.x - sim.size / 2, sim.center.y - sim.size / 2, sim.size, 1 ) );
			cs.SetTexture( kernel, "_TWSeaDetailNoise", detail.texture );
			cs.SetVector( "_TWSeaDetail", new Vector4( detail.offset.x, detail.offset.y, detail.gustAmount, detail.slickAmount ) );
			cs.SetVector( "_TWSeaDetail2", new Vector4( detail.streakAmount, 0, 0, 0 ) );
			cs.SetVector( "_TWWind", new Vector4( G.windDir.x, G.windDir.y, G.windSpeed, 0 ) );

			foreach ( var lv in levels )
			{
				// snapped to whole texels: the texel centres stay fixed in the world
				float half = lv.extent / 2;
				lv.origin = new Vector2( Mathf.Floor( camX / lv.texel ) * lv.texel - half, Mathf.Floor( camZ / lv.texel ) * lv.texel - half );
				cs.SetVector( "_UwBake", new Vector4( lv.origin.x, lv.origin.y, lv.texel, seaLevel ) );
				cs.SetTexture( kernel, "_UwOutA", lv.A );
				cs.SetTexture( kernel, "_UwOutB", lv.B );
				cs.Dispatch( kernel, MAP_N / 8, MAP_N / 8, 1 );
			}

			// highest the water can reach on the shore: the swash run-up grows with the surf height. Terrain and props above it (the
			// dry beach) skip the wave evaluation entirely.
			float reach = shore.amplitude * 1.5f + 1.2f;
			Shader.SetGlobalTexture( "_TWUwWaves0", levels[ 0 ].A ); Shader.SetGlobalTexture( "_TWUwLevel0", levels[ 0 ].B );
			Shader.SetGlobalTexture( "_TWUwWaves1", levels[ 1 ].A ); Shader.SetGlobalTexture( "_TWUwLevel1", levels[ 1 ].B );
			Shader.SetGlobalVector( "_TWUwOrigins", new Vector4( levels[ 0 ].origin.x, levels[ 0 ].origin.y, levels[ 1 ].origin.x, levels[ 1 ].origin.y ) );
			Shader.SetGlobalVector( "_TWUwParams", new Vector4( reach, seaLevel, 0, 0 ) );
		}

		// no ocean (yet): the lighting pass finds nothing below a sea that reaches nowhere
		public static void SetDisabledGlobals()
		{
			Shader.SetGlobalVector( "_TWUwParams", new Vector4( -1e5f, 0, 0, 0 ) );
		}

		public void Dispose()
		{
			foreach ( var lv in levels )
			{
				if ( lv == null ) continue;
				if ( lv.A != null ) { lv.A.Release(); UnityEngine.Object.DestroyImmediate( lv.A ); }
				if ( lv.B != null ) { lv.B.Release(); UnityEngine.Object.DestroyImmediate( lv.B ); }
			}
		}
	}
}
