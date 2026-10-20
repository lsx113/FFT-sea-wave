# FFT 海洋（Unity 6.3 + URP）

基于 GPU FFT 的实时海面：用 JONSWAP 波谱在频域生成海浪，再用逆 FFT 变换回空间域，
最后由海面着色器把平面网格「吹」成波浪。

原版参考实现：Gasgiant / Scrawk 的 *Ocean*（内置管线 + Surface Shader），
本项目把它移植到 **URP 17**，并重写了注释、修了几个原始 bug。

---

## 快速开始

1. 打开 `Assets/Scenes/SampleScene.unity`
2. 点 Play
3. 想调水面效果：选中 `/Ocean`，改材质 `Assets/Materials/Ocean.mat`
4. 想调海浪形状：选中 `/Waves`，改 `WavesGenerator` 上的参数；
   风速/风向/风程在 `Assets/Settings/WavesSettings.asset`

---

## 文件地图

| 文件 | 作用 |
|------|------|
| `Assets/Scripts/OceanGrid.cs` | 程序化生成正方形海面网格（按「米」配置尺寸与格子大小） |
| `Assets/Scripts/WavesGenerator.cs` | 总调度：建噪声、建三个级联、每帧推进时间、可选 CPU 读回 |
| `Assets/Scripts/WavesCascade.cs` | 单个级联：初始频谱 + 每帧 4 次逆 FFT + 合并纹理 |
| `Assets/Scripts/FastFourierTransform.cs` | GPU 二维 FFT 的 C# 封装（乒乓缓冲 + 旋转因子查表） |
| `Assets/Scripts/WavesSettings.cs` | 波谱参数资产（风速、风向、风程、γ…），换算成 GPU 系数 |
| `Assets/Scripts/OceanSurface.cs` | 把运行时生成的纹理绑定到海面材质上 |
| `Assets/ComputeShaders/InitialSpectrum.compute` | JONSWAP 初始频谱 H0(k) 及其共轭 |
| `Assets/ComputeShaders/TimeDependentSpectrum.compute` | 按时间旋转相位，得到位移/导数四组复数场 |
| `Assets/ComputeShaders/FastFourierTransform.compute` | 水平/垂直蝶形运算、归一化、拼缝处理 |
| `Assets/ComputeShaders/WavesTexturesMerger.compute` | 把 4 组 IFFT 结果合并成位移、斜率、泡沫三张纹理 |
| `Assets/Shaders/Ocean.shader` | URP 海面着色器（顶点位移 + 法线/泡沫/菲涅尔/SSS + 深度 Pass） |

---

## 数据流

```
WavesSettings.asset（风速/风向/风程）
        │  换算成 JONSWAP 系数
        ▼
InitialSpectrum.compute ──► H0(k) 纹理（只算一次）
        │
        ▼  每帧：乘 e^{iωt}
TimeDependentSpectrum.compute ──► 4 组复数场：Dx_Dz / Dy_Dxz / Dyx_Dyz / Dxx_Dzz
        │
        ▼  4 次逆 FFT（水平 → 垂直 → 归一化 → 拼缝）
FastFourierTransform.compute
        │
        ▼
WavesTexturesMerger.compute ──► Displacement / Derivatives / Turbulence 三张纹理
        │
        ▼  每帧绑定到材质
OceanSurface.cs ──► Ocean.shader
        │
        ▼
顶点阶段：三级位移叠加 → 网格起伏
片元阶段：斜率算法线、雅可比算泡沫、菲涅尔、SSS、主光 + 环境光
```

### 为什么要三个级联

一张 256×256 的频谱波长跨度有限：用一张图同时表现 250 m 的涌浪和 1 m 的细浪，
小波会精度不足、闪烁。所以拆成三段波长：

| 级联 | LengthScale | 负责 |
|------|-------------|------|
| 0 | 250 m | 大尺度涌浪 |
| 1 | 17 m | 中尺度风浪 |
| 2 | 5 m | 近处细浪 |

渲染时按到相机的距离给每一级一个权重 `lod = min(LOD_scale × LengthScale / 距离, 1)`，
远处的细节自动淡出。

---

## 常用参数

**`WavesGenerator`（挂在 `/Waves`）**

| 参数 | 含义 |
|------|------|
| `size` | 频谱分辨率，必须是 2 的幂（8~512）。256 是画质/性能的平衡点 |
| `lengthScale0/1/2` | 三个级联的世界尺寸（米），决定了各级负责的波长 |
| `enableHeightReadback` | 每帧把位移读回 CPU，供 `GetWaterHeight()` 查询（有开销） |

**`WavesSettings`（`Assets/Settings/WavesSettings.asset`）**

| 参数 | 含义 |
|------|------|
| `windSpeed` | 10 米高处风速，越大浪越大 |
| `windDirection` | 风向（度），0 = +X，90 = +Z |
| `fetch` | 风程，风持续吹过的距离，越大低频能量越多 |
| `swell` | 方向集中度，越大越像远处传来的长波 |
| `peakEnhancement` | JONSWAP 的 γ，典型值 3.3 |

**`Ocean.mat`**

| 参数 | 含义 |
|------|------|
| `_Color` | 水体颜色 |
| `_ReflectionColor` | 掠射角时的反射色（近似天空） |
| `_FoamScale` / `_FoamBiasLOD*` | 泡沫多少。调大 bias = 更少泡沫 |
| `_LOD_scale` | 细节随距离衰减的快慢 |
| `_ContactFoam` | 岸边接触泡沫，需要场景里有水下地形；纯海面保持 0 |

---

## 相对原版的改动

- **URP 移植**：原版是内置管线的 Surface Shader；这里手写 `ForwardLit` + `DepthOnly` 两个 Pass，
  自行调用 URP 的 `GetMainLight` / `GetShadowCoord` / `SampleSH` / `MixFog`，
  并用 `SampleSceneDepth` 替换 `_CameraDepthTexture`。
- **噪声纹理改为运行时生成**：原版用 `AssetDatabase.CreateAsset` 存成 .asset，
  这里用 Box-Muller 在启动时算一张，去掉了对 `UnityEditor` 的依赖。
- **修复 kernel 顺序 bug**：原版 `FastFourierTransform` 构造函数先调用预计算、后 `FindKernel`，
  在某些 Unity 版本上会拿到无效 kernel，这里调整了顺序。
- **泡沫纹理初始化**：新建浮点纹理是全黑的，而泡沫公式是逐帧累积的，
  不初始化会开局满屏白沫；这里在创建时清成基准值 1。
- **网格材质**：`OceanGrid` 原本每次重建网格都会把材质换回内置的 URP Lit 备胎，
  现在会保留已经挂好的海面材质。
- **岸边泡沫默认关闭**：原版默认 1，但纯海面场景没有水下地形，会整片起沫。

---

## 注意事项

- 频谱纹理边长必须是 **2 的幂**，因为 FFT 是基 2 的。
- 海面网格必须是不带旋转/缩放的轴对齐平面：着色器按世界坐标 XZ 采样位移纹理。
- 网格包围盒在竖直方向留了 ±50 m 余量，否则大浪会把网格挤出视锥被剔除。
- URP Asset 里已勾选 **Depth Texture**（`Assets/Settings/PC_RPAsset.asset`），
  岸边泡沫和深度 Pass 依赖它。
