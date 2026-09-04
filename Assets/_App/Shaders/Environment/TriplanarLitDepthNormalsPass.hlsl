#ifndef QLONE_TRIPLANAR_LIT_DEPTH_NORMALS_PASS_INCLUDED
#define QLONE_TRIPLANAR_LIT_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// Writes the triplanar-blended normal rather than the flat geometric one, so
// SSAO and anything else reading the depth-normals prepass sees the same surface
// the forward pass shades. The unused map samples are dead-code eliminated.
struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float3 positionWS : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings DepthNormalsVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs   normalInput = GetVertexNormalInputs(input.normalOS);

    output.positionWS = vertexInput.positionWS;
    output.normalWS   = half3(normalInput.normalWS);
    output.positionCS = vertexInput.positionCS;

    return output;
}

void DepthNormalsFragment(
    Varyings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out float4 outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    SurfaceData surfaceData;
    half3 normalWS;
    InitializeTriplanarSurfaceData(input.positionWS, NormalizeNormalPerPixel(input.normalWS), surfaceData, normalWS);
    normalWS = NormalizeNormalPerPixel(normalWS);

#if defined(_GBUFFER_NORMALS_OCT)
    float2 octNormalWS         = PackNormalOctQuadEncode(normalWS);   // [-1, +1], fp32 on some platforms
    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);   // [ 0,  1]
    half3  packedNormalWS      = PackFloat2To888(remappedOctNormalWS);
    outNormalWS = half4(packedNormalWS, 0.0);
#else
    outNormalWS = half4(normalWS, 0.0);
#endif

#ifdef _WRITE_RENDERING_LAYERS
    uint renderingLayers = GetMeshRenderingLayer();
    outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
#endif
}

#endif // QLONE_TRIPLANAR_LIT_DEPTH_NORMALS_PASS_INCLUDED
