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
		public ComputeShader fftShader, foamShader;
		public Material material;
		public Light sun;
		public int gridSize = 32;

		// sea state on the Conditions ladder: 0 Calm .. 3 Storm (1 = Breezy, the JS default spectrum)
		[Range( 0, 3 )] public float seaState = 1;
		public float windDirection = 25; // degrees (Conditions windDir)

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
#endif
		}

		void OnEnable()
		{
			FillDefaults();
			if ( fftShader == null || foamShader == null ) { enabled = false; Debug.LogError( "OceanRenderer: compute shaders not assigned" ); return; }
			Build();
			RenderPipelineManager.beginCameraRendering += OnBeginCamera;
		}

		void OnDisable()
		{
			RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
			Release();
		}

		void Build()
		{
			Release();
			fft = new OceanFFT( fftShader );
			foamTexture = FoamTexture.Create( foamShader );
			lod = new CDLOD( gridSize, 8, 12, 2.5, 0.66, 1500, - 25, 25, null, null );
			if ( material == null ) material = new Material( Shader.Find( "Tidewater/Water" ) ) { name = "Water" };
			material.enableInstancing = true; // DrawMeshInstanced needs it
			for ( int i = 0; i < Batch; i ++ ) identity[ i ] = Matrix4x4.identity;
			block = new MaterialPropertyBlock();
			var morph = new Vector4[ 16 ];
			Array.Copy( lod.morph, morph, lod.morph.Length );
			Shader.SetGlobalVectorArray( "_TWOceanLodMorph", morph );
			ApplySeaState( true );
			// the first spectrum is installed and one frame run, so the maps exist before the first camera renders
			fft.Update( 1f / 60f );
		}

		void Release()
		{
			if ( fft != null ) fft.Dispose();
			fft = null;
			if ( foamTexture != null ) { foamTexture.Release(); DestroyImmediate( foamTexture ); }
			foamTexture = null; lod = null;
		}

		// Conditions.writeConditions: a condition on the ladder drives the spectrum, the wind and the foam
		void ApplySeaState( bool jump )
		{
			var c = Conditions.ConditionAt( seaState, windDirection );
			Conditions.WriteConditions( fft, c, out Vector2 wd, out float ws, true, jump );
			G.windDir = wd;
			G.windSpeed = ws;
			appliedSeaState = seaState;
		}

		void Update()
		{
			if ( fft == null ) return;
			// a changed slider is a jump in the sea (clears the foam); the weather will drift it instead
			if ( ! Mathf.Approximately( seaState, appliedSeaState ) ) ApplySeaState( true );
			if ( Application.isPlaying )
			{
				G.dt = Mathf.Min( Time.deltaTime, 0.1f );
				G.time += G.dt;
				fft.Update( G.dt );
			}
		}

		Vector3 SunDirSim()
		{
			if ( sun == null ) sun = RenderSettings.sun;
			if ( sun == null ) return new Vector3( 0.3f, 0.8f, 0.5f ).normalized;
			// toward the sun = against the light's travel direction; Unity world -> sim
			return Sim.FromUnity( - sun.transform.forward ).normalized;
		}

		Vector3 SunColor()
		{
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

		void OnBeginCamera( ScriptableRenderContext context, Camera cam )
		{
			if ( fft == null || ! isActiveAndEnabled ) return;
			if ( cam.cameraType != CameraType.Game && cam.cameraType != CameraType.SceneView ) return;

			// everything the water shader reads, in sim space (WaterSurface params, the sun, the water volume)
			var cp = cam.transform.position;
			fft.SetGlobals();
			Shader.SetGlobalTexture( "_TWFoamTex", foamTexture );
			Shader.SetGlobalVector( "_TWViewPos", new Vector4( cp.x, cp.y, - cp.z, 0 ) );
			Shader.SetGlobalVector( "_TWCamera", new Vector4( cp.x, cp.y, - cp.z, G.cameraWaterHeight ) );
			Shader.SetGlobalVector( "_TWWaterA", new Vector4( amplitude, slopeScale, foamCoverage, foamSharpness ) );
			Shader.SetGlobalVector( "_TWWaterB", new Vector4( foamScale, backscatter, sss, foamIntensity ) );
			Shader.SetGlobalVector( "_TWWaterC", new Vector4( waterRoughness, reflectionStrength, ssr ? 1 : 0, G.seaLevel ) );
			Shader.SetGlobalVector( "_TWWaterAbsorption", G.waterAbsorption );
			Shader.SetGlobalVector( "_TWWaterScattering", G.waterScattering );
			var sd = SunDirSim();
			Shader.SetGlobalVector( "_TWSunDir", new Vector4( sd.x, sd.y, sd.z, 0 ) );
			var sc = SunColor();
			Shader.SetGlobalVector( "_TWSunColor", new Vector4( sc.x, sc.y, sc.z, 0 ) );
			Shader.SetGlobalVector( "_TWDebug", new Vector4( debugView, 0, 0, 0 ) );
			Shader.SetGlobalVector( "_TWWind", new Vector4( G.windDir.x, G.windDir.y, G.windSpeed, 0 ) );

			lod.Update( cam );
			for ( int start = 0; start < lod.count; start += Batch )
			{
				int n = Math.Min( Batch, lod.count - start );
				Array.Copy( lod.nodeArray, start, batchData, 0, n );
				block.SetVectorArray( NodeData, batchData );
				Graphics.DrawMeshInstanced( lod.gridMesh, 0, material, identity, n, block, ShadowCastingMode.Off, false, gameObject.layer, cam );
			}
		}
	}
}
