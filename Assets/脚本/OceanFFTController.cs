using UnityEngine;

public class OceanFFTController : MonoBehaviour
{
    [System.Serializable]
    public class Settings
    {
        [Header("Compute Shader 引用")]
        public ComputeShader oceanComputeShader;

        [Header("海面设置")]
        public float basePatchSize = 1000f;           // 最大级联的物理边长 L0，其余三级由它推算
        public float cascadeRatio = 4f;               // 相邻级联的 L 比值（也是波段分割比），上限 N/2
        public float windSpeed = 15f;                 // 风速
        public Vector2 windDirection = new Vector2(1, 0); // 风向
        public float spreadPower = 11.5f;             // 扩散指数上限 s_max，谱峰处最窄（Mitsuyasu 标准值 11.5）
        [Header("手动引用的纹理")]
        [Tooltip("必须是一个 Dimension 为 2D Array，Slices 为 4，且开启 Enable Random Write 的高精度浮点纹理！")]
        public RenderTexture initialSpectrumTexture;
    }
    public Settings settings=new Settings();
    private int kernelInitSpectrum;
    private float[] lengthScales = new float[4]; // 存放4层级联的 L
    private const int CASCADE_COUNT = 4;
    void Start()
    {

        if (!settings.oceanComputeShader)
        {
            Debug.LogError("请先赋予 Compute Shader ！");
            return;
        }

        if (!settings.initialSpectrumTexture || !settings.initialSpectrumTexture.enableRandomWrite)
        {
            Debug.LogError("纹理未引用，或者没有勾选 Enable Random Write！");
            return;
        }

        // 获取 Kernel 索引
        kernelInitSpectrum = settings.oceanComputeShader.FindKernel("CS_InitializeSpectrum");

        RefreshLengthScales(true);

        // 生成初始频谱 H0
        GenerateInitialSpectrum(true);
    }

    /// <summary>
    /// 重算 H0。RenderPass 每帧都会调（非播放模式），也可以在 Inspector 里手动触发。
    /// 为什么需要：H0 存在一张 RenderTexture 资产里，而 RT 的内容不落盘。
    /// 域重载、脚本重编译、.compute 或这张纹理资产被重新导入、编辑器的 CRT 自动初始化，
    /// 任何一条都会把它清成全 0；而 RenderPass 的资源缓存只在「分辨率或 shader 变了」时
    /// 才重建，所以清空可能发生在资源已就绪之后，之后没人再补 —— 表现就是海面变成一块死板。
    /// H0 的随机相位来自像素索引的哈希，重算是逐位确定的，多算几次不会让画面抖。
    /// </summary>
    /// <param name="verbose">是否打日志。每帧调用时必须关掉，否则 Console 会被刷爆。</param>
    public void RegenerateInitialSpectrum(bool verbose = false)
    {
        if (settings == null || settings.oceanComputeShader == null || settings.initialSpectrumTexture == null)
            return;
        if (!settings.initialSpectrumTexture.enableRandomWrite)
            return; // 没有随机写入权限，Dispatch 会静默失败，重算也没用

        kernelInitSpectrum = settings.oceanComputeShader.FindKernel("CS_InitializeSpectrum");
        RefreshLengthScales(verbose);
        GenerateInitialSpectrum(verbose);
    }

    /// <summary>按 basePatchSize / cascadeRatio 推出 4 个级联的物理边长 L_i。</summary>
    void RefreshLengthScales(bool verbose)
    {
        // 4 层级联的物理边长：L_i = L0 / ratio^i
        // 只给最大级联的 L0 就能推出全部，最上级联的 L 自然算出，不需要单独指定最小值。
        //
        // 为什么这样切出来的波段不重叠：
        //   级联 i 的波数窗口是 [2π/L_i, πN/L_i]，但真正"归它管"的只有
        //   [上一级的 Nyquist, 本级的 Nyquist] = [πN/L_{i-1}, πN/L_i]，
        //   级联 0 例外，它管到底：[2π/L_0, πN/L_0]。
        //   这样的分段首尾相接、既不重叠也不留缝。
        //
        // ratio 的上限是 N/2：要让本级下沿够低、接得住上一级的 Nyquist，
        // 需要 πN/L_{i-1} >= 2π/L_i，即 L_{i-1}/L_i <= N/2。
        float resolution = Mathf.Max(1, settings.initialSpectrumTexture.width);
        float ratio = settings.cascadeRatio < 1.5f ? 4f : settings.cascadeRatio; // 老场景反序列化时新字段可能是 0
        float maxRatio = resolution * 0.5f;
        if (ratio > maxRatio)
        {
            Debug.LogWarning(string.Format(
                "cascadeRatio = {0} 超过上限 N/2 = {1}，相邻级联的波段之间会漏掉一段，已钳制。", ratio, maxRatio));
            ratio = maxRatio;
        }

        for (int i = 0; i < CASCADE_COUNT; i++)
        {
            lengthScales[i] = settings.basePatchSize / Mathf.Pow(ratio, i);
        }

        if (verbose)
        {
            Debug.Log(string.Format(
                "级联 L = {0:F2} / {1:F2} / {2:F3} / {3:F4}（比值 {4}），波长覆盖 {5:F1} m ~ {6:F3} m",
                lengthScales[0], lengthScales[1], lengthScales[2], lengthScales[3],
                ratio, lengthScales[0], 2f * lengthScales[CASCADE_COUNT - 1] / resolution));
        }
    }
    void GenerateInitialSpectrum(bool verbose)
    {
        // 扩散指数上限 s_max。归一化系数现在改成在 shader 里逐像素现算
        // （因为 s 跟着 omega 走，每个像素都不一样），所以这里不再需要预计算 Gamma 比值。
        float s = settings.spreadPower;
        // 1. 传入基础参数 (名字必须与 Shader 里的变量名完全一致)
        settings.oceanComputeShader.SetFloat("spreadPower", s);
        settings.oceanComputeShader.SetInt("_Resolution", settings.initialSpectrumTexture.width);
        settings.oceanComputeShader.SetVector("_LengthScale", new Vector4(lengthScales[0], lengthScales[1], lengthScales[2], lengthScales[3])); // 传入 float 数组
        settings.oceanComputeShader.SetFloat("_Gravity", 9.81f);
        settings.oceanComputeShader.SetFloat("_WindSpeed", settings.windSpeed);

        // 归一化风向
        Vector2 normWindDir = settings.windDirection.normalized;
        settings.oceanComputeShader.SetVector("_WindDir", new Vector4(normWindDir.x, normWindDir.y, 0, 0));

        // 2. 绑定你手动创建的纹理
        settings.oceanComputeShader.SetTexture(kernelInitSpectrum, "_InitialSpectrumTexture", settings.initialSpectrumTexture);

        // 3. 计算线程组数量并 Dispatch
        int threadGroupsX = Mathf.CeilToInt(settings.initialSpectrumTexture.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(settings.initialSpectrumTexture.height / 8.0f);

        settings.oceanComputeShader.Dispatch(kernelInitSpectrum, threadGroupsX, threadGroupsY, 1);

        if (verbose)
            Debug.Log("初始频谱 H0 写入成功！");
    }
}
