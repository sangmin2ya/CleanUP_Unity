// 더러움 마스크 페인팅 셰이더
// 메쉬를 마스크 UV 공간에 펼쳐 그리고, 월드 공간 브러시 구(sphere) 안에 들어온 표면만 닦습니다.
// UV 섬 경계(seam)나 섬마다 다른 텍셀 밀도와 무관하게 브러시가 월드 크기로 일정하게 작동합니다.
Shader "Hidden/CleanUp/DirtPaint"
{
    Properties
    {
        _MaskUVChannel ("Mask UV Channel (0=UV0, 1=UV1)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float _MaskUVChannel;
        CBUFFER_END

        // 스탬프마다 CommandBuffer에서 갱신하는 전역 값
        float4 _CleanBrushPosWS;     // xyz: 브러시 중심, w: 반경
        float4 _CleanBrushNormalWS;  // xyz: 브러시 노멀, w: 강도
        float4 _CleanCameraPosWS;
        float4 _CleanMaskSize;       // xy: 마스크 해상도
        float4 _CleanBrushParams;    // x: 패턴 타일링, y: 회전(라디안), z: 패턴 영향도

        TEXTURE2D(_CleanBrushTex);
        SAMPLER(sampler_CleanBrushTex);

        // R: 거리 감쇠, G: 각도 감쇠
        TEXTURE2D(_CleanFalloffLut);
        SAMPLER(sampler_CleanFalloffLut);

        TEXTURE2D(_CleanSourceTex);
        TEXTURE2D(_CleanCoverageTex);

        struct UnwrapAttributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv0 : TEXCOORD0;
            float2 uv1 : TEXCOORD1;
        };

        struct UnwrapVaryings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 normalWS : TEXCOORD1;
        };

        UnwrapVaryings UnwrapVert(UnwrapAttributes input)
        {
            UnwrapVaryings output;

            float2 maskUV = lerp(input.uv0, input.uv1, _MaskUVChannel);
            float4 positionCS = float4(maskUV * 2.0 - 1.0, 0.5, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
            positionCS.y = -positionCS.y;
            #endif

            output.positionCS = positionCS;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            return output;
        }
        ENDHLSL

        // 0: 브러시 페인팅 (마스크 값 감산)
        Pass
        {
            Name "Paint"
            Blend One One
            BlendOp RevSub
            ColorMask R

            HLSLPROGRAM
            #pragma vertex UnwrapVert
            #pragma fragment PaintFrag

            half4 PaintFrag(UnwrapVaryings input) : SV_Target
            {
                float radius = _CleanBrushPosWS.w;
                float3 offset = input.positionWS - _CleanBrushPosWS.xyz;
                float t = length(offset) / radius;
                clip(1.0 - t);

                float3 normalWS = normalize(input.normalWS);
                float3 brushNormal = _CleanBrushNormalWS.xyz;

                // 카메라 반대편 면, 브러시 반대 방향 면(얇은 판의 뒷면 등)은 닦지 않음
                float3 viewDirWS = normalize(_CleanCameraPosWS.xyz - input.positionWS);
                float facing = dot(normalWS, viewDirWS);
                clip(facing);
                clip(dot(normalWS, brushNormal));

                // 0: 표면과 평행, 1: 정면
                float angle01 = 1.0 - acos(saturate(facing)) * (2.0 / PI);

                float distanceFactor = SAMPLE_TEXTURE2D(_CleanFalloffLut, sampler_CleanFalloffLut, float2(1.0 - t, 0.5)).r;
                float angleFactor = SAMPLE_TEXTURE2D(_CleanFalloffLut, sampler_CleanFalloffLut, float2(angle01, 0.5)).g;

                // 브러시 노멀 기준 접평면에 투영해서 브러시 모양 샘플
                float3 helper = abs(brushNormal.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
                float3 tangent = normalize(cross(helper, brushNormal));
                float3 bitangent = cross(brushNormal, tangent);
                float2 local = float2(dot(offset, tangent), dot(offset, bitangent)) / radius;
                float sinR;
                float cosR;
                sincos(_CleanBrushParams.y, sinR, cosR);
                local = float2(cosR * local.x - sinR * local.y, sinR * local.x + cosR * local.y);
                float2 brushUV = frac((local * 0.5 + 0.5) * max(_CleanBrushParams.x, 1.0));
                float patternAlpha = SAMPLE_TEXTURE2D(_CleanBrushTex, sampler_CleanBrushTex, brushUV).a;
                float brushAlpha = lerp(1.0, patternAlpha, _CleanBrushParams.z);

                float amount = _CleanBrushNormalWS.w * distanceFactor * angleFactor * brushAlpha;
                return half4(amount, 0, 0, 0);
            }
            ENDHLSL
        }

        // 1: 커버리지 (메쉬가 차지하는 텍셀 = 1)
        Pass
        {
            Name "Coverage"

            HLSLPROGRAM
            #pragma vertex UnwrapVert
            #pragma fragment CoverageFrag

            half4 CoverageFrag(UnwrapVaryings input) : SV_Target
            {
                return half4(1, 1, 1, 1);
            }
            ENDHLSL
        }

        // 2: 딜레이트 (UV 섬 바깥 텍셀을 인접 값으로 채워 seam 번짐 방지)
        Pass
        {
            Name "Dilate"

            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment DilateFrag

            static const int2 NeighborOffsets[8] =
            {
                int2(-1, 0), int2(1, 0), int2(0, -1), int2(0, 1),
                int2(-1, -1), int2(1, -1), int2(-1, 1), int2(1, 1)
            };

            struct FullscreenAttributes
            {
                uint vertexID : SV_VertexID;
            };

            struct FullscreenVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            FullscreenVaryings FullscreenVert(FullscreenAttributes input)
            {
                FullscreenVaryings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            half4 DilateFrag(FullscreenVaryings input) : SV_Target
            {
                int2 coord = int2(input.positionCS.xy);
                if (LOAD_TEXTURE2D(_CleanCoverageTex, coord).r > 0.5)
                {
                    return LOAD_TEXTURE2D(_CleanSourceTex, coord);
                }

                int2 maxCoord = int2(_CleanMaskSize.xy) - 1;

                [unroll]
                for (int ring = 1; ring <= 3; ring++)
                {
                    [unroll]
                    for (int i = 0; i < 8; i++)
                    {
                        int2 neighbor = clamp(coord + NeighborOffsets[i] * ring, int2(0, 0), maxCoord);
                        if (LOAD_TEXTURE2D(_CleanCoverageTex, neighbor).r > 0.5)
                        {
                            return LOAD_TEXTURE2D(_CleanSourceTex, neighbor);
                        }
                    }
                }

                return LOAD_TEXTURE2D(_CleanSourceTex, coord);
            }
            ENDHLSL
        }
    }
}
