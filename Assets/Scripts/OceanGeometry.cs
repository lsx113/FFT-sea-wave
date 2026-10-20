using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 跟随相机的「clipmap」海面网格 —— 这是让海面看起来平滑的关键。
///
/// 为什么不用一张均匀网格：
///   海浪顶点是 GPU 按纹理位移的。如果整片海都用同样的格子（比如 2.5 米一格），
///   5 米波长的小浪每两个顶点就跨一个完整波峰，采样不足 → 顶点高度乱跳 →
///   画面里出现一圈圈三角形尖刺（锯齿）。
///   反过来，如果为了近处细腻把整片海都做成 0.1 米一格，那 1000×1000 格就是一百万顶点，
///   而且远处根本看不清，纯浪费。
///
/// clipmap 的思路：像「俄罗斯套娃」一样一层套一层——
///   - 中心块：以相机为中心，格子最细（默认约 0.12 米）
///   - 外面套一圈 Ring：格子边长翻倍、覆盖范围翻倍
///   - 再套一圈，再翻倍……一共 clipLevels 层
///   - 最外面用一块巨大的 Skirt 盖到地平线
/// 相机移动时这些块会按「格子尺寸」对齐吸附（Snap），所以不会看到网格在脚下抖动。
///
/// 拼缝处理：相邻两层格子大小差一倍，内层的两条边每隔一个顶点才和外层对齐，
/// 所以内层边缘的顶点要「两两合并」（代码里的 Seams 标志就是干这个的），
/// 否则边界上会出现裂缝。
///
/// 注意：这个组件在 Play 模式下才生成网格（和原版一致）。
/// 想在编辑器里直接预览，把 [ExecuteAlways] 打开即可（本文件已经默认打开）。
/// </summary>
[DisallowMultipleComponent]
public class OceanGeometry : MonoBehaviour
{
    [Header("引用")]
    [Tooltip("海浪生成器。留空会自动在场景里找")]
    [SerializeField] WavesGenerator wavesGenerator;

    [Tooltip("跟随谁的视角。留空则用主相机")]
    [SerializeField] Transform viewer;

    [Tooltip("海面材质。留空会自动在场景里找使用了 Ocean/Ocean 着色器的材质")]
    [SerializeField] Material oceanMaterial;

    /// <summary>当前用的海面材质。调试面板（<see cref="OceanTuning"/>）改参数要用它。</summary>
    public Material OceanMaterial => oceanMaterial;

    [Header("网格参数")]
    [Tooltip("最内层「一块格子」的参考边长（米）。典型 10~20")]
    [SerializeField] float lengthScale = 15f;

    [Tooltip("中心块每个方向上的格子段数。越大近处越细腻，顶点数平方增长")]
    [SerializeField, Range(1, 40)] int vertexDensity = 30;

    [Tooltip("往外套多少层（每层格子翻倍）。层数越多，看得越远")]
    [SerializeField, Range(0, 8)] int clipLevels = 8;

    [Tooltip("最外层裙边的宽度（以最外层格子为单位）")]
    [SerializeField, Range(0, 100)] float skirtSize = 50f;

    // 所有生成出来的块：中心块 / 一圈圈的 Ring / 围着 Ring 内圈的补缝条（Collar）/ 最外面的 Skirt
    readonly List<Element> rings = new List<Element>();
    readonly List<Element> trims = new List<Element>();
    Element center;
    Element skirt;

    // 三套材质 LOD 变体：0=CLOSE（三级联全用），1=MID（两级），2=只用 c0
    Material[] materials;

    int previousVertexDensity;
    float previousSkirtSize;
    float previousLengthScale;
    int previousClipLevels;
    bool built;
    bool texturesBound;

    void OnEnable()
    {
        RebuildIfNeeded();
    }

    void OnDisable()
    {
        // 子物体是临时生成的（HideFlags.DontSave），停用/卸载时自己清理干净
        ClearChildren();
        built = false;
    }

    void Update()
    {
        RebuildIfNeeded();
        UpdatePositions();
        TryBindTextures();
        UpdateMaterials();
    }

