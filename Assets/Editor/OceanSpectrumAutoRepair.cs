using UnityEditor;
using UnityEngine;

/// <summary>
/// H0 的自愈：海面突然变成一块平板时，问题通常出在这张纹理上。
///
/// 初始频谱 H0 存在一张 RenderTexture 资产里，而 RT 的内容**不落盘**。
/// 编辑器里几乎任何一次资源导入（保存资产、手动 Refresh、重新编译脚本引起的域重载）
/// 都会把它重置成全 0，于是 CS_Spectrumtime 拿 0 去做时间演化，FFT 出来的位移和斜率
/// 也全是 0 —— 海面就成了一张没有任何起伏的蓝色床单。
///
/// OceanFFTRenderPass 在「重建资源」时会重算一次，但那只覆盖域重载那一类；
/// 资产被单独重新导入时它察觉不到。所以这里在每次导入之后补算一次。
/// 代价只有一次 256×256 的 dispatch，而且 H0 的生成是确定性的（同样的风参数得到同样的谱），
/// 重算不会让海面换一个样子。
/// </summary>
class OceanSpectrumAutoRepair : AssetPostprocessor
{
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
                                       string[] movedAssets, string[] movedFromAssetPaths)
    {
        // 直接在这里 dispatch 是不行的：OnPostprocessAllAssets 跑在导入过程中，
        // 那会儿图形设备可能正忙，也会被后续的域重载打断。推迟到这一轮导入结束再算。
        EditorApplication.delayCall += Repair;
    }

    static void Repair()
    {
        var controller = Object.FindFirstObjectByType<OceanFFTController>();
        if (controller == null)
            return; // 当前场景没有 FFT 海面，什么都不用做

        controller.RegenerateInitialSpectrum();
    }
}
