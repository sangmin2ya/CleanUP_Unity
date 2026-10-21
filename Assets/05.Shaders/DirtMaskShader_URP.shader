Shader "Custom/DirtMaskShader_URP"
{
    Properties
    {
        _MainTex ("Base Texture", 2D) = "white" {}
        _MainColor ("Base Color", Color) = (1,1,1,1)
        _DirtMask ("Dirt Mask (R Channel)", 2D) = "white" {}
        _MaskUVChannel ("Mask UV Channel (0=UV0, 1=UV1)", Float) = 0
        _DirtTex ("Dirt Pattern Texture", 2D) = "white" {}
        _DirtColor ("Dirt Color", Color) = (0.4, 0.3, 0.2, 1)
        _DirtIntensity ("Dirt Intensity", Range(0, 1)) = 0.8

        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0

        _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _LIGHT_LAYERS
            // Forward+ 렌더러에서 메인 라이트 감쇠/추가 라이트 루프가 동작하려면 필요
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2;
                float3 tangentWS : TEXCOORD3;
                float3 bitangentWS : TEXCOORD4;
                float2 rawUV : TEXCOORD5;
                float2 maskUV : TEXCOORD6;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_DirtMask);
            SAMPLER(sampler_DirtMask);

            TEXTURE2D(_DirtTex);
            SAMPLER(sampler_DirtTex);

            TEXTURE2D(_BumpMap);
            SAMPLER(sampler_BumpMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _DirtTex_ST;
                half4 _MainColor;
                half4 _DirtColor;
                half _DirtIntensity;
                half _Smoothness;
                half _Metallic;
                half _BumpScale;
                float _MaskUVChannel;
            CBUFFER_END

            Varyings vert(Attributes input)
            {
                Varyings output;

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.rawUV = input.uv;
                output.maskUV = lerp(input.uv, input.uv1, _MaskUVChannel);

                output.normalWS = normalInput.normalWS;
                output.tangentWS = normalInput.tangentWS;
                output.bitangentWS = normalInput.bitangentWS;

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // 베이스 컬러
                half4 baseColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _MainColor;

                // 더러움 마스크 (R 채널)
                half dirtAmount = saturate(SAMPLE_TEXTURE2D(_DirtMask, sampler_DirtMask, input.maskUV).r);

                // 얼룩 패턴 (타일링 적용)
                float2 dirtUV = TRANSFORM_TEX(input.rawUV, _DirtTex);
                half4 dirtPattern = SAMPLE_TEXTURE2D(_DirtTex, sampler_DirtTex, dirtUV);

                // 더러움 컬러 계산
                half3 dirtFinalColor = lerp(baseColor.rgb, _DirtColor.rgb * dirtPattern.rgb, _DirtIntensity);

                // 최종 컬러
                half4 finalColor;
                finalColor.rgb = lerp(baseColor.rgb, dirtFinalColor, dirtAmount);
                finalColor.a = 1.0;

                // 노멀맵
                half4 normalMap = SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv);
                half3 normalTS = UnpackNormalScale(normalMap, _BumpScale);

                float3x3 tangentToWorld = float3x3(input.tangentWS, input.bitangentWS, input.normalWS);
                half3 normalWS = SafeNormalize(mul(normalTS, tangentToWorld));

                // 라이팅 계산
                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalWS = normalWS;
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                lightingInput.bakedGI = SampleSH(normalWS);
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                // Surface Data
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = finalColor.rgb;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness * (1.0 - dirtAmount * 0.3);
                surfaceData.normalTS = normalTS;
                surfaceData.occlusion = 1.0;
                surfaceData.alpha = 1.0;

                // PBR 라이팅
                half4 color = UniversalFragmentPBR(lightingInput, surfaceData);

                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            float3 _LightDirection;

            Varyings ShadowPassVertex(Attributes input)
            {
                Varyings output;

                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));

                return output;
            }

            half4 ShadowPassFragment(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
