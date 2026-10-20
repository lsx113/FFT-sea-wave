using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 一个「级联」（cascade）负责一段波长范围的海浪。
///
/// 为什么要有级联：一张 256×256 的频谱，波长跨度是固定的。用一张图同时表现
/// 250 米的大浪和 1 米的细浪，小波会精度不足、闪烁。所以拆成三级：
///   级联 0：250 米级别的涌浪（大尺度、低频）
///   级联 1：17 米级别的中波
///   级联 2：5 米级别的细浪（近处细节）
/// 渲染时三层叠加，远处只采样前几层（远处看不到细节）。
///
/// 每个级联的固定开销：一张初始频谱 H0（只算一次）+ 每帧 4 次逆 FFT。
/// </summary>
public class WavesCascade
{
    /// <summary>顶点位移纹理（RGBA32F，xyz = 位移米数）</summary>
    public RenderTexture Displacement => displacement;
    /// <summary>斜率/二阶导纹理（RGBA32F，xy = 斜率，zw = 二阶导）</summary>
    public RenderTexture Derivatives => derivatives;
    /// <summary>雅可比行列式纹理（RGBA32F，r = J，用来生成泡沫）</summary>
    public RenderTexture Turbulence => turbulence;
    public RenderTexture InitialSpectrum => initialSpectrum;

    readonly int size;
    readonly ComputeShader initialSpectrumShader;
    readonly ComputeShader timeDependentSpectrumShader;
    readonly ComputeShader texturesMergerShader;
    readonly FastFourierTransform fft;
    readonly Texture2D gaussianNoise;
    readonly ComputeBuffer paramsBuffer;

    // 只算一次的中间结果
    readonly RenderTexture initialSpectrum; // H0
    readonly RenderTexture precomputedData; // 每个频率格点的 k 和 ω

    // 每帧复用的临时纹理（IFFT 的输入/输出/乒乓缓冲）
    readonly RenderTexture buffer;
    readonly RenderTexture DxDz;
    readonly RenderTexture DyDxz;
    readonly RenderTexture DyxDyz;
    readonly RenderTexture DxxDzz;

    // 最终产物
    readonly RenderTexture displacement;
    readonly RenderTexture derivatives;
    readonly RenderTexture turbulence;

    float lambda;

    public WavesCascade(int size,
                        ComputeShader initialSpectrumShader,
                        ComputeShader timeDependentSpectrumShader,
                        ComputeShader texturesMergerShader,
                        FastFourierTransform fft,
                        Texture2D gaussianNoise)
    {
        this.size = size;
        this.initialSpectrumShader = initialSpectrumShader;
        this.timeDependentSpectrumShader = timeDependentSpectrumShader;
        this.texturesMergerShader = texturesMergerShader;
        this.fft = fft;
        this.gaussianNoise = gaussianNoise;

        KERNEL_INITIAL_SPECTRUM = initialSpectrumShader.FindKernel("CalculateInitialSpectrum");
        KERNEL_CONJUGATE_SPECTRUM = initialSpectrumShader.FindKernel("CalculateConjugatedSpectrum");
        KERNEL_TIME_DEPENDENT_SPECTRUMS = timeDependentSpectrumShader.FindKernel("CalculateAmplitudes");
        KERNEL_RESULT_TEXTURES = texturesMergerShader.FindKernel("FillResultTextures");

        // 频谱类纹理用全精度浮点，避免半精度导致波浪抖动
        initialSpectrum = FastFourierTransform.CreateRenderTexture(size, RenderTextureFormat.ARGBFloat);
        precomputedData = FastFourierTransform.CreateRenderTexture(size, RenderTextureFormat.ARGBFloat);
        displacement = FastFourierTransform.CreateRenderTexture(size, RenderTextureFormat.ARGBFloat);
        derivatives = FastFourierTransform.CreateRenderTexture(size, RenderTextureFormat.ARGBFloat, true);
        turbulence = FastFourierTransform.CreateRenderTexture(size, RenderTextureFormat.ARGBFloat, true);

        // 泡沫纹理是「逐帧累积」的：每帧存 min(雅可比行列式, 上一帧 + 增量)。
        // 新建的浮点纹理内容是未定义的（通常是全 0），按这个公式第一秒会算出满屏白沫，
        // 所以先把它填成雅可比行列式的基准值 1，让开局就是干净的海面。
        ClearTexture(turbulence, Color.white);

        // 8 个 float × 2 个频谱（主波场 + 涌浪）
        paramsBuffer = new ComputeBuffer(2, 8 * sizeof(float));

        buffer = FastFourierTransform.CreateRenderTexture(size);
        DxDz = FastFourierTransform.CreateRenderTexture(size);
        DyDxz = FastFourierTransform.CreateRenderTexture(size);
        DyxDyz = FastFourierTransform.CreateRenderTexture(size);
        DxxDzz = FastFourierTransform.CreateRenderTexture(size);
    }

