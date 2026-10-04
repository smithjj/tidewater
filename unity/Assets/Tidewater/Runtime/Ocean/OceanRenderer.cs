using System;
using Tidewater.Core;
using Tidewater.Util;
using Tidewater.World.Terrain;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

// The sea: owner of the FFT (OceanFFT), the foam pattern and the CDLOD surface mesh, and the one place that publishes
// what the water shader needs (src/ocean/WaterSurface.js params, the sun, the water volume). The mesh is drawn per camera
// in beginCameraRendering with GPU instancing, one instance per selected CDLOD node (like the terrain).
//
// App.js: oceanLOD = new CDLOD( { gridSize: 32, leafSize: 8, levels: 12, minY: -25, maxY: 25 } ), a camera-centred 3 x 3
// grid of root nodes (no fixed domain).
namespace Tidewater.Ocean
{
	[ExecuteAlways]
	public sealed class OceanRenderer : MonoBehaviour
	{
		public ComputeShader fftShader, foamShader, queryShader, shoreSimShader, underwaterLightShader, sprayShader, breakersShader, wakeShader;
		public Material material;
		public Light sun;
		public int gridSize = 32;

		// sea state on the Conditions ladder: 0 Calm .. 3 Storm (1 = Breezy, the JS default spectrum)
		[Range( 0, 3 )] public float seaState = 1;
		public float windDirection = 25; // degrees (Conditions windDir)
		// the weather (World/Weather.cs) walks the sea state while the game runs: the slider above is then ignored (the JS hands the wheel back to the player on a manual change)
		[NonSerialized] public bool weatherDriven;
		// shore wave amplitude (offshore H/2) and period (s): written by the sea conditions (surf, period) once the weather exists
		public float shoreAmplitude = 0.34f, shorePeriod = 9f;

		// WaterSurface params
		public float amplitude = 1, slopeScale = 1, foamCoverage = 1, foamSharpness = 2.2f;
		public float foamScale = 0.09f; // pattern repeats per metre
		// WaterMaterial uniforms
		public float backscatter = 0.035f, sss = 1f, foamIntensity = 1f, waterRoughness = 0.035f, reflectionStrength = 1f;
		public bool ssr = true;
		// debug view of the water shader (0 = none; 1 normal, 2 foam, 3 transmitted, 4 reflection, 5 sun specular, 6 scene colour,
		// 7 in-scattered sun, 8 in-scattered ambient, 9 Fresnel, 10 path length, 11 sky irradiance, 12 sun light, 13 exposure)
		public int debugView;

		public OceanFFT fft { get; private set; }
		// water-surface queries for gameplay (boats, swimmer, particles): slot 0 is the camera
		public WaterQuery query { get; private set; }
		// the shoreline waves (needs the terrain's shore field); null until the terrain exists
		public ShoreWaves shore { get; private set; }
		// the Eulerian foam / wetness state over the main beach
		public ShoreSim shoreSim { get; private set; }
		// gusts, slicks and windrows (world-space sea variation)
		public SeaDetail seaDetail { get; private set; }
		// caustics (photon splatting) and the baked wave maps the underwater lighting pass reads
		public Caustics caustics { get; private set; }
		public UnderwaterLighting underwaterLighting { get; private set; }
		// spray particles (drops, mist, bow sheets); created with the shore sim once the terrain exists
		public Tidewater.Fx.Spray spray { get; private set; }
		// plunging breakers along the main beach: crest finder, spray emitters and the thrown lip sheet
		public Breakers breakers { get; private set; }
		Vector3 cameraSim;
		Tidewater.World.TerrainGPU terrainGpu;
		public CDLOD lod { get; private set; }
		RenderTexture foamTexture;

		const int Batch = 1023;
		readonly Matrix4x4[] identity = new Matrix4x4[ Batch ];
		readonly Vector4[] batchData = new Vector4[ Batch ];
		MaterialPropertyBlock block;
		static readonly int NodeData = Shader.PropertyToID( "_TWNodeData" );
		float appliedSeaState = -1;

		void Reset() { FillDefaults(); }

