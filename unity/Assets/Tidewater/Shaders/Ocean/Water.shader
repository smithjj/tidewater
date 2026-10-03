// The sea surface: src/ocean/WaterSurface.js (vertex + fragment surface) and src/ocean/WaterMaterial.js (shading),
// as an HDRP transparent forward pass that shades itself.
//
// The JS draws the water in its own pass after the opaques and gives it copies of the opaque colour and depth; here
// that is HDRP's colour pyramid and depth texture, available to transparent materials. The pass writes the final
// colour (Blend One Zero: the shader already includes what is seen through the water) and depth, then applies HDRP's
// fog. The surface comes from the FFT cascades (Shaders/Ocean/OceanFFT.compute) over a CDLOD mesh drawn by
// OceanRenderer with GPU instancing, one instance per node (_TWNodeData).
//
// Not ported yet (their systems do not exist): shore waves / swash, the wake, surf foam, sea detail (gusts, slicks),
// caustics, the refraction pass, the hull mask, local lights, the sun shadow on the water. Their hooks are marked
// in Water.hlsl.
Shader "Tidewater/Water"
{
    Properties
    {
        [HideInInspector] _MainTex("Unused", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal

            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap

            #define SHADERPASS SHADERPASS_FORWARD_UNLIT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"

            #include "Water.hlsl"

            ENDHLSL
        }
    }

    FallBack Off
}
