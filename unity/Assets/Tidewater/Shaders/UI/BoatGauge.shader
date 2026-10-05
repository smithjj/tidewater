// The boat instruments' two SVGs (UI.js _buildBoat, the `.tw-dial` and `.tw-compass` styles), drawn into a render target the IMGUI then shows: pass 0 the rpm dial (the 270 degree
// arc with its red zone, the value arc with its gradient and glow, the ticks, the head dot), pass 1 the compass (the dark ring, the card's ticks turned by the heading, the lubber
// mark). The numbers and the card's letters are IMGUI's. Everything is in the SVG's own units (the dial's viewBox is 120, the compass's 100), y down, and a margin round the
// viewBox keeps the glows the SVG lets overflow. Colours are the CSS ones (sRGB), converted to linear: the target is sRGB, which encodes on write and IMGUI decodes on read.
Shader "Hidden/Tidewater/BoatGauge"
{
    SubShader
    {
        Cull Off ZWrite Off ZTest Always Blend Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

        float4 _Size;  // target width, height (px)
        float4 _View;  // the viewBox's top-left in the target (units, negative: the margin), px per unit, the viewBox size (units), 0
        float4 _Val;   // dial: the rpm 0..1, 1 over the redline; compass: the heading (deg), 0, 0, 0

        struct A { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
        struct V { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

        V Vert( A a )
        {
            V o;
            o.pos = float4( a.uv * 2.0 - 1.0, 0.0, 1.0 );
#if UNITY_UV_STARTS_AT_TOP
            o.pos.y = - o.pos.y;
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

        float4 OverC( float4 dst, float3 c255, float a, float cov ) { return Over( dst, S2L( c255 ), a * cov ); }

        // the mass of a unit Gaussian's lower tail past x: a logistic stand-in (the blur of a drop-shadow's edge)
        float Tail( float x ) { return 1.0 / ( 1.0 + exp( 1.702 * x ) ); }

        float Cov( float d, float aa ) { return saturate( 0.5 - d / aa ); }

        // a segment with round caps: the distance to its stroke's edge
        float SegRound( float2 p, float2 a, float2 b, float w )
        {
            float2 pa = p - a, ba = b - a;
            float h = saturate( dot( pa, ba ) / max( dot( ba, ba ), 1e-6 ) );
            return length( pa - ba * h ) - w * 0.5;
        }

        // an arc of radius R about c from the angle a0 over `sweep` degrees (clockwise on screen, from +x), stroked w wide: the distance to its edge
        float ArcDist( float2 q, float R, float a0, float sweep, float w, bool round )
        {
            float r = length( q );
            float ang = degrees( atan2( q.y, q.x ) );
            float a = fmod( ang - a0 + 720.0, 360.0 );
            float radial = abs( r - R ) - w * 0.5;
            if ( a <= sweep ) return radial;
            float2 e0 = R * float2( cos( radians( a0 ) ), sin( radians( a0 ) ) );
            float2 e1 = R * float2( cos( radians( a0 + sweep ) ), sin( radians( a0 + sweep ) ) );
            if ( round ) return min( length( q - e0 ), length( q - e1 ) ) - w * 0.5;
            // butt caps: the stroke ends on the radial line
            float past = min( a - sweep, 360.0 - a ) ; // degrees beyond the nearer end
            return max( radial, radians( past ) * R );
        }

        float2 Units( V i )
        {
            float2 p = float2( i.uv.x * _Size.x, ( 1.0 - i.uv.y ) * _Size.y );
            return p / _View.y + _View.xx;
        }
        ENDHLSL

        // ---- the rpm dial: viewBox 120, the arc about (60, 60) at r 50 from 135 degrees over 270
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag( V i ) : SV_Target
            {
                float2 u = Units( i );
                float aa = 1.0 / _View.y;
                float2 q = u - 60.0;
                float rpm = saturate( _Val.x );
                bool red = _Val.y > 0.5;
                float4 o = float4( 0, 0, 0, 0 );

                // the track, and the red zone over its last 14 %
                o = OverC( o, float3( 170, 215, 235 ), 0.12, Cov( ArcDist( q, 50.0, 135.0, 270.0, 6.0, true ), aa ) );
                o = OverC( o, float3( 255, 122, 133 ), 0.38, Cov( ArcDist( q, 50.0, 135.0 + 270.0 * 0.86, 270.0 * 0.14, 6.0, false ), aa ) );

                // the value arc: round caps (a zero-length dash is a dot), the gradient across the arc's bounding box (10, 10)-(110, 95.36) from its lower left to its upper right,
                // and the drop-shadow( 0 0 4px aqua .45 ) under it
                float dv = ArcDist( q, 50.0, 135.0, 270.0 * rpm, 6.0, true );
                o = OverC( o, float3( 95, 227, 212 ), 0.45, Tail( dv / 2.0 ) );
                float gx = ( u.x - 10.0 ) / 100.0, gy = ( u.y - 10.0 ) / 85.36;
                float t = saturate( ( gx + ( 1.0 - gy ) ) * 0.5 );
                float3 g = t < 0.62 ? lerp( float3( 63, 192, 214 ), float3( 95, 227, 212 ), t / 0.62 ) : lerp( float3( 95, 227, 212 ), float3( 255, 184, 107 ), ( t - 0.62 ) / 0.38 );
                o = OverC( o, g, 1.0, Cov( dv, aa ) );

                // the ticks, at 135 + 27 i degrees from r 37.5 (major) or 40.5 to 44
                for ( int k = 0; k <= 10; k ++ )
                {
                    bool maj = ( k % 5 ) == 0;
                    float a = radians( 135.0 + 27.0 * k );
                    float2 dir = float2( cos( a ), sin( a ) );
                    float d = SegRound( q, dir * ( maj ? 37.5 : 40.5 ), dir * 44.0, maj ? 1.6 : 1.2 );
                    o = OverC( o, maj ? float3( 230, 245, 250 ) : float3( 220, 240, 245 ), maj ? 0.7 : 0.32, Cov( d, aa ) );
                }

                // the head dot at the value's end, with its glow
                float ha = radians( 135.0 + 270.0 * rpm );
                float dh = length( q - 50.0 * float2( cos( ha ), sin( ha ) ) ) - 3.0;
                o = OverC( o, red ? float3( 255, 122, 133 ) : float3( 95, 227, 212 ), red ? 0.95 : 0.9, Tail( dh / ( red ? 2.5 : 2.0 ) ) );
                o = OverC( o, float3( 255, 255, 255 ), 1.0, Cov( dh, aa ) );
                return o;
            }
            ENDHLSL
        }

        // ---- the compass: viewBox 100, about (50, 50); the card turns by minus the heading
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float TriDist( float2 p, float2 a, float2 b, float2 c )
            {
                float2 e0 = b - a, e1 = c - b, e2 = a - c;
                float2 v0 = p - a, v1 = p - b, v2 = p - c;
                float2 p0 = v0 - e0 * saturate( dot( v0, e0 ) / dot( e0, e0 ) );
                float2 p1 = v1 - e1 * saturate( dot( v1, e1 ) / dot( e1, e1 ) );
                float2 p2 = v2 - e2 * saturate( dot( v2, e2 ) / dot( e2, e2 ) );
                float s = sign( e0.x * e2.y - e0.y * e2.x );
                float2 d = min( min( float2( dot( p0, p0 ), s * ( v0.x * e0.y - v0.y * e0.x ) ), float2( dot( p1, p1 ), s * ( v1.x * e1.y - v1.y * e1.x ) ) ), float2( dot( p2, p2 ), s * ( v2.x * e2.y - v2.y * e2.x ) ) );
                return - sqrt( d.x ) * sign( d.y );
            }

            float4 Frag( V i ) : SV_Target
            {
                float2 u = Units( i );
                float aa = 1.0 / _View.y;
                float2 q = u - 50.0;
                float r = length( q );
                float4 o = float4( 0, 0, 0, 0 );

                // the ring: the dark fill, and its 1 unit line
                o = OverC( o, float3( 6, 12, 18 ), 0.35, Cov( r - 47.0, aa ) );
                o = OverC( o, float3( 170, 215, 235 ), 0.16, Cov( abs( r - 47.0 ) - 0.5, aa ) );

                // the card: a tick every 10 degrees from the top (north), long every 30; d is the card's own angle at this pixel
                float d = fmod( degrees( atan2( q.y, q.x ) ) + 90.0 + _Val.x + 720.0, 360.0 );
                float n = round( d / 10.0 );
                float off = radians( abs( d - n * 10.0 ) );
                float across = r * sin( min( off, 1.5 ) );
                bool maj = fmod( n, 3.0 ) < 0.5 || fmod( n, 3.0 ) > 2.5;
                float r0 = maj ? 37.0 : 40.0, w = maj ? 1.5 : 1.0;
                float along = max( r0 - r, r - 44.0 );
                float dt = max( across - w * 0.5, along );
                o = OverC( o, maj ? float3( 230, 245, 250 ) : float3( 220, 240, 245 ), maj ? 0.7 : 0.28, Cov( dt, aa ) );

                // the lubber mark at the top, with its glow
                float dl = TriDist( u, float2( 45.5, 1.5 ), float2( 54.5, 1.5 ), float2( 50.0, 8.5 ) );
                o = OverC( o, float3( 95, 227, 212 ), 0.9, Tail( dl / 1.5 ) );
                o = OverC( o, float3( 95, 227, 212 ), 1.0, Cov( dl, aa ) );
                return o;
            }
            ENDHLSL
        }
    }
}
