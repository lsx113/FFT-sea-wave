// 程序化阴天天空盒
//
// 为什么需要它：海面本身只是「水」，观感上的天色、地平线雾霭、水面的反射色
// 都是一体的。默认的 Default-Skybox 是干净的蓝色渐变，和实拍的海边阴天差了
// 一大截。这里用几层值噪声画云，不需要任何贴图。
//
// 云的做法：把视线方向投影到 y = 1 的平面上，取 xz 做纹理坐标。
// 这样云会自然地随视线压低而「拉伸」，靠近地平线的云被压扁成一条条，
// 和真实透视一致。再乘上随时间缓慢平移的偏移量，云就会飘。

Shader "Ocean/CloudySky"
{
    Properties
    {
        [Header(Sky)]
        _ZenithColor ("Zenith Color", Color) = (0.36, 0.48, 0.63, 1)
        _HorizonColor ("Horizon Color", Color) = (0.78, 0.82, 0.85, 1)

        [Header(Clouds)]
        _CloudColor ("Cloud Bright Color", Color) = (0.97, 0.97, 0.96, 1)
        _CloudShadowColor ("Cloud Shadow Color", Color) = (0.55, 0.58, 0.62, 1)
        _CloudCoverage ("Cloud Coverage", Range(0, 1)) = 0.46
        _CloudScale ("Cloud Scale", Range(0.1, 8)) = 1.4
        _CloudSpeed ("Drift Speed", Range(0, 0.2)) = 0.012
        _CloudContrast ("Cloud Contrast", Range(0.01, 0.6)) = 0.22

        [Header(Sun)]
        // 由 SkySunSync 每帧从主方向光同步，不用手填
        [HideInInspector] _SunDirection ("Sun Direction", Vector) = (0, 0.2, 1, 0)
        _SunGlowColor ("Sun Glow Color", Color) = (1, 0.72, 0.4, 1)
        _SunGlowStrength ("Sun Glow Strength", Range(0, 4)) = 0
        _SunGlowPower ("Sun Glow Falloff", Range(1, 64)) = 8
        _SunDiscSize ("Sun Disc Size", Range(0.0005, 0.1)) = 0.012
        _SunDiscIntensity ("Sun Disc Intensity", Range(0, 8)) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _ZenithColor;
                float4 _HorizonColor;
                float4 _CloudColor;
                float4 _CloudShadowColor;
                float _CloudCoverage;
                float _CloudScale;
                float _CloudSpeed;
                float _CloudContrast;

                float4 _SunGlowColor;
                float4 _SunDirection;      // 由 SkySunSync 每帧按主方向光写入
                float _SunGlowStrength;
                float _SunGlowPower;
                float _SunDiscSize;
                float _SunDiscIntensity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 direction  : TEXCOORD0;
            };

            // 便宜的哈希 + 值噪声（不需要纹理，逐像素算也够快）
            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);   // smoothstep 插值

                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 分形叠加：低层给大团块，高层给边缘的碎絮
            float Fbm(float2 p)
            {
                float sum = 0.0;
                float amplitude = 0.5;
                for (int i = 0; i < 4; i++)
                {
                    sum += ValueNoise(p) * amplitude;
                    p = p * 2.03 + 17.1;
                    amplitude *= 0.5;
                }
                return sum;
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // 天空盒的物体空间已经跟着相机转了，所以物体空间方向就是视线方向
                output.direction = TransformObjectToWorldDir(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.direction);
                float up = saturate(dir.y);

                // ---------- 1. 天空底色 ----------
                // pow(up, 0.45)：地平线附近的渐变拉长，天顶收窄，接近真实的散射
                float3 color = lerp(_HorizonColor.rgb, _ZenithColor.rgb, pow(up, 0.45));

                // ---------- 2. 云层 ----------
                // 投影到 y = 1 平面；denom 设下限，避免地平线附近坐标飞出去
                float denom = max(dir.y, 0.06);
                float2 uv = dir.xz / denom;
                uv *= _CloudScale;
                uv += _Time.y * _CloudSpeed * float2(1.0, 0.35);

                float noise = Fbm(uv);

                float density = smoothstep(_CloudCoverage, _CloudCoverage + _CloudContrast, noise);
                // 越靠近地平线，云被大气压得越淡
                density *= smoothstep(-0.02, 0.16, dir.y);

                // 云块内部有明暗：厚的地方压暗，边缘被天光照亮
                float shading = smoothstep(_CloudCoverage + _CloudContrast * 0.6,
                                           _CloudCoverage + _CloudContrast * 2.2, noise);
                float3 cloud = lerp(_CloudShadowColor.rgb, _CloudColor.rgb, shading);

                color = lerp(color, cloud, density * 0.92);

                // ---------- 3. 地平线雾霭 ----------
                // 让海天交界处融合成一条淡带，和远处的雾接上
                color = lerp(color, _HorizonColor.rgb * 1.02, pow(1.0 - up, 12.0));

                // ---------- 4. 日落光晕与日盘 ----------
                // 太阳方向由 SkySunSync 每帧从场景里的 Directional Light 同步进来。
                // （不能用 _MainLightPosition：画天空盒时这个全局变量还没被 URP 写，读到的是 0）
                float3 sunDir = normalize(_SunDirection.xyz);
                float sunDot = saturate(dot(dir, sunDir));

                // 云会挡太阳：云越厚光晕越弱，但留 15% 透光，免得日落看起来像贴图
                float sunVisible = 1.0 - density * 0.85;

                color += _SunGlowColor.rgb * pow(sunDot, _SunGlowPower) * _SunGlowStrength * sunVisible;

                // 日盘：只有没被云挡住时才露出来
                float disc = smoothstep(1.0 - _SunDiscSize, 1.0 - _SunDiscSize * 0.55, sunDot);
                color += _SunGlowColor.rgb * disc * _SunDiscIntensity * (1.0 - density);

                return half4(color, 1);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