    /// <summary>
    /// 把海浪纹理绑到海面材质上。绑定失败（WavesGenerator 还没初始化）就下一帧再试。
    /// </summary>
    void TryBindTextures()
    {
        if (oceanMaterial == null)
            return;

        // 不能只信一个 bool，也不要靠 GetTexture 判断「纹理还在不在」：
        // 材质被重新导入（改着色器、保存 .mat）时，运行时塞进去的 RenderTexture
        // 引用会变成「已销毁的假 null」，而且旧的引用也可能还挂在那里但已经无效。
        // 一次 SetTexture 只有 9 次调用，干脆每帧都绑，省得追这种时序 bug。
        if (wavesGenerator == null)
            wavesGenerator = FindFirstObjectByType<WavesGenerator>();

        texturesBound = OceanSurface.BindTextures(wavesGenerator, oceanMaterial);
    }

    /// <summary>
    /// 参数变了才重建网格。每帧检查一次，但只有真的变了才动手。
    /// </summary>
    void RebuildIfNeeded()
    {
        if (viewer == null)
            viewer = Camera.main != null ? Camera.main.transform : null;
        if (viewer == null)
            return;

        bool parametersChanged = !built
            || previousVertexDensity != vertexDensity
            || !Mathf.Approximately(previousSkirtSize, skirtSize)
            || !Mathf.Approximately(previousLengthScale, lengthScale)
            || previousClipLevels != clipLevels
            || rings.Count != clipLevels;

        if (!parametersChanged)
            return;

        InstantiateMeshes();

        previousVertexDensity = vertexDensity;
        previousSkirtSize = skirtSize;
        previousLengthScale = lengthScale;
        previousClipLevels = clipLevels;
        built = true;
    }

    /// <summary>
    /// 每帧把各块挪到相机周围，并按格子尺寸对齐。
    ///
    /// 对齐（Snap）很重要：不对齐的话相机一动，网格顶点就会相对海面滑动，
    /// 画面里能看到波浪在「爬行」。对齐之后顶点位置永远落在格子边界上。
    /// </summary>
    void UpdatePositions()
    {
        if (viewer == null || center == null)
            return;

        int k = GridSize();
        int activeLevels = ActiveLodlevels();

        // ---------- 中心块 ----------
        float scale = ClipLevelScale(-1, activeLevels);
        Vector3 previousSnappedPosition = Snap(viewer.position, scale * 2);
        center.Transform.position = previousSnappedPosition + OffsetFromCenter(-1, activeLevels);
        center.Transform.localScale = new Vector3(scale, 1f, scale);

        // ---------- 一圈圈的 Ring 和 Trim ----------
        for (int i = 0; i < clipLevels; i++)
        {
            bool active = i < activeLevels;      // 相机会抬高时，用不到的层直接关掉
            rings[i].Transform.gameObject.SetActive(active);
            trims[i].Transform.gameObject.SetActive(active);
            if (!active)
                continue;

            scale = ClipLevelScale(i, activeLevels);
            Vector3 centerOffset = OffsetFromCenter(i, activeLevels);
            Vector3 snappedPosition = Snap(viewer.position, scale * 2);

            // 补缝条和 Ring 用同一个变换：它是「回」字形，四面都能补，
            // 不用像原版那样按相机跨格的方向旋转 90°/180°。
            trims[i].Transform.position = snappedPosition + centerOffset;
            trims[i].Transform.rotation = Quaternion.identity;
            trims[i].Transform.localScale = new Vector3(scale, 1f, scale);

            rings[i].Transform.position = snappedPosition + centerOffset;
            rings[i].Transform.localScale = new Vector3(scale, 1f, scale);

            previousSnappedPosition = snappedPosition;
        }

        // ---------- 最外面的裙边 ----------
        // 它是一整块巨大的平面，一直铺到远裁剪面附近，负责「地平线」
        scale = lengthScale * 2f * Mathf.Pow(2f, clipLevels);
        skirt.Transform.position = new Vector3(-1f, 0f, -1f) * scale * (skirtSize + 0.5f - 0.5f / GridSize())
                                   + previousSnappedPosition;
        skirt.Transform.localScale = new Vector3(scale, 1f, scale);
    }

