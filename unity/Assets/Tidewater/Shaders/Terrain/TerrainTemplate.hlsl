// The pass template of HDRP's TerrainLitTemplate.hlsl with the terrain data replaced by ours
// (TerrainFragment.hlsl): no splat layers, no holes, no alpha test, no per-pixel-normal variant.
#define HAVE_MESH_MODIFICATION

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/FragInputs.hlsl"
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"

#if SHADERPASS == SHADERPASS_GBUFFER && !defined(DEBUG_DISPLAY)
    // When we have alpha test, we will force a depth prepass so we always bypass the clip instruction in the GBuffer
    // Don't do it with debug display mode as it is possible there is no depth prepass in this case
    #define SHADERPASS_GBUFFER_BYPASS_ALPHA_TEST
#endif

#if SHADERPASS == SHADERPASS_FORWARD && !defined(_SURFACE_TYPE_TRANSPARENT) && !defined(DEBUG_DISPLAY)
    // In case of opaque we don't want to perform the alpha test, it is done in depth prepass and we use depth equal for ztest (setup from UI)
    // Don't do it with debug display mode as it is possible there is no depth prepass in this case
    #define SHADERPASS_FORWARD_BYPASS_ALPHA_TEST
#endif

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#ifdef DEBUG_DISPLAY
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Debug/DebugDisplay.hlsl"
#endif
#ifdef SCENESELECTIONPASS
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/PickingSpaceTransforms.hlsl"
#endif
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"

#if SHADERPASS == SHADERPASS_FORWARD
    // The light loop (or lighting architecture) is in charge to:
    // - Define light list
    // - Define the light loop
    // - Setup the constant/data
    // - Do the reflection hierarchy
    // - Provide sampling function for shadowmap, ies, cookie and reflection (depends on the specific use with the light loops like index array or atlas or single and texture format (cubemap/latlong))
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

#if SHADERPASS != SHADERPASS_DEPTH_ONLY || defined(WRITE_NORMAL_BUFFER) || defined(OUTPUT_DECAL_BUFER)
    #define ATTRIBUTES_NEED_NORMAL
    #define ATTRIBUTES_NEED_TEXCOORD0
    #define ATTRIBUTES_NEED_TANGENT // will be filled by ApplyMeshModification()
    // Varying - Use for pixel shader
    // This second set of define allow to say which varyings will be output in the vertex (no more tesselation)
    #define VARYINGS_NEED_POSITION_WS
    #define VARYINGS_NEED_TANGENT_TO_WORLD
    #define VARYINGS_NEED_TEXCOORD0
#endif

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/VaryingMesh.hlsl"
#include "TerrainFragment.hlsl"

#if SHADERPASS == SHADERPASS_GBUFFER
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassGBuffer.hlsl"
#elif SHADERPASS == SHADERPASS_SHADOWS || SHADERPASS == SHADERPASS_DEPTH_ONLY
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassDepthOnly.hlsl"
#elif SHADERPASS == SHADERPASS_FORWARD
#include_with_pragmas "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassForward.hlsl"
#endif
