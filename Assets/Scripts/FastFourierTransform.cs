using UnityEngine;

/// <summary>
/// GPU 二维 FFT 的 C# 封装。
///
/// 用法：<c>new FastFourierTransform(size, shader)</c> 一次，然后每帧对每个复数场调用
/// <see cref="IFFT2D"/>。内部会用两张大小相同的 RenderTexture 来回倒着算（ping-pong），
/// 所以调用方只需要提供「输入纹理」和「一个空闲的临时纹理」。
///
/// 为什么要 FFT：海面是大量正弦波的叠加，直接在时空域逐波计算是 O(N²)；
/// 在频域生成波长/方向/振幅，再用 IFFT 一次性变回空间域，每帧只要 O(N log N)。
/// </summary>
public class FastFourierTransform
{
    const int LOCAL_WORK_GROUPS_X = 8;
    const int LOCAL_WORK_GROUPS_Y = 8;

    readonly int size;
    readonly ComputeShader fftShader;
    readonly RenderTexture precomputedData; // 预计算的旋转因子 + 下标的查表纹理

    /// <summary>
    /// 创建一张 FFT 用的 RenderTexture。
    /// 必须开启 random write，因为 compute shader 会把结果直接写进去。
    /// </summary>
    public static RenderTexture CreateRenderTexture(int size, RenderTextureFormat format = RenderTextureFormat.RGFloat, bool useMips = false)
    {
        RenderTexture rt = new RenderTexture(size, size, 0, format, RenderTextureReadWrite.Linear);
        rt.useMipMap = useMips;
        rt.autoGenerateMips = false;
        rt.anisoLevel = 6;
        rt.filterMode = FilterMode.Trilinear;
        rt.wrapMode = TextureWrapMode.Repeat;
        rt.enableRandomWrite = true;
        rt.Create();
        return rt;
    }

    public FastFourierTransform(int size, ComputeShader fftShader)
    {
        this.size = size;
        this.fftShader = fftShader;

        // 先查 kernel，再预计算——预计算本身要用到 KERNEL_PRECOMPUTE
        KERNEL_PRECOMPUTE = fftShader.FindKernel("PrecomputeTwiddleFactorsAndInputIndices");
        KERNEL_HORIZONTAL_STEP_FFT = fftShader.FindKernel("HorizontalStepFFT");
        KERNEL_VERTICAL_STEP_FFT = fftShader.FindKernel("VerticalStepFFT");
        KERNEL_HORIZONTAL_STEP_IFFT = fftShader.FindKernel("HorizontalStepInverseFFT");
        KERNEL_VERTICAL_STEP_IFFT = fftShader.FindKernel("VerticalStepInverseFFT");
        KERNEL_SCALE = fftShader.FindKernel("Scale");
        KERNEL_PERMUTE = fftShader.FindKernel("Permute");

        precomputedData = PrecomputeTwiddleFactorsAndInputIndices();
    }

