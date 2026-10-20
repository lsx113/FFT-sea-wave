using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 整条海浪模拟管线的总指挥，挂在场景里的一个空物体上。
///
/// 每帧做的事：
///   1. 三个级联各自推进时间（compute shader 里做相位旋转 + 4 次逆 FFT）
///   2. 结果写进 RenderTexture（Displacement / Derivatives / Turbulence）
///   3. 材质的着色器在顶点/片元阶段采样这些纹理，把平面网格「吹」成波浪
///
/// 初始化时做的事：
///   - 生成高斯噪声纹理（让每个频率分量的相位随机）
///   - 建一个共享的 FFT 计算器
///   - 为三个级联分配纹理、算初始频谱
///
/// 注意：频谱纹理边长必须是 2 的幂（4 / 8 / 16 ... 512），因为 FFT 是基 2 的。
/// </summary>
[DisallowMultipleComponent]
public class WavesGenerator : MonoBehaviour
{
    [Header("网格与频谱")]
    [Tooltip("频谱纹理边长，必须是 2 的幂。越大波长分辨率越高，但 GPU 开销是平方增长")]
    [SerializeField] int size = 256;

    [Tooltip("波谱参数资产（风速、风向、风程等）")]
    [SerializeField] WavesSettings wavesSettings;

    [Tooltip("改了参数就每帧重算初始频谱（调试用，正式运行时关掉）")]
    [SerializeField] bool alwaysRecalculateInitials = false;

    [Header("三个级联的海面尺寸（米）")]
    [Tooltip("级联 0：大尺度涌浪，建议 200~300")]
    [SerializeField] float lengthScale0 = 250;
    [Tooltip("级联 1：中尺度波，建议长度的 1/10 左右")]
    [SerializeField] float lengthScale1 = 17;
    [Tooltip("级联 2：小尺度细浪，建议 3~6")]
    [SerializeField] float lengthScale2 = 5;

    [Header("Compute Shader")]
    [SerializeField] ComputeShader fftShader;
    [SerializeField] ComputeShader initialSpectrumShader;
    [SerializeField] ComputeShader timeDependentSpectrumShader;
    [SerializeField] ComputeShader texturesMergerShader;

    [Header("CPU 查询（可选）")]
    [Tooltip("每帧把位移纹理读回 CPU，供 GetWaterHeight 查询。开销较大，不需要时关掉")]
    [SerializeField] bool enableHeightReadback = false;

    public WavesCascade Cascade0 => cascade0;
    public WavesCascade Cascade1 => cascade1;
    public WavesCascade Cascade2 => cascade2;

    WavesCascade cascade0;
    WavesCascade cascade1;
    WavesCascade cascade2;

    Texture2D gaussianNoise;
    FastFourierTransform fft;
    Texture2D physicsReadback;

    void Awake()
    {
        if (wavesSettings == null || fftShader == null || initialSpectrumShader == null
            || timeDependentSpectrumShader == null || texturesMergerShader == null)
        {
            Debug.LogError("[WavesGenerator] 波谱参数或 Compute Shader 没有在 Inspector 里赋值，海浪不会生成。", this);
            enabled = false;
            return;
        }

        size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(size), 8, 512);

        fft = new FastFourierTransform(size, fftShader);
        gaussianNoise = GenerateNoiseTexture(size);

        cascade0 = CreateCascade();
        cascade1 = CreateCascade();
        cascade2 = CreateCascade();

        InitialiseCascades();

