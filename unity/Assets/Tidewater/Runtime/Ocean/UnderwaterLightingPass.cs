using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

// The HDRP custom pass that applies the underwater lighting (Shaders/Ocean/UnderwaterLighting.shader) to everything lit below the
// water, after the opaque lighting and before the colour pyramid the water reads. Add it to a global CustomPassVolume with injection
// point BeforePreRefraction (SceneBuilder does).
namespace Tidewater.Ocean
{
	[System.Serializable]
	public sealed class UnderwaterLightingPass : CustomPass
	{
		Material material;
		// debug: show the terms of the lighting instead of applying it (UnderwaterLightingCore.hlsl)
		public static bool debug;

		protected override void Setup( ScriptableRenderContext renderContext, CommandBuffer cmd )
		{
			var shader = Shader.Find( "Hidden/Tidewater/UnderwaterLighting" );
			if ( shader != null ) material = CoreUtils.CreateEngineMaterial( shader );
		}

		protected override void Execute( CustomPassContext ctx )
		{
			if ( material == null ) return;
			// the factor is multiplied into the colour: only the colour buffer is bound (the depth is read as a texture)
			CoreUtils.SetRenderTarget( ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None );
			// the camera's own depth and normal buffers (the global _CameraDepthTexture is not valid at this point of the frame)
			ctx.propertyBlock.SetTexture( "_UwDepthTex", ctx.cameraDepthBuffer );
			ctx.propertyBlock.SetTexture( "_UwNormalTex", ctx.cameraNormalBuffer );
			CoreUtils.DrawFullScreen( ctx.cmd, material, ctx.propertyBlock, shaderPassId: debug ? 1 : 0 );
		}

		protected override void Cleanup()
		{
			CoreUtils.Destroy( material );
		}
	}
}
