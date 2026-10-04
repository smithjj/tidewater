// The pass template of HDRP's LitTemplate.hlsl with the surface replaced by the boat's procedural materials (BoatFragment.hlsl).
// Mesh data: position, normal, uv0 (meters where it matters), uv1 = ( aux.x, aux.y ), uv2 = ( aux.z, aux.w ) with aux = ( rough, metal, pattern,
// anim ), vertex colour = linear albedo. Kind 8 (the rod) also keeps the rest position of a vertex in uv0 = ( x, y ) and uv3 = ( z, 0 ).
#define HAVE_MESH_MODIFICATION

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

#if SHADERPASS == SHADERPASS_GBUFFER && !defined(DEBUG_DISPLAY)
    #define SHADERPASS_GBUFFER_BYPASS_ALPHA_TEST
#endif

#if SHADERPASS == SHADERPASS_FORWARD && !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(DEBUG_DISPLAY)
    #define SHADERPASS_FORWARD_BYPASS_ALPHA_TEST
#endif

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#ifdef DEBUG_DISPLAY
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Debug/DebugDisplay.hlsl"
#endif
#ifdef SCENESELECTIONPASS
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/PickingSpaceTransforms.hlsl"
#endif
// (HDRP's Material.hlsl reads the blend mode of transparent surfaces: alpha = 0)
float _BlendMode;
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"

#if SHADERPASS == SHADERPASS_FORWARD
    #define HAS_LIGHTLOOP
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Lighting.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoopDef.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoop.hlsl"
#else
    #include_with_pragmas "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
#endif

#if defined(WRITE_DECAL_BUFFER) || (defined(WRITE_RENDERING_LAYER) && !defined(_DISABLE_DECALS))
#define OUTPUT_DECAL_BUFER
#endif

#if SHADERPASS != SHADERPASS_DEPTH_ONLY || defined(WRITE_NORMAL_BUFFER) || defined(OUTPUT_DECAL_BUFER) || defined(_ALPHATEST_ON)
    #define ATTRIBUTES_NEED_NORMAL
    #define ATTRIBUTES_NEED_TANGENT
    #define ATTRIBUTES_NEED_TEXCOORD0
    #define ATTRIBUTES_NEED_TEXCOORD1
    #define ATTRIBUTES_NEED_TEXCOORD2
    #define ATTRIBUTES_NEED_TEXCOORD3
    #define ATTRIBUTES_NEED_COLOR
    #define VARYINGS_NEED_POSITION_WS
    #define VARYINGS_NEED_TANGENT_TO_WORLD
    #define VARYINGS_NEED_TEXCOORD0
    #define VARYINGS_NEED_TEXCOORD1
    #define VARYINGS_NEED_TEXCOORD2
    #define VARYINGS_NEED_TEXCOORD3
    #define VARYINGS_NEED_COLOR
    #define VARYINGS_NEED_CULLFACE
#endif

// the vertex animation reads uv1 / uv2 (aux) and, for the rod, the normal in every pass, shadows and depth included
#ifndef ATTRIBUTES_NEED_NORMAL
    #define ATTRIBUTES_NEED_NORMAL
#endif
#ifndef ATTRIBUTES_NEED_TEXCOORD1
    #define ATTRIBUTES_NEED_TEXCOORD1
#endif
#ifndef ATTRIBUTES_NEED_TEXCOORD2
    #define ATTRIBUTES_NEED_TEXCOORD2
#endif

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/VaryingMesh.hlsl"
#include "BoatFragment.hlsl"

#if SHADERPASS == SHADERPASS_GBUFFER
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassGBuffer.hlsl"
#elif SHADERPASS == SHADERPASS_SHADOWS || SHADERPASS == SHADERPASS_DEPTH_ONLY
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassDepthOnly.hlsl"
#elif SHADERPASS == SHADERPASS_FORWARD
#include_with_pragmas "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassForward.hlsl"
#endif