        if (enableHeightReadback)
            physicsReadback = new Texture2D(size, size, TextureFormat.RGBAFloat, false);
    }

    WavesCascade CreateCascade()
    {
        return new WavesCascade(size, initialSpectrumShader, timeDependentSpectrumShader,
            texturesMergerShader, fft, gaussianNoise);
    }

    /// <summary>
    /// 算出三个级联的波数分界，并让每个级联各自算一次初始频谱。
    ///
    /// 分界取值 2π/长度 × 6：大致让相邻级联在「6 个波长」的地方交接，
    /// 太小会让交界处出现明显断层，太大则同一段波被两层重复叠加（能量翻倍）。
    /// </summary>
    void InitialiseCascades()
    {
        float boundary1 = 2 * Mathf.PI / lengthScale1 * 6f;
        float boundary2 = 2 * Mathf.PI / lengthScale2 * 6f;

        cascade0.CalculateInitials(wavesSettings, lengthScale0, 0.0001f, boundary1);
        cascade1.CalculateInitials(wavesSettings, lengthScale1, boundary1, boundary2);
        cascade2.CalculateInitials(wavesSettings, lengthScale2, boundary2, 9999);

        // 着色器里按世界坐标采样三级位移纹理，需要知道各自的平铺尺寸
        Shader.SetGlobalFloat("LengthScale0", lengthScale0);
        Shader.SetGlobalFloat("LengthScale1", lengthScale1);
        Shader.SetGlobalFloat("LengthScale2", lengthScale2);
    }

    void Update()
    {
        if (alwaysRecalculateInitials)
            InitialiseCascades();

        float time = Time.time;
        cascade0.CalculateWavesAtTime(time);
        cascade1.CalculateWavesAtTime(time);
        cascade2.CalculateWavesAtTime(time);

        if (enableHeightReadback)
            RequestReadbacks();
    }

    void OnDestroy()
    {
        cascade0?.Dispose();
        cascade1?.Dispose();
        cascade2?.Dispose();

        if (gaussianNoise != null)
            Destroy(gaussianNoise);
    }

    void OnValidate()
    {
        size = Mathf.Clamp(Mathf.ClosestPowerOfTwo(size), 8, 512);
    }

    /// <summary>
    /// 生成 GaussianNoiseTexture：每个像素是一对标准正态分布的随机数。
    /// 用 Box-Muller 变换把两个均匀分布变成两个正态分布。
    /// </summary>
    Texture2D GenerateNoiseTexture(int size)
    {
        Texture2D noise = new Texture2D(size, size, TextureFormat.RGFloat, false, true);
        noise.name = "Gaussian Noise " + size;
        noise.filterMode = FilterMode.Point;
        noise.wrapMode = TextureWrapMode.Repeat;

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                noise.SetPixel(i, j, new Vector4(NormalRandom(), NormalRandom()));
            }
        }

        noise.Apply();
        return noise;
    }

    /// <summary>Box-Muller：由两个均匀随机数得到标准正态随机数。</summary>
    static float NormalRandom()
    {
        return Mathf.Cos(2 * Mathf.PI * Random.value) * Mathf.Sqrt(-2 * Mathf.Log(Random.value));
    }

    // ------------------------------------------------------------------
    // 以下为可选的 CPU 侧查询：游戏逻辑（浮标、船）需要知道某点水面高度时用。
    // 原理是把级联 0 的位移纹理异步读回 CPU，再按世界坐标查像素。
    // ------------------------------------------------------------------

    void RequestReadbacks()
    {
        AsyncGPUReadback.Request(cascade0.Displacement, 0, TextureFormat.RGBAFloat, OnCompleteReadback);
    }

    void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        if (request.hasError)
        {
            Debug.LogWarning("[WavesGenerator] GPU 读回失败，GetWaterHeight 这一帧会返回旧数据。");
            return;
        }

        physicsReadback.LoadRawTextureData(request.GetData<Color>());
        physicsReadback.Apply();
    }

    /// <summary>
    /// 查询某点水面高度。因为水平位移会把水「推」过去，单次采样是不准的，
    /// 这里用固定点迭代逼近：先按原点位移找，再沿位移修正两三次。
    /// </summary>
    public float GetWaterHeight(Vector3 position)
    {
        if (physicsReadback == null)
        {
            Debug.LogWarning("[WavesGenerator] 没有开启 enableHeightReadback，GetWaterHeight 不可用。");
            return 0f;
        }

        Vector3 displacement = GetWaterDisplacement(position);
        displacement = GetWaterDisplacement(position - displacement);
        displacement = GetWaterDisplacement(position - displacement);

        return GetWaterDisplacement(position - displacement).y;
    }

    public Vector3 GetWaterDisplacement(Vector3 position)
    {
        Color c = physicsReadback.GetPixelBilinear(position.x / lengthScale0, position.z / lengthScale0);
        return new Vector3(c.r, c.g, c.b);
    }
}
