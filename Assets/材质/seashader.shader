// =====================================================================
// FFT 海面着色器
// ---------------------------------------------------------------------
// 数据来源（全部由 OceanFFTRenderPass 通过 Shader.SetGlobal* 发布）：
//   _OceanSpectrum       8 层 Tex2DArray，做完 IFFT 后的空域数据
//   _OceanCascadeLength  float4，4 个级联各自的物理边长 L0..L3（米）
//   _OceanResolution     每个级联贴图的边长 N（用来算纹素尺寸）
//
// 每层的通道排布（CS_Spectrumtime 里的打包顺序，别改这里）：
//   第 i*2   层：R = 水平位移 X，G = 水平位移 Z，B = 垂直位移 Y，A = dZ/dx
//   第 i*2+1 层：R = dY/dx，     G = dY/dz，     B = dX/dx，     A = dZ/dz
//   注意第 i*2 层的顺序是 X、Z、Y —— 和 float3 的 XYZ 不一致，容易看错。
//
// 用法：
//   1. 材质挂到海面网格上（网格必须是水平、未旋转的平面，本 shader 按世界 XZ 采样）
//   2. 场景里要有 OceanFFTController，且 OceanFFTRenderFeature 已挂到 PC_Renderer.asset
//   3. 网格本身越密，_TessMax 就要越小，否则三角形数量爆炸
//      （201×201 的网格建议 _TessMax ≤ 2；想让细分真正起作用，先把网格放粗到 25×25 左右）
//
// 光照：**不再走 UniversalFragmentPBR**。
//   水不是漫反射表面。用 PBR 的 albedo/GGX 去画，只会得到「深蓝色塑料」：
//   漫反射项把水色压成一片死板的蓝，GGX 粗糙度 0.1 又把高光收成一颗小白点，
//   而真实海面的颜色七成来自**天空反射**，两成来自**水体散射**，只有一点是太阳高光。
//   所以这里照抄离线/教程里那套老配方：
//       color = (1 - F) * body(散射) + F * sky(环境反射) + specular(太阳)
//   其中 F 是 Schlick 菲涅尔 —— 掠射角趋近 1，远处整片海就是天空的颜色；
//   垂直看下去趋近 0.02，露出水体的本色。海面「像不像水」主要就靠这一项。
//
// 抗锯齿（两条，缺一不可）：
//   1. 距离淡出：级联越细，纹素越小，超过一定距离后一个像素里压着好几个纹素，
//      采样结果就是逐帧乱跳的噪声 —— 所以按「像素足迹 / 纹素尺寸」把细级联淡掉。
//   2. 粗糙度补偿：被淡掉的那部分斜率不是噪声，是这个像素「本来应该看到的起伏」。
//      把它折算进粗糙度（思路同 Toksvig 法线过滤），远处高光就变成柔和的反射，
//      而不是粗糙度 0.1 下逐像素乱闪的火花。
//
// 几何衰减：
//   位移和法线都按距离淡出到 0（_DisplaceFadeDistance）。远处的海面退回一块平面，
//   靠天空反射 + 空气透视撑住，而不是让一列浪尖在 250 米外堆成连绵山脉。
// =====================================================================
Shader "Custom/seashader"
{
    Properties
    {
        [Header(Colors)]
        [MainColor] _OceanColor ("Ocean Color 水体颜色", Color) = (0.03, 0.20, 0.32, 1)
        _ScatterColor ("Scatter Color 浪尖透光色", Color) = (0.28, 0.62, 0.60, 1)
        _FoamColor ("Foam Color 泡沫颜色", Color) = (0.92, 0.95, 1.0, 1)

        [Header(Surface)]
        _Roughness ("Roughness 粗糙度", Range(0, 1)) = 0.08
        _NormalStrength ("Normal Strength 法线强度", Range(0, 4)) = 1.0
        _RoughnessBoost ("Distance Roughness 距离粗糙度补偿", Range(0, 4)) = 1.0
        _SunBodyStrength ("Sun Body 太阳照到水体的比例", Range(0, 2)) = 0.35
        _ScatterStrength ("Scatter 浪尖透光强度", Range(0, 8)) = 2.0
        _CrestHeight ("Crest Height 浪尖高度(米)", Range(0.1, 20)) = 3.0

        [Header(Reflection)]
        _EnvStrength ("Sky Reflection 天空反射强度", Range(0, 4)) = 1.0
        _FresnelStrength ("Fresnel 菲涅尔强度", Range(0, 4)) = 1.0

        [Header(Distance Fade)]
        _DisplaceFadeDistance ("Displace Fade 位移衰减距离(米)", Range(5, 2000)) = 400
        _DisplaceFadeFalloff ("Displace Falloff 位移衰减曲线", Range(0.1, 8)) = 1.2

        [Header(Foam)]
        _FoamEdgePower ("Foam Edge 泡沫边缘收紧", Range(0.1, 6)) = 1.0
        _FoamDepthAttenuation ("Foam Depth 泡沫距离衰减指数", Range(0, 40)) = 4
        _FoamNoiseScale ("Foam Noise Scale 泡沫絮状纹理密度", Range(0.02, 4)) = 0.35
        _FoamNoiseContrast ("Foam Noise Contrast 泡沫絮状对比", Range(0, 4)) = 1.0

        [Header(Waves)]
        _DisplacementScale ("Displacement Scale 位移强度", Range(0, 3)) = 1.0
        _LayerContribute ("Layer Contribute 各级联权重 xyzw=L0 L1 L2 L3", Vector) = (1, 1, 1, 1)

        [Header(Tessellation)]
        _TessMax ("Tess Near 近处细分", Range(1, 32)) = 8
        _TessMin ("Tess Far 远处细分", Range(1, 16)) = 1
        _TessDistance ("Tess Distance 衰减距离(米)", Range(1, 2000)) = 200
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            // 细分必须开 4.6（hull / domain 阶段从这开始支持）
            #pragma target 4.6

            #pragma vertex vert
            #pragma hull HullProgram
            #pragma domain DomainProgram
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/BRDF.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GlobalIllumination.hlsl"

            // ---------------------------------------------------------
            // 材质参数。必须全塞进 UnityPerMaterial，否则 SRP Batcher 会把这个 shader 判为不兼容
            // ---------------------------------------------------------
            CBUFFER_START(UnityPerMaterial)
                float4 _OceanColor;
                float4 _ScatterColor;
                float4 _FoamColor;
                float  _Roughness;
                float  _NormalStrength;
                float  _RoughnessBoost;
                float  _SunBodyStrength;
                float  _ScatterStrength;
                float  _CrestHeight;
                float  _EnvStrength;
                float  _FresnelStrength;
                float  _DisplaceFadeDistance;
                float  _DisplaceFadeFalloff;
                float  _FoamEdgePower;
                float  _FoamDepthAttenuation;
                float  _FoamNoiseScale;
                float  _FoamNoiseContrast;
                float  _DisplacementScale;
                float4 _LayerContribute;
                float  _TessMax;
                float  _TessMin;
                float  _TessDistance;
            CBUFFER_END

            // ---------------------------------------------------------
            // 脚本发布的全局资源。放在 CBUFFER 外面，才不会破坏 SRP Batcher
            // ---------------------------------------------------------
            #define OCEAN_CASCADE_COUNT 4   // 位移 / 法线 / 泡沫都用全部 4 级

            // 1080 行、垂直视场 60° 时，一个像素在世界空间的足迹 ≈ 距离 × 0.0011。
            // 这里取 0.002，给更低分辨率和更窄的视场留出余量，宁可早点淡掉也别让它抖。
            #define DETAIL_FOOTPRINT_PER_METER 0.002

            // 水的垂直入射反射率。0.02 是水的真实值（折射率 1.33）。
            #define WATER_F0 0.02

            TEXTURE2D_ARRAY(_OceanSpectrum);
            SAMPLER(sampler_OceanSpectrum);
            float4 _OceanCascadeLength; // xyz = L0 L1 L2 L3
            float  _OceanResolution;    // 每个级联贴图的边长 N

            // 泡沫：CS_UpdateFoam 每帧更新，4 层 = 4 个级联，
            // 和 _OceanSpectrum 同分辨率、同 uv、同平铺，.r = 该级联的泡沫存量。
            // 时间累积（涨得快、落得慢）已经全部在 CS 里做完，这里只负责采样和距离衰减。
            TEXTURE2D_ARRAY(_OceanFoam);
            SAMPLER(sampler_OceanFoam);

            // ---------------------------------------------------------
            // 泡沫絮状纹理（不占贴图资源，现算）
            // ---------------------------------------------------------
            // 光靠阈值切出来的泡沫边缘是一整块硬白，像雪盖在山头上；真实的泡沫
            // 是一层网状、絮状的膜，边缘被啃得毛毛的。这里用世界坐标的值噪声当遮罩，
            // 把那张「白板」打碎成絮状 —— 噪声本身无序，看不出重复周期。
            float FoamHash21(float2 p)
            {
                p = frac(p * float2(123.34f, 456.21f));
                p += dot(p, p + 45.32f);
                return frac(p.x * p.y);
            }

            float FoamValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0f - 2.0f * f); // 平滑插值，避免格子边界显出来
                float a = FoamHash21(i);
                float b = FoamHash21(i + float2(1.0f, 0.0f));
                float c = FoamHash21(i + float2(0.0f, 1.0f));
                float d = FoamHash21(i + float2(1.0f, 1.0f));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // ---------------------------------------------------------
            // 结构体
            // ---------------------------------------------------------
            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION; // 控制点里也带着它，domain 出来后要重新算
                float3 positionWS : TEXCOORD0;
            };

            struct TessFactors
            {
                float edge[3] : SV_TessFactor;
                float inside  : SV_InsideTessFactor;
            };

            // 一次采样拿回三样结果，免得在像素阶段把 4 个级联遍历两遍
            struct OceanDetail
            {
                float4 derivatives; // xy = (dY/dx, dY/dz)，zw = (dX/dx, dZ/dz)
                float  lostSlope2;  // 被淡出掉的斜率方差，折算成粗糙度
                float  foam;        // 已按距离淡出的泡沫存量，CS_UpdateFoam 每帧算好
            };

            // ---------------------------------------------------------
            // 采样辅助
            // ---------------------------------------------------------

            // float4 不能动态下标（常量缓冲里代价不小），这里用三元展开
            float CascadeLength(int cascade)
            {
                float L = cascade == 0 ? _OceanCascadeLength.x :
                          cascade == 1 ? _OceanCascadeLength.y :
                          cascade == 2 ? _OceanCascadeLength.z : _OceanCascadeLength.w;
                // 防御：pass 还没跑过时全局变量是 0，除零会得到 inf 再变成 NaN
                return max(L, 1e-3);
            }

            // 每个级联的手动权重（Inspector 里的 Layer Contribute）。
            // 想压掉最粗的那一级（它波长上千、最容易把海面顶成山脉）就把 x 调小。
            float LayerWeight(int cascade)
            {
                return cascade == 0 ? _LayerContribute.x :
                       cascade == 1 ? _LayerContribute.y :
                       cascade == 2 ? _LayerContribute.z : _LayerContribute.w;
            }

            // 每个级联是一张 L_i 米见方的周期贴图，所以它的 uv 就是 worldXZ / L_i
            float2 CascadeUV(float2 worldXZ, int cascade)
            {
                return worldXZ / CascadeLength(cascade);
            }

            // 级联随距离淡出：1 = 全用，0 = 完全不用。
            //
            // 判据是「像素足迹 vs 纹素尺寸」：级联 i 一个纹素覆盖 L_i / N 米，
            // 一旦像素在世界空间跨过的距离超过这个值，相邻像素就在读互不相关的纹素，
            // 而这些纹素每帧都在变 —— 屏幕上看就是满片闪烁的噪点。
            //
            // 代入本项目实测参数 L = (1000, 250, 62.5, 15.6)、N = 256：
            //   c0 纹素 3.91 米 → 2000 米外才开始淡（500 米的平面内永远有效）
            //   c1 纹素 0.98 米 → 490 米外开始淡
            //   c2 纹素 0.24 米 → 122~244 米淡出
            //   c3 纹素 0.06 米 → 30~61 米淡出
            // 也就是近处保留全部细节，远处自动退回低频。
            float CascadeFade(int cascade, float3 worldPos)
            {
                float texel = CascadeLength(cascade) / max(_OceanResolution, 1.0);
                float footprint = distance(_WorldSpaceCameraPos, worldPos) * DETAIL_FOOTPRINT_PER_METER;
                // 足迹到一个纹素时开始淡，到两个纹素时淡光
                return 1.0 - saturate((footprint - texel) / texel);
            }

            // 整体几何随距离压平：1 = 全幅位移，0 = 完全平。
            // 远处的浪再大，在屏幕上也只是几像素的抖动；让它保持满幅只会把地平线
            // 堆成一列锯齿状的山。压平之后远处交给天空反射和雾去做。
            float DisplacementFade(float3 worldPos)
            {
                float dist = distance(_WorldSpaceCameraPos, worldPos);
                float t = saturate(1.0 - dist / max(_DisplaceFadeDistance, 1e-3));
                return pow(t, max(_DisplaceFadeFalloff, 1e-3));
            }

            // 世界空间位移 (X, Y, Z)，Y 是高度
            float3 SampleDisplacement(float3 worldPos)
            {
                float3 acc = float3(0.0, 0.0, 0.0);
                [unroll]
                for (int i = 0; i < OCEAN_CASCADE_COUNT; i++)
                {
                    float4 s = SAMPLE_TEXTURE2D_ARRAY_LOD(
                        _OceanSpectrum, sampler_OceanSpectrum, CascadeUV(worldPos.xz, i), i * 2, 0);

                    float w = CascadeFade(i, worldPos) * LayerWeight(i);

                    acc.x += s.r * w; // 水平位移 X
                    acc.y += s.b * w; // 垂直位移 Y（高度）
                    acc.z += s.g * w; // 水平位移 Z
                }
                return acc;
            }

            // 每像素要用的导数，外加两个副产品（泡沫挤压量、丢失的斜率方差）
            OceanDetail SampleDetail(float3 worldPos)
            {
                OceanDetail o;
                o.derivatives = float4(0.0, 0.0, 0.0, 0.0);
                o.lostSlope2  = 0.0;
                o.foam        = 0.0;

                [unroll]
                for (int i = 0; i < OCEAN_CASCADE_COUNT; i++)
                {
                    float4 s = SAMPLE_TEXTURE2D_ARRAY_LOD(
                        _OceanSpectrum, sampler_OceanSpectrum, CascadeUV(worldPos.xz, i), i * 2 + 1, 0);

                    float w = CascadeFade(i, worldPos) * LayerWeight(i);

                    o.derivatives += s * w; // xy = 坡度，zw = 水平挤压

                    // 泡沫和位移共用同一个级联、同一套 uv、同一个淡出权重。
                    // 这一点是整段的关键：级联淡出本身就等于「按像素足迹做带限」，
                    // 远处像素足迹大于纹素时，那级泡沫自动退出，不会再出现
                    // 「一个像素随机跳到几个互不相关的纹素上」的雪花噪点。
                    // 时间累积（涨得快、落得慢）已经在 CS_UpdateFoam 里按纹素做完。
                    float f = SAMPLE_TEXTURE2D_ARRAY_LOD(
                        _OceanFoam, sampler_OceanFoam, CascadeUV(worldPos.xz, i), i, 0).r;
                    // 由粗到细「换档」，不是相加：
                    // 第 i 级的泡沫已经在 CS 里把 0..i 级全部算进去了，
                    // 所以 fade=1 时整份就用这一级，fade=0 时退回上一级。
                    // 相加会重复计算（4 级各算一遍同一片水面），结果就是全白。
                    o.foam = (i == 0) ? f : lerp(o.foam, f, CascadeFade(i, worldPos));

                    // 被淡掉的那部分斜率不是噪声，是「这个像素本应看到、但采样不出来」的起伏。
                    // 攒起来，后面折算成粗糙度。
                    o.lostSlope2 += (1.0 - w) * (s.r * s.r + s.g * s.g);
                }
                return o;
            }

            // ---------------------------------------------------------
            // 细分
            // ---------------------------------------------------------

            // 距离越远细分越少。返回值要落在 [1, 64]，1 表示完全不细分
            float CalcTessFactor(float3 worldPos)
            {
                float dist = distance(_WorldSpaceCameraPos, worldPos);
                float t = saturate(dist / max(_TessDistance, 1e-3));
                return lerp(_TessMax, _TessMin, t);
            }

            // 每个 patch 算一次：三条边 + 内部
            // 边因子取两端点的较小值，保证相邻 patch 共享的那条边因子一致，否则会出现裂缝
            TessFactors PatchConstantFunc(InputPatch<Varyings, 3> patch)
            {
                TessFactors f;

                float3 p0 = patch[0].positionWS;
                float3 p1 = patch[1].positionWS;
                float3 p2 = patch[2].positionWS;

                float t0 = CalcTessFactor(p0);
                float t1 = CalcTessFactor(p1);
                float t2 = CalcTessFactor(p2);

                f.edge[0] = min(t1, t2); // p0 的对边
                f.edge[1] = min(t2, t0); // p1 的对边
                f.edge[2] = min(t0, t1); // p2 的对边
                f.inside  = min(min(t0, t1), t2);

                return f;
            }

            // ---------------------------------------------------------
            // 顶点 / 外壳 / 细分后
            // ---------------------------------------------------------

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);

                OUT.positionWS = posInputs.positionWS;
                OUT.positionCS = posInputs.positionCS;

                return OUT;
            }

            // 外壳阶段只做「把控制点原样传下去」，细分因子由 PatchConstantFunc 给
            [domain("tri")]
            [partitioning("fractional_odd")]
            [outputtopology("triangle_cw")]
            [outputcontrolpoints(3)]
            [patchconstantfunc("PatchConstantFunc")]
            Varyings HullProgram(InputPatch<Varyings, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            // 细分出来的每个顶点在这里做真正的位移。
            // 位移放在 domain 而不是 vert：vert 阶段只有一个平面三角形，
            // 先细分再位移，浪尖才不会因为顶点太稀被拉成三角形。
            [domain("tri")]
            Varyings DomainProgram(TessFactors factors,
                                   OutputPatch<Varyings, 3> patch,
                                   float3 bary : SV_DomainLocation)
            {
                Varyings OUT;

                float3 positionWS = patch[0].positionWS * bary.x +
                                    patch[1].positionWS * bary.y +
                                    patch[2].positionWS * bary.z;

                // 用「位移前」的世界 XZ 去查谱：
                // 频谱是定义在静止平面上的，不能拿位移后的坐标去查（会自己追自己）
                float3 displacement = SampleDisplacement(positionWS) * _DisplacementScale;

                // 远处的位移整体收掉，地平线才是一条水平线而不是一串浪尖
                positionWS += displacement * DisplacementFade(positionWS);

                OUT.positionWS = positionWS;
                OUT.positionCS = TransformWorldToHClip(positionWS);

                return OUT;
            }

            // ---------------------------------------------------------
            // 像素
            // ---------------------------------------------------------
            half4 frag(Varyings IN) : SV_Target
            {
                // 注意这里用的是细分后、已经位移过的世界坐标。
                // 采样位置理论上该用位移前的，但水平和垂直位移都在米级以内，
                // 换算成各级联的 uv 只差千分之几，肉眼看不出（domain 里必须严格用位移前的）。
                OceanDetail od = SampleDetail(IN.positionWS);

                // ---- 1. 从 FFT 导数场现算法线 ----
                // 高度场 y = h(x, z) 的法线是 normalize(-dh/dx, 1, -dh/dz)
                float4 detail = od.derivatives * _DisplacementScale;

                // 远处的法线和几何一起压平（几何都平了，法线还起伏就会像「贴了张皱纹纸」）
                float geoFade = DisplacementFade(IN.positionWS);
                float2 slope = detail.xy * geoFade;

                float3 normalWS = normalize(float3(
                    -slope.x * _NormalStrength,
                     1.0,
                    -slope.y * _NormalStrength));

                // ---- 2. 粗糙度：基础粗糙度 + 被淡出细节折算出来的额外粗糙度 ----
                // 组合方式用 sqrt(a² + b²)（和 Unity 合并两张粗糙度贴图的口径一致）。
                // 法线压平后剩下的起伏不再体现在高光形状上，但它并没有消失，把它换成粗糙度，
                // 远处高光才会是柔和的反射，而不是逐像素乱闪的火花（或反过来一块死镜面）。
                float lostFromGeo = (1.0 - geoFade * geoFade) * (detail.x * detail.x + detail.y * detail.y);
                float roughness = sqrt(_Roughness * _Roughness +
                                       (od.lostSlope2 + lostFromGeo) * _RoughnessBoost);
                roughness = saturate(roughness);

                // ---- 3. 泡沫 ----
                // 全部在 CS_UpdateFoam 里算完：雅可比 J、阈值判定、跨帧「涨得快落得慢」的累积，
                // 都是按级联纹素做的。这里只做两件事 —— 按级联淡出权重混合、按距离衰减。
                //
                // 距离衰减是参考实现里最不起眼但最关键的一步：
                // 它的 clipDepth 是「到相机的距离」，取 20 次方之后 100 米处只剩 12%、
                // 300 米处只剩 0.08%。泡沫本来只在近处几十米有分辨率，远处那一片
                // 全是亚像素噪声，衰减到看不见才是对的。以前我们完全没做这一层，
                // 于是整片海从近到远糊成一块均匀的白色。
                float foamDist = distance(_WorldSpaceCameraPos, IN.positionWS);
                float clipDepth = saturate(1.0 - foamDist / max(_DisplaceFadeDistance, 1e-3));
                float foam = od.foam * pow(clipDepth, max(_FoamDepthAttenuation, 1e-3));

                // 幂次收紧：泡沫从「一片薄雾」变成「一条边缘清楚的带子」
                foam = pow(saturate(foam), _FoamEdgePower);

                // 絮状遮罩：三层 fBm，并且先用低频噪声把 uv 扭一下（domain warp）。
                // 直接用没扭曲的 value noise 会露出网格 —— 噪声的格点轴和世界坐标是对齐的，
                // 再被对比度推成 0/1 之后，海面上一片片边缘笔直的方块，人眼立刻就看得出来。
                // 扭曲把格子边界揉开，剩下的才是不规则的絮状团块。
                float2 baseUV = IN.positionWS.xz * _FoamNoiseScale;
                float2 warpUV = baseUV * 0.61f;
                float2 warp = float2(FoamValueNoise(warpUV + 7.3f),
                                     FoamValueNoise(warpUV - 3.9f)) - 0.5f;
                float2 noiseUV = baseUV + warp * 1.6f;
                float foamNoise = FoamValueNoise(noiseUV) * 0.55f
                                + FoamValueNoise(noiseUV * 3.17f + 19.3f) * 0.30f
                                + FoamValueNoise(noiseUV * 9.73f - 5.10f) * 0.15f;
                // 往 0/1 两端推，但别推太狠：推过头会把噪声压成二元值，方块感又回来了
                foamNoise = saturate((foamNoise - 0.5f) * (2.0f * max(_FoamNoiseContrast, 1e-3)) + 0.5f);
                // 留一点底噪：噪声恰好压在 0 的位置不至于把整块泡沫抹掉
                foam *= saturate(0.45f + 0.60f * foamNoise);

                // ---- 4. 视线、主光 ----
                float3 viewDir = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                Light mainLight = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                float3 lightDir = mainLight.direction;
                float3 lightColor = mainLight.color;
                float shadow = mainLight.shadowAttenuation;

                float NdotL = saturate(dot(normalWS, lightDir));
                float NdotV = saturate(dot(normalWS, viewDir));

                // ---- 5. 菲涅尔（Schlick）----
                // 掠射角（视线贴着水面）时趋近 1，正对水面时趋近 F0。
                // 这是海面看起来「是水」的第一要素：远处的海几乎全是天空的颜色。
                float fresnel = WATER_F0 + (1.0 - WATER_F0) * pow(1.0 - NdotV, 5.0);
                fresnel = saturate(fresnel * _FresnelStrength);

                // ---- 6. 天空反射 ----
                // 用 URP 的采样函数：它按粗糙度选 mip、处理反射探针、HDR 解码，
                // 比直接采 unity_SpecCube0 稳。场景没放反射探针时会退回天空盒生成的默认立方图。
                float3 reflectDir = reflect(-viewDir, normalWS);

                // 把朝下的反射方向掰回水平（水下没有可以反射的东西）。
                // 不处理的话，浪背面朝下的法线会去采样天空盒的「地面」那一半，
                // 满屏都是棕灰色的斑块 —— 也就是默认 Skybox 的地面颜色。
                reflectDir.y = max(reflectDir.y, 0.02);
                reflectDir = normalize(reflectDir);

                half3 skyReflection = GlossyEnvironmentReflection(reflectDir, roughness, 1.0);
                skyReflection *= _EnvStrength;
                // 处于阴影里时天空还在，只是暗一点（天空光本身就是漫射的）
                skyReflection *= saturate(shadow + 0.5);

                // ---- 7. 太阳高光 ----
                // 借 URP 的 GGX 高光瓣，但把 diffuse 设成 0：
                // 只有太阳的镜面反射走这条路，水体颜色交给下面自己算。
                BRDFData brdfData;
                half brdfAlpha = 1.0;
                InitializeBRDFData(half3(0.0, 0.0, 0.0), 0.0, half3(WATER_F0, WATER_F0, WATER_F0),
                                   1.0 - roughness, brdfAlpha, brdfData);
                half3 specular = DirectBRDF(brdfData, normalWS, lightDir, viewDir)
                                 * lightColor * saturate(shadow + 0.3);

                // ---- 8. 水体（散射）----
                // k1 浪尖透光：太阳在镜头对面时（逆光），薄薄的浪尖会透出青绿色。
                // 高度直接用像素的世界 Y —— 海底平面在 y = 0，位移只在竖直方向，所以 y 就是波高，
                // 白捡一个变量，不用再采样一次位移纹理。
                float crest = saturate(IN.positionWS.y / max(_CrestHeight, 1e-3));
                float backLight = pow(saturate(dot(lightDir, -viewDir)), 4.0);

                half3 ambient = SampleSH(normalWS);
                half3 body = _OceanColor.rgb * (ambient + lightColor * NdotL * _SunBodyStrength);
                body += _ScatterColor.rgb * (backLight * crest * crest * _ScatterStrength);

                // ---- 9. 合成 ----
                float3 color = lerp(body, skyReflection, fresnel) + specular;

                // ---- 10. 泡沫 ----
                // 泡沫是不透明的漫反射白，盖掉水体和反射
                color = lerp(color, _FoamColor.rgb * (lightColor * NdotL + ambient), saturate(foam));

                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