		void FillDefaults()
		{
#if UNITY_EDITOR
			if ( fftShader == null ) fftShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/OceanFFT.compute" );
			if ( foamShader == null ) foamShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/FoamPattern.compute" );
			if ( queryShader == null ) queryShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/WaterQuery.compute" );
			if ( shoreSimShader == null ) shoreSimShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/ShoreSim.compute" );
			if ( underwaterLightShader == null ) underwaterLightShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/UnderwaterLight.compute" );
			if ( sprayShader == null ) sprayShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Fx/Spray.compute" );
			if ( breakersShader == null ) breakersShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/Breakers.compute" );
			if ( wakeShader == null ) wakeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>( "Assets/Tidewater/Shaders/Ocean/WakeSim.compute" );
#endif
		}

		// the active ocean, for the passes that read its state
		public static OceanRenderer instance { get; private set; }
		// closed volumes the sea is not drawn inside (the boats register theirs)
		HullMask hullMaskInstance;
		public HullMask hullMask => hullMaskInstance ??= new HullMask();

		// can this camera see anything below the water? The water height at the camera is the query's (a frame or two old, and only
		// the game camera has a query), so a margin covers the waves: above it the underwater composite has nothing to do
		public bool MayBeUnderwater( Camera cam )
		{
			if ( cam.cameraType != CameraType.Game || query == null ) return false;
			return cam.transform.position.y < G.cameraWaterHeight + 2f;
		}

		void OnEnable()
		{
			instance = this;
			FillDefaults();
			if ( fftShader == null || foamShader == null || queryShader == null || shoreSimShader == null || underwaterLightShader == null || sprayShader == null || breakersShader == null ) { enabled = false; Debug.LogError( "OceanRenderer: compute shaders not assigned" ); return; }
			Build();
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			if ( instance == this ) instance = null;
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			hullMask.Dispose();
			Release();
		}

		void Build()
		{
			Release();
			fft = new OceanFFT( fftShader );
			seaDetail = new SeaDetail();
			caustics = new Caustics( fft, Shader.Find( "Hidden/Tidewater/CausticsSplat" ) );
			foamTexture = FoamTexture.Create( foamShader );
			lod = new CDLOD( gridSize, 8, 12, 2.5, 0.66, 1500, - 25, 25, null, null );
			if ( material == null ) material = new Material( Shader.Find( "Tidewater/Water" ) ) { name = "Water" };
			material.enableInstancing = true; // DrawMeshInstanced needs it
			for ( int i = 0; i < Batch; i ++ ) identity[ i ] = Matrix4x4.identity;
			block = new MaterialPropertyBlock();
			var morph = new Vector4[ 16 ];
			Array.Copy( lod.morph, morph, lod.morph.Length );
			Shader.SetGlobalVectorArray( "_TWOceanLodMorph", morph );
			WakeSim.SetDisabledGlobals(); // (the boat's wake publishes its own once it exists)
			ApplySeaState( true );
			// the first spectrum is installed and one frame run, so the maps exist before the first camera renders
			fft.Update( 1f / 60f );
			PublishSun();
			fft.SetGlobals();
			caustics.Update();
		}

		void Release()
		{
			if ( query != null ) query.Dispose();
			query = null;
			if ( breakers != null ) breakers.Dispose();
			breakers = null;
			if ( spray != null ) spray.Dispose();
			spray = null;
			if ( underwaterLighting != null ) underwaterLighting.Dispose();
			underwaterLighting = null;
			if ( caustics != null ) caustics.Dispose();
			caustics = null;
			if ( seaDetail != null ) seaDetail.Dispose();
			seaDetail = null;
			if ( shoreSim != null ) shoreSim.Dispose();
			shoreSim = null;
			if ( shore != null ) shore.Destroy();
			shore = null;
			if ( fft != null ) fft.Dispose();
			fft = null;
			if ( foamTexture != null ) { foamTexture.Release(); DestroyImmediate( foamTexture ); }
			foamTexture = null; lod = null;
		}

		// Conditions.writeConditions: a condition on the ladder drives the spectrum, the wind and the foam
		void ApplySeaState( bool jump )
		{
			ApplyConditions( Conditions.ConditionAt( seaState, windDirection ), true, jump, true );
			appliedSeaState = seaState;
		}

		// Weather.write: a condition (`cover`: the clouds' cover rides on whole steps only), published the way the JS writeConditions + its callers do
		public void ApplyConditions( Condition c, bool spectrum, bool resetFoam, bool cover )
		{
			if ( fft == null ) return;
			Conditions.WriteConditions( fft, c, out Vector2 wd, out float ws, spectrum, resetFoam );
			G.windDir = wd;
			G.windSpeed = ws;
			if ( cover ) G.cover = ( float ) c.cover;
			shoreAmplitude = ( float ) c.surf; shorePeriod = ( float ) c.period;
		}

