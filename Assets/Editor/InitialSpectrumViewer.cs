using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 把 _InitialSpectrumTexture（Texture2DArray，4 层级联）逐层显示出来。
/// H0 的数值只有 1e-6 ~ 1e-3 量级，直接按 0~1 显示会是一片黑，
/// 所以这里提供对数曝光和自动归一化两种放大方式。
/// </summary>
public class InitialSpectrumViewer : EditorWindow
{
    private enum Channel { RealR, ImagG, Magnitude, RGComposite }

    [SerializeField] private RenderTexture target;
    [SerializeField] private int slice;
    [SerializeField] private Channel channel = Channel.RealR;
    [SerializeField] private float exposure = 1f;
    [SerializeField] private bool autoFit = true;
    [SerializeField] private bool logScale = true;
    [SerializeField] private float logDecades = 3f;
    [SerializeField] private bool absoluteValue = true;

    private Texture2D preview;
    private Color[][] slices;
    private float[] absMaxPerSlice;
    private bool dataDirty = true;
    private bool previewDirty = true;
    private Vector2 scroll;

    [MenuItem("Window/Ocean/Initial Spectrum Viewer")]
    private static void Open()
    {
        InitialSpectrumViewer window = GetWindow<InitialSpectrumViewer>("初始频谱");
        window.minSize = new Vector2(340, 460);
        if (window.target == null)
            window.target = FindDefaultTarget();
        window.dataDirty = true;
        window.Repaint();
    }

    private static RenderTexture FindDefaultTarget()
    {
        RenderTexture rt = AssetDatabase.LoadAssetAtPath<RenderTexture>("Assets/InitialSpectrum_Tex.asset");
        if (rt != null)
            return rt;

        string[] guids = AssetDatabase.FindAssets("t:CustomRenderTexture");
        for (int i = 0; i < guids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(guids[i]);
            RenderTexture candidate = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (candidate != null && candidate.name.Contains("Spectrum"))
                return candidate;
        }
        return null;
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUI.BeginChangeCheck();
        target = (RenderTexture)EditorGUILayout.ObjectField("目标纹理", target, typeof(RenderTexture), false);
        if (EditorGUI.EndChangeCheck())
            dataDirty = true;

        if (target == null)
        {
            EditorGUILayout.HelpBox("没有指定纹理。把 Assets/InitialSpectrum_Tex.asset 拖到上面的槽位里。", MessageType.Info);
            if (GUILayout.Button("自动找 InitialSpectrum_Tex"))
            {
                target = FindDefaultTarget();
                dataDirty = true;
            }
            return;
        }

        if (!target.IsCreated())
        {
            EditorGUILayout.HelpBox("纹理还没创建（尺寸为 0）。进 Play 模式让 OceanFFTController 跑一次。", MessageType.Warning);
        }

        if (target.dimension != TextureDimension.Tex2DArray)
        {
            EditorGUILayout.HelpBox(
                string.Format("这个纹理的 Dimension 是 {0}，不是 Texture2DArray。\n在 Inspector 里把 Texture Shape 改成 2D Array、Slices 改成 4。", target.dimension),
                MessageType.Warning);
        }

        EditorGUILayout.LabelField("尺寸",
            string.Format("{0} x {1}，{2} 层，格式 {3}", target.width, target.height, target.volumeDepth, target.format));

        EditorGUILayout.Space(6);
        slice = EditorGUILayout.IntSlider("层 (Cascade)", slice, 0, Mathf.Max(0, target.volumeDepth - 1));
        if (GUILayout.Button("全部重读一遍"))
            dataDirty = true;

        EditorGUI.BeginChangeCheck();
        channel = (Channel)EditorGUILayout.EnumPopup("通道", channel);
        logScale = EditorGUILayout.Toggle("对数显示 (推荐)", logScale);
        absoluteValue = EditorGUILayout.Toggle("取绝对值 |x|", absoluteValue);
        using (new EditorGUI.DisabledScope(!logScale))
            logDecades = EditorGUILayout.Slider("对数动态范围 (数量级)", logDecades, 1f, 8f);
        using (new EditorGUI.DisabledScope(logScale || autoFit))
            exposure = EditorGUILayout.Slider("曝光倍率", exposure, 0.000001f, 1000000f);
        using (new EditorGUI.DisabledScope(logScale))
            autoFit = EditorGUILayout.Toggle("自动归一化", autoFit);
        if (EditorGUI.EndChangeCheck())
            previewDirty = true;

        if (dataDirty)
            ReloadData();

        if (previewDirty)
            RebuildPreview();

        EditorGUILayout.Space(6);
        if (GUILayout.Button("导出 PNG（当前层 + 当前通道）"))
            ExportPng();

        EditorGUILayout.Space(6);
        DrawStats();

        EditorGUILayout.Space(6);
        if (preview == null)
            return;

        float size = Mathf.Min(position.width - 20f, position.height - 300f);
        size = Mathf.Max(128f, size);
        Rect rect = GUILayoutUtility.GetRect(size, size);
        rect.x = (position.width - size) * 0.5f;
        EditorGUI.DrawPreviewTexture(rect, preview, null, ScaleMode.ScaleToFit);
    }

