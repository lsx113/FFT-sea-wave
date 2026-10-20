// URP 海洋着色器
//
// 数据流：
//   WavesGenerator（CPU/GPU 计算）→ 3 张位移纹理 + 3 张斜率纹理 + 3 张泡沫纹理
//   → OceanSurface 把纹理塞进这个材质 → 本着色器在顶点阶段把平面「吹」成波浪，
//     在片元阶段用斜率算法线、用雅可比行列式算泡沫。
//
// 与原版（内置管线 Surface Shader）的区别：
//   - 内置管线没有 surface shader 了，这里手写顶点/片元，并自己调用 URP 光照函数
//   - 深度采样改用 URP 的 SampleSceneDepth（需要 RP Asset 里勾选 Depth Texture）
//
// 坐标约定：位移纹理按「世界坐标 XZ / 级联尺寸」采样，所以网格可以放在任意位置，
//           但网格本身必须是不带旋转/缩放的轴对齐平面。

Shader "Ocean/Ocean"
{
    Properties
    {
        [Header(Surface)]
        _Color ("Water Color", Color) = (0.035, 0.123, 0.198, 1)
        _ReflectionColor ("Reflection Color", Color) = (0.55, 0.7, 0.85, 1)

        [Header(Subsurface Scattering)]
        _SSSColor ("SSS Color", Color) = (0.154, 0.886, 0.99, 1)
        _SSSStrength ("SSS Strength", Range(0,1)) = 0.133
        _SSSScale ("SSS Scale", Range(0.1,50)) = 4.8
        _SSSBase ("SSS Base", Range(-5,1)) = -0.1

        [Header(Detail)]
        _LOD_scale ("LOD Scale", Range(0,10)) = 7.13
        _MaxGloss ("Max Gloss", Range(0,1)) = 0.91
        _Roughness ("Distant Roughness", Range(0,1)) = 0.311
        _RoughnessScale ("Roughness Scale", Range(0, 0.01)) = 0.0044

        [Header(Foam)]
        _FoamColor ("Foam Color", Color) = (1,1,1,1)
        _FoamTexture ("Foam Texture", 2D) = "grey" {}
        _FoamBiasLOD0 ("Foam Bias LOD0", Range(0,7)) = 0.84
        _FoamBiasLOD1 ("Foam Bias LOD1", Range(0,7)) = 1.83
        _FoamBiasLOD2 ("Foam Bias LOD2", Range(0,7)) = 2.72
        _FoamScale ("Foam Scale", Range(0,20)) = 2.4
        // 岸边接触泡沫：需要场景里有水下地形才有意义，纯海面场景保持 0
        _ContactFoam ("Contact Foam", Range(0,1)) = 0

        // 运行时由上到下绑定，不在 Inspector 里显示
        [HideInInspector] _Displacement_c0 ("Displacement C0", 2D) = "black" {}
        [HideInInspector] _Derivatives_c0 ("Derivatives C0", 2D) = "black" {}
        [HideInInspector] _Turbulence_c0 ("Turbulence C0", 2D) = "white" {}
        [HideInInspector] _Displacement_c1 ("Displacement C1", 2D) = "black" {}
        [HideInInspector] _Derivatives_c1 ("Derivatives C1", 2D) = "black" {}
        [HideInInspector] _Turbulence_c1 ("Turbulence C1", 2D) = "white" {}
        [HideInInspector] _Displacement_c2 ("Displacement C2", 2D) = "black" {}
        [HideInInspector] _Derivatives_c2 ("Derivatives C2", 2D) = "black" {}
        [HideInInspector] _Turbulence_c2 ("Turbulence C2", 2D) = "white" {}
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        // ============================================================
        // 主渲染 Pass
        // ============================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            // URP 光照/阴影关键字
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // 材质属性统一放进 CBUFFER，SRP Batcher 才能合批
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _ReflectionColor;
                float4 _SSSColor;
                float4 _FoamColor;
                float _SSSStrength;
                float _SSSScale;
                float _SSSBase;
                float _LOD_scale;
                float _MaxGloss;
                float _Roughness;
                float _RoughnessScale;
                float _FoamBiasLOD0;
                float _FoamBiasLOD1;
                float _FoamBiasLOD2;
                float _FoamScale;
                float _ContactFoam;
                float4 _FoamTexture_ST;
            CBUFFER_END

            TEXTURE2D(_Displacement_c0); SAMPLER(sampler_Displacement_c0);
            TEXTURE2D(_Derivatives_c0);  SAMPLER(sampler_Derivatives_c0);
            TEXTURE2D(_Turbulence_c0);   SAMPLER(sampler_Turbulence_c0);
            TEXTURE2D(_Displacement_c1); SAMPLER(sampler_Displacement_c1);
            TEXTURE2D(_Derivatives_c1);  SAMPLER(sampler_Derivatives_c1);
            TEXTURE2D(_Turbulence_c1);   SAMPLER(sampler_Turbulence_c1);
            TEXTURE2D(_Displacement_c2); SAMPLER(sampler_Displacement_c2);
            TEXTURE2D(_Derivatives_c2);  SAMPLER(sampler_Derivatives_c2);
            TEXTURE2D(_Turbulence_c2);   SAMPLER(sampler_Turbulence_c2);
            TEXTURE2D(_FoamTexture);     SAMPLER(sampler_FoamTexture);

            // 由 WavesGenerator 每帧设置（Shader.SetGlobalFloat）
            float LengthScale0;
            float LengthScale1;
            float LengthScale2;

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 worldUV : TEXCOORD0;
                float4 lodScales : TEXCOORD1;   // x/y/z = 三级 LOD 权重，w = SSS 高度
                float3 viewVector : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                half fogFactor : TEXCOORD4;
                float3 positionWS : TEXCOORD5;  // 位移之后的世界坐标（算岸边泡沫的深度用）
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;

                // ---------- 1. 顶点位移 ----------
                // 世界坐标决定采样位置，所以移动网格不会让波浪跟着「游动」
                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float2 worldUV = worldPos.xz;

                float3 viewVector = _WorldSpaceCameraPos.xyz - worldPos;
                float viewDist = max(length(viewVector), 1e-4);

                // 离相机越远，小细节越不需要（也避免远处闪烁）
                float lod0 = min(_LOD_scale * LengthScale0 / viewDist, 1.0);
                float lod1 = min(_LOD_scale * LengthScale1 / viewDist, 1.0);
                float lod2 = min(_LOD_scale * LengthScale2 / viewDist, 1.0);

                float3 displacement = 0;
                float3 disp0 = SAMPLE_TEXTURE2D_LOD(_Displacement_c0, sampler_Displacement_c0, worldUV / LengthScale0, 0).xyz;
                displacement += disp0 * lod0;
                float largeWavesBias = disp0.y;

                // 中/小尺度级联：远处直接淡出
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c1, sampler_Displacement_c1, worldUV / LengthScale1, 0).xyz * lod1;
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c2, sampler_Displacement_c2, worldUV / LengthScale2, 0).xyz * lod2;

                // 位移是米制世界单位，换算回物体空间再加到顶点上
                input.positionOS.xyz += TransformWorldToObjectDir(displacement);

                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positions.positionCS;
                output.shadowCoord = GetShadowCoord(positions);
                output.positionWS = positions.positionWS;

                output.worldUV = worldUV;
                output.viewVector = viewVector;
                output.fogFactor = ComputeFogFactor(positions.positionCS.z);

                // SSS：只有波峰（比大浪平均高度高出一截）才透光
                float sss = saturate(max(displacement.y - largeWavesBias * 0.8 - _SSSBase, 0) / max(_SSSScale, 1e-4));
                output.lodScales = float4(lod0, lod1, lod2, sss);

                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // ---------- 2. 用斜率重建法线 ----------
                // Derivatives 纹理存的是 ∂y/∂x、∂y/∂z 和二阶导；水平位移会让斜率放大，
                // 所以要除以 (1 + 二阶导) 做修正。
                float4 derivatives = SAMPLE_TEXTURE2D(_Derivatives_c0, sampler_Derivatives_c0, input.worldUV / LengthScale0);
                derivatives += SAMPLE_TEXTURE2D(_Derivatives_c1, sampler_Derivatives_c1, input.worldUV / LengthScale1) * input.lodScales.y;
                derivatives += SAMPLE_TEXTURE2D(_Derivatives_c2, sampler_Derivatives_c2, input.worldUV / LengthScale2) * input.lodScales.z;

                float2 slope = float2(derivatives.x / (1 + derivatives.z),
                                      derivatives.y / (1 + derivatives.w));
                float3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

                // ---------- 3. 泡沫 ----------
                // 雅可比行列式 J < 1 表示水面被水平挤压（波峰处），越小越容易起白沫
                float turbulence = SAMPLE_TEXTURE2D(_Turbulence_c0, sampler_Turbulence_c0, input.worldUV / LengthScale0).r;
                turbulence += SAMPLE_TEXTURE2D(_Turbulence_c1, sampler_Turbulence_c1, input.worldUV / LengthScale1).r * input.lodScales.y;
                turbulence += SAMPLE_TEXTURE2D(_Turbulence_c2, sampler_Turbulence_c2, input.worldUV / LengthScale2).r * input.lodScales.z;

                float foam = saturate((-turbulence + _FoamBiasLOD2) * _FoamScale);

                // 岸边泡沫（需要场景深度里存在水下地形；纯海面时 _ContactFoam = 0）
                if (_ContactFoam > 0)
                {
                    float2 screenUV = input.positionCS.xy / _ScreenParams.xy;
                    float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                    float surfaceEyeDepth = -TransformWorldToView(input.positionWS).z;
                    float depthDifference = max(0, sceneEyeDepth - surfaceEyeDepth - 0.1);

                    float foamTex = SAMPLE_TEXTURE2D(_FoamTexture, sampler_FoamTexture, input.worldUV * 0.5 + _Time.r).r;
                    foam += _ContactFoam * saturate(max(0, foamTex - depthDifference) * 5) * 0.9;
                    foam = saturate(foam);
                }

                // ---------- 4. 光照 ----------
                float3 viewDir = normalize(input.viewVector);
                Light mainLight = GetMainLight(input.shadowCoord);
                float NdotL = saturate(dot(normalWS, mainLight.direction));

                float distanceGloss = lerp(1 - _Roughness, _MaxGloss, 1 / (1 + length(input.viewVector) * _RoughnessScale));
                float smoothness = lerp(distanceGloss, 0, foam);

                // Blinn-Phong 高光（URP 里也可以用 GGX，这里保持和原版一致的观感）
                float3 halfDir = normalize(mainLight.direction + viewDir);
                float specPower = exp2(10.0 * smoothness + 1.0);
                float specular = pow(saturate(dot(normalWS, halfDir)), specPower) * smoothness * NdotL;

                float3 ambient = SampleSH(normalWS);
                float3 albedo = lerp(_Color.rgb, _FoamColor.rgb, foam);

                float3 color = albedo * (mainLight.color * NdotL + ambient) + mainLight.color * specular;

                // 菲涅尔：掠射角时水面更像镜面，往「反射色」混合
                float fresnel = pow(saturate(1.0 - saturate(dot(normalWS, viewDir))), 5.0);
                color = lerp(color, _ReflectionColor.rgb * lerp(0.25, 1.0, NdotL), fresnel * (1 - foam) * 0.85);

                // ---------- 5. 次表面散射（SSS）----------
                // 逆光看波峰时，光会从水里透出来，让水看起来通透
                float3 sssHalf = normalize(-normalWS + mainLight.direction);
                float sss = pow(saturate(dot(viewDir, -sssHalf)), 5.0) * 30.0 * _SSSStrength;
                color += _SSSColor.rgb * sss * input.lodScales.w * (1 - foam);

                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ============================================================
        // 深度 Pass：URP 生成 _CameraDepthTexture 时用
        // 位移必须和主 Pass 一致，否则水下物体的深度判断会错
        // ============================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 必须和 ForwardLit 里的 CBUFFER 完全一致，SRP Batcher 才认为这是同一个着色器
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float4 _ReflectionColor;
                float4 _SSSColor;
                float4 _FoamColor;
                float _SSSStrength;
                float _SSSScale;
                float _SSSBase;
                float _LOD_scale;
                float _MaxGloss;
                float _Roughness;
                float _RoughnessScale;
                float _FoamBiasLOD0;
                float _FoamBiasLOD1;
                float _FoamBiasLOD2;
                float _FoamScale;
                float _ContactFoam;
                float4 _FoamTexture_ST;
            CBUFFER_END

            // 全局量，由 WavesGenerator 设置，不参与材质 CBUFFER
            float LengthScale0;
            float LengthScale1;
            float LengthScale2;

            TEXTURE2D(_Displacement_c0); SAMPLER(sampler_Displacement_c0);
            TEXTURE2D(_Displacement_c1); SAMPLER(sampler_Displacement_c1);
            TEXTURE2D(_Displacement_c2); SAMPLER(sampler_Displacement_c2);

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;

                float3 worldPos = TransformObjectToWorld(input.positionOS.xyz);
                float viewDist = max(length(_WorldSpaceCameraPos.xyz - worldPos), 1e-4);

                float lod0 = min(_LOD_scale * LengthScale0 / viewDist, 1.0);
                float lod1 = min(_LOD_scale * LengthScale1 / viewDist, 1.0);
                float lod2 = min(_LOD_scale * LengthScale2 / viewDist, 1.0);

                float3 displacement = 0;
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c0, sampler_Displacement_c0, worldPos.xz / LengthScale0, 0).xyz * lod0;
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c1, sampler_Displacement_c1, worldPos.xz / LengthScale1, 0).xyz * lod1;
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c2, sampler_Displacement_c2, worldPos.xz / LengthScale2, 0).xyz * lod2;

                input.positionOS.xyz += TransformWorldToObjectDir(displacement);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
