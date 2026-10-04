// The boat's glass (BoatMaterials.createGlass): a transparent forward Lit surface, double sided, no depth write. Kind 7 is the factor-based
// windshield of the glTF boats (Pelagic30.js _material: the glTF colour at opacity 0.25).
Shader "Tidewater/BoatGlass"
{
    Properties
    {
        [HideInInspector] _StencilRef("_StencilRef", Int) = 0
        [HideInInspector] _StencilWriteMask("_StencilWriteMask", Int) = 3
        [HideInInspector] _BlendMode("Blend mode", Float) = 0
        [HideInInspector] _BoatKind("Kind", Float) = 4
        [HideInInspector] _FacColor("Factor colour (alpha: opacity)", Color) = (0.8, 0.8, 0.8, 0.25)
        [HideInInspector] _FacPbr("Factor roughness, metalness, clear coat, coat roughness", Vector) = (0.05, 0, 0, 0.1)
        [HideInInspector] _FacEmissive("Factor emissive", Color) = (0, 0, 0, 0)
        [HideInInspector] _AlphaCutoff("Alpha cutoff", Float) = 0.5
        [HideInInspector] _FlagPivot("Flag pivot", Vector) = (0,0,0,0)
        [HideInInspector] _FlagDir("Flag direction", Vector) = (0,0,-1,0)
        [HideInInspector] _FlagWind("Flag wind", Float) = 0.5
        [HideInInspector] _NavOn("Nav lights", Float) = 1
        [HideInInspector] _EmissionColor("Color", Color) = (1, 1, 1)
        [HideInInspector] _MainTex("Albedo", 2D) = "white" {}
        [HideInInspector] _Color("Color", Color) = (1,1,1,1)
    }

    HLSLINCLUDE
    #pragma target 4.5
    #pragma shader_feature_local _DISABLE_DECALS
    #define SUPPORT_GLOBAL_MIP_BIAS
    ENDHLSL

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
            Name "Forward"
            Tags { "LightMode" = "Forward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma instancing_options assumeuniformscaling nolightprobe nolightmap

            #pragma multi_compile _ DEBUG_DISPLAY
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile_fragment _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ PROBE_VOLUMES_L1 PROBE_VOLUMES_L2
            #pragma multi_compile_fragment SCREEN_SPACE_SHADOWS_OFF SCREEN_SPACE_SHADOWS_ON
            #pragma multi_compile_fragment _ CONTACT_SHADOWS_OFF
            #pragma multi_compile_fragment DECALS_OFF DECALS_3RT DECALS_4RT
            #pragma multi_compile_fragment _ DECAL_SURFACE_GRADIENT
            #pragma multi_compile_fragment PUNCTUAL_SHADOW_LOW PUNCTUAL_SHADOW_MEDIUM PUNCTUAL_SHADOW_HIGH
            #pragma multi_compile_fragment DIRECTIONAL_SHADOW_LOW DIRECTIONAL_SHADOW_MEDIUM DIRECTIONAL_SHADOW_HIGH
            #pragma multi_compile_fragment AREA_SHADOW_MEDIUM AREA_SHADOW_HIGH
            #pragma multi_compile USE_FPTL_LIGHTLIST USE_CLUSTERED_LIGHTLIST

            #define _SURFACE_TYPE_TRANSPARENT
            #define _BLENDMODE_ALPHA
            #define SHADERPASS SHADERPASS_FORWARD
            #include_with_pragmas "BoatTemplate.hlsl"

            ENDHLSL
        }
    }

    FallBack "Hidden/HDRP/FallbackError"
}
