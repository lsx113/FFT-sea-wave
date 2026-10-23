using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// =====================================================================
// FFT 海面：URP 的 Pass 注入
// ---------------------------------------------------------------------
// 分工：
//   OceanFFTController（MonoBehaviour，原脚本，没动）
//       —— 一次性的事：算 L 级联、灌静态参数、跑一次 CS_InitializeSpectrum 得到 H0。
//   OceanFFTRenderFeature（继承 ScriptableRendererFeature）
//       —— 挂在 Renderer 资产上的「入口」，决定这个 pass 每帧跑不跑。
//   OceanFFTRenderPass（继承 ScriptableRenderPass）
//       —— 真正干活：脚本里建纹理、每帧跑「时间演化 → 行 FFT → 列 FFT」、
//          把结果发布成全局纹理给后面的着色器用。
//
// 为什么用 Pass 注入而不是 Update()：
//   1. 时序确定。Update() 和渲染没有先后保证，可能出现「这一帧画海面时用的还是上一帧的数据」，
//      严重时半边屏幕是新半边是旧。
//   2. 次数对齐。一个相机一帧渲染一次，但 Update() 一帧只跑一次。
//      Scene 视图 + Game 视图同时开着时，用 Update() 会算少了。
//   3. 能进 Profiler / Frame Debugger，一眼看出这几次 dispatch 花在哪。
//
// 用法（只需做一次）：
//   选中 Assets/Settings/PC_Renderer.asset → Inspector 底部
//   Add Renderer Feature → OceanFFTRenderFeature。
// =====================================================================

/// <summary>
/// Renderer Feature：URP 会把它当成渲染管线的「插件」，
/// 每帧调用 AddRenderPasses，由我们决定往队列里塞什么 pass。
/// 它继承自 ScriptableRendererFeature，不是 MonoBehaviour，所以不能挂在 GameObject 上，
/// 只能挂到 Renderer 资产里。
/// </summary>
public class OceanFFTRenderFeature : ScriptableRendererFeature
{
    [Header("时间")]
    [Tooltip("无缝循环周期 T（秒）。t = T 时每个波都绕回整数圈，海面回到 t = 0 的状态")]
    public float repeatTime = 200f;

    [Header("泡沫")]
    [Tooltip("泡沫每秒还剩多少比例：0.35 表示 1 秒后剩 35%，0.9 表示能挂很久，0 表示不保留（等于没有累积）")]
    [Range(0f, 0.99f)]
    public float foamRetention = 0.35f;

    [Tooltip("每秒在「挤压最狠」处新增的泡沫量。稳态上限 ≈ 该值 / (−ln 保留率)，" +
             "默认 0.35 的保留率下约等于该值 × 0.95")]
    [Range(0f, 10f)]
    public float foamAdd = 1.5f;

    [Tooltip("雅可比阈值：J 低于它才开始长泡沫。J = 1 是原样，J < 1 是被挤压，" +
             "J < 0 是水面已经折叠（浪要破了）。调大泡沫范围变大")]
    [Range(-1f, 1f)]
    public float foamThreshold = 0f;

    [Tooltip("破碎量的幂次：越高越只认折叠最深的地方，泡沫越集中在硬浪尖")]
    [Range(1f, 8f)]
    public float foamJPower = 2f;

    [Tooltip("逐纹素阈值抖动的幅度：把泡沫边缘打碎，避免大块平滑的白斑。0 = 边缘完全平滑")]
    [Range(0f, 1f)]
    public float foamDetail = 0.25f;

    [Tooltip("水平位移的缩放，必须和材质上的 Displacement Scale 一致，" +
             "否则泡沫的位置和实际被挤压的位置对不上")]
    [Range(0.01f, 3f)]
    public float foamChoppiness = 0.45f;

    [Tooltip("浪脊门槛：坡度（乘以 Choppiness 后的无量纲斜率）低于它的地方不出泡沫。" +
             "只靠雅可比的话，大片缓坡上 J 也会略小于 1，泡沫会铺成一条均匀的白带；" +
             "加这一道之后泡沫只贴着陡的浪脊出现。调小泡沫范围变大")]
    [Range(0f, 2f)]
    public float foamCrestBias = 0.45f;

    [Tooltip("浪脊过渡宽度：坡度从 Bias 到 Bias+Range 之间泡沫从 0 涨到满。越小边缘越硬")]
    [Range(0.01f, 2f)]
    public float foamCrestRange = 0.45f;