    private void DrawStats()
    {
        if (slices == null || slices.Length == 0)
            return;
        int index = Mathf.Clamp(slice, 0, slices.Length - 1);
        Color[] pixels = slices[index];
        if (pixels == null)
            return;

        float minR = float.MaxValue, maxR = float.MinValue;
        float minG = float.MaxValue, maxG = float.MinValue;
        double sumAbsR = 0, sumAbsG = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            float r = pixels[i].r, g = pixels[i].g;
            if (r < minR) minR = r;
            if (r > maxR) maxR = r;
            if (g < minG) minG = g;
            if (g > maxG) maxG = g;
            sumAbsR += Mathf.Abs(r);
            sumAbsG += Mathf.Abs(g);
        }
        double count = pixels.Length;
        EditorGUILayout.LabelField("R(实部)", string.Format("{0:E3} ~ {1:E3}", minR, maxR));
        EditorGUILayout.LabelField("G(虚部)", string.Format("{0:E3} ~ {1:E3}", minG, maxG));
        EditorGUILayout.LabelField("平均 |R| / |G|", string.Format("{0:E3} / {1:E3}", sumAbsR / count, sumAbsG / count));
        EditorGUILayout.LabelField("本层 |值| 最大值", string.Format("{0:E3}", absMaxPerSlice[index]));
    }

    /// <summary>把 4 层逐个读成 CPU 端浮点数组。数组纹理必须先用 SetRenderTarget 指定层，ReadPixels 才读得到那一层。</summary>
    private void ReloadData()
    {
        dataDirty = false;
        if (target == null || target.width <= 0 || target.height <= 0)
        {
            slices = null;
            return;
        }

        int width = target.width;
        int height = target.height;
        int layerCount = Mathf.Max(1, target.volumeDepth);
        slices = new Color[layerCount][];
        absMaxPerSlice = new float[layerCount];

        RenderTexture previous = RenderTexture.active;
        Texture2D scratch = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
        try
        {
            for (int layer = 0; layer < layerCount; layer++)
            {
                Graphics.SetRenderTarget(target, 0, CubemapFace.Unknown, layer);
                scratch.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                scratch.Apply(false, false);

                Color[] pixels = scratch.GetPixels();
                slices[layer] = pixels;

                float maxAbs = 0f;
                for (int i = 0; i < pixels.Length; i++)
                {
                    float magnitude = Mathf.Max(Mathf.Abs(pixels[i].r), Mathf.Abs(pixels[i].g));
                    if (magnitude > maxAbs)
                        maxAbs = magnitude;
                }
                absMaxPerSlice[layer] = maxAbs;
            }
        }
        finally
        {
            RenderTexture.active = previous;
            DestroyImmediate(scratch);
        }

        previewDirty = true;
    }

    private void RebuildPreview()
    {
        previewDirty = false;
        if (slices == null || slices.Length == 0)
            return;

        int index = Mathf.Clamp(slice, 0, slices.Length - 1);
        Color[] source = slices[index];
        if (source == null)
            return;

        int width = target.width;
        int height = target.height;
        if (preview == null || preview.width != width || preview.height != height)
        {
            if (preview != null)
                DestroyImmediate(preview);
            // 最后一个参数 linear 必须是 false：这里的灰度值是要直接给人看的，
            // 标成线性空间的话编辑器会把 0.5 当成线性亮度再转 sRGB，整体明显发白
            preview = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            preview.hideFlags = HideFlags.HideAndDontSave;
        }

        // 自动归一化时用最大幅值做除数，最亮的一格刚好饱和
        float scale = autoFit
            ? 1f / Mathf.Max(absMaxPerSlice[index], 1e-12f)
            : exposure;
        // 对数窗口的顶：autoFit 时正好是本层最大幅值，最亮的一格刚好饱和
        float hi = 1f / Mathf.Max(scale, 1e-20f);

        Color32[] output = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * width;
            int outputRow = (height - 1 - y) * width;   // Texture2D 原点在左下，GUI 绘制原点在左上
            for (int x = 0; x < width; x++)
            {
                float r = source[sourceRow + x].r;
                float g = source[sourceRow + x].g;

                if (channel == Channel.RGComposite)
                {
                    // 一张图里同时看两个分量：红 = |实部|，绿 = |虚部|，两者都强的地方偏黄
                    output[outputRow + x] = new Color32(Level(r, scale, hi), Level(g, scale, hi), 0, 255);
                    continue;
                }

                float raw = channel == Channel.RealR ? r
                          : channel == Channel.ImagG ? g
                          : Mathf.Sqrt(r * r + g * g);

                byte level = Level(raw, scale, hi);
                if (channel == Channel.RealR)
                    output[outputRow + x] = new Color32(level, 0, 0, 255);         // 越大越红，0 全黑
                else if (channel == Channel.ImagG)
                    output[outputRow + x] = new Color32(0, level, 0, 255);         // 越大越绿，0 全黑
                else
                    output[outputRow + x] = new Color32(level, level, level, 255); // 幅值走灰阶
            }
        }

        preview.SetPixels32(output);
        preview.Apply(false, false);
    }

    /// <summary>
    /// 把一个分量的数值映射成 0~255 的亮度：越大越亮，0 全黑。
    /// 默认取绝对值 —— H0 是零均值高斯随机数，正负各占一半，按符号显示只会得到随机黑白噪点，
    /// 看不出能量分布。想要严格「负值画黑」就把 absoluteValue 关掉。
    /// </summary>
    private byte Level(float raw, float scale, float hi)
    {
        float value = absoluteValue ? Mathf.Abs(raw) : Mathf.Max(raw, 0f);
        if (value <= 0f)
            return 0;   // 严格为 0 说明这格没能量（被 fade 衰减到下溢了），直接画黑

        float t;
        if (logScale)
        {
            // 对数窗口锚定在 hi 上，往下 logDecades 个数量级。
            // 早先用固定的 1e-6 ~ 1，而这一层的实际数据只跨 1.5 个数量级，
            // 全被压进中间一小段亮度，整张图看起来均匀一片、分不出高低。
            float lo = hi * Mathf.Pow(10f, -logDecades);
            t = Mathf.Clamp01((Mathf.Log10(Mathf.Max(value, lo)) - Mathf.Log10(lo)) / logDecades);
        }
        else
        {
            t = Mathf.Clamp01(value * scale);
        }

        return (byte)Mathf.RoundToInt(t * 255f);
    }

    /// <summary>把当前这一层按当前色标存成 PNG，方便直接双击打开或贴进文档里对比。</summary>
    private void ExportPng()
    {
        if (preview == null)
            return;

        string directory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
        string path = EditorUtility.SaveFilePanel(
            "导出频谱图",
            directory,
            string.Format("fft_spectrum_slice{0}.png", slice),
            "png");
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            System.IO.File.WriteAllBytes(path, preview.EncodeToPNG());
            Debug.Log("频谱图已导出：" + path);
        }
        catch (System.Exception e)
        {
            Debug.LogError("导出频谱图失败：" + e.Message);
        }
    }

    private void OnDisable()
    {
        if (preview != null)
        {
            DestroyImmediate(preview);
            preview = null;
        }
    }
}
