// The impostor atlases (src/world/vegetation/VegMaterials.js createCanopyBakeMaterials): every plant variant drawn from the hemi-octahedral directions, unlit.
//   _VegBakeWhich 0 (A): ( leaf brightness / 1.4 | bark brightness, leaf flag, card colour random, 1 )
//   _VegBakeWhich 1 (B): ( plant-local normal * 0.5 + 0.5, exposure )
// The vertices are in the sim frame; _VegBakeVP takes them (through _VegBakeM, the frame's matrix) to the clip space of the atlas.
Shader "Hidden/Tidewater/VegImpostorBake"
{
	SubShader
	{
		Pass
		{
			Cull Off ZWrite On ZTest LEqual
			HLSLPROGRAM
			#pragma vertex Vert
			#pragma fragment Frag
			#pragma target 4.5
			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
			#include "VegBake.hlsl"

			float4x4 _VegBakeVP;
			float4x4 _VegBakeM;
			float _VegBakeWhich;
			TEXTURE2D( _VegLeafAtlas ); SAMPLER( sampler_VegLeafAtlas );

			struct VIn { float4 pos : POSITION; float3 nrm : NORMAL; float4 col : COLOR; float2 uv : TEXCOORD0; };
			struct VOut { float4 pos : SV_POSITION; float4 mat : TEXCOORD0; float3 localN : TEXCOORD1; float2 uv : TEXCOORD2; };

			VOut Vert( VIn v )
			{
				VOut o;
				o.pos = mul( _VegBakeVP, mul( _VegBakeM, float4( v.pos.xyz, 1.0 ) ) );
				o.mat = v.col; o.localN = v.nrm; o.uv = v.uv;
				return o;
			}

			float4 leafSample( float2 st, float tile )
			{
				float2 tuv = ( float2( frac( tile * 0.5 ) * 2.0, floor( tile * 0.5 ) ) + clamp( st, 0.004, 0.996 ) ) * 0.5;
				return SAMPLE_TEXTURE2D( _VegLeafAtlas, sampler_VegLeafAtlas, tuv );
			}

			float4 Frag( VOut i ) : SV_Target
			{
				float part = i.mat.x;
				bool isBark = part < 0.5 || part > 4.5;
				bool isShrub = part > 2.5;
				float2 st = i.uv;
				float4 L = leafSample( st, isShrub ? 2.0 : 0.0 );
				if ( ! ( isBark || L.x > 0.5 ) ) discard;
				if ( _VegBakeWhich < 0.5 )
				{
					float vBright = L.y * 1.4;
					float barkN = vegNoise( float2( st.x * 30.0, st.y * 2.5 ) ) * 0.6 + vegNoise( float2( st.x * 6.0, st.y * 0.7 ) ) * 0.4;
					float bright = isBark ? barkN * 0.5 + 0.4 : vBright / 1.4;
					return float4( bright, isBark ? 0.0 : 1.0, i.mat.z, 1.0 );
				}

				return float4( normalize( i.localN ) * 0.5 + 0.5, isBark ? 0.6 : i.mat.y );
			}
			ENDHLSL
		}
	}
}
