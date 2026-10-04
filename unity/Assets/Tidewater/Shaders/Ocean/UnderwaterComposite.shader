// Port of src/post/Underwater.js: everything that happens when the camera is (partly) underwater, as an HDRP custom pass before
// the post-processing. It reads a copy of the lit scene colour and the camera depth and writes the composite:
//  - the medium at the lens, per pixel. The lens is the near clip plane; where the water surface crosses it the view splits into an
//    above-water and an underwater part. (The JS reads the side from a mask the water material writes; here the surface within the
//    few cm of the lens is the plane through the camera's water query: height and normal.)
//  - participating medium: Beer-Lambert absorption of the scene plus single scattering of the depth-attenuated sun and sky
//    light (analytic), with ray-marched caustic light shafts near the camera
//  - the meniscus band along the waterline on the lens: refraction through a rounded water edge, a dark contact line and a rim
//  - the diver's torch: single scattering of the flashlight cone along the view ray (LocalLightsView publishes _TWFlash*)
// Not yet: the half-resolution shaft march (the march runs per pixel here, with fewer steps), the torch in the marine snow.
Shader "Hidden/Tidewater/UnderwaterComposite"
{
    SubShader
    {
        Pass
        {
            Name "UnderwaterComposite"
            Blend Off
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnderwaterCompositeCore.hlsl"
            ENDHLSL
        }
    }
    Fallback Off
}
