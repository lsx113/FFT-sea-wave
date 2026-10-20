# FFT 海洋（Unity 6.3 + URP）

基于 GPU FFT 的实时海面：用 JONSWAP 波谱在频域生成海浪，再用逆 FFT 变换回空间域，
最后由海面着色器把平面网格「吹」成波浪。

原版参考实现：Gasgiant / Scrawk 的 *Ocean*（内置管线 + Surface Shader），
本项目把它移植到 **URP 17**，并重写了注释、修了几个原始 bug。

---

## 快速开始

1. 打开 `Assets/Scenes/SampleScene.unity`
2. 点 Play
3. 想调参数：选中 `/Ocean`，在 `OceanTuning` 组件上拖滑条，海面会立刻跟着变
   （波形、水色、高光、泡沫都在里面）
4. 想在场景里飞着看：Play 之后用 `Main Camera` 上的 `FreeCamera`

> 运行时拖动 `OceanTuning` 改的只是内存里的值，退出 Play 就还原。
> 调满意了在组件右上角菜单点 **「保存参数到资产」**，或者照抄到 Inspector 里。

### 自由相机操作

| 按键 | 作用 |
|------|------|
| 按住鼠标右键拖动 | 转视角（按住时锁定光标，松开释放） |
| `W` / `A` / `S` / `D` | 前进 / 左移 / 后退 / 右移（沿视线，抬头就一直往上飞） |
| `E` / `Q`、`Space` / `Ctrl` | 上升 / 下降 |
| 按住 `Shift` | 加速 |
| 滚轮 | 调基础飞行速度 |

屏幕左上角有速度 / 坐标 / 操作提示。海面 clipmap 跟着相机走，飞到哪儿近处都有细网格。

---

## 文件地图

| 文件 | 作用 |
|------|------|
| `Assets/Scripts/OceanGrid.cs` | 程序化生成正方形海面网格（按「米」配置尺寸与格子大小） |
| `Assets/Scripts/OceanGeometry.cs` | 运行时拼出可见海面：多层 clipmap 瓦片 + 补缝的 collar 环，跟着相机走的近处细、远处粗 |
| `Assets/Scripts/OceanTuning.cs` | 运行时调参面板（滑条直接写进材质 / 波谱），可一键写回资产 |
| `Assets/Scripts/FreeCamera.cs` | 自由飞行相机 |
| `Assets/Scripts/SkySunSync.cs` | 把主方向光的朝向同步成天空盒里的太阳方向（日落就是靠它） |
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
| `Assets/Shaders/CloudySky.shader` | 程序化天空盒：FBM 值噪声画云 + 日落光晕 / 日盘，不用任何贴图 |

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
| 0 | 380 m | 大尺度涌浪 |
| 1 | 17 m | 中尺度风浪 |
| 2 | 5 m | 近处细浪 |

级联 0 拉大到 380 m 是为了让 1 级（17 m）与 0 级之间有足够多的中间波长，
浪看起来是「一层层涌过来」而不是只有细碎的小浪。

渲染时按到相机的距离给每一级一个权重 `lod = min(LOD_scale × LengthScale / 距离, 1)`，
远处的细节自动淡出。

---

## 日落是怎么做的

场景里没有额外的灯光，只有原本那盏 `Directional Light`：

| 对象 | 设置 |
|------|------|
| `Directional Light` | 旋转 `(9, 195, 0)`，颜色 `(1.0, 0.66, 0.42)`，强度 1.8 |
| `CloudySky.mat`（天空盒） | 天顶偏紫蓝、地平线橙、云被染成暖白；`_SunDiscSize 0.0015`、`_SunGlowPower 20` |
| `Directional Light` 上的 `SkySunSync` | 每帧把 `-transform.forward` 写进天空盒的 `_SunDirection` |

要点：

- `transform.forward` 是**光线传播方向**，太阳在反方向，所以要取反。
- 天空盒着色器不能读 `_MainLightPosition`——那是 URP 在画受光物体时才写的全局量，
  画天空盒时它还是 0，光晕会完全算不出来。所以方向由 `SkySunSync` 从 C# 写进材质。
- 太阳贴近地平线时 `SkySunSync` 会自动压暗光晕与日盘，避免「太阳已经落下天上还挂着一团亮」。
- 想换成别的时段：只改这盏灯的旋转即可，颜色 / 强度 / 天空盒配色跟着微调，海面的反射色也最好一起改。

---

## 常用参数

**`OceanTuning`（挂在 `/Ocean`，Play 模式下调）**

| 参数分组 | 有什么 |
|----------|--------|
| 波形 | `windSpeed` 风速、`windDirection` 风向、`lambda` 水平位移强度、`shortWavesFade` 短波衰减 |
| 水面颜色 | `waterColor` 水体底色、`reflectionColor` 掠射反射色 |
| 高光 / 细节 | `maxGloss` 高光锐度、`roughness` 远处粗糙度、`detailNormalStrength` / `detailScale` 高频碎波法线 |
| 泡沫 | `foamScale` 浓度、`foamBiasLOD0/1/2` 远中近三档阈值（越小泡沫越多）、噪声尺度 / 强度 / 拉丝方向 |

改 `windSpeed` / `windDirection` / `lambda` / `shortWavesFade` 之后会按每秒约 5 次的频率
重算初始频谱（重算比较贵，所以做成节流）。

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
| `_FoamNoiseScale` / `_FoamNoiseStrength` | 泡沫边缘的碎纹，避免泡沫像一块白漆 |
| `_FoamStretch` / `_FoamDirection` | 顺着风向把泡沫拉成条状 |
| `_DetailNormalStrength` / `_DetailScale` | 高频碎波法线，近处水面的「颗粒感」 |
| `_LOD_scale` | 细节随距离衰减的快慢 |
| `_ContactFoam` | 岸边接触泡沫，需要场景里有水下地形；纯海面保持 0 |

> 避坑：**不要在 Play 模式里直接改 `Ocean.mat` 资产或改它的着色器**。
> 材质被重新导入时，运行时塞进去的级联 RenderTexture 引用会失效，
> 海面会突然变成一块平面的灰色，而且 `GetTexture() != null` 还检测不出来。
> `OceanGeometry` 现在每帧都重新绑一次纹理来兜底，但调参还是用 `OceanTuning` 最省事。

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
- `PlayerSettings.runInBackground` 已打开。编辑器失去焦点时默认会停掉播放循环，
  用命令行 / 外部工具截图或改参数时会看到「改了没反应」，实际是根本没在跑。
- 改脚本后要 **先退出 Play 再重新编译**。在 Play 中途热重载会让 `WavesGenerator`
  的级联变成 null，控制台刷 `NullReferenceException`。