    /// <summary>
    /// 相机越高，近处细节越没必要，可以少用几层。
    /// 返回当前需要启用的层数。
    /// </summary>
    int ActiveLodlevels()
    {
        return clipLevels - Mathf.Clamp(
            (int)Mathf.Log((1.7f * Mathf.Abs(viewer.position.y) + 1f) / lengthScale, 2f),
            0, clipLevels);
    }

    /// <summary>第 level 层每个「局部单位」对应的世界尺寸（米）。</summary>
    float ClipLevelScale(int level, int activeLevels)
    {
        return lengthScale / GridSize() * Mathf.Pow(2f, clipLevels - activeLevels + level + 1);
    }

    /// <summary>
    /// 第 level 层相对相机所在的偏移。
    /// 因为每一层是「挖空的环」，所以它要往外推一段距离才不会压住内层。
    /// </summary>
    Vector3 OffsetFromCenter(int level, int activeLevels)
    {
        return (Mathf.Pow(2f, clipLevels)
                + GeometricProgressionSum(2f, 2f, clipLevels - activeLevels + level + 1, clipLevels - 1))
               * lengthScale / GridSize() * (GridSize() - 1) / 2f * new Vector3(-1f, 0f, -1f);
    }

    /// <summary>等比数列求和：b0 + b0·q + … + b0·q^(n2-n1-1)。</summary>
    float GeometricProgressionSum(float b0, float q, int n1, int n2)
    {
        return b0 / (1f - q) * (Mathf.Pow(q, n2) - Mathf.Pow(q, n1));
    }

    /// <summary>一块网格每条边的格子数（中心块是它的两倍）。</summary>
    int GridSize()
    {
        return 4 * vertexDensity + 1;
    }

    /// <summary>
    /// 把世界坐标吸附到 scale 的整数倍上。
    /// 正负半轴用不同取整方向，是为了让「相机刚好在格线上」时不会来回跳。
    /// </summary>
    Vector3 Snap(Vector3 coords, float scale)
    {
        if (coords.x >= 0)
            coords.x = Mathf.Floor(coords.x / scale) * scale;
        else
            coords.x = Mathf.Ceil((coords.x - scale + 1f) / scale) * scale;

        if (coords.z < 0)
            coords.z = Mathf.Floor(coords.z / scale) * scale;
        else
            coords.z = Mathf.Ceil((coords.z - scale + 1f) / scale) * scale;

        coords.y = 0f;
        return coords;
    }

    // ==================================================================
    //  网格生成
    // ==================================================================

    /// <summary>销毁旧的子物体，重新造一整套网格。</summary>
    void InstantiateMeshes()
    {
        ClearChildren();

        rings.Clear();
        trims.Clear();

        if (viewer == null)
            viewer = Camera.main != null ? Camera.main.transform : null;

        if (oceanMaterial == null)
            oceanMaterial = FindOceanMaterial();
        if (oceanMaterial == null)
        {
            Debug.LogError("[OceanGeometry] 没有找到海面材质（Ocean/Ocean 着色器），网格不会生成。", this);
            return;
        }

        // 把三级联的位移/斜率/泡沫纹理绑到材质上（和 OceanSurface 用的是同一套逻辑）。
        // 注意：这里可能绑不上 —— 脚本的 OnEnable 执行顺序不保证 WavesGenerator 已经跑过 Awake，
        // 所以真正绑定时在 Update 里重试，直到成功为止。
        if (wavesGenerator == null)
            wavesGenerator = FindFirstObjectByType<WavesGenerator>();
        texturesBound = false;
        TryBindTextures();

        // 三套材质 LOD 变体：远瓦片只算 c0，省采样也省得远处的细浪闪烁
        materials = new Material[3];
        materials[0] = new Material(oceanMaterial) { name = oceanMaterial.name + " (CLOSE)" };
        materials[1] = new Material(oceanMaterial) { name = oceanMaterial.name + " (MID)" };
        materials[2] = new Material(oceanMaterial) { name = oceanMaterial.name + " (FAR)" };
        ApplyLodKeywords();
        for (int i = 0; i < 3; i++)
            OceanSurface.BindTextures(wavesGenerator, materials[i]);

        int k = GridSize();

        // 中心块：2k × 2k，所有边都要合缝（它四面都被 Ring 包着）
        center = InstantiateElement("Center", CreatePlaneMesh(2 * k, 2 * k, 1f, Seams.All));

        // Ring 和补缝条的网格只造一次，各层共用（靠 object scale 放大缩小）
        Mesh ringMesh = CreateRingMesh(k, 1f);
        Mesh collarMesh = CreateCollarMesh(k, 1f);
        for (int i = 0; i < clipLevels; i++)
        {
            rings.Add(InstantiateElement("Ring " + i, ringMesh));
            trims.Add(InstantiateElement("Collar " + i, collarMesh));
        }

        // 最外层裙边：盖到地平线
        skirt = InstantiateElement("Skirt", CreateSkirtMesh(k, skirtSize));
    }

