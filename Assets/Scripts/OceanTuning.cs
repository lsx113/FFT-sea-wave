using UnityEngine;

/// <summary>
/// 海面的「实时调参」面板 —— 把它挂到场景里的 Ocean 物体上，在 Play 模式下调参数就能立刻看到效果。
///
/// 为什么不直接改材质的 Inspector：
///   海面是由 <see cref="OceanGeometry"/> 在运行时生成的十几块瓦片拼出来的，
///   每块瓦片用的是材质的独立副本。在 Inspector 里改 Ocean.mat 本身，
///   有机会触发材质重新导入，把运行时塞进去的级联 RenderTexture 引用弄失效，
///   海面就会突然变成一块平面的灰色 —— 这个坑不好判断。
///   这里改成每帧主动把参数写到材质上，绕开资产导入的问题。
///
/// 改完之后要不要留下来：Play 模式里改的是内存中的值，退出 Play 会还原。
///   调满意了用右键菜单「保存参数到资产」，或者在 Inspector 里照抄数值。
/// </summary>
[DisallowMultipleComponent]
public class OceanTuning : MonoBehaviour
{
    [Header("引用（留空自动找）")]
    [SerializeField] OceanGeometry geometry;
    [SerializeField] WavesGenerator wavesGenerator;

    [Header("波形（改完会自动重算，约每秒 5 次）")]
    [Tooltip("10 米高度风速。越大浪越大、越碎。4 左右接近参考图的浪高")]
    [Range(0f, 20f)] [SerializeField] float windSpeed = 4f;

    [Tooltip("风向（度）。0 = 沿 +X，90 = 沿 +Z")]
    [Range(0f, 360f)] [SerializeField] float windDirection = 330.19f;

    [Tooltip("水平位移强度：越大浪峰越尖、越「卷」，0 = 只上下起伏")]
    [Range(0f, 1f)] [SerializeField] float lambda = 0.6f;

    [Tooltip("短波衰减：越大越早削掉高频细浪，海面更平滑")]
    [Range(0f, 0.3f)] [SerializeField] float shortWavesFade = 0.1f;

    [Header("水面颜色")]
    [Tooltip("水体底色（浪谷的颜色）。注意这里显示的是线性值，和截图上量到的 sRGB 值不是一回事")]
    [SerializeField] Color waterColor = new Color(0.13f, 0.28f, 0.42f);

    [Tooltip("掠射角的反射色，一般取接近天空的颜色")]
    [SerializeField] Color reflectionColor = new Color(0.74f, 0.83f, 0.92f);

    [Header("高光 / 细节")]
    [Tooltip("高光锐度：越小高光越散、越像阴天的漫反射")]
    [Range(0f, 1f)] [SerializeField] float maxGloss = 0.62f;

    [Tooltip("远处粗糙度：越大远处越灰哑")]
    [Range(0f, 1f)] [SerializeField] float roughness = 0.26f;

    [Tooltip("高频碎波法线强度。0 = 关掉，近处水面会变得很「塑料」")]
    [Range(0f, 0.5f)] [SerializeField] float detailNormalStrength = 0.15f;

    [Tooltip("高频碎波的尺度。越大碎纹越密")]
    [Range(0.5f, 12f)] [SerializeField] float detailScale = 1.8f;

    [Header("泡沫")]
    [SerializeField] Color foamColor = Color.white;

    [Tooltip("泡沫浓度：越大泡沫面积越大")]
    [Range(0f, 20f)] [SerializeField] float foamScale = 10f;

    [Tooltip("近处（CLOSE 瓦片）泡沫阈值。调小 = 泡沫变多")]
    [Range(0f, 7f)] [SerializeField] float foamBiasLOD0 = 0.50f;

    [Tooltip("中距（MID 瓦片）泡沫阈值")]
    [Range(0f, 7f)] [SerializeField] float foamBiasLOD1 = 1.42f;

    [Tooltip("远处（FAR 瓦片）泡沫阈值")]
    [Range(0f, 7f)] [SerializeField] float foamBiasLOD2 = 2.38f;

    [Tooltip("泡沫噪声尺度：越大泡沫边缘越碎")]
    [Range(0.2f, 8f)] [SerializeField] float foamNoiseScale = 2.2f;

    [Tooltip("泡沫噪声强度：越大泡沫边缘越不规则、越不像一块白漆")]
    [Range(0f, 0.4f)] [SerializeField] float foamNoiseStrength = 0.09f;

    [Tooltip("泡沫拉丝长度：顺着风向把泡沫拉成条状")]
    [Range(1f, 6f)] [SerializeField] float foamStretch = 2.6f;

    [Tooltip("拉丝方向（度），一般和风向一致")]
    [Range(0f, 360f)] [SerializeField] float foamDirection = 150f;

    const float RecalculateInterval = 0.2f;   // 重算初始频谱比较贵，拖动滑条时限制一下频率

    Material material;
    WavesSettings settings;

    bool resolved;
    bool materialDirty = true;
    bool wavesDirty;
    float lastRecalculate;