		// the surf the sea state asks for, on the shore waves: before anything evaluates them this frame (the sim, the crest finder and
		// the water shader must see the same period and amplitude, or the crests they find are not the crests that are drawn)
		void SyncShore()
		{
			if ( shore == null ) return;
			shore.amplitude = shoreAmplitude; shore.period = shorePeriod;
		}

		void Update()
		{
			if ( fft == null ) return;
			SyncShore();
			// a changed slider is a jump in the sea (clears the foam); the weather will drift it instead
			if ( ! weatherDriven && ! Mathf.Approximately( seaState, appliedSeaState ) ) ApplySeaState( true );
			if ( Application.isPlaying )
			{
				G.dt = Mathf.Min( Time.deltaTime, 0.1f );
				G.time += G.dt;
				fft.Update( G.dt );
				if ( shore != null ) shore.Update( G.dt );
				if ( shoreSim != null ) shoreSim.Update( G.dt, G.time, G.seaLevel );
				seaDetail.Update( G.dt );
				if ( breakers != null ) breakers.Update( cameraSim, amplitude, G.dt, G.time );
				if ( spray != null ) spray.Update( G.dt, G.time, amplitude );
				PublishSun();
				fft.SetGlobals();
				caustics.Update();
			}
		}

		// Run the sea forward by `seconds` (editor tools and tests: the Editor does not tick Update when it is not playing).
		public void Advance( float seconds, float step = 1f / 30f )
		{
			if ( fft == null ) return;
			SyncShore();
			for ( float t = 0; t < seconds; t += step )
			{
				G.dt = step; G.time += step;
				fft.Update( step );
				if ( shore != null ) shore.Update( step );
				if ( shoreSim != null ) shoreSim.Update( step, G.time, G.seaLevel );
				seaDetail.Update( step );
				if ( breakers != null ) breakers.Update( cameraSim, amplitude, step, G.time );
				if ( spray != null ) spray.Update( step, G.time, amplitude );
			}

			PublishSun();
			fft.SetGlobals();
			caustics.Update();
		}

		Vector3 SunDirSim()
		{
			if ( G.skyDriven ) return G.sunDir; // the day-night sky says which of the sun and the moon is the key light
			if ( sun == null ) sun = RenderSettings.sun;
			if ( sun == null ) return new Vector3( 0.3f, 0.8f, 0.5f ).normalized;
			// toward the sun = against the light's travel direction; Unity world -> sim
			return Sim.FromUnity( - sun.transform.forward ).normalized;
		}

		Vector3 SunColor()
		{
			if ( G.skyDriven ) return G.sunColor;
			if ( sun == null ) sun = RenderSettings.sun;
			if ( sun == null ) return new Vector3( 100000, 100000, 100000 );
			// HDRP directional lights are in lux (Light.intensity in the light's HDRP unit); the colour is the tint
			float lux = sun.intensity;
			var col = sun.color.linear;
			if ( sun.useColorTemperature )
			{
				var t = Mathf.CorrelatedColorTemperatureToRGB( sun.colorTemperature ).linear;
				col = new Color( col.r * t.r, col.g * t.g, col.b * t.b );
			}

			return new Vector3( col.r, col.g, col.b ) * lux;
		}

		// the sun as the water and the underwater lighting read it: direction (sim space) and illuminance
		void PublishSun()
		{
			var sd = SunDirSim();
			Shader.SetGlobalVector( "_TWSunDir", new Vector4( sd.x, sd.y, sd.z, 0 ) );
			var sc = SunColor();
			Shader.SetGlobalVector( "_TWSunColor", new Vector4( sc.x, sc.y, sc.z, 0 ) );
			// G.night (App.js): 0 by day, 1 with the sun well below the horizon. The day-night sky writes it from the real sun; without one it is
			// derived from the light (which is the sun)
			double night = G.night;
			if ( ! G.skyDriven )
			{
				night = Tidewater.Engine.MathUtils.smoothstep( - sd.y, 0.02, 0.18 );
				G.night = ( float ) night;
			}
			Shader.SetGlobalVector( "_TWFrame", new Vector4( ( float ) G.time, ( float ) G.windSpeed, ( float ) night, 0 ) );
		}