    /// <summary>
    /// 每帧：把海面材质的属性同步到三个 LOD 变体，再按「这块瓦片离相机多远」挑一个用。
    ///
    /// 为什么挑材质而不是在着色器里按距离分支：
    /// 一个瓦片里所有像素属于同一级，用材质做分支最省 —— 远的瓦片根本不去采样 c1/c2。
    /// </summary>
    void UpdateMaterials()
    {
        if (materials == null || oceanMaterial == null)
            return;

        bool canBind = wavesGenerator != null && wavesGenerator.Cascade0 != null;

        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] == null)
                continue;

            materials[i].CopyPropertiesFromMaterial(oceanMaterial);

            // 瓦片材质是独立实例，复制过来的纹理引用可能是失效的（见 TryBindTextures），
            // 所以复制完再自己绑一次，保证每块瓦片都拿到当前有效的级联纹理。
            if (canBind)
                OceanSurface.BindTextures(wavesGenerator, materials[i]);
        }

        // CopyPropertiesFromMaterial 不会复制关键字，要重新设一遍
        ApplyLodKeywords();

        int activeLevels = ActiveLodlevels();

        if (center != null)
            center.MeshRenderer.sharedMaterial = GetMaterial(clipLevels - activeLevels - 1);

        for (int i = 0; i < rings.Count; i++)
        {
            Material material = GetMaterial(clipLevels - activeLevels + i);
            rings[i].MeshRenderer.sharedMaterial = material;
            trims[i].MeshRenderer.sharedMaterial = material;
        }

        if (skirt != null)
            skirt.MeshRenderer.sharedMaterial = materials[2];
    }

    /// <summary>给三套材质打上各自的着色器关键字（决定它叠加几级海浪）。</summary>
    void ApplyLodKeywords()
    {
        if (materials == null)
            return;

        if (materials[0] != null)
        {
            materials[0].EnableKeyword("CLOSE");
            materials[0].DisableKeyword("MID");
        }
        if (materials[1] != null)
        {
            materials[1].EnableKeyword("MID");
            materials[1].DisableKeyword("CLOSE");
        }
        if (materials[2] != null)
        {
            materials[2].DisableKeyword("MID");
            materials[2].DisableKeyword("CLOSE");
        }
    }

    /// <summary>按层级挑材质：最里面两层用 CLOSE，再往外几层用 MID，更远只用 c0。</summary>
    Material GetMaterial(int lodLevel)
    {
        if (lodLevel - 2 <= 0)
            return materials[0];
        if (lodLevel - 2 <= 2)
            return materials[1];
        return materials[2];
    }

    Material FindOceanMaterial()
    {
        // 找不到就用场景里第一个使用了 Ocean/Ocean 着色器的材质
        foreach (var renderer in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            if (renderer.sharedMaterial != null && renderer.sharedMaterial.shader != null
                && renderer.sharedMaterial.shader.name == "Ocean/Ocean")
                return renderer.sharedMaterial;
        }
        return null;
    }

    /// <summary>
    /// 销毁所有生成出来的子物体和网格。
    /// 运行时 Destroy 是延迟到帧末执行的，所以先 SetActive(false)，
    /// 否则重建的那一帧新旧网格会同时出现、互相打架。
    /// </summary>
    void ClearChildren()
    {
        List<Mesh> oldMeshes = new List<Mesh>();
        List<GameObject> oldChildren = new List<GameObject>();
        foreach (Transform child in GetComponentsInChildren<Transform>(true))
        {
            if (child == transform)
                continue;

            MeshFilter filter = child.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                oldMeshes.Add(filter.sharedMesh);

            child.gameObject.SetActive(false);
            oldChildren.Add(child.gameObject);
        }

        foreach (GameObject child in oldChildren)
        {
            if (Application.isPlaying)
                Destroy(child);
            else
                DestroyImmediate(child);
        }

        foreach (Mesh oldMesh in oldMeshes)
        {
            if (oldMesh == null)
                continue;

            if (Application.isPlaying)
                Destroy(oldMesh);
            else
                DestroyImmediate(oldMesh);
        }

        if (materials != null)
        {
            foreach (Material material in materials)
            {
                if (material == null)
                    continue;

                if (Application.isPlaying)
                    Destroy(material);
                else
                    DestroyImmediate(material);
            }
            materials = null;
        }

        center = null;
        skirt = null;
        rings.Clear();
        trims.Clear();
    }

    Element InstantiateElement(string name, Mesh mesh)
    {
        GameObject go = new GameObject(name);
        go.hideFlags = HideFlags.DontSave;   // 临时物体，不要被写进场景文件
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        mesh.hideFlags = HideFlags.DontSave;
        MeshFilter meshFilter = go.AddComponent<MeshFilter>();
        meshFilter.sharedMesh = mesh;

        MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = oceanMaterial;
        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;   // 海面不投影，省一遍阴影
        meshRenderer.receiveShadows = true;
        meshRenderer.allowOcclusionWhenDynamic = false;           // 海面一直占满屏幕，别做遮挡剔除

        return new Element(go.transform, meshRenderer);
    }

    /// <summary>
    /// 「裙边」：中间一个大洞，外面一圈巨大的边，把 Ring 之外的视野填满。
    /// 由 8 块拼成（4 个角 + 4 条边），否则单个平面的顶点数会不够拉伸。
    /// </summary>
    Mesh CreateSkirtMesh(int k, float outerBorderScale)
    {
        Mesh mesh = new Mesh { name = "Clipmap skirt" };

        Mesh quad = CreatePlaneMesh(1, 1, 1f);
        Mesh hStrip = CreatePlaneMesh(k, 1, 1f);
        Mesh vStrip = CreatePlaneMesh(1, k, 1f);

        Vector3 cornerQuadScale = new Vector3(outerBorderScale, 1f, outerBorderScale);
        Vector3 midQuadScaleVert = new Vector3(1f / k, 1f, outerBorderScale);
        Vector3 midQuadScaleHor = new Vector3(outerBorderScale, 1f, 1f / k);

        CombineInstance[] combine = new CombineInstance[8];

        combine[0].mesh = quad;
        combine[0].transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, cornerQuadScale);

        combine[1].mesh = hStrip;
        combine[1].transform = Matrix4x4.TRS(Vector3.right * outerBorderScale, Quaternion.identity, midQuadScaleVert);

        combine[2].mesh = quad;
        combine[2].transform = Matrix4x4.TRS(Vector3.right * (outerBorderScale + 1f), Quaternion.identity, cornerQuadScale);

        combine[3].mesh = vStrip;
        combine[3].transform = Matrix4x4.TRS(Vector3.forward * outerBorderScale, Quaternion.identity, midQuadScaleHor);

        combine[4].mesh = vStrip;
        combine[4].transform = Matrix4x4.TRS(Vector3.right * (outerBorderScale + 1f)
                                             + Vector3.forward * outerBorderScale,
                                             Quaternion.identity, midQuadScaleHor);

        combine[5].mesh = quad;
        combine[5].transform = Matrix4x4.TRS(Vector3.forward * (outerBorderScale + 1f), Quaternion.identity, cornerQuadScale);

        combine[6].mesh = hStrip;
        combine[6].transform = Matrix4x4.TRS(Vector3.right * outerBorderScale
                                             + Vector3.forward * (outerBorderScale + 1f),
                                             Quaternion.identity, midQuadScaleVert);

        combine[7].mesh = quad;
        combine[7].transform = Matrix4x4.TRS(Vector3.right * (outerBorderScale + 1f)
                                             + Vector3.forward * (outerBorderScale + 1f),
                                             Quaternion.identity, cornerQuadScale);

        mesh.CombineMeshes(combine, true);
        ApplyDisplacementBounds(mesh);
        return mesh;
    }

    /// <summary>
    /// 补缝用的「回」字形边框：四条带子围住 Ring 的内圈。
    ///
    /// 为什么需要它：中心块边长 2k，Ring 的内圈边长 k+1，两者天生差 1 个格子
    /// （2k 是偶数，k+1 是奇数，对齐不了），这条缝会漏出天空盒 —— 看上去就是
    /// 海面上一条土黄色的细缝。原版只补了一个拐角的两条边，另外两条边是漏的；
    /// 这里改成四面都补。
    /// </summary>
    Mesh CreateCollarMesh(int k, float lengthScale)
    {
        Mesh mesh = new Mesh { name = "Clipmap collar" };

        // 每条带子向缝的两侧各覆盖 pad 个格子。为什么必须是 2 而不是 1：
        //   1) 内层块比这一层的内圈小 1 个格子（2k 和 k+1 对不齐），每边天然差 0.5 格；
        //   2) 两层按各自的格子边长吸附（本层吸附步长 = 2 格，内层 = 1 格），
        //      相对位置还会再漂 0 到 0.5 格。
        //   两者相加最多差 1 格。pad = 1 时带子边缘刚好和内层块擦边对齐 ——
        //   相机一动，缝就在 0 ~ 0.5 格之间开合，画面里是一条透出天空色的
        //   「条纹光」在闪。pad = 2 才能保证任何吸附组合下都有 1 格实打实的重叠。
        const int pad = 2;
        const float drop = -0.02f;  // 略微下沉，和相邻块必然重叠的部分不会闪（z-fighting）

        float inner = (k - 1) / 2f;              // 内圈起始坐标
        int stripLength = k + 1 + 2 * pad;       // 带子长度：内圈边长 + 两端各多盖一个拐角
        int thickness = 2 * pad;

        Mesh hStrip = CreatePlaneMesh(stripLength, thickness, lengthScale, Seams.None, 1);
        Mesh vStrip = CreatePlaneMesh(thickness, stripLength, lengthScale, Seams.None, 1);

        float near = (inner - pad) * lengthScale;
        float far = (inner + k + 1 - pad) * lengthScale;

        CombineInstance[] combine = new CombineInstance[4];
        combine[0].mesh = hStrip;
        combine[0].transform = Matrix4x4.TRS(new Vector3(near, drop, near), Quaternion.identity, Vector3.one);

        combine[1].mesh = hStrip;
        combine[1].transform = Matrix4x4.TRS(new Vector3(near, drop, far), Quaternion.identity, Vector3.one);

        combine[2].mesh = vStrip;
        combine[2].transform = Matrix4x4.TRS(new Vector3(near, drop, near), Quaternion.identity, Vector3.one);

        combine[3].mesh = vStrip;
        combine[3].transform = Matrix4x4.TRS(new Vector3(far, drop, near), Quaternion.identity, Vector3.one);

        mesh.CombineMeshes(combine, true);
        ApplyDisplacementBounds(mesh);
        return mesh;
    }

    /// <summary>
    /// 一圈「回」字形的 Ring：由上下两条横带和左右两条竖带拼成。
    /// 内边（靠近相机那边）需要合缝，外边不用。
    /// </summary>
    Mesh CreateRingMesh(int k, float lengthScale)
    {
        Mesh mesh = new Mesh { name = "Clipmap ring" };
        if ((2 * k + 1) * (2 * k + 1) >= 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        CombineInstance[] combine = new CombineInstance[4];

        combine[0].mesh = CreatePlaneMesh(2 * k, (k - 1) / 2, lengthScale, Seams.Bottom | Seams.Right | Seams.Left);
        combine[0].transform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);

        combine[1].mesh = CreatePlaneMesh(2 * k, (k - 1) / 2, lengthScale, Seams.Top | Seams.Right | Seams.Left);
        combine[1].transform = Matrix4x4.TRS(new Vector3(0f, 0f, k + 1 + (k - 1) / 2) * lengthScale, Quaternion.identity, Vector3.one);

        combine[2].mesh = CreatePlaneMesh((k - 1) / 2, k + 1, lengthScale, Seams.Left);
        combine[2].transform = Matrix4x4.TRS(new Vector3(0f, 0f, (k - 1) / 2) * lengthScale, Quaternion.identity, Vector3.one);

        combine[3].mesh = CreatePlaneMesh((k - 1) / 2, k + 1, lengthScale, Seams.Right);
        combine[3].transform = Matrix4x4.TRS(new Vector3(k + 1 + (k - 1) / 2, 0f, (k - 1) / 2) * lengthScale, Quaternion.identity, Vector3.one);

        mesh.CombineMeshes(combine, true);
        ApplyDisplacementBounds(mesh);
        return mesh;
    }

    /// <summary>
    /// 造一块矩形平面网格。
    ///
    /// seams 参数：这条边要和「格子小一半的邻居」对接时，
    /// 边缘顶点只保留偶数编号，相当于把两个顶点并成一个，
    /// 这样邻居那些多出来的顶点正好落在我们的线段上，不会裂开。
    /// </summary>
    Mesh CreatePlaneMesh(int width, int height, float lengthScale, Seams seams = Seams.None, int trianglesShift = 0)
    {
        Mesh mesh = new Mesh { name = "Clipmap plane" };
        if ((width + 1) * (height + 1) >= 65535)
            mesh.indexFormat = IndexFormat.UInt32;

        Vector3[] vertices = new Vector3[(width + 1) * (height + 1)];
        Vector3[] normals = new Vector3[(width + 1) * (height + 1)];
        int[] triangles = new int[width * height * 2 * 3];

        for (int i = 0; i < height + 1; i++)
        {
            for (int j = 0; j < width + 1; j++)
            {
                int x = j;
                int z = i;

                // 合缝边：把奇数编号的顶点「挪」到前一个顶点上
                if ((i == 0 && seams.HasFlag(Seams.Bottom)) || (i == height && seams.HasFlag(Seams.Top)))
                    x = x / 2 * 2;
                if ((j == 0 && seams.HasFlag(Seams.Left)) || (j == width && seams.HasFlag(Seams.Right)))
                    z = z / 2 * 2;

                vertices[j + i * (width + 1)] = new Vector3(x, 0f, z) * lengthScale;
                normals[j + i * (width + 1)] = Vector3.up;
            }
        }

        // 三角形交替方向，避免所有三角形都朝同一方向排成条纹（那样会有明显的网格感）
        int tris = 0;
        for (int i = 0; i < height; i++)
        {
            for (int j = 0; j < width; j++)
            {
                int k = j + i * (width + 1);
                if ((i + j + trianglesShift) % 2 == 0)
                {
                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + width + 2;

                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 2;
                    triangles[tris++] = k + 1;
                }
                else
                {
                    triangles[tris++] = k;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + 1;

                    triangles[tris++] = k + 1;
                    triangles[tris++] = k + width + 1;
                    triangles[tris++] = k + width + 2;
                }
            }
        }

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        ApplyDisplacementBounds(mesh);
        return mesh;
    }

    /// <summary>
    /// 手工把包围盒在竖直方向撑开。
    ///
    /// 网格本身是平的（y = 0），但顶点着色器会把顶点抬高几百毫米。
    /// 如果不撑开，Unity 会按「平的网格」做视锥剔除，相机稍微一斜海面就整块消失。
    /// 这里的数值是「局部单位」，各层会被 object scale 放大，所以给得比较宽松。
    /// </summary>
    static void ApplyDisplacementBounds(Mesh mesh)
    {
        Bounds bounds = mesh.bounds;
        bounds.Expand(new Vector3(0f, 2f * 1000f, 0f));
        mesh.bounds = bounds;
    }

    /// <summary>一块网格（Transform + Renderer）的简单包装。</summary>
    class Element
    {
        public readonly Transform Transform;
        public readonly MeshRenderer MeshRenderer;

        public Element(Transform transform, MeshRenderer meshRenderer)
        {
            Transform = transform;
            MeshRenderer = meshRenderer;
        }
    }

    [System.Flags]
    enum Seams
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
        All = Left | Right | Top | Bottom,
    }
}
