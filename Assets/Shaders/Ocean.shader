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
        // 高频法线扰动：FFT 网格的波长下限是级联尺寸，比这更细的碎波只能靠它补
        _DetailNormalStrength ("Detail Normal Strength", Range(0, 0.5)) = 0.12
        _DetailScale ("Detail Scale", Range(0.5, 12)) = 3.2

        [Header(Foam)]
        _FoamColor ("Foam Color", Color) = (1,1,1,1)
        _FoamTexture ("Foam Texture", 2D) = "grey" {}
        _FoamBiasLOD0 ("Foam Bias LOD0", Range(0,7)) = 0.84
        _FoamBiasLOD1 ("Foam Bias LOD1", Range(0,7)) = 1.83
        _FoamBiasLOD2 ("Foam Bias LOD2", Range(0,7)) = 2.72
        _FoamScale ("Foam Scale", Range(0,20)) = 2.4
        // 泡沫细节：用程序化噪声扰动阈值，边缘才会碎成絮状而不是一坨白漆
        _FoamNoiseScale ("Foam Noise Scale", Range(0.2, 8)) = 2.2
        _FoamNoiseStrength ("Foam Noise Strength", Range(0, 0.4)) = 0.09
        _FoamStretch ("Foam Streak Stretch", Range(1, 6)) = 2.6
        _FoamDirection ("Foam Streak Direction", Range(0, 360)) = 150
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

            // 三套材质 LOD 变体：CLOSE = 三级联全用，MID = 用两级，都不定义 = 只用 c0。
            // 远处的瓦片不需要小尺度细节；更重要的是泡沫 bias 要和「实际叠加了几级」匹配，
            // 否则远处只加了一级（≈1）却按三级（2.72）去减，整片海会变成白色。
            #pragma multi_compile _ MID CLOSE

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
                float _DetailNormalStrength;
                float _DetailScale;
                float _FoamBiasLOD0;
                float _FoamBiasLOD1;
                float _FoamBiasLOD2;
                float _FoamScale;
                float _FoamNoiseScale;
                float _FoamNoiseStrength;
                float _FoamStretch;
                float _FoamDirection;
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

            // ---------- 泡沫用的程序化噪声 ----------
            // 值噪声足够：这里只要「碎」的观感，不需要梯度噪声的圆润。
            float FoamHash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float FoamNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);

                float a = FoamHash(i);
                float b = FoamHash(i + float2(1, 0));
                float c = FoamHash(i + float2(0, 1));
                float d = FoamHash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 两层叠加：低层是大团块的疏密，高层给边缘的碎絮
            float FoamFbm(float2 p)
            {
                return FoamNoise(p) * 0.65 + FoamNoise(p * 2.7 + 13.7) * 0.35;
            }

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

                // 中/小尺度级联：远处瓦片直接不采样（见上面的 multi_compile 说明）
#if defined(MID) || defined(CLOSE)
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c1, sampler_Displacement_c1, worldUV / LengthScale1, 0).xyz * lod1;
#endif
#ifdef CLOSE
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c2, sampler_Displacement_c2, worldUV / LengthScale2, 0).xyz * lod2;
#endif

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
#if defined(MID) || defined(CLOSE)
                derivatives += SAMPLE_TEXTURE2D(_Derivatives_c1, sampler_Derivatives_c1, input.worldUV / LengthScale1) * input.lodScales.y;
#endif
#ifdef CLOSE
                derivatives += SAMPLE_TEXTURE2D(_Derivatives_c2, sampler_Derivatives_c2, input.worldUV / LengthScale2) * input.lodScales.z;
#endif

                float2 slope = float2(derivatives.x / (1 + derivatives.z),
                                      derivatives.y / (1 + derivatives.w));
                float3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

                // ---------- 2.5 细节法线 ----------
                // FFT 只能表示到最小级联的波长，比之更细的碎波这里用噪声坡度补上，
                // 否则水面会像一层光滑的塑料膜。
                //
                // 两个必须注意的点，否则会看到「方块」和「条纹光闪烁」：
                //   1) 差分步长要远小于噪声格子。步子一大，格子边界上的坡度就跳变，
                //      近处水面会浮出一片片方块；
                //   2) 一个像素覆盖的噪声格子超过半个时就必须淡出。光靠距离判断是不够的：
                //      掠射角下水面在屏幕上被极度压缩，10 米外一个像素就横跨好几个格子，
                //      噪声变成摩尔纹，相机一动就是一片条纹光在闪。
                //      这里改用 ddx/ddy 直接量出「一个像素盖了多大的水面」。
                float2 uvDx = ddx(input.worldUV);
                float2 uvDy = ddy(input.worldUV);
                float worldFootprint = max(length(uvDx), length(uvDy));   // 米 / 像素

                float detailFootprint = worldFootprint * _DetailScale;
                float detailFade = saturate(1.0 - detailFootprint * 2.0)
                                 * saturate(1.0 - length(input.viewVector) / 120.0);
                if (detailFade > 0.001)
                {
                    // 步长要远小于噪声格子（1/_DetailScale），否则有限差分会在格子边界跳变，
                    // 近处水面上就会浮出一片片方块。
                    const float step = 0.10;
                    float2 duv = input.worldUV * _DetailScale;
                    float n0 = FoamFbm(duv);
                    float dx = FoamFbm(duv + float2(step * _DetailScale, 0)) - n0;
                    float dz = FoamFbm(duv + float2(0, step * _DetailScale)) - n0;
                    float2 detailSlope = float2(dx, dz) * _DetailNormalStrength * detailFade * 4.0;
                    normalWS = normalize(normalWS + float3(-detailSlope.x, 0.0, -detailSlope.y));
                }

                // ---------- 3. 泡沫 ----------
                // 雅可比行列式 J < 1 表示水面被水平挤压（波峰处），越小越容易起白沫
                float turbulence = SAMPLE_TEXTURE2D(_Turbulence_c0, sampler_Turbulence_c0, input.worldUV / LengthScale0).r;