    /// <summary>
    /// 二维正变换。先逐行、再逐列，每轮 log2(size) 个蝶形 pass。
    /// </summary>
    public void FFT2D(RenderTexture input, RenderTexture buffer, bool outputToInput = false)
    {
        int logSize = (int)Mathf.Log(size, 2);
        bool pingPong = false; // false = 这一轮从 input 读到 buffer

        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_FFT, PROP_ID_PRECOMPUTED_DATA, precomputedData);
        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_FFT, PROP_ID_BUFFER0, input);
        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_FFT, PROP_ID_BUFFER1, buffer);
        for (int i = 0; i < logSize; i++)
        {
            pingPong = !pingPong;
            fftShader.SetInt(PROP_ID_STEP, i);
            fftShader.SetBool(PROP_ID_PINGPONG, pingPong);
            fftShader.Dispatch(KERNEL_HORIZONTAL_STEP_FFT, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }

        fftShader.SetTexture(KERNEL_VERTICAL_STEP_FFT, PROP_ID_PRECOMPUTED_DATA, precomputedData);
        fftShader.SetTexture(KERNEL_VERTICAL_STEP_FFT, PROP_ID_BUFFER0, input);
        fftShader.SetTexture(KERNEL_VERTICAL_STEP_FFT, PROP_ID_BUFFER1, buffer);
        for (int i = 0; i < logSize; i++)
        {
            pingPong = !pingPong;
            fftShader.SetInt(PROP_ID_STEP, i);
            fftShader.SetBool(PROP_ID_PINGPONG, pingPong);
            fftShader.Dispatch(KERNEL_VERTICAL_STEP_FFT, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }

        // 最后结果可能停在任意一张纹理上，这里统一到调用方指定的那张
        if (pingPong && outputToInput)
            Graphics.Blit(buffer, input);

        if (!pingPong && !outputToInput)
            Graphics.Blit(input, buffer);
    }

    /// <summary>
    /// 二维逆变换。参数含义：
    /// outputToInput — 结果写回 input（而不是写进 buffer）
    /// scale         — 是否除以 N²（IDFT 的归一化）
    /// permute       — 是否做频域搬移（消除网格拼接接缝）
    /// </summary>
    public void IFFT2D(RenderTexture input, RenderTexture buffer, bool outputToInput = false, bool scale = true, bool permute = false)
    {
        int logSize = (int)Mathf.Log(size, 2);
        bool pingPong = false;

        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_IFFT, PROP_ID_PRECOMPUTED_DATA, precomputedData);
        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_IFFT, PROP_ID_BUFFER0, input);
        fftShader.SetTexture(KERNEL_HORIZONTAL_STEP_IFFT, PROP_ID_BUFFER1, buffer);
        for (int i = 0; i < logSize; i++)
        {
            pingPong = !pingPong;
            fftShader.SetInt(PROP_ID_STEP, i);
            fftShader.SetBool(PROP_ID_PINGPONG, pingPong);
            fftShader.Dispatch(KERNEL_HORIZONTAL_STEP_IFFT, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }

        fftShader.SetTexture(KERNEL_VERTICAL_STEP_IFFT, PROP_ID_PRECOMPUTED_DATA, precomputedData);
        fftShader.SetTexture(KERNEL_VERTICAL_STEP_IFFT, PROP_ID_BUFFER0, input);
        fftShader.SetTexture(KERNEL_VERTICAL_STEP_IFFT, PROP_ID_BUFFER1, buffer);
        for (int i = 0; i < logSize; i++)
        {
            pingPong = !pingPong;
            fftShader.SetInt(PROP_ID_STEP, i);
            fftShader.SetBool(PROP_ID_PINGPONG, pingPong);
            fftShader.Dispatch(KERNEL_VERTICAL_STEP_IFFT, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }

        if (pingPong && outputToInput)
            Graphics.Blit(buffer, input);

        if (!pingPong && !outputToInput)
            Graphics.Blit(input, buffer);

        // 频域符号翻转，让相邻瓦片拼接处不出接缝
        if (permute)
        {
            fftShader.SetInt(PROP_ID_SIZE, size);
            fftShader.SetTexture(KERNEL_PERMUTE, PROP_ID_BUFFER0, outputToInput ? input : buffer);
            fftShader.Dispatch(KERNEL_PERMUTE, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }

        // 逆变换归一化：否则振幅会大 N² 倍
        if (scale)
        {
            fftShader.SetInt(PROP_ID_SIZE, size);
            fftShader.SetTexture(KERNEL_SCALE, PROP_ID_BUFFER0, outputToInput ? input : buffer);
            fftShader.Dispatch(KERNEL_SCALE, size / LOCAL_WORK_GROUPS_X, size / LOCAL_WORK_GROUPS_Y, 1);
        }
    }

    /// <summary>
    /// 启动时跑一次：把每一步蝶形要用的旋转因子和两个输入下标存进一张
    /// logSize × size 的纹理，之后每个 pass 只查表。
    /// </summary>
    RenderTexture PrecomputeTwiddleFactorsAndInputIndices()
    {
        int logSize = (int)Mathf.Log(size, 2);
        RenderTexture rt = new RenderTexture(logSize, size, 0,
            RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        rt.filterMode = FilterMode.Point;
        rt.wrapMode = TextureWrapMode.Repeat;
        rt.enableRandomWrite = true;
        rt.Create();

        fftShader.SetInt(PROP_ID_SIZE, size);
        fftShader.SetTexture(KERNEL_PRECOMPUTE, PROP_ID_PRECOMPUTE_BUFFER, rt);
        fftShader.Dispatch(KERNEL_PRECOMPUTE, logSize, size / 2 / LOCAL_WORK_GROUPS_Y, 1);
        return rt;
    }

    // Kernel ID（用名字查一次，避免每帧查字符串）
    readonly int KERNEL_PRECOMPUTE;
    readonly int KERNEL_HORIZONTAL_STEP_FFT;
    readonly int KERNEL_VERTICAL_STEP_FFT;
    readonly int KERNEL_HORIZONTAL_STEP_IFFT;
    readonly int KERNEL_VERTICAL_STEP_IFFT;
    readonly int KERNEL_SCALE;
    readonly int KERNEL_PERMUTE;

    // Shader 属性 ID
    readonly int PROP_ID_PRECOMPUTE_BUFFER = Shader.PropertyToID("PrecomputeBuffer");
    readonly int PROP_ID_PRECOMPUTED_DATA = Shader.PropertyToID("PrecomputedData");
    readonly int PROP_ID_BUFFER0 = Shader.PropertyToID("Buffer0");
    readonly int PROP_ID_BUFFER1 = Shader.PropertyToID("Buffer1");
    readonly int PROP_ID_SIZE = Shader.PropertyToID("Size");
    readonly int PROP_ID_STEP = Shader.PropertyToID("Step");
    readonly int PROP_ID_PINGPONG = Shader.PropertyToID("PingPong");
}
