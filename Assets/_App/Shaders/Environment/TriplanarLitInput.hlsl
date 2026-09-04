#ifndef QLONE_TRIPLANAR_LIT_INPUT_INCLUDED
#define QLONE_TRIPLANAR_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

// SurfaceInput.hlsl already declares _BaseMap, _BumpMap and _EmissionMap.
TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);

// Keep one layout for every keyword permutation - the SRP Batcher cannot handle a
// UnityPerMaterial buffer whose size changes between variants.
CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    float4 _BaseMap_TexelSize;
    half4  _BaseColor;
    half4  _EmissionColor;
    half   _Metallic;
    half   _Smoothness;
    half   _BumpScale;
    half   _OcclusionStrength;
    half   _BlendSharpness;
    UNITY_TEXTURE_STREAMING_DEBUG_VARS;
CBUFFER_END

///////////////////////////////////////////////////////////////////////////////
//                          Triplanar projection                             //
///////////////////////////////////////////////////////////////////////////////

// The three axis-aligned projections of one surface point, plus the weights they
// are mixed with. "Mapping space" is world space, or the rotation-only frame of
// the object when _MAPSPACE_OBJECT is set.
struct TriplanarInput
{
    float2 uvX;         // plane facing X, samples (z,y)
    float2 uvY;         // plane facing Y, samples (x,z) - floors and ceilings
    float2 uvZ;         // plane facing Z, samples (x,y)
    half3  weights;     // per-plane blend weights, sums to 1
    half3  axisSign;    // +-1 per axis, undoes the back-face mirroring
    half3  normalMS;    // geometric normal, mapping space
};

TriplanarInput GetTriplanarInput(float3 positionMS, half3 normalMS)
{
    TriplanarInput t;
    t.normalMS = normalMS;
    t.axisSign = normalMS < 0.0h ? -1.0h : 1.0h;

    float2 uvX = positionMS.zy;
    float2 uvY = positionMS.xz;
    float2 uvZ = positionMS.xy;

    // Mirror the back-facing half of each projection so opposite faces are not
    // sampled with flipped handedness. Faces pointing the same way still share
    // one continuous mapping, which is what makes neighbouring modules line up.
    uvX.x *=  t.axisSign.x;
    uvY.x *=  t.axisSign.y;
    uvZ.x *= -t.axisSign.z;

    // Tiling/offset comes from the standard _BaseMap tiling fields, so a tiling
    // of 1 means "one texture tile per world unit".
    t.uvX = TRANSFORM_TEX(uvX, _BaseMap);
    t.uvY = TRANSFORM_TEX(uvY, _BaseMap);
    t.uvZ = TRANSFORM_TEX(uvZ, _BaseMap);

#if defined(_MAPBLEND_DOMINANT)
    // Axis-aligned geometry gets an identical result from one sample instead of
    // three, so modular corridors can take the cheap path.
    float3 an = abs(normalMS);
    t.weights = (an.x >= an.y && an.x >= an.z) ? half3(1, 0, 0)
              : (an.y >= an.z)                 ? half3(0, 1, 0)
              :                                  half3(0, 0, 1);
#else
    // Normalise before pow() so the largest weight is always 1: raising raw
    // normal components to a high power underflows half precision and can leave
    // the sum at zero.
    float3 an = abs(normalMS);
    an /= max(max(an.x, max(an.y, an.z)), 1e-6);
    float3 w = pow(an + 1e-6, _BlendSharpness);
    t.weights = half3(w / (w.x + w.y + w.z));
#endif

    return t;
}

half4 SampleTriplanar(TEXTURE2D_PARAM(tex, samp), TriplanarInput t)
{
#if defined(_MAPBLEND_DOMINANT)
    float2 uv = t.weights.x > 0.5h ? t.uvX : (t.weights.y > 0.5h ? t.uvY : t.uvZ);
    return SAMPLE_TEXTURE2D(tex, samp, uv);
#else
    return SAMPLE_TEXTURE2D(tex, samp, t.uvX) * t.weights.x
         + SAMPLE_TEXTURE2D(tex, samp, t.uvY) * t.weights.y
         + SAMPLE_TEXTURE2D(tex, samp, t.uvZ) * t.weights.z;
#endif
}