#if defined(MID) || defined(CLOSE)
                turbulence += SAMPLE_TEXTURE2D(_Turbulence_c1, sampler_Turbulence_c1, input.worldUV / LengthScale1).r;
#endif
#ifdef CLOSE
                turbulence += SAMPLE_TEXTURE2D(_Turbulence_c2, sampler_Turbulence_c2, input.worldUV / LengthScale2).r;
#endif

                // 噪声沿风向拉长，泡沫就会顺风连成条，而不是一坨坨圆斑。
                float foamAngle = radians(_FoamDirection);
                float2 foamAxis = float2(cos(foamAngle), sin(foamAngle));
                float2 foamPerp = float2(-foamAxis.y, foamAxis.x);
                float2 foamUV = float2(dot(input.worldUV, foamAxis) / _FoamStretch,
                                       dot(input.worldUV, foamPerp));

                // 粗噪声只负责「轮廓」：扰动阈值，让泡沫边界不圆
                // 细噪声负责「质地」：把泡沫内部打散成絮状，否则就是一整块白漆
                //
                // 两种噪声都按屏幕足迹淡出（和细节法线同一套判据）：一像素盖住
                // 半个格子以后它就只剩噪声了，留着只会让泡沫边缘抖成一条条亮线。
                float coarseFade = saturate(1.0 - worldFootprint * _FoamNoiseScale * 2.0);
                float fineFade = saturate(1.0 - worldFootprint * 11.0 * 2.0);
                float foamNoise = (FoamFbm(foamUV * _FoamNoiseScale) - 0.5)
                                  * _FoamNoiseStrength * 2.0 * coarseFade;

                // 每个变体用自己那级的 bias：叠加了几级就用几级的基准值去减
#ifdef CLOSE
                float foam = saturate((-turbulence + _FoamBiasLOD2 + foamNoise) * _FoamScale);
#elif defined(MID)
                float foam = saturate((-turbulence + _FoamBiasLOD1 + foamNoise) * _FoamScale);
#else
                float foam = saturate((-turbulence + _FoamBiasLOD0 + foamNoise) * _FoamScale);
#endif

                // 细噪声只在近处起作用：远处它已经比像素还小，留着只会闪
                float fine = FoamFbm(foamUV * 11.0 + 7.3);
                float breakup = lerp(1.0, 0.45 + 1.10 * smoothstep(0.28, 0.72, fine), fineFade);
                // 远处的 fine 已经比一个像素还小，只能退化成它的平均值，不能继续拿来抖明暗
                float fineShade = lerp(0.7, fine, fineFade);
                foam = saturate(foam * breakup);

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
                // 泡沫内部也要有明暗：全白会像贴上去的纸片
                float3 foamAlbedo = _FoamColor.rgb * lerp(0.72, 1.0, fineShade);
                float3 albedo = lerp(_Color.rgb, foamAlbedo, foam);

                float3 color = albedo * (mainLight.color * NdotL + ambient) + mainLight.color * specular;

                // 菲涅尔：掠射角时水面更像镜面，往「反射色」混合。
                // 混合系数别给满：给满的话整片海都会被天空的灰色盖住，
                // 水体本身的蓝色一点也透不出来，看上去就像一片水泥地。
                float fresnel = pow(saturate(1.0 - saturate(dot(normalWS, viewDir))), 5.0);
                color = lerp(color, _ReflectionColor.rgb * lerp(0.35, 1.0, NdotL), fresnel * (1 - foam) * 0.65);

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

            // 和主 Pass 保持一致，否则不同 LOD 瓦片的深度会和颜色对不上
            #pragma multi_compile _ MID CLOSE

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
                float _DetailNormalStrength;
                float _DetailScale;
                float _FoamBiasLOD0;
                float _FoamBiasLOD1;
                float _FoamBiasLOD2;
                float _FoamScale;
                float _FoamNoiseScale;
                float _FoamNoiseStrength;
                float _FoamStretch;
                float _FoamDirection;
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
#if defined(MID) || defined(CLOSE)
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c1, sampler_Displacement_c1, worldPos.xz / LengthScale1, 0).xyz * lod1;
#endif
#ifdef CLOSE
                displacement += SAMPLE_TEXTURE2D_LOD(_Displacement_c2, sampler_Displacement_c2, worldPos.xz / LengthScale2, 0).xyz * lod2;
#endif

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
