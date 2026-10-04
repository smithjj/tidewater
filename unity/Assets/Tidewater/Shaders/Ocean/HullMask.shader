// Port of the hull mask pass of src/engine/render/SceneRenderer.js: the closed hull volumes drawn as the camera distance of the nearest
// face (r16float, 0 = none; the depth test keeps the nearest). The water shader discards the sea behind that face (Water.hlsl).
// Self-contained (own matrices: the pass is rendered from OceanRenderer.OnBeginCamera with the camera's view-projection).
Shader "Hidden/Tidewater/HullMask"
{
    Properties { [HideInInspector] _TWHullZTest ("ztest", Float) = 4 }
    SubShader
    {
        Pass
        {
            Name "HullMask"
            Blend Off
            ZTest [_TWHullZTest] // the nearest face wins: LEqual, or GEqual with a reversed-Z projection (HullMask.cs sets it)
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag

            float4x4 _TWHullM;     // object -> Unity world
            float4x4 _TWHullVP;    // Unity world -> clip (the GPU projection)
            float4 _TWHullCam;     // camera position (Unity world)

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 world : TEXCOORD0; };

            Varyings Vert( Attributes i )
            {
                Varyings o;
                float3 w = mul( _TWHullM, float4( i.positionOS, 1.0 ) ).xyz;
                o.world = w;
                o.positionCS = mul( _TWHullVP, float4( w, 1.0 ) );
                return o;
            }

            float4 Frag( Varyings i ) : SV_Target0
            {
                return float4( length( i.world - _TWHullCam.xyz ), 0, 0, 0 );
            }
            ENDHLSL
        }
    }
    Fallback Off
}
