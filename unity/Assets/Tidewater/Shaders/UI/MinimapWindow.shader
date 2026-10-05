// The minimap's window (Minimap.js, the `.gm-map` styles), drawn into a render target the IMGUI then shows: the glass disc with its shadow, and in it the baked island
// image turned and scaled to the view (the CSS transform of the canvas, inverted per pixel: translate( R, R ) rotate( rot ) scale( s ) translate( ax, ay )), the sea's colour
// outside the image, the vignette. The markers and the text are IMGUI's. Positions are in px of the target, y down, the window's centre at _Geo.xy. Colours are the CSS
// ones (sRGB), converted to linear: the target is sRGB, which encodes on write and IMGUI decodes on read.
Shader "Hidden/Tidewater/MinimapWindow"
{
    Properties { _MainTex ("Island", 2D) = "black" {} }
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
            SAMPLER( sampler_linear_clamp );
            float4 _Size;  // target width, height
            float4 _Geo;   // window centre x, y; the window's radius R; the glass disc's radius
            float4 _Map;   // image rotation (rad), scale, ax, ay: the canvas transform
            float4 _Map2;  // image size (px), 1 when the image is there, the shadow's unit (u), the window's inset shadow blur
            float4 _Look;  // opacity, inset shadow alpha, inset ring alpha, 0

            struct A { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            V Vert( A a )
            {
                V o;
                o.pos = float4( a.uv * 2.0 - 1.0, 0.0, 1.0 );
#if UNITY_UV_STARTS_AT_TOP
                o.pos.y = - o.pos.y; // the target is written top row first: uv.y = 1 at the top
#endif
                o.uv = a.uv;
                return o;
            }

            float3 S2L( float3 c255 )
            {
                float3 c = c255 / 255.0;
                return c <= 0.04045 ? c / 12.92 : pow( ( c + 0.055 ) / 1.055, 2.4 );
            }

            // straight-alpha "over"
            float4 Over( float4 dst, float3 src, float a )
            {
                float oa = a + dst.a * ( 1.0 - a );
                float3 rgb = ( src * a + dst.rgb * dst.a * ( 1.0 - a ) ) / max( oa, 1e-5 );
                return float4( rgb, oa );
            }

            // the mass of a unit Gaussian's lower tail past x: a logistic stand-in (the blur of a box-shadow's edge)
            float Tail( float x ) { return 1.0 / ( 1.0 + exp( 1.702 * x ) ); }

            float4 Frag( V i ) : SV_Target
            {
                float2 p = float2( i.uv.x * _Size.x, ( 1.0 - i.uv.y ) * _Size.y );
                float2 q = p - _Geo.xy;
                float d = length( q );
                float R = _Geo.z, Rp = _Geo.w, u = _Map2.z;
                float4 col = float4( 0, 0, 0, 0 );

                // box-shadow: 0 1px 2px rgba( 0, 0, 0, 0.25 ), 0 12px 32px -12px rgba( 0, 0, 0, 0.55 ), outside the glass only
                float outside = saturate( d - Rp + 0.5 );
                float s1 = 0.25 * Tail( ( length( q - float2( 0, u ) ) - Rp ) / max( u, 1.0 ) );
                float s2 = 0.55 * Tail( ( length( q - float2( 0, 12.0 * u ) ) - ( Rp - 12.0 * u ) ) / ( 16.0 * u ) );
                col = Over( col, float3( 0, 0, 0 ), saturate( s1 + s2 ) * outside );

                // the glass: linear-gradient( 180deg, rgba( 255, 255, 255, 0.045 ), transparent 40 % ) over rgba( 12, 18, 26, 0.62 ), a 1px border rgba( 170, 215, 235, 0.12 ),
                // an inset highlight 0 1px 0 rgba( 255, 255, 255, 0.07 )
                float disc = saturate( Rp - d + 0.5 );
                float yf = ( p.y - ( _Geo.y - Rp ) ) / ( 2.0 * Rp );
                float4 glass = Over( float4( S2L( float3( 12, 18, 26 ) ), 0.62 ), S2L( float3( 255, 255, 255 ) ), 0.045 * saturate( 1.0 - yf / 0.4 ) );
                float inner = saturate( Rp - 1.0 - d + 0.5 );
                glass = Over( glass, S2L( float3( 170, 215, 235 ) ), 0.12 * ( 1.0 - inner ) );
                glass = Over( glass, S2L( float3( 255, 255, 255 ) ), 0.07 * inner * saturate( length( q - float2( 0, 1 ) ) - ( Rp - 1.0 ) + 0.5 ) );
                col = Over( col, glass.rgb, glass.a * disc );

                // the window
                float win = saturate( R - d + 0.5 );
                if ( win > 0.0 )
                {
                    float3 bg = S2L( float3( 11, 44, 72 ) );
                    // the canvas point under this pixel: the inverse of translate( R, R ) rotate( rot ) scale( s ) translate( ax, ay )
                    float cs = cos( _Map.x ), sn = sin( _Map.x );
                    float2 c = float2( cs * q.x + sn * q.y, - sn * q.x + cs * q.y ) / _Map.y - _Map.zw;
                    float3 rgb = bg;
                    float inImg = 0;
                    if ( _Map2.y > 0.5 && c.x >= 0.0 && c.y >= 0.0 && c.x <= _Map2.x && c.y <= _Map2.x )
                    {
                        // (the texture's rows run bottom to top; the image's top row is the north edge)
                        rgb = SAMPLE_TEXTURE2D_LOD( _MainTex, sampler_linear_clamp, float2( c.x / _Map2.x, 1.0 - c.y / _Map2.x ), 0 ).rgb;
                        inImg = 1;
                    }
                    else
                    {
                        // the view's own box-shadows show where the image does not cover it: inset 0 0 0 1px rgba( 255, 255, 255, 0.08 ), inset 0 0 18px rgba( 0, 0, 0, 0.45 )
                        float e = R - d;
                        rgb = lerp( rgb, float3( 0, 0, 0 ), _Look.y * Tail( e / ( 0.5 * _Map2.w ) ) );
                        rgb = lerp( rgb, S2L( float3( 255, 255, 255 ) ), _Look.z * saturate( 1.0 - e ) );
                    }

                    // the vignette: radial-gradient( circle at 50% 50 %, transparent 58 %, rgba( 4, 12, 20, 0.45 ) 100 % ), the circle sized to the farthest corner
                    float t = d / ( R * 1.41421356 );
                    rgb = lerp( rgb, S2L( float3( 4, 12, 20 ) ), 0.45 * saturate( ( t - 0.58 ) / 0.42 ) );
                    col = Over( col, rgb, win );
                }

                col.a *= _Look.x;
                return col;
            }
            ENDHLSL
        }
    }
}
