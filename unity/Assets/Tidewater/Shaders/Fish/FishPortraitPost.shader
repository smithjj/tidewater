// The resolve of the catch card's fish portrait (FishPortrait.js _pass): a SS x SS box of ACES-tone-mapped samples (the silhouette is the whole picture: no aliased
// edges), the colour in linear light (the picture's target is sRGB: it encodes on write and IMGUI decodes on read), with the coverage as alpha, and the card's mask (the stage fades out toward both ends). The source is the studio camera's linear
// HDR image, one scene unit = 1.0 (FishPortrait sets the exposure to match).
Shader "Hidden/Tidewater/FishPortraitPost"
{
    Properties { _MainTex ("HDR", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D( _MainTex );
            SAMPLER( sampler_point_clamp );
            float _SS;       // supersampling per axis
            float _Exposure; // PortraitParams.exposure
            float4 _DstSize; // destination width, height

            struct A { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            V Vert( A a )
            {
                V o;
                o.pos = float4( a.uv * 2.0 - 1.0, 0.0, 1.0 );
#if UNITY_UV_STARTS_AT_TOP
                o.pos.y = - o.pos.y; // the render target is written top row first: keep uv.y = 1 at the top of the picture
#endif
                o.uv = a.uv;
                return o;
            }

            float3 RRTAndODTFit( float3 v )
            {
                float3 a = v * ( v + 0.0245786 ) - 0.000090537;
                float3 b = v * ( 0.983729 * v + 0.4329510 ) + 0.238081;
                return a / b;
            }

            // PostFX.js ACES (three's acesFilmicToneMapping)
            float3 acesFilmic( float3 colorIn, float exposure )
            {
                const float3x3 inMat = float3x3( 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 );
                const float3x3 outMat = float3x3( 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 );
                float3 color = colorIn * exposure / 0.6;
                color = mul( inMat, color );
                color = RRTAndODTFit( color );
                color = mul( outMat, color );
                return saturate( color );
            }

            float4 Frag( V i ) : SV_Target
            {
                int n = ( int ) _SS;
                float2 dstPx = floor( i.uv * _DstSize.xy );
                float3 rgb = 0;
                float a = 0;
                for ( int y = 0; y < n; y ++ )
                    for ( int x = 0; x < n; x ++ )
                    {
                        float4 c = SAMPLE_TEXTURE2D_LOD( _MainTex, sampler_point_clamp, ( dstPx * n + float2( x, y ) + 0.5 ) / ( _DstSize.xy * n ), 0 );
                        float cov = saturate( c.a );
                        rgb += acesFilmic( max( c.rgb, 0.0 ) / max( cov, 1e-4 ), _Exposure ) * cov;
                        a += cov;
                    }
                float k = 1.0 / ( n * n );
                rgb *= k; a *= k;
                float3 straight = rgb / max( a, 1e-4 );
                // the card's mask (gm-catch-stage): the stage fades out over the first and the last 14 % of its width
                float m = saturate( i.uv.x / 0.14 ) * saturate( ( 1.0 - i.uv.x ) / 0.14 );
                return float4( straight, a * m ); // linear: the sRGB target encodes it
            }
            ENDHLSL
        }
    }
}