    /// <summary>把一张 RenderTexture 清成纯色（浮点纹理刚创建时内容不确定，必须显式初始化）。</summary>
    static void ClearTexture(RenderTexture target, Color color)
    {
        CommandBuffer cmd = new CommandBuffer { name = "Clear " + target.name };
        cmd.SetRenderTarget(target);
        cmd.ClearRenderTarget(false, true, color);
        Graphics.ExecuteCommandBuffer(cmd);
        cmd.Release();
    }

    /// <summary>释放 GPU 资源。场景卸载时调用。</summary>
    public void Dispose()
    {
        paramsBuffer?.Release();
    }

    /// <summary>
    /// 启动时（或改了波谱参数后）算一次初始频谱。
    /// cutoffLow / cutoffHigh 决定本级联负责的波数区间，相邻级联的区间首尾相接，
    /// 避免同一个波形被两层重复叠加。
    /// </summary>
    public void CalculateInitials(WavesSettings wavesSettings, float lengthScale,
                                  float cutoffLow, float cutoffHigh)
    {
        lambda = wavesSettings.lambda;

        initialSpectrumShader.SetInt(SIZE_PROP, size);
        initialSpectrumShader.SetFloat(LENGTH_SCALE_PROP, lengthScale);
        initialSpectrumShader.SetFloat(CUTOFF_HIGH_PROP, cutoffHigh);
        initialSpectrumShader.SetFloat(CUTOFF_LOW_PROP, cutoffLow);
        wavesSettings.SetParametersToShader(initialSpectrumShader, KERNEL_INITIAL_SPECTRUM, paramsBuffer);

        initialSpectrumShader.SetTexture(KERNEL_INITIAL_SPECTRUM, H0K_PROP, buffer);
        initialSpectrumShader.SetTexture(KERNEL_INITIAL_SPECTRUM, PRECOMPUTED_DATA_PROP, precomputedData);
        initialSpectrumShader.SetTexture(KERNEL_INITIAL_SPECTRUM, NOISE_PROP, gaussianNoise);
        initialSpectrumShader.Dispatch(KERNEL_INITIAL_SPECTRUM, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);

        initialSpectrumShader.SetTexture(KERNEL_CONJUGATE_SPECTRUM, H0_PROP, initialSpectrum);
        initialSpectrumShader.SetTexture(KERNEL_CONJUGATE_SPECTRUM, H0K_PROP, buffer);
        initialSpectrumShader.Dispatch(KERNEL_CONJUGATE_SPECTRUM, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
    }

    /// <summary>
    /// 每帧调用：按当前时间推进相位 → 4 次逆 FFT → 合并成渲染用的 3 张纹理。
    /// </summary>
    public void CalculateWavesAtTime(float time)
    {
        // 1) 频域里把相位转 ωt，得到这一帧的 4 组复数场
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, Dx_Dz_PROP, DxDz);
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, Dy_Dxz_PROP, DyDxz);
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, Dyx_Dyz_PROP, DyxDyz);
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, Dxx_Dzz_PROP, DxxDzz);
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, H0_PROP, initialSpectrum);
        timeDependentSpectrumShader.SetTexture(KERNEL_TIME_DEPENDENT_SPECTRUMS, PRECOMPUTED_DATA_PROP, precomputedData);
        timeDependentSpectrumShader.SetFloat(TIME_PROP, time);
        timeDependentSpectrumShader.Dispatch(KERNEL_TIME_DEPENDENT_SPECTRUMS, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);

        // 2) 各自做逆 FFT（outputToInput=true：结果写回原纹理；scale=true：归一化；permute=true：拼缝处理）
        fft.IFFT2D(DxDz, buffer, true, false, true);
        fft.IFFT2D(DyDxz, buffer, true, false, true);
        fft.IFFT2D(DyxDyz, buffer, true, false, true);
        fft.IFFT2D(DxxDzz, buffer, true, false, true);

        // 3) 把 4 组结果拆开、合并成最终纹理
        texturesMergerShader.SetFloat("DeltaTime", Time.deltaTime);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, Dx_Dz_PROP, DxDz);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, Dy_Dxz_PROP, DyDxz);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, Dyx_Dyz_PROP, DyxDyz);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, Dxx_Dzz_PROP, DxxDzz);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, DISPLACEMENT_PROP, displacement);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, DERIVATIVES_PROP, derivatives);
        texturesMergerShader.SetTexture(KERNEL_RESULT_TEXTURES, TURBULENCE_PROP, turbulence);
        texturesMergerShader.SetFloat(LAMBDA_PROP, lambda);
        texturesMergerShader.Dispatch(KERNEL_RESULT_TEXTURES, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);

        // 4) 斜坡/泡沫在远处需要 mipmap 来抗锯齿
        derivatives.GenerateMips();
        turbulence.GenerateMips();
    }

    const int LOCAL_WORK_GROUPS_X = 8;
    const int LOCAL_WORK_GROUPS_Y = 8;

    // Kernel ID
    int KERNEL_INITIAL_SPECTRUM;
    int KERNEL_CONJUGATE_SPECTRUM;
    int KERNEL_TIME_DEPENDENT_SPECTRUMS;
    int KERNEL_RESULT_TEXTURES;

    // Shader 属性 ID
    readonly int SIZE_PROP = Shader.PropertyToID("Size");
    readonly int LENGTH_SCALE_PROP = Shader.PropertyToID("LengthScale");
    readonly int CUTOFF_HIGH_PROP = Shader.PropertyToID("CutoffHigh");
    readonly int CUTOFF_LOW_PROP = Shader.PropertyToID("CutoffLow");

    readonly int NOISE_PROP = Shader.PropertyToID("Noise");
    readonly int H0_PROP = Shader.PropertyToID("H0");
    readonly int H0K_PROP = Shader.PropertyToID("H0K");
    readonly int PRECOMPUTED_DATA_PROP = Shader.PropertyToID("WavesData");
    readonly int TIME_PROP = Shader.PropertyToID("Time");

    readonly int Dx_Dz_PROP = Shader.PropertyToID("Dx_Dz");
    readonly int Dy_Dxz_PROP = Shader.PropertyToID("Dy_Dxz");
    readonly int Dyx_Dyz_PROP = Shader.PropertyToID("Dyx_Dyz");
    readonly int Dxx_Dzz_PROP = Shader.PropertyToID("Dxx_Dzz");
    readonly int LAMBDA_PROP = Shader.PropertyToID("Lambda");

    readonly int DISPLACEMENT_PROP = Shader.PropertyToID("Displacement");
    readonly int DERIVATIVES_PROP = Shader.PropertyToID("Derivatives");
    readonly int TURBULENCE_PROP = Shader.PropertyToID("Turbulence");
}