// Returns a mapping-space normal. Each plane tangent-space normal is swizzled
// onto its own axis and summed with the geometric normal (whiteout blend), so no
// mesh tangents or UVs are needed anywhere in this shader.
half3 SampleTriplanarNormal(TEXTURE2D_PARAM(bumpMap, samp), TriplanarInput t, half scale)
{
#if !defined(_NORMALMAP)
    return t.normalMS;
#else
    half3 n  = t.normalMS;
    half3 an = abs(n);

  #if defined(_MAPBLEND_DOMINANT)
    // Select the plane first so this stays one sample and one return - early
    // returns per branch make the D3D compiler warn about uninitialised paths.
    bool useX = t.weights.x > 0.5h;
    bool useY = t.weights.y > 0.5h;

    float2 uv   = useX ? t.uvX : (useY ? t.uvY : t.uvZ);
    half   flip = useX ? t.axisSign.x : (useY ? t.axisSign.y : -t.axisSign.z);

    half3 tn = UnpackNormalScale(SAMPLE_TEXTURE2D(bumpMap, samp, uv), scale);
    tn.x *= flip;

    half3 result = half3(tn.x + n.x, tn.y + n.y, an.z);                 // Z plane
    result = useY ? half3(tn.x + n.x, an.y,        tn.y + n.z) : result;
    result = useX ? half3(an.x,       tn.y + n.y,  tn.x + n.z) : result;
    return normalize(result);
  #else
    half3 tnx = UnpackNormalScale(SAMPLE_TEXTURE2D(bumpMap, samp, t.uvX), scale);
    half3 tny = UnpackNormalScale(SAMPLE_TEXTURE2D(bumpMap, samp, t.uvY), scale);
    half3 tnz = UnpackNormalScale(SAMPLE_TEXTURE2D(bumpMap, samp, t.uvZ), scale);

    tnx.x *=  t.axisSign.x;
    tny.x *=  t.axisSign.y;
    tnz.x *= -t.axisSign.z;

    return normalize(
          half3(an.x,        tnx.y + n.y, tnx.x + n.z) * t.weights.x
        + half3(tny.x + n.x, an.y,        tny.y + n.z) * t.weights.y
        + half3(tnz.x + n.x, tnz.y + n.y, an.z       ) * t.weights.z);
  #endif
#endif
}

///////////////////////////////////////////////////////////////////////////////
//                              Surface data                                 //
///////////////////////////////////////////////////////////////////////////////

// positionWS/normalWS in, URP SurfaceData plus a world-space shading normal out.
// Shared by the forward, depth-normals and meta passes.
void InitializeTriplanarSurfaceData(float3 positionWS, half3 normalWS,
                                    out SurfaceData surfaceData, out half3 outNormalWS)
{
    float3 positionMS = positionWS;
    half3  normalMS   = normalWS;

#if defined(_MAPSPACE_OBJECT)
    // Project in the frame of the object with its scale divided out, so the
    // mapping travels with a moving door or lift while texel density stays in
    // world units. rot is orthonormal, so its inverse is its transpose.
    float3x3 o2w = (float3x3)UNITY_MATRIX_M;
    float3 objScale = max(float3(length(o2w._m00_m10_m20),
                                 length(o2w._m01_m11_m21),
                                 length(o2w._m02_m12_m22)), 1e-6);
    float3x3 rot = o2w;
    rot[0] /= objScale;
    rot[1] /= objScale;
    rot[2] /= objScale;

    positionMS = TransformWorldToObject(positionWS) * objScale;
    normalMS   = half3(normalize(mul((float3)normalWS, rot)));
#endif

    TriplanarInput t = GetTriplanarInput(positionMS, normalMS);

    surfaceData = (SurfaceData)0;

    surfaceData.albedo = SampleTriplanar(TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap), t).rgb * _BaseColor.rgb;
    surfaceData.alpha  = 1.0h;

    surfaceData.metallic   = _Metallic;
    surfaceData.smoothness = _Smoothness;

#if defined(_MASKSOURCE_METALLICSMOOTHNESS)
    half4 mask = SampleTriplanar(TEXTURE2D_ARGS(_MetallicGlossMap, sampler_MetallicGlossMap), t);
    surfaceData.metallic   = mask.r;
    surfaceData.smoothness = mask.a;
#elif defined(_MASKSOURCE_ROUGHNESS)
    // Standalone grayscale roughness map, which is what most PBR sets ship.
    half roughness = SampleTriplanar(TEXTURE2D_ARGS(_MetallicGlossMap, sampler_MetallicGlossMap), t).g;
    surfaceData.smoothness = 1.0h - roughness;
#endif

#if defined(_OCCLUSIONMAP)
    half occlusion = SampleTriplanar(TEXTURE2D_ARGS(_OcclusionMap, sampler_OcclusionMap), t).g;
    surfaceData.occlusion = LerpWhiteTo(occlusion, _OcclusionStrength);
#else
    surfaceData.occlusion = 1.0h;
#endif

#if defined(_EMISSION)
    surfaceData.emission = SampleTriplanar(TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap), t).rgb * _EmissionColor.rgb;
#else
    surfaceData.emission = 0.0h;
#endif

    // We build a world-space normal directly, so normalTS stays unused.
    surfaceData.normalTS = half3(0, 0, 1);

    half3 blendedNormalMS = SampleTriplanarNormal(TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), t, _BumpScale);
#if defined(_MAPSPACE_OBJECT)
    outNormalWS = half3(normalize(mul(rot, (float3)blendedNormalMS)));
#else
    outNormalWS = blendedNormalMS;
#endif
}

#endif // QLONE_TRIPLANAR_LIT_INPUT_INCLUDED
