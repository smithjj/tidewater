// The underwater lighting of lit surfaces: what the JS does with the directModulation / ambientModulation hooks of every scene
// material (UnderwaterLighting.js), as an HDRP custom pass. HDRP lights its materials as it likes, so the pass runs after the
// lighting (injection point BeforePreRefraction, before the colour pyramid the water reads) and MULTIPLIES the lit colour of every
// pixel below the water surface:
//  - the direct sun: attenuated along the refracted sun path through the water column (Beer-Lambert), modulated by the caustics
//    that follow the waves above (swell + shore waves tilt the light, surf foam and bubbles shade the floor)
//  - the ambient light: attenuated and tinted with depth
// A lit pixel is one sum of both, so the two factors are blended by the share of the pixel's light that comes from the sun, estimated
// from its normal (the JS applies them to the two terms separately; shadows are not known here, so a shadowed pixel gets a little
// too much caustic).
// Pass 0 draws with Blend DstColor Zero: the shader outputs the factor. Pixels above the water, and the sky, get 1.
// Pass 1 is a debug view of the terms (UnderwaterLightingCore.hlsl).
Shader "Hidden/Tidewater/UnderwaterLighting"
{
    SubShader
    {
        Pass
        {
            Name "UnderwaterLighting"
            Blend DstColor Zero
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnderwaterLightingCore.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "UnderwaterLightingDebug"
            Blend Off
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment FragDebug
            #include "UnderwaterLightingCore.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