    // 这个 pass 由 Create() 建一次，之后每帧复用（不要每帧 new，会一直产生垃圾）
    private OceanFFTRenderPass oceanPass;

    /// <summary>Feature 被启用 / 脚本重编译时调用一次，用来建 pass。</summary>
    public override void Create()
    {
        oceanPass = new OceanFFTRenderPass
        {
            // 排在不透明物体之前：等画海面时，纹理里已经是最新的空域数据
            renderPassEvent = RenderPassEvent.BeforeRenderingOpaques,
        };
    }

    /// <summary>每帧、每个相机都会走这里，决定要不要把 pass 塞进队列。</summary>
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (oceanPass == null)
            return;

        // 只给 Game 和 Scene 视图算。Preview / Reflection / UI 这些相机不需要，
        // 而且它们一帧可能被调很多次，白烧算力。
        var cameraType = renderingData.cameraData.cameraType;
        if (cameraType != CameraType.Game && cameraType != CameraType.SceneView)
            return;

        // 场景里没有控制器（比如切到别的场景了）就跳过，别让 pass 空跑
        if (OceanFFTRenderPass.FindController() == null)
            return;

        oceanPass.repeatTime = repeatTime; // 在 Inspector 里改了立刻生效
        oceanPass.foamRetention = foamRetention;
        oceanPass.foamAdd = foamAdd;
        oceanPass.foamThreshold = foamThreshold;
        oceanPass.foamJPower = foamJPower;
        oceanPass.foamDetail = foamDetail;
        oceanPass.foamChoppiness = foamChoppiness;
        oceanPass.foamCrestBias = foamCrestBias;
        oceanPass.foamCrestRange = foamCrestRange;
        renderer.EnqueuePass(oceanPass);
    }

    /// <summary>Feature 被销毁 / 重编译时调用，把纹理还给显存。</summary>
    protected override void Dispose(bool disposing)
    {
        if (oceanPass != null)
        {
            oceanPass.ReleaseResources();
            oceanPass = null;
        }
    }
}

/// <summary>
/// 真正干活的 pass。继承自 ScriptableRenderPass。
///
/// 它什么都不画（不设置渲染目标、不 DrawMesh），只往 command buffer 里录三次 dispatch。
/// 纹理全部在脚本里创建，不依赖任何手动拖的资产。
/// </summary>
public class OceanFFTRenderPass : ScriptableRenderPass
{
    // ---- 常量，必须和 CS 里保持一致 ----
    private const int CASCADE_COUNT = 4;                // CS_Spectrumtime 里循环 4 次
    private const int SLICES_TOTAL = CASCADE_COUNT * 2; // 每级 2 层：i*2 = 位移，i*2+1 = 斜率
    private const int THREAD_GROUP_XY = 8;              // CS 里 numthreads(8,8,1)
    private const int MAX_RESOLUTION = 512;             // FFT 用 groupshared 装整行，上限 512

    private const string PASS_NAME = "OceanFFT";

    /// <summary>当前活着的 pass 实例。别处想拿结果纹理可以从这取（调试用）。</summary>
    public static OceanFFTRenderPass Active { get; private set; }

    /// <summary>最终的 8 层结果纹理（空域）。</summary>
    public RenderTexture SpectrumTexture { get { return spectrum; } }

    [Tooltip("无缝循环周期，由 Feature 每帧同步过来")]
    public float repeatTime = 200f;

    [Tooltip("泡沫每秒还剩多少比例，由 Feature 每帧同步过来")]
    public float foamRetention = 0.35f;

    [Tooltip("每秒新增的泡沫量，由 Feature 每帧同步过来")]
    public float foamAdd = 1.5f;

    [Tooltip("J 的判定阈值，由 Feature 每帧同步过来")]
    public float foamThreshold = 0f;

    [Tooltip("破碎量的幂次，由 Feature 每帧同步过来")]
    public float foamJPower = 2f;

    [Tooltip("逐纹素阈值抖动幅度，由 Feature 每帧同步过来")]
    public float foamDetail = 0.25f;

    [Tooltip("水平位移缩放，由 Feature 每帧同步过来，需与材质的 Displacement Scale 一致")]
    public float foamChoppiness = 0.45f;

    [Tooltip("浪脊坡度门槛，由 Feature 每帧同步过来")]
    public float foamCrestBias = 0.45f;

    [Tooltip("浪脊坡度过渡宽度，由 Feature 每帧同步过来")]
    public float foamCrestRange = 0.45f;

