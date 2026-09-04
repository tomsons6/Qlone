#ifndef QLONE_TRIPLANAR_LIT_META_PASS_INCLUDED
#define QLONE_TRIPLANAR_LIT_META_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/MetaInput.hlsl"

// The lightmapper needs albedo and emission for bounce light, so the meta pass
// has to run the same projection. UnityMetaVertexPosition only rewrites the clip
// position (geometry is rasterised into lightmap UV space), which leaves the
// real object-to-world transform intact for the mapping.
struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float2 uv0        : TEXCOORD0;
    float2 uv1        : TEXCOORD1;
    float2 uv2        : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3  normalWS   : TEXCOORD1;
#ifdef EDITOR_VISUALIZATION
    float2 VizUV      : TEXCOORD2;
    float4 LightCoord : TEXCOORD3;
#endif
};

Varyings TriplanarLitVertexMeta(Attributes input)
{
    Varyings output = (Varyings)0;

    output.positionCS = UnityMetaVertexPosition(input.positionOS.xyz, input.uv1, input.uv2);
    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
    output.normalWS   = half3(TransformObjectToWorldNormal(input.normalOS));

#ifdef EDITOR_VISUALIZATION
    UnityEditorVizData(input.positionOS.xyz, input.uv0, input.uv1, input.uv2, output.VizUV, output.LightCoord);
#endif

    return output;
}

half4 TriplanarLitFragmentMeta(Varyings input) : SV_Target
{
    SurfaceData surfaceData;
    half3 normalWS;
    InitializeTriplanarSurfaceData(input.positionWS, NormalizeNormalPerPixel(input.normalWS), surfaceData, normalWS);

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular,
                       surfaceData.smoothness, surfaceData.alpha, brdfData);

    MetaInput metaInput;
    metaInput.Albedo   = brdfData.diffuse + brdfData.specular * brdfData.roughness * 0.5;
    metaInput.Emission = surfaceData.emission;
#ifdef EDITOR_VISUALIZATION
    metaInput.VizUV      = input.VizUV;
    metaInput.LightCoord = input.LightCoord;
#endif

    return UnityMetaFragment(metaInput);
}

#endif // QLONE_TRIPLANAR_LIT_META_PASS_INCLUDED
