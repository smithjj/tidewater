using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The HDRP custom pass of the underwater composite (Shaders/Ocean/UnderwaterComposite.shader, port of src/post/Underwater.js):
// the medium at the lens, absorption and in-scatter of the water, caustic shafts, the meniscus. Add it to a global
// CustomPassVolume with injection point BeforePostProcess (SceneBuilder does). The shader writes the camera colour, so the
// colour is copied first; the pass does nothing for a camera that is entirely above the water.
namespace Tidewater.Ocean
{
	[System.Serializable]
	public sealed class UnderwaterCompositePass : CustomPass
	{
		public float shafts = 1f;
		public float band = 9f;      // meniscus half-width in pixels (<= 16)
		Material material;
		RTHandle copy;
		// debug counters: Execute calls, and those that drew
		public static int calls, draws;
		public static int debugMode;

		protected override void Setup( ScriptableRenderContext renderContext, CommandBuffer cmd )
		{
			var shader = Shader.Find( "Hidden/Tidewater/UnderwaterComposite" );
			if ( shader != null ) material = CoreUtils.CreateEngineMaterial( shader );
			copy = RTHandles.Alloc( Vector2.one, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
				useDynamicScale: true, name: "Underwater scene copy" );
		}

		protected override void Execute( CustomPassContext ctx )
		{
			calls ++;
			if ( material == null || copy == null ) return;
			// only a camera that can see below the water pays for it: near the surface or below it (the query state is the
			// camera's water height, resolved on the GPU: OceanRenderer.cameraUnderwater is its CPU copy, a frame or two old)
			var oc = OceanRenderer.instance;
			if ( oc == null || ! oc.MayBeUnderwater( ctx.hdCamera.camera ) ) return;

			draws ++;
			CustomPassUtils.Copy( ctx, ctx.cameraColorBuffer, copy );
			var pb = ctx.propertyBlock;
			pb.SetTexture( "_UwDepthTex", ctx.cameraDepthBuffer );
			pb.SetTexture( "_UwSceneTex", copy );
			pb.SetVector( "_UwParams", new Vector4( shafts, band, 1f, debugMode ) );
			CoreUtils.SetRenderTarget( ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None );
			CoreUtils.DrawFullScreen( ctx.cmd, material, pb, shaderPassId: 0 );
		}

		protected override void Cleanup()
		{
			CoreUtils.Destroy( material );
			if ( copy != null ) { copy.Release(); copy = null; }
		}
	}
}
