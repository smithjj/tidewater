// The spray sprites of src/fx/Spray.js: one camera-facing quad per particle slot, drawn procedurally from the particle buffers
// (Spray.cs). Soft camera-facing sprites, premultiplied alpha, as an HDRP transparent forward pass that shades itself:
//  * drops and ligaments are clear water: sub-pixel drops are drawn as motion-blurred streaks whose opacity conserves the drop's
//    cross-section, they show the bright sky they refract, a tiny sun glint, and light up strongly when backlit
//  * dense spray (clouds of drops) is white from every side (multiple scattering), with a forward lobe and its own shadow
//  * mist is a thin, strongly forward-scattering, sky-tinted veil
//  * clear sheets (a boat's bow sheet) are thin water: translucent, showing the sky, glowing when backlit, torn into strands
//  * all of it is darkened in the shadow of the wave that made it, of the clouds
Shader "Tidewater/Spray"
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
            "Queue" = "Transparent+20"
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            // premultiplied: color one / one-minus-src-alpha (alpha likewise)
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal

            #pragma vertex Vert
            #pragma fragment Frag

            #define SHADERPASS SHADERPASS_FORWARD_UNLIT
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/AmbientProbe.hlsl"

            #include "SprayCore.hlsl"

            ENDHLSL
        }
    }

    FallBack Off
}