    void Update()
    {
        if (!resolved && !Resolve())
            return;

        ApplyMaterial();
        ApplyWaves();

        if (wavesDirty && Time.time - lastRecalculate >= RecalculateInterval)
        {
            wavesGenerator.RecalculateInitials();
            wavesDirty = false;
            lastRecalculate = Time.time;
        }
    }

    /// <summary>找到材质和波谱参数，并把面板的初始值对齐到它们当前的真实值。</summary>
    bool Resolve()
    {
        if (geometry == null)
            geometry = FindFirstObjectByType<OceanGeometry>();
        if (wavesGenerator == null)
            wavesGenerator = FindFirstObjectByType<WavesGenerator>();

        if (geometry == null || geometry.OceanMaterial == null || wavesGenerator == null
            || wavesGenerator.Settings == null)
            return false;

        material = geometry.OceanMaterial;
        settings = wavesGenerator.Settings;

        // 面板上的数值以材质 / 资产里的现状为准，避免一进来就把场景改跑偏
        waterColor = material.GetColor("_Color");
        reflectionColor = material.GetColor("_ReflectionColor");
        maxGloss = material.GetFloat("_MaxGloss");
        roughness = material.GetFloat("_Roughness");
        detailNormalStrength = material.GetFloat("_DetailNormalStrength");
        detailScale = material.GetFloat("_DetailScale");
        foamColor = material.GetColor("_FoamColor");
        foamScale = material.GetFloat("_FoamScale");
        foamBiasLOD0 = material.GetFloat("_FoamBiasLOD0");
        foamBiasLOD1 = material.GetFloat("_FoamBiasLOD1");
        foamBiasLOD2 = material.GetFloat("_FoamBiasLOD2");
        foamNoiseScale = material.GetFloat("_FoamNoiseScale");
        foamNoiseStrength = material.GetFloat("_FoamNoiseStrength");
        foamStretch = material.GetFloat("_FoamStretch");
        foamDirection = material.GetFloat("_FoamDirection");

        windSpeed = settings.local.windSpeed;
        windDirection = Mathf.Repeat(settings.local.windDirection, 360f);
        lambda = settings.lambda;
        shortWavesFade = settings.local.shortWavesFade;

        resolved = true;
        return true;
    }

    void ApplyMaterial()
    {
        if (material == null)
        {
            resolved = false;
            return;
        }

        // 值没变就别每帧写，省得把材质一直标脏
        if (!materialDirty)
            return;

        material.SetColor("_Color", waterColor);
        material.SetColor("_ReflectionColor", reflectionColor);
        material.SetFloat("_MaxGloss", maxGloss);
        material.SetFloat("_Roughness", roughness);
        material.SetFloat("_DetailNormalStrength", detailNormalStrength);
        material.SetFloat("_DetailScale", detailScale);
        material.SetColor("_FoamColor", foamColor);
        material.SetFloat("_FoamScale", foamScale);
        material.SetFloat("_FoamBiasLOD0", foamBiasLOD0);
        material.SetFloat("_FoamBiasLOD1", foamBiasLOD1);
        material.SetFloat("_FoamBiasLOD2", foamBiasLOD2);
        material.SetFloat("_FoamNoiseScale", foamNoiseScale);
        material.SetFloat("_FoamNoiseStrength", foamNoiseStrength);
        material.SetFloat("_FoamStretch", foamStretch);
        material.SetFloat("_FoamDirection", foamDirection);

        materialDirty = false;
    }

    void ApplyWaves()
    {
        if (settings == null)
        {
            resolved = false;
            return;
        }

        // 风向在资产里是 -29.81 这种写法，面板上是 330.19，比较时要折算到同一区间
        if (Mathf.Approximately(settings.local.windSpeed, windSpeed)
            && Mathf.Approximately(Mathf.Repeat(settings.local.windDirection, 360f), windDirection)
            && Mathf.Approximately(settings.lambda, lambda)
            && Mathf.Approximately(settings.local.shortWavesFade, shortWavesFade))
            return;

        settings.local.windSpeed = windSpeed;
        settings.local.windDirection = windDirection;
        settings.lambda = lambda;
        settings.local.shortWavesFade = shortWavesFade;

        wavesDirty = true;
    }

    /// <summary>面板在 Inspector 里被拖动时调用。</summary>
    void OnValidate()
    {
        materialDirty = true;
    }

    /// <summary>
    /// 把当前面板上的数值写回磁盘上的资产（材质 + 波谱参数），这样退出 Play 也还在。
    /// 在 Inspector 里右键组件即可看到这一项。
    /// </summary>
    [ContextMenu("保存参数到资产")]
    void SaveToAssets()
    {
#if UNITY_EDITOR
        if (!Resolve() || material == null || settings == null)
            return;

        UnityEditor.EditorUtility.SetDirty(material);
        UnityEditor.EditorUtility.SetDirty(settings);
        UnityEditor.AssetDatabase.SaveAssets();
        Debug.Log("[OceanTuning] 参数已写回 " + material.name + " 与 " + settings.name);
#else
        Debug.LogWarning("[OceanTuning] 只能在编辑器里保存到资产。");
#endif
    }
}