    // ---- 脚本创建的两张纹理 ----
    private RenderTexture spectrum;     // 8 层：先装频谱，FFT 之后就变成空域的位移/斜率
    private RenderTexture tempSpectrum; // 8 层：行变换的中间结果，做完列变换就没用了

    // ---- 泡沫历史 ----
    // 4 层 = 4 个级联，每层和对应的位移贴图同分辨率、同 uv、同平铺。
    // 泡沫是「跨帧累积」的量，只能活在这里；着色器只负责采样 + 距离衰减。
    // CS_UpdateFoam 就地更新：每个线程只读自己那个纹素、再写回同一个，
    // 线程之间没有读写交集，所以一张图就够，不需要双缓冲和交换。
    private RenderTexture foamState;
    private bool foamNeedsReset = true;  // 刚建好时是垃圾数据，第一帧忽略历史

    // ---- 缓存，避免每帧重复做 ----
    private int resolution = -1;
    private ComputeShader cachedShader;
    private RenderTexture initialSpectrum; // 控制器那张 H0 纹理，每帧当输入绑定
    private int kernelTime = -1;
    private int kernelH = -1;
    private int kernelV = -1;
    private int kernelFoam = -1;
    private bool warnedMissing;

    // 4 级级联的物理边长，跟 _OceanCascadeLength 是同一份数据。
    // CS_UpdateFoam 需要它把「纹素坐标」换算成「世界坐标」，每帧显式喂进去，
    // 不依赖初始化时写进 ComputeShader 的那一份 —— 那份额外的写入在
    // .compute 被重新导入后会丢失（见 CS 里 CS_UpdateFoam 顶部那段说明）。
    private Vector4 cascadeLengths = new Vector4(1000f, 250f, 62.5f, 15.625f);

    /// <summary>Render Graph 路径的数据包：把状态带进 render func 的 lambda。</summary>
    private class PassData
    {
        public OceanFFTRenderPass pass;
        public float elapsedTime;
        public bool foamReset;     // 这一帧是否要丢掉历史
        public float foamDecay;    // 这一帧的保留比例（pow(retention, dt)）
        public float foamAdd;      // 这一帧的增量（rate * dt）
    }

    public OceanFFTRenderPass()
    {
        Active = this;
    }

    // =================================================================
    // 资源准备
    // =================================================================

    /// <summary>在场景里找控制器。Feature 是被资产持有的，拿不到场景引用，只能运行时找。</summary>
    public static OceanFFTController FindController()
    {
        return Object.FindFirstObjectByType<OceanFFTController>();
    }

