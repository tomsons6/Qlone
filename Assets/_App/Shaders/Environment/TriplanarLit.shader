// World-space projected lit shader for modular environment geometry.
//
// Textures are projected from world space instead of read from mesh UVs, so
// unwrapped/untextured corridor modules tile seamlessly and neighbouring pieces
// line up automatically wherever they are placed. Tiling and offset come from
// the standard Base Map tiling fields: a tiling of 1 is one texture tile per
// world unit, and the same values drive every map.
//
// Lighting, shadows, fog, lightmaps and probes match URP Lit (metallic
// workflow, opaque only). Meshes need no UV0 or tangents; UV1/UV2 are still
// used for lightmaps.
Shader "Qlone/Environment/Triplanar Lit"
{
    Properties
    {
        [Header(Surface)]
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5

        [Space(8)][Header(Projection)]
        // World: shared grid, modules line up. Object: mapping travels with the
        // object, for doors/lifts and anything else that moves at runtime.
        [KeywordEnum(World, Object)] _MapSpace("Mapping Space", Float) = 0
        // Dominant takes one sample instead of three - identical result on
        // axis-aligned geometry, seams on curved or angled surfaces.
        [KeywordEnum(Triplanar, Dominant)] _MapBlend("Axis Blend", Float) = 0
        _BlendSharpness("Blend Sharpness", Range(1.0, 32.0)) = 8.0

        [Space(8)][Header(Normal Map)]
        [Toggle(_NORMALMAP)] _UseNormalMap("Enable Normal Map", Float) = 0
        [NoScaleOffset][Normal] _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1.0

        [Space(8)][Header(Mask Map)]
        // MetallicSmoothness: URP layout, metallic in R and smoothness in A.
        // Roughness: a plain grayscale roughness map, inverted for smoothness.
        [KeywordEnum(None, MetallicSmoothness, Roughness)] _MaskSource("Mask Source", Float) = 0
        [NoScaleOffset] _MetallicGlossMap("Mask Map", 2D) = "white" {}

        [Space(8)][Header(Occlusion)]
        [Toggle(_OCCLUSIONMAP)] _UseOcclusionMap("Enable Occlusion Map", Float) = 0
        [NoScaleOffset] _OcclusionMap("Occlusion Map", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Range(0.0, 1.0)) = 1.0

        [Space(8)][Header(Emission)]
        [Toggle(_EMISSION)] _UseEmission("Enable Emission", Float) = 0
        [HDR] _EmissionColor("Emission Color", Color) = (0, 0, 0, 1)
        [NoScaleOffset] _EmissionMap("Emission Map", 2D) = "white" {}

        [Space(8)][Header(Advanced)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull("Render Face", Float) = 2
        [ToggleOff] _SpecularHighlights("Specular Highlights", Float) = 1.0
        [ToggleOff] _EnvironmentReflections("Environment Reflections", Float) = 1.0
        [Toggle(_RECEIVE_SHADOWS_OFF)] _ReceiveShadowsOff("Disable Receive Shadows", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Blend One Zero
            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex TriplanarLitVertex
            #pragma fragment TriplanarLitFragment

            // -------------------------------------
            // Material keywords
            #pragma shader_feature_local _MAPSPACE_WORLD _MAPSPACE_OBJECT
            #pragma shader_feature_local _MAPBLEND_TRIPLANAR _MAPBLEND_DOMINANT
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _MASKSOURCE_NONE _MASKSOURCE_METALLICSMOOTHNESS _MASKSOURCE_ROUGHNESS
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _SPECULARHIGHLIGHTS_OFF
            #pragma shader_feature_local_fragment _ENVIRONMENTREFLECTIONS_OFF

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _FORWARD_PLUS
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"

            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            #include "TriplanarLitInput.hlsl"
            #include "TriplanarLitForwardPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "TriplanarLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #pragma multi_compile_instancing

            #include "TriplanarLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        // Feeds SSAO and any other depth-normals consumer with the projected
        // normal rather than the flat geometric one.
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment

            #pragma shader_feature_local _MAPSPACE_WORLD _MAPSPACE_OBJECT
            #pragma shader_feature_local _MAPBLEND_TRIPLANAR _MAPBLEND_DOMINANT
            #pragma shader_feature_local _NORMALMAP

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
            #pragma multi_compile_instancing

            #include "TriplanarLitInput.hlsl"
            #include "TriplanarLitDepthNormalsPass.hlsl"
            ENDHLSL
        }

        // Albedo and emission for the lightmapper, sampled through the same
        // projection so baked bounce light matches what is rendered.
        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode" = "Meta"
            }

            Cull Off

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex TriplanarLitVertexMeta
            #pragma fragment TriplanarLitFragmentMeta

            #pragma shader_feature_local _MAPSPACE_WORLD _MAPSPACE_OBJECT
            #pragma shader_feature_local _MAPBLEND_TRIPLANAR _MAPBLEND_DOMINANT
            #pragma shader_feature_local_fragment _MASKSOURCE_NONE _MASKSOURCE_METALLICSMOOTHNESS _MASKSOURCE_ROUGHNESS
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature EDITOR_VISUALIZATION

            #include "TriplanarLitInput.hlsl"
            #include "TriplanarLitMetaPass.hlsl"
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
