// Leaf-cluster cards baked once on the GPU (src/world/vegetation/LeafTextures.js): a 2 x 2 atlas of tiles, each tile a card of many small twig-end leaf whorls on a jittered
// grid with empty cells, in two offset layers, 4x supersampled. R coverage, G brightness structure / 1.4, B per-leaf random. v up (the card's).
// Tiles: 0 tree, broad leaves   1 tree, narrow leaves   2 shrub, round leaves   3 shrub, narrow leaves
Shader "Hidden/Tidewater/VegLeafBake"
{
	SubShader
	{
		Pass
		{
			Cull Off ZWrite Off ZTest Always
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 4.5
			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
			#include "VegBake.hlsl"

			struct VIn { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
			struct VOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
			// a full-target quad ( Graphics.Blit's ): the pixel's uv is the sample's, v up in the stored image
			VOut Vert( VIn v )
			{
				VOut o;
				o.pos = float4( v.uv * 2.0 - 1.0, 0.0, 1.0 );
#if UNITY_UV_STARTS_AT_TOP
				o.pos.y = - o.pos.y;
#endif
				o.uv = v.uv;
				return o;
			}

			// leaf rosettes tiled G x G over a card: each cell holds one rosette with its own rotation / size; leaves have a short petiole gap at the centre.
			// Returns ( d: signed distance to the leaf edge ( in rosette units, > 0 inside ), bright, cell ).
			void vegRosette( float2 st, float G, float nLeaves, float width, float rot, out float d, out float bright, out float cellR )
			{
				float2 cuv = st * G;
				float2 cell = floor( cuv );
				float h1 = vegHash12( cell + rot * 17.3 );
				float h2 = vegHash12( cell * 1.7 + rot * 31.1 + 5.2 );
				float h3 = vegHash12( cell * 2.3 + rot * 7.7 + 1.9 );
				// whorls of different sizes, off the cell centres; some cells stay empty ( sky holes )
				float sc = lerp( 0.62, 1.05, h2 ) * ( h3 < 0.2 ? 0.001 : 1.0 );
				float2 off = float2( h1 - 0.5, h3 - 0.5 ) * 0.34;
				float2 p = ( frac( cuv ) - 0.5 - off ) * 2.0 / sc;
				float r = length( p );
				float ang = atan2( p.y, p.x ) + rot + h1 * 6.2832;
				float sector = ang * ( nLeaves / 6.2832 );
				float k = floor( sector + 0.5 );
				// irregular leaf spacing and lengths so clusters don't read as flowers
				float lr = vegHash12( float2( k, h1 * 13.7 ) );
				float jit = ( lr - 0.5 ) * 0.8;
				float L = lerp( 0.5, 1.0, frac( lr * 7.13 ) );
				// each blade curves a little to one side ( leaves, not a star )
				float curve = ( frac( lr * 3.31 ) - 0.5 ) * 0.9;
				float da = ( sector - k - jit ) * ( 6.2832 / nLeaves ) - curve * ( r / L ) * ( r / L ) * 0.35;
				float along = r * cos( da );
				float across = abs( r * sin( da ) );
				float x = along / L;
				float bx = clamp( ( x - 0.18 ) / 0.82, 0.0, 1.0 ); // blade after a short petiole
				// obovate ( widest beyond the middle ) with a short pointed tip
				float shape = pow( max( sin( pow( bx, 0.62 ) * 3.14159 ), 0.0 ), 0.8 ) * ( vsm( 1.0, 0.86, bx ) * 0.25 + 0.75 );
				float hw = shape * width * L;
				bool inBlade = x > 0.16 && x < 1.0;
				float dBlade = inBlade ? hw - across : -1.0;
				float dPet = x < 0.2 ? 0.012 - across : -1.0;
				d = max( dBlade, dPet );
				float vein = vsm( 0.03, 0.0, across ) * vsm( 0.05, 0.25, bx ) * vsm( 1.0, 0.7, bx );
				bright = ( ( h1 * 0.7 + lr * 0.3 ) * 0.22 + 0.88 ) * lerp( 0.82, 1.08, bx ) * ( vein * 0.09 + 1.0 );
				cellR = h1 * 0.7 + lr * 0.3;
			}

			static const float TG[ 4 ] = { 4.0, 4.0, 3.0, 3.0 };
			static const float TL[ 4 ] = { 6.0, 8.0, 6.0, 8.0 };
			static const float TW[ 4 ] = { 0.34, 0.2, 0.45, 0.22 };

			float4 Frag( VOut i ) : SV_Target
			{
				float2 st = i.uv;
				float2 tile = floor( st * 2.0 );
				int tid = ( int )( tile.x + tile.y * 2.0 );
				float2 localUv = frac( st * 2.0 );
				float px = 1.0 / 512.0; // texel size in tile uv
				float cov = 0.0; float bright = 0.0; float cellR = 0.0;
				for ( int s = 0; s < 4; s ++ )
				{
					float ox = ( ( float )( s % 2 ) - 0.5 ) * px * 0.5, oy = ( floor( s / 2 ) - 0.5 ) * px * 0.5;
					float2 q = localUv + float2( ox, oy );
					float dA, bA, cA, dB, bB, cB;
					vegRosette( q, TG[ tid ], TL[ tid ], TW[ tid ], 0.0, dA, bA, cA );
					vegRosette( q + 0.5 / TG[ tid ], TG[ tid ], TL[ tid ], TW[ tid ], 2.1, dB, bB, cB );
					bool inA = dA > 0.0; bool inB = dB > 0.0;
					if ( inA || inB )
					{
						cov += 0.25;
						bright += ( inA ? bA : bB ) * 0.25;
						cellR += ( inA ? cA : cB ) * 0.25;
					}
				}

				// colour channels are stored un-premultiplied ( valid where covered )
				float inv = 1.0 / max( cov, 1e-3 );
				return float4( cov, bright * inv / 1.4, cellR * inv, 1.0 );
			}
			ENDHLSL
		}
	}
}
