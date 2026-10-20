using UnityEngine;

/// <summary>
/// 传给 compute shader 的波谱参数（显式布局，和 HLSL 里的 struct SpectrumParameters 一一对应）。
/// 注意字段顺序和类型必须和 compute shader 中的声明完全一致，否则 GPU 读到的数据会错位。
/// </summary>
public struct SpectrumSettings
{
    public float scale;
    public float angle;
    public float spreadBlend;
    public float swell;
    public float alpha;
    public float peakOmega;
    public float gamma;
    public float shortWavesFade;
}

/// <summary>
/// 挂在 Inspector 上给美术/策划调的参数。
/// 这里填的是「人话」参数（风速、风向、风程），真正的 JONSWAP 系数在运行前由 C# 算出来。
/// </summary>
[System.Serializable]
public struct DisplaySpectrumSettings
{
    [Tooltip("这个频谱的整体强度，0 = 不参与")]
    [Range(0, 1)]
    public float scale;

    [Tooltip("10 米高度处的风速（m/s）。越大浪越大")]
    public float windSpeed;

    [Tooltip("风向（度）。0 = 沿 +X，90 = 沿 +Z")]
    public float windDirection;

    [Tooltip("风程（米）：风持续吹过的距离，越大浪越「成熟」（低频能量越多）")]
    public float fetch;

    [Tooltip("方向分布混合：0 = 宽（cos²），1 = 窄（cos-2s）")]
    [Range(0, 1)]
    public float spreadBlend;

    [Tooltip("涌浪程度：越大方向越集中、越像远处传来的长波")]
    [Range(0, 1)]
    public float swell;

    [Tooltip("JONSWAP 峰值增强系数 γ，约 3.3 是典型值")]
    public float peakEnhancement;

    [Tooltip("短波衰减尺度，用来压掉网格分辨率撑不住的高频")]
    public float shortWavesFade;
}

/// <summary>
/// 波谱配置文件（ScriptableObject）。
/// 右键 Create > Ocean > Waves Settings 可以新建一份，然后在 WavesGenerator 上引用。
/// </summary>
[CreateAssetMenu(fileName = "New waves settings", menuName = "Ocean/Waves Settings")]
public class WavesSettings : ScriptableObject
{
    [Tooltip("重力加速度，9.81 是地球")]
    public float g = 9.81f;

    [Tooltip("水深（米）。500 近似深水；浅水会削弱高频波")]
    public float depth = 500f;

    [Tooltip("水平位移强度：0 = 只上下起伏，1 = 完全按物理水平偏移（会形成尖浪）")]
    [Range(0, 1)]
    public float lambda = 1f;

    [Tooltip("主波场（风浪）")]
    public DisplaySpectrumSettings local = new DisplaySpectrumSettings
    {
        scale = 1f,
        windSpeed = 0.5f,
        windDirection = -29.81f,
        fetch = 100000f,
        spreadBlend = 1f,
        swell = 0.198f,
        peakEnhancement = 3.3f,
        shortWavesFade = 0.01f,
    };

    [Tooltip("涌浪波场（远处传来的长波）。scale = 0 表示关闭")]
    public DisplaySpectrumSettings swell = new DisplaySpectrumSettings
    {
        scale = 0f,
        windSpeed = 1f,
        windDirection = 0f,
        fetch = 300000f,
        spreadBlend = 1f,
        swell = 1f,
        peakEnhancement = 3.3f,
        shortWavesFade = 0.01f,
    };

    // 复用的临时数组，避免每帧给 ComputeBuffer 分配新内存
    readonly SpectrumSettings[] spectrums = new SpectrumSettings[2];

    /// <summary>
    /// 把「人话」参数换算成 GPU 用的系数，写进 paramsBuffer。
    /// </summary>
    public void SetParametersToShader(ComputeShader shader, int kernelIndex, ComputeBuffer paramsBuffer)
    {
        shader.SetFloat(G_PROP, g);
        shader.SetFloat(DEPTH_PROP, depth);

        FillSettingsStruct(local, ref spectrums[0]);
        FillSettingsStruct(swell, ref spectrums[1]);

        paramsBuffer.SetData(spectrums);
        shader.SetBuffer(kernelIndex, SPECTRUMS_PROP, paramsBuffer);
    }

    void FillSettingsStruct(DisplaySpectrumSettings display, ref SpectrumSettings settings)
    {
        settings.scale = display.scale;
        settings.angle = display.windDirection / 180 * Mathf.PI;
        settings.spreadBlend = display.spreadBlend;
        settings.swell = Mathf.Clamp(display.swell, 0.01f, 1f);

        // JONSWAP 的两个经验公式：α 和峰值频率都由风速 + 风程决定
        settings.alpha = JonswapAlpha(g, display.fetch, display.windSpeed);
        settings.peakOmega = JonswapPeakFrequency(g, display.fetch, display.windSpeed);

        settings.gamma = display.peakEnhancement;
        settings.shortWavesFade = display.shortWavesFade;
    }

    float JonswapAlpha(float g, float fetch, float windSpeed)
    {
        return 0.076f * Mathf.Pow(g * fetch / windSpeed / windSpeed, -0.22f);
    }

    float JonswapPeakFrequency(float g, float fetch, float windSpeed)
    {
        return 22 * Mathf.Pow(windSpeed * fetch / g / g, -0.33f);
    }

    readonly int G_PROP = Shader.PropertyToID("GravityAcceleration");
    readonly int DEPTH_PROP = Shader.PropertyToID("Depth");
    readonly int SPECTRUMS_PROP = Shader.PropertyToID("Spectrums");
}
