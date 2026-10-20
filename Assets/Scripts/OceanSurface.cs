using UnityEngine;

/// <summary>
/// 把 <see cref="WavesGenerator"/> 算出来的纹理塞进海洋材质。
///
/// 纹理是运行时创建的 RenderTexture，没法在编辑器里拖到材质槽上，
/// 所以需要这个桥接组件在启动时绑定一次。
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
public class OceanSurface : MonoBehaviour
{
    [Tooltip("海浪生成器。留空会在场景里自动找第一个")]
    [SerializeField] WavesGenerator wavesGenerator;

    [Tooltip("海洋材质。留空则用 MeshRenderer 上当前挂的材质")]
    [SerializeField] Material oceanMaterial;

    Material boundMaterial;
    bool isBound;

    void Start()
    {
        Bind();
    }

    void OnDisable()
    {
        isBound = false;
    }

    public void Bind()
    {
        if (isBound)
            return;

        if (wavesGenerator == null)
            wavesGenerator = FindFirstObjectByType<WavesGenerator>();

        if (wavesGenerator == null || wavesGenerator.Cascade0 == null)
        {
            Debug.LogWarning("[OceanSurface] 找不到可用的 WavesGenerator，海面会保持平面。", this);
            return;
        }

        MeshRenderer renderer = GetComponent<MeshRenderer>();
        boundMaterial = oceanMaterial != null ? oceanMaterial : renderer.sharedMaterial;

        if (boundMaterial == null)
        {
            Debug.LogWarning("[OceanSurface] 没有绑定材质，海面会保持平面。", this);
            return;
        }

        BindTextures(wavesGenerator, boundMaterial);
        isBound = true;
    }

    /// <summary>
    /// 把三级联的 9 张纹理绑到任意一个海面材质上。
    /// 做成静态方法是因为 <see cref="OceanGeometry"/>（clipmap 网格）自己创建子物体和材质，
    /// 也要绑同样的纹理 —— 两边共用一份代码，避免属性名写岔。
    /// </summary>
    public static bool BindTextures(WavesGenerator wavesGenerator, Material material)
    {
        if (wavesGenerator == null || material == null || wavesGenerator.Cascade0 == null)
            return false;

        // 三级联各自三张纹理：顶点位移、斜率、泡沫
        material.SetTexture("_Displacement_c0", wavesGenerator.Cascade0.Displacement);
        material.SetTexture("_Derivatives_c0", wavesGenerator.Cascade0.Derivatives);
        material.SetTexture("_Turbulence_c0", wavesGenerator.Cascade0.Turbulence);

        material.SetTexture("_Displacement_c1", wavesGenerator.Cascade1.Displacement);
        material.SetTexture("_Derivatives_c1", wavesGenerator.Cascade1.Derivatives);
        material.SetTexture("_Turbulence_c1", wavesGenerator.Cascade1.Turbulence);

        material.SetTexture("_Displacement_c2", wavesGenerator.Cascade2.Displacement);
        material.SetTexture("_Derivatives_c2", wavesGenerator.Cascade2.Derivatives);
        material.SetTexture("_Turbulence_c2", wavesGenerator.Cascade2.Turbulence);

        return true;
    }
}