		void OnBeginCamera( ScriptableRenderContext context, Camera cam )
		{
			if ( fft == null || ! isActiveAndEnabled ) return;
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;

			// everything the water shader reads, in sim space (WaterSurface params, the sun, the water volume)
			var cp = cam.transform.position;
			cameraSim = new Vector3( cp.x, cp.y, - cp.z );
			fft.SetGlobals();
			seaDetail.SetGlobals();
			hullMask.Render( context, cam );
			Shader.SetGlobalTexture( "_TWFoamTex", foamTexture );
			Shader.SetGlobalVector( "_TWViewPos", new Vector4( cp.x, cp.y, - cp.z, 0 ) );
			Shader.SetGlobalVector( "_TWCamera", new Vector4( cp.x, cp.y, - cp.z, 0 ) );
			Shader.SetGlobalVector( "_TWWaterA", new Vector4( amplitude, slopeScale, foamCoverage, foamSharpness ) );
			Shader.SetGlobalVector( "_TWWaterB", new Vector4( foamScale, backscatter, sss, foamIntensity ) );
			Shader.SetGlobalVector( "_TWWaterC", new Vector4( waterRoughness, reflectionStrength, ssr ? 1 : 0, G.seaLevel ) );
			Shader.SetGlobalVector( "_TWWaterAbsorption", G.waterAbsorption );
			Shader.SetGlobalVector( "_TWWaterScattering", G.waterScattering );
			PublishSun();
			caustics.SetGlobals();
			Shader.SetGlobalVector( "_TWDebug", new Vector4( debugView, 0, 0, 0 ) );
			Shader.SetGlobalVector( "_TWWind", new Vector4( G.windDir.x, G.windDir.y, G.windSpeed, 0 ) );

			// the queries: built once the terrain exists (the depth and sea floor come from it); slot 0 follows the game
			// camera, and the shader reads its result the same frame (frame.cameraWaterHeight)
			if ( query == null )
			{
				var tr = FindAnyObjectByType<Tidewater.World.TerrainRenderer>();
				if ( tr != null && tr.gpu != null && tr.shoreField != null )
				{
					shore = new ShoreWaves( tr.gpu, tr.shoreField );
					shoreSim = new ShoreSim( shoreSimShader, tr.gpu, shore );
					terrainGpu = tr.gpu;
					underwaterLighting = new UnderwaterLighting( underwaterLightShader );
					spray = new Tidewater.Fx.Spray( sprayShader, Shader.Find( "Tidewater/Spray" ), fft, terrainGpu, shore, shoreSim );
					breakers = new Breakers( breakersShader, Shader.Find( "Tidewater/BreakersLip" ), fft, terrainGpu, shore, tr.data, spray );
				}
				query = new WaterQuery( queryShader, fft, tr != null ? tr.gpu : null, shore ) { amplitude = amplitude };
			}

			if ( shore != null )
			{
				SyncShore();
				shore.SetGlobals( G.time, G.seaLevel );
			}
			else ShoreWaves.SetDisabledGlobals();
			if ( shoreSim != null ) shoreSim.SetGlobals(); else ShoreSim.SetDisabledGlobals();

			if ( cam.cameraType == CameraType.Game )
			{
				query.amplitude = amplitude;
				query.SetCamera( cp.x, - cp.z );
				query.Update();
				G.cameraWaterHeight = query.cpuValid ? query.Get( 0 ).height : G.seaLevel;
			}

			Shader.SetGlobalBuffer( "_TWWaterQuery", query.resultsBuffer );

			// the baked wave maps of the underwater lighting, around this camera
			if ( underwaterLighting != null && shoreSim != null )
				underwaterLighting.Update( cp, fft, terrainGpu, shore, shoreSim, seaDetail, G.time, G.seaLevel );
			else UnderwaterLighting.SetDisabledGlobals();

			lod.Update( cam );
			for ( int start = 0; start < lod.count; start += Batch )
			{
				int n = Math.Min( Batch, lod.count - start );
				Array.Copy( lod.nodeArray, start, batchData, 0, n );
				block.SetVectorArray( NodeData, batchData );
				Graphics.DrawMeshInstanced( lod.gridMesh, 0, material, identity, n, block, ShadowCastingMode.Off, false, gameObject.layer, cam );
			}

			if ( breakers != null ) breakers.Draw( cam );
			if ( spray != null ) spray.Draw( cam );
		}
	}
}
