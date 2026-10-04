// Port of the render pipeline of CausticLayer in src/ocean/Caustics.js: caustics by rasterized photon splatting (as in Evan
// Wallace's "WebGL Water").
//
// A fine grid covering one FFT tile (plus a margin) is drawn into an offscreen target. Each vertex is a point on the real wave
// surface; the sun ray is refracted through the surface normal there and followed down to a plane D metres below, and the
// vertex is placed at that landing point. The fragment writes (area on the surface / area on the floor) with additive
// blending, which is exactly the light concentration, so focusing folds form the bright caustic networks physically.
// Drawn with DrawProcedural (no mesh): vertex v is corner (v % 6) of quad (v / 6), the same triangles as the JS index buffer.
//   _CParams = ( grid, margin, tile L, cascade ), _CParams2 = ( slopeLevel, plane depth D, channel (0 R / 1 G), 0 )
Shader "Hidden/Tidewater/CausticsSplat"
{
    SubShader
    {
        Pass
        {
            Name "Splat"
            Blend One One
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vs
            #pragma fragment fs
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D_ARRAY(_TWOceanDeriv); SAMPLER(sampler_TWOceanDeriv);
            float4 _TWSunDir;     // toward the sun, sim space
            float4 _CParams;
            float4 _CParams2;

            struct CauOut { float4 pos : SV_POSITION; float2 vOld : TEXCOORD0; float2 vNew : TEXCOORD1; };

            CauOut vs( uint vid : SV_VertexID )
            {
                float grid = _CParams.x, margin = _CParams.y, L = _CParams.z;
                int cascade = ( int ) _CParams.w;
                float slopeLevel = _CParams2.x, D = _CParams2.y;

                uint q = vid / 6u;
                uint corner = vid % 6u;
                uint qx = q % ( uint ) grid, qy = q / ( uint ) grid;
                const uint2 off6[ 6 ] = { uint2( 0, 0 ), uint2( 1, 0 ), uint2( 0, 1 ), uint2( 0, 1 ), uint2( 1, 0 ), uint2( 1, 1 ) };
                // vertex of a grid x grid quad grid over [-margin, 1 + margin]^2
                float2 uv = float2( qx + off6[ corner ].x, qy + off6[ corner ].y ) / grid * ( 1.0 + 2.0 * margin ) - margin;
                float4 d = SAMPLE_TEXTURE2D_ARRAY_LOD( _TWOceanDeriv, sampler_TWOceanDeriv, uv, cascade, slopeLevel );
                float2 s = float2( d.x / max( d.z + 1.0, 0.3 ), d.y / max( d.w + 1.0, 0.3 ) );
                float3 n = normalize( float3( -s.x, 1.0, -s.y ) );
                float3 sun = normalize( _TWSunDir.xyz );
                float3 T = refract( -sun, n, 1.0 / 1.333 );
                float tDown = max( -T.y, 0.15 );
                // flat-surface refraction offset is removed so the pattern stays registered with the entry point (the lookup
                // re-applies it with the real depth)
                float3 T0 = refract( -sun, float3( 0.0, 1.0, 0.0 ), 1.0 / 1.333 );
                float2 offs = ( T.xz / tDown - T0.xz / max( -T0.y, 0.15 ) ) * D;
                float2 p = uv * L;
                float2 qq = p + offs;
                CauOut o;
                o.vOld = p;
                o.vNew = qq;
                float2 ndc = qq / L * 2.0 - 1.0;
                o.pos = float4( ndc.x, ndc.y, 0.0, 1.0 );
                return o;
            }

            float4 fs( CauOut i ) : SV_Target
            {
                // area ratio between the surface patch and its image on the floor
                float2 dxo = ddx( i.vOld ), dyo = ddy( i.vOld );
                float2 dxn = ddx( i.vNew ), dyn = ddy( i.vNew );
                float ao = abs( dxo.x * dyo.y - dxo.y * dyo.x );
                float an = abs( dxn.x * dyn.y - dxn.y * dyn.x );
                // soft limit: a single nearly-folded cell must not become a flat white hot spot (the finite sun disk spreads
                // real caustic peaks to a few times the mean anyway)
                float I = ao / max( an + ao * ( 1.0 / 8.0 ), 1e-9 );
                return float4( _CParams2.z < 0.5 ? I : 0.0, _CParams2.z > 0.5 ? I : 0.0, 0.0, 1.0 );
            }
            ENDHLSL
        }
    }
}
