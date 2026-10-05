// The portrait studio's pass of the fish (Game/FishPortrait.cs): FishSurface lit by the JS STUDIO_LIGHTING (src/engine/render/wgsl/lighting.js shadeSurface with the
// pass define on) instead of HDRP's light loop: the key light, a grey sweep with one softbox for the specular environment, the sweep's gradient for the diffuse one, no
// shadows, local lights, fog or world hooks. Drawn by the studio camera as a forward-only opaque object; the colour is the JS radiance in scene units (the camera's
// fixed exposure makes one unit 1.0), the alpha the coverage.
//
// Directions are taken in sim space (the world mirrors z: Sim.cs), so the JS formulas, the softbox azimuth included, apply as written.

#include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/VertMesh.hlsl"

float4 _StudioKey;      // xyz: direction to the key light (sim space, unit)
float4 _StudioKeyColor; // rgb: the key light's colour (scene units)
float4 _StudioEnv;      // x: the softbox azimuth (radians), y: the environment's intensity

#define SPI 3.14159265359
#define SEPS 1e-6

float3 sSim( float3 v ) { return float3( v.x, v.y, - v.z ); }

float3 studioEnvSpecular( float3 R, float roughness )
{
    float3 sweep = lerp( float3( 0.035, 0.04, 0.045 ), float3( 0.55, 0.57, 0.6 ), smoothstep( -0.35, 0.85, R.y ) );
    float az = atan2( R.x, R.z ) - _StudioEnv.x;
    float w = 0.35 + roughness * 1.6;
    // (WGSL smoothstep( 0.98, 0.55, y ) has its edges reversed: it falls from 1 to 0, as 1 - smoothstep( 0.55, 0.98, y ))
    float box = exp( - az * az / ( w * w ) ) * smoothstep( -0.05, 0.3, R.y ) * ( 1.0 - smoothstep( 0.55, 0.98, R.y ) );
    return ( sweep + box * float3( 2.4, 2.35, 2.25 ) / ( 1.0 + roughness * 3.0 ) ) * _StudioEnv.y;
}

float3 studioEnvDiffuse( float3 N )
{
    return lerp( float3( 0.05, 0.055, 0.06 ), float3( 0.3, 0.31, 0.33 ), N.y * 0.5 + 0.5 ) * _StudioEnv.y;
}

float3 sFSchlick( float3 f0, float f90, float dotVH )
{
    float fresnel = exp2( ( -5.55473 * dotVH - 6.98316 ) * dotVH );
    return f0 * ( 1.0 - fresnel ) + f90 * fresnel;
}

float sVGGX( float alpha, float dotNL, float dotNV )
{
    float a2 = alpha * alpha;
    float gv = dotNL * sqrt( a2 + ( 1.0 - a2 ) * dotNV * dotNV );
    float gl = dotNV * sqrt( a2 + ( 1.0 - a2 ) * dotNL * dotNL );
    return 0.5 / max( gv + gl, SEPS );
}

float sDGGX( float alpha, float dotNH )
{
    float a2 = alpha * alpha;
    float d = dotNH * dotNH * ( a2 - 1.0 ) + 1.0;
    return a2 / ( SPI * d * d );
}

float3 sBRDF( float3 L, float3 V, float3 N, float3 f0, float f90, float roughness )
{
    float alpha = roughness * roughness;
    float3 H = normalize( L + V );
    float dotNL = saturate( dot( N, L ) ); float dotNV = saturate( dot( N, V ) );
    float dotNH = saturate( dot( N, H ) ); float dotVH = saturate( dot( V, H ) );
    return sFSchlick( f0, f90, dotVH ) * sVGGX( alpha, dotNL, dotNV ) * sDGGX( alpha, dotNH );
}

// Karis' split-sum DFG fit and the multi-scattering compensation of three's computeMultiscattering
float2 sDFG( float dotNV, float roughness )
{
    float4 c0 = float4( -1.0, -0.0275, -0.572, 0.022 );
    float4 c1 = float4( 1.0, 0.0425, 1.04, -0.04 );
    float4 r = roughness * c0 + c1;
    float a004 = min( r.x * r.x, exp2( -9.28 * dotNV ) ) * r.x + r.y;
    return float2( -1.04, 1.04 ) * a004 + r.zw;
}

