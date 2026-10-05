// The fish of the catch card's portrait (Game/FishPortrait.cs): the fish props' skin (FishSurface.hlsl) under the JS studio lighting (FishStudioPass.hlsl), drawn by the
// studio camera (its own layer and fixed exposure). Everything it needs per fish comes from a MaterialPropertyBlock like Tidewater/Fish; the studio's light from properties
// of the material.
Shader "Tidewater/FishStudio"
{
    Properties
    {
        [HideInInspector] _CullMode("__cullmode", Float) = 2.0
    }

    HLSLINCLUDE
    #pragma target 4.5
    #define _DEFERRED_CAPABLE_MATERIAL
    ENDHLSL

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "HDRenderPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }

            Cull [_CullMode]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag

            #define FISH_STUDIO
            #define SHADERPASS SHADERPASS_GBUFFER
            #include_with_pragmas "FishTemplate.hlsl"

            ENDHLSL
        }
    }

    FallBack "Hidden/HDRP/FallbackError"
}