    /// <summary>
    /// 保证纹理和 kernel 都就绪。
    /// 第一次跑、分辨率变了、或者换了 ComputeShader，都会重建一次。
    /// </summary>
    private bool EnsureResources(OceanFFTController controller)
    {
        var settings = controller.settings;
        ComputeShader cs = settings != null ? settings.oceanComputeShader : null;
        RenderTexture init = settings != null ? settings.initialSpectrumTexture : null;

        if (cs == null || init == null)
        {
            // 只报一次，否则每帧刷屏
            if (!warnedMissing)
            {
                Debug.LogError("[OceanFFT] RenderFeature 需要 OceanFFTController 上的 " +
                               "oceanComputeShader 和 initialSpectrumTexture 都已赋值。");
                warnedMissing = true;
            }
            return false;
        }

        // 分辨率跟着控制器那张初始频谱走，保证两边是同一个 N
        // （两个 kernel 用同一个 _Resolution，不一致的话 FFT 会整个错位）
        int res = Mathf.Clamp(Mathf.ClosestPowerOfTwo(init.width), 64, MAX_RESOLUTION);

        if (spectrum != null && resolution == res && cachedShader == cs)
            return true; // 已经就绪

        ReleaseTextures();
        warnedMissing = false;

        spectrum        = CreateRWArray("Ocean_Spectrum", res, SLICES_TOTAL);
        tempSpectrum    = CreateRWArray("Ocean_TempSpectrum", res, SLICES_TOTAL);
        initialSpectrum = init;
        resolution = res;
        cachedShader = cs;

        // 泡沫历史：和谱纹理同分辨率的 2D Array，每级联一层
        foamState = CreateRWArray("Ocean_Foam", res, CASCADE_COUNT);
        foamNeedsReset = true;

        kernelTime = cs.FindKernel("CS_Spectrumtime");
        kernelH    = cs.FindKernel("CS_FFTHorizontal");
        kernelV    = cs.FindKernel("CS_FFTVertical");
        kernelFoam = cs.FindKernel("CS_UpdateFoam");

        // FFT 那两个 kernel 也要读 _Resolution，这里再写一次，
        // 让本 pass 不依赖控制器 Start() 的执行时机。
        // 注意用 SetFloat：shader 里 _Resolution 声明成 float。
        cs.SetFloat("_Resolution", res);

        // ---- 发布成全局纹理 ----
        // 后面的海面着色器直接声明同名变量就能采样到，不用再通过材质往下传
        // （顶点着色器尤其麻烦，全局纹理是最省事的传递方式）。
        Shader.SetGlobalTexture("_OceanSpectrum", spectrum);
        Shader.SetGlobalTexture("_OceanInitialSpectrum", init);
        Shader.SetGlobalFloat("_OceanResolution", res);
        Shader.SetGlobalInt("_OceanCascadeCount", CASCADE_COUNT);

        // 泡沫历史：每级联一层，着色器按和位移完全相同的 uv 去采
        Shader.SetGlobalTexture("_OceanFoam", foamState);

        // ---- 发布级联边长，海面着色器靠它把 worldXZ 换算成每个级联的 uv ----
        // 控制器里的 lengthScales 是 private，这里按同一套公式重算：
        // L_i = basePatchSize / ratio^i，ratio 同样要钳到 N/2（见 OceanFFTController.Start）。
        // 两边公式必须一致，否则着色器取的 uv 和 CS 里的 k 对不上，海面会整体错位。
        float ratio = settings.cascadeRatio < 1.5f ? 4f : settings.cascadeRatio;
        float maxRatio = res * 0.5f;
        if (ratio > maxRatio)
            ratio = maxRatio;

        float baseSize = settings.basePatchSize;
        cascadeLengths = new Vector4(
            baseSize,
            baseSize / ratio,
            baseSize / (ratio * ratio),
            baseSize / (ratio * ratio * ratio));
        Shader.SetGlobalVector("_OceanCascadeLength", cascadeLengths);

        // 域重载后 RT 资产的内容会丢（RT 不落盘），而控制器的 Start() 又可能
        // 跑在资产加载完成之前，重算的结果被随后的加载覆盖。所以这里在「建资源」
        // 这个只走一次的时机再算一遍，保证 H0 非空 —— 否则整个海面会是一块死板。
        // 代价只有一次 dispatch，而且是重建资源时才有。
        controller.RegenerateInitialSpectrum();

        Debug.Log(string.Format("[OceanFFT] RenderPass 就绪：{0}×{0}，结果纹理 {1} 层。", res, SLICES_TOTAL));
        return true;
    }

    /// <summary>
    /// 建一张 2D Array 的浮点 RenderTexture。
    /// 三个参数缺一不可：dimension = Tex2DArray、volumeDepth = 层数、enableRandomWrite = true。
    /// 少任何一个，CS 里那个 RWTexture2DArray 都会静默绑不上（只在 Frame Debugger 里看得出来）。
    /// </summary>
    private static RenderTexture CreateRWArray(string name, int size, int slices)
    {
        var rt = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
        {
            name = name,
            dimension = TextureDimension.Tex2DArray, // 关键：2D Array，不是普通 2D
            volumeDepth = slices,
            enableRandomWrite = true,                // 关键：compute 里要写它
            useMipMap = false,
            autoGenerateMips = false,
            wrapMode = TextureWrapMode.Repeat,       // FFT 天然周期性，边界要 wrap
            filterMode = FilterMode.Bilinear,        // 顶点采样时平滑插值
            hideFlags = HideFlags.DontSave,          // 运行时资源，别被存进场景
        };
        rt.Create(); // 必须显式 Create，否则 GPU 资源还没分配
        return rt;
    }

    /// <summary>释放纹理。Feature 被销毁时调用，避免显存泄漏。</summary>
    public void ReleaseResources()
    {
        ReleaseTextures();
        resolution = -1;
        cachedShader = null;
        initialSpectrum = null;
        if (Active == this)
            Active = null;
    }

    private void ReleaseTextures()
    {
        ReleaseRT(ref spectrum);
        ReleaseRT(ref tempSpectrum);
        ReleaseRT(ref foamState);
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null)
            return;

