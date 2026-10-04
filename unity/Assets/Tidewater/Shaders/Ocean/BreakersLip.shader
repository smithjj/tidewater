// The thrown lip of the plunging breakers (src/ocean/Breakers.js, lip sheet): a separate ribbon mesh extruded along the crest
// (Thuerey et al. 2007): a ballistic curtain leaving the crest horizontally and falling in front of the concave face, shaded like the
// water (Fresnel sky reflection, light through the thin sheet, aerated streaks that grow toward the tip) and blended over the water
// with premultiplied alpha. Its root lies on the crest of the water surface and fades in there, so there is no visible seam.
// The strips are drawn procedurally (Breakers.cs): the crest table written by Breakers.compute is the only input.
Shader "Tidewater/BreakersLip"
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
            "Queue" = "Transparent+10"
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            // premultiplied alpha over the water
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

            #include "BreakersLipCore.hlsl"

            ENDHLSL
        }
    }

    FallBack Off
}