void sMultiscatter( float3 N, float3 V, float3 specColor, float specF90, float roughness, out float3 single, out float3 multi )
{
    float2 fab = sDFG( saturate( dot( N, V ) ), roughness );
    float3 Fr = specColor;
    float3 FssEss = Fr * fab.x + specF90 * fab.y;
    float Ess = fab.x + fab.y;
    float Ems = 1.0 - Ess;
    float3 Favg = Fr + ( 1.0 - Fr ) * 0.047619;
    float3 Fms = FssEss * Favg / ( 1.0 - Ems * Favg );
    single = FssEss;
    multi = Fms * Ems;
}

float3 studioShade( FishOut s, float3 geomN, float3 V )
{
    float3 N = sSim( s.normal );
    float3 Vs = sSim( V );
    float3 L = _StudioKey.xyz;
    float rough = clamp( s.roughness, 0.03, 1.0 );
    float3 diffuseColor = s.albedo * ( 1.0 - s.metalness );
    float3 specF0 = lerp( ( float3 ) ( 0.04 * s.spec ), s.albedo, s.metalness );
    float specF90 = lerp( s.spec, 1.0, s.metalness );

    // ---- the key light
    float3 lightColor = _StudioKeyColor.rgb;
    float dotNL = saturate( dot( N, L ) );
    float3 irradiance = dotNL * lightColor;
    float3 directDiffuse = irradiance * diffuseColor / SPI;
    float3 directSpecular = irradiance * sBRDF( L, Vs, N, specF0, specF90, rough );
    // thin-surface transmission (the fins): lit from behind as well
    directDiffuse += s.albedo * ( s.transl * 0.5 ) * lightColor;

    float3 ccN = sSim( geomN );
    float ccNL = saturate( dot( ccN, L ) );
    float3 ccSpec = ccNL * lightColor * sBRDF( L, Vs, ccN, ( float3 ) 0.04, 1.0, 0.2 );

    // ---- indirect: the studio environment
    float3 R = reflect( - Vs, N );
    float3 Rr = normalize( lerp( R, N, rough * rough ) );
    float3 envIrr = studioEnvDiffuse( N ) * SPI;
    float3 radiance = studioEnvSpecular( Rr, rough );
    float3 single, multi;
    sMultiscatter( N, Vs, specF0, specF90, rough, single, multi );
    float3 totalScatter = single + multi;
    float3 diffuseMS = diffuseColor * ( 1.0 - max( max( totalScatter.r, totalScatter.g ), totalScatter.b ) );
    float3 indirectSpecular = radiance * single + multi * envIrr / SPI;
    float3 indirectDiffuse = diffuseMS * envIrr / SPI;

    // specular occlusion after Lagarde (the fish have no ambient occlusion: ao = 1)
    float dotNV = saturate( dot( N, Vs ) );
    float specAO = saturate( pow( dotNV + 1.0, exp2( -16.0 * rough - 1.0 ) ) - 1.0 + 1.0 );
    indirectSpecular *= specAO;

    float3 color = directDiffuse + directSpecular + indirectDiffuse + indirectSpecular;

    // ---- the wet sheen: a clear coat over it all
    float ccNV = saturate( dot( ccN, Vs ) );
    float3 Fcc = sFSchlick( ( float3 ) 0.04, 1.0, ccNV ) * s.coat;
    float3 ccRad = studioEnvSpecular( reflect( - Vs, ccN ), 0.2 ) * specAO;
    color = color * ( 1.0 - Fcc ) + ( ccSpec * s.coat + ccRad * Fcc );
    return color;
}

PackedVaryingsType Vert( AttributesMesh inputMesh )
{
    VaryingsType varyingsType;
    varyingsType.vmesh = VertMesh( inputMesh );
    return PackVaryingsType( varyingsType );
}

float4 Frag( PackedVaryingsToPS packedInput ) : SV_Target0
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX( packedInput );
    FragInputs input = UnpackVaryingsToFragInputs( packedInput );
    float3 V = GetWorldSpaceNormalizeViewDir( input.positionRWS );
#include "FishFragBody.hlsl"
    float3 color = studioShade( s, normalize( input.tangentToWorld[ 2 ] ), V );
    // HDRP's pre-exposure: the camera's fixed exposure makes this 1.0
    return float4( color * GetCurrentExposureMultiplier(), 1.0 );
}
