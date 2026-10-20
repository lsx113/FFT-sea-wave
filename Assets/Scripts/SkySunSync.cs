using UnityEngine;

/// <summary>
/// 把场景里主方向光的朝向同步到天空盒材质的 <c>_SunDirection</c>。
///
/// 为什么不在着色器里直接读 <c>_MainLightPosition</c>：
///   那个变量由 URP 在画「受光物体」时才写进全局常量。画天空盒的时机比它早，
///   那时读到的还是 0，光晕和日盘就全都算不出来（表现为天空一片均匀的橙色）。
///   所以在 C# 里算好方向塞进材质，最省事也最好调试。
///
/// 同时按太阳高度自动压低光晕强度：太阳落下去以后天空只剩余晖，不该还挂着一团亮斑。
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
public class SkySunSync : MonoBehaviour
{
    [Tooltip("主方向光。留空则在场景里找第一个方向光")]
    [SerializeField] Light sun;

    [Tooltip("太阳低于地平线多少度开始完全熄灭光晕")]
    [SerializeField] float fadeBelowHorizon = 6f;

    Vector3 lastDirection = new Vector3(float.MaxValue, 0f, 0f);
    float lastFade = -1f;

    void OnEnable()
    {
        Sync();
    }

    void Update()
    {
        Sync();
    }

    void Sync()
    {
        Material sky = RenderSettings.skybox;
        if (sky == null || !sky.HasProperty("_SunDirection"))
            return;

        if (sun == null)
            sun = FindSun();

        if (sun == null)
            return;

        // transform.forward 是光线的传播方向，指向太阳要取反
        Vector3 direction = -sun.transform.forward;

        // 太阳贴近地平线时把光晕收掉，避免「太阳已经落下但天上还亮着一坨」
        float fade = Mathf.InverseLerp(-fadeBelowHorizon, fadeBelowHorizon * 0.5f, direction.y);

        // 只在变化时写，免得编辑器每帧把材质标脏
        if ((direction - lastDirection).sqrMagnitude < 1e-6f && Mathf.Approximately(fade, lastFade))
            return;

        lastDirection = direction;
        lastFade = fade;

        sky.SetVector("_SunDirection", direction);
        sky.SetFloat("_SunGlowStrength", glowStrength * fade);
        sky.SetFloat("_SunDiscIntensity", discIntensity * fade);
    }

    [Header("亮度（会再乘上太阳高度）")]
    [SerializeField, Range(0f, 4f)] float glowStrength = 1.5f;
    [SerializeField, Range(0f, 8f)] float discIntensity = 2f;

    static Light FindSun()
    {
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light.type == LightType.Directional)
                return light;
        }
        return null;
    }
}