        rt.Release();
        // 注意要写 Object. 前缀：ScriptableRenderPass 不是 UnityEngine.Object 的子类，
        // 这里没有实例方法可以调
        if (Application.isPlaying)
            Object.Destroy(rt);
        else
            Object.DestroyImmediate(rt); // 编辑器里不用 DestroyImmediate 会留下泄漏的贴图
        rt = null;
    }

    // =================================================================
    // 录命令（两条路径共用）
    // =================================================================

    /// <summary>
    /// 把这一帧要跑的 3 个 dispatch 录进 command buffer。
    /// 顺序绝对不能变：时间演化 → 行 FFT → 列 FFT。
    /// 三步之间有数据依赖，录进同一个 buffer 才能保证 GPU 按顺序执行。
    /// </summary>
    /// <param name="cmd">原生 CommandBuffer</param>
    /// <param name="elapsedTime">当前循环周期内的时间 [0, repeatTime)。<b>不是 deltaTime</b>，喂 delta 相位会乱跳。</param>
    /// <param name="foamReset">这一帧是否丢掉泡沫历史（刚建资源、分辨率变了时为 true）</param>
    /// <param name="foamDecay">这一帧的保留比例</param>
    /// <param name="foamAdd">这一帧的增量</param>
    private void RecordFrameCommands(CommandBuffer cmd, float elapsedTime,
                                     bool foamReset, float foamDecay, float foamAdd)
    {
        if (spectrum == null || cachedShader == null || initialSpectrum == null)
            return;

        var cs = cachedShader;
        var init = initialSpectrum;

        int groups = Mathf.CeilToInt(resolution / (float)THREAD_GROUP_XY);

        // ---- 1. 时间演化：把 H0 推进到当前时刻，顺便算出位移谱和斜率谱 ----
        cmd.SetComputeFloatParam(cs, "_FrameTime", elapsedTime);
        cmd.SetComputeFloatParam(cs, "_RepeatTime", Mathf.Max(0.001f, repeatTime));
        cmd.SetComputeTextureParam(cs, kernelTime, "_InitialSpectrumTexture", init);
        cmd.SetComputeTextureParam(cs, kernelTime, "_SpectrumTexture", spectrum);
        cmd.DispatchCompute(cs, kernelTime, groups, groups, 1);

        // ---- 2. 行变换：spectrum → tempSpectrum ----
        // x 方向只发 1 组（组内 256 个线程一起处理一整行），
        // y 方向 N 组（每行一组），z 方向 8 层。
        cmd.SetComputeTextureParam(cs, kernelH, "_SpectrumTexture", spectrum);
        cmd.SetComputeTextureParam(cs, kernelH, "_TempSpectrumTexture", tempSpectrum);
        cmd.DispatchCompute(cs, kernelH, 1, resolution, SLICES_TOTAL);

        // ---- 3. 列变换：tempSpectrum → spectrum ----
        // 写回时顺手补上 (-1)^(x+y)（谱是中心化存的，见 CS 里的说明），
        // 这一步做完 spectrum 里就是空域的位移 / 斜率了。
        cmd.SetComputeTextureParam(cs, kernelV, "_SpectrumTexture", spectrum);
        cmd.SetComputeTextureParam(cs, kernelV, "_TempSpectrumTexture", tempSpectrum);
        cmd.DispatchCompute(cs, kernelV, 1, resolution, SLICES_TOTAL);

        // ---- 4. 泡沫：每级联算 J，再做跨帧累积 ----
        // 必须排在列变换之后：读的就是上一步刚写出来的空域位移和斜率。
        // z 方向一次发 4 组，正好对上 4 个级联；每层只碰自己那两块 slice。
        if (kernelFoam >= 0 && foamState != null)
        {
            cmd.SetComputeVectorParam(cs, "_LengthScale", cascadeLengths);
            cmd.SetComputeFloatParam(cs, "_FoamDecay", foamDecay);
            cmd.SetComputeFloatParam(cs, "_FoamAdd", foamAdd);
            cmd.SetComputeFloatParam(cs, "_FoamThreshold", foamThreshold);
            cmd.SetComputeFloatParam(cs, "_FoamJPower", foamJPower);
            cmd.SetComputeFloatParam(cs, "_FoamDetail", foamDetail);
            cmd.SetComputeFloatParam(cs, "_FoamChoppiness", foamChoppiness);
            cmd.SetComputeFloatParam(cs, "_FoamCrestBias", foamCrestBias);
            cmd.SetComputeFloatParam(cs, "_FoamCrestRange", foamCrestRange);
            cmd.SetComputeFloatParam(cs, "_FoamReset", foamReset ? 1f : 0f);
            cmd.SetComputeTextureParam(cs, kernelFoam, "_SpectrumTexture", spectrum);
            cmd.SetComputeTextureParam(cs, kernelFoam, "_FoamTexture", foamState);
            cmd.DispatchCompute(cs, kernelFoam, groups, groups, CASCADE_COUNT);
        }
    }

    // =================================================================
    // Render Graph 路径（Unity 6 / URP 17 的标准做法）
    // -----------------------------------------------------------------
    // 不再覆盖 Execute：那套接口在 URP 17 已经 [Obsolete]，
    // 而且只在「兼容模式」下才会被调用。本项目的
    // UniversalRenderPipelineGlobalSettings 里 m_EnableRenderCompatibilityMode = 0，
    // 也就是 Render Graph 是开着的，走的就是这里。
    // =================================================================
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        var controller = FindController();
        if (controller == null || !EnsureResources(controller))
            return;

        // 每次录命令前都补一次 H0（非播放模式）。
        // 上面那段资源的缓存条件只认「分辨率 / ComputeShader 变了」，而 H0 被清空
        // 这件事（域重载、资产重新导入、CustomRenderTexture 自身的重新初始化）跟这些
        // 条件完全无关，缓存命中时它照样会变成全 0，之后没人再补 —— 海面就成了死板。
        // 清空的时机和我们的渲染时机之间没有任何契约，所以不能靠「建资源时补一次」，
        // 也不能靠帧号节流（一帧里可能已经清过了）。干脆每次录命令都补：
        // H0 的随机相位由像素索引哈希得出，重算逐位一致，多算不会让画面抖；
        // 一次 512×512×4 的 dispatch 代价可以忽略。播放模式下 Start() 已经算好，不用重复。
        if (!Application.isPlaying)
            controller.RegenerateInitialSpectrum();

        using (var builder = renderGraph.AddUnsafePass<PassData>(PASS_NAME, out var passData))
        {
            passData.pass = this;
            // 只把「当前周期内」的时间喂给 shader：t ∈ [0, repeatTime)。
            // 为什么取模不改画面：shader 里 ω 已被量化成 w0 = 2π/T 的整数倍，
            // 所以 t 和 t - mT 只差相位 2π 的整数倍，e^{i·} 完全相等。
            // 好处是相位被锁在一个周期内，跑再久也不会因为 float 精度掉而抖。
            // 用 timeAsDouble 取模：Time.time 本身是 float，跑几十小时后它自己就有
            // 量化误差（步长已经到毫秒级），先取模再把结果压回 float 才真正无损。
            // 这里的 Math 要写全 System.Math：本文件没 using System，
            // 加了反而会让下面的 Object.Destroy 和 System.Object 撞名。
            double period = System.Math.Max(0.001, repeatTime);
            double cycleTime = Time.timeAsDouble - System.Math.Floor(Time.timeAsDouble / period) * period;
            passData.elapsedTime = (float)cycleTime;

            // Inspector 里的值是「每秒」的口径，这里换算到「这一帧」。
            // 保留率用 pow(r, dt)：30 帧和 144 帧下衰减速度一致。
            // 增量用 rate * dt：同样与帧率无关。
            // 两者合起来的稳态是  add / (−ln retention)，改帧率不会改变泡沫浓度。
            // dt 夹住：编辑器卡一下、或者从暂停恢复时，不能让泡沫一步跳完。
            float dt = Mathf.Clamp(Time.deltaTime, 0f, 0.1f);
            passData.foamReset = foamNeedsReset;
            passData.foamDecay = Mathf.Pow(Mathf.Clamp(foamRetention, 1e-4f, 1f), dt);
            passData.foamAdd = Mathf.Max(0f, foamAdd) * dt;
            foamNeedsReset = false; // 只在录命令这一步清掉，render func 是之后才跑的

            // 这个 pass 没有渲染目标、也不读写 Render Graph 管理的资源。
            // 不显式关掉剔除的话，它会被当成「空 pass」整个优化掉，一帧都不跑。
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((PassData data, UnsafeGraphContext context) =>
            {
                // Render Graph 给的是一层包装过的 buffer，
                // 取出原生的 CommandBuffer 才好调 SetComputeTextureParam / DispatchCompute
                var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                data.pass.RecordFrameCommands(cmd, data.elapsedTime,
                                              data.foamReset, data.foamDecay, data.foamAdd);
            });
        }
    }

}
