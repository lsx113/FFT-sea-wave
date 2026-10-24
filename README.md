# FFT Ocean — URP 实时海面

基于 **海洋波谱 + 二维逆 FFT** 的实时海面渲染：每帧在 GPU 上用 Compute Shader 生成位移、法线与各阶导数，
着色器端做曲面细分位移、斜率重建法线、菲涅尔反射与含跨帧累积的白浪泡沫。

- 引擎：Unity **6000.3.10f1**，URP **17.3**（Render Graph）
- 全 GPU：CPU 只在初始化时算一次初始频谱，之后每帧 4 段 compute dispatch
- 4 级级联 × 512² = 约 **100 万个波**，成本与波数无关

---

## 特性

| 项 | 说明 |
| --- | --- |
| 频谱 | JONSWAP 频率谱 × Mitsuyasu 方向扩散 × 短波截断 × 级联带通 |
| 时间演化 | `h(k,t) = H0(k)e^{iωt} + conj(H0(−k))e^{−iωt}`，ω 量化到 `2π/200s` 实现无缝循环 |
| 逆变换 | 自写 Cooley-Tukey（按时间抽取），按行、按列两遍，打包复场一次出 4 个实场 |
| 几何 | 曲面细分 + **Domain 阶段**位移，顶点数与细分因子随距离衰减 |
| 法线 | 由 FFT 导出的斜率重建，远离相机时法线随几何一起淡出，损失折进粗糙度 |
| 泡沫 | 雅可比行列式 J + 浪脊遮罩，Compute Shader 内跨帧累积，逐级一张历史纹理 |
| 光照 | 自定义合成：菲涅尔 + 天空反射 + 太阳高光 + 浪尖逆光散射 |
| 相机 | 自由飞行（新输入系统） |

---

## 数据流

```
风浪参数 ──► CS_InitializeSpectrum ──► _InitialSpectrumTexture（512² × 4 层，一次性 / 自愈时重算）

每帧 4 段 compute：
  ① CS_Spectrumtime   时间演化，位移谱 + 导数谱 → 8 层
  ② CS_FFTHorizontal  按行 1D IFFT
  ③ CS_FFTVertical    按列 1D IFFT，写回补 (−1)^{x+y}
  ④ CS_UpdateFoam     J + 浪脊遮罩 + 跨帧递推 → _OceanFoam（512² × 4 层）

着色器：
  vert  仅变换
  hull  细分因子 lerp(TessMax, TessMin, dist/TessDistance)
  domain 用「位移前」的世界坐标查谱，位移 + 距离淡出
  frag  斜率→法线→粗糙度→泡沫合成→菲涅尔/反射/高光→输出
```

级联：`L = 1000 / 250 / 62.5 / 15.625 m`（级间比 4），纹素 `1.95 m ~ 3.05 cm`，最短波长 `6.1 cm`。

---

## 目录结构

```
Assets/
├── CS/
│   └── CS_H0.compute            频谱生成、时间演化、IFFT、泡沫累积
├── 脚本/
│   ├── OceanFFTRenderFeature.cs 建纹理、发全局变量、录 4 段 dispatch
│   ├── OceanFFTController.cs    风浪参数 → 调度初始频谱
│   ├── OceanGrid.cs             生成静态海面网格
│   └── FreeFlyCamera.cs         自由飞行相机
├── 材质/
│   ├── seashader.shader         Hull/Domain/Fragment，位移与着色
│   ├── Ocean.mat
│   └── SunsetSky.mat            项目自有的程序化天空（保证存盘）
├── Editor/
│   ├── OceanSpectrumAutoRepair.cs  H0 清空后自动补算
│   └── InitialSpectrumViewer.cs    查看初始频谱
├── Texture/
│   └── InitialSpectrum_Tex.asset   H0 的 RenderTexture 资产
├── Scenes/SampleScene.unity
└── Settings/PC_Renderer.asset      挂了 OceanFFTRenderFeature
```

> 注意：RenderTexture **内容不落盘**。域重载或资产重导入后 H0 会被清空，海面变成一块没有起伏的蓝布——
> 工程里有四处自愈（控制器 Start、建资源时、非播放模式录命令前、资源导入后）会自动重算。

---

## 运行

1. 用 Unity **6000.3.10f1**（URP 17.3）打开工程；
2. 打开 `Assets/Scenes/SampleScene.unity`，确认 `PC_Renderer` 上挂着 `OceanFFTRenderFeature`；
3. Play。相机操作：右键拖动转视角、`WASD` 平移、`Q/E` 升降、`Shift` 加速、滚轮调速。

---

## 参数速查

**风浪**（场景 `Ocean` → `OceanFFTController`）

| 参数 | 值 | 说明 |
| --- | --- | --- |
| Base Patch Size | 1000 | 最大级联边长 L0 |
| Cascade Ratio | 4 | 级间比（上限 N/2） |
| Wind Speed | 15 | 波高约 ∝ U² |
| Spread Power | 11.5 | 方向扩散指数上限 |

**渲染特性**（`PC_Renderer.asset`）

| 参数 | 值 |
| --- | --- |
| Repeat Time | 200 s |
| Foam Retention / Add | 0.35 / 3.5 |
| Foam Threshold / J Power | 0.55 / 2 |
| Foam Crest Bias / Range | 0.22 / 0.4 |

**材质**（`Ocean.mat`）

| 参数 | 值 |
| --- | --- |
| `_DisplacementScale` | 0.42 |
| `_Roughness` / `_RoughnessBoost` | 0.16 / 1.5 |
| `_TessMax` / `_TessMin` / `_TessDistance` | 7.4 / 1 / 408 |
| `_DisplaceFadeDistance` / `Falloff` | 400 / 1.2 |
| `_FoamEdgePower` / `_FoamDepthAttenuation` | 2.77 / 17.8 |
| `_FoamNoiseScale` / `_FoamNoiseContrast` | 1.54 / 1.31 |

---

## 已知限制

- **泡沫浓度参数与位移强度未完全对齐**：`Foam Choppiness`（0.45）与材质 `_DisplacementScale`（0.42）设计上应一致，
  当前略有偏差，泡沫会出现在实际挤压稍弱的位置。
- **多相机双算**：pass 对 Game 和 Scene 相机各跑一遍，两个视图同时打开时泡沫累积速度翻倍。
- **没有破碎浪**：谱方法是线性的，浪不会翻卷。
- **只做深水**：未实现浅水色散 `ω² = gk·tanh(kh)`。
- **移动端不可用**：依赖曲面细分、计算着色器与较大显存带宽。
- **无逐 pass GPU 计时**：目前只有整体帧率，未用 RenderDoc/Frame Debugger 做 per-pass 基线。

---

## 参考

- Tessendorf, *Simulating Ocean Water*, SIGGRAPH course notes
- GPU Gems 1, Ch.1 *Effective Water Simulation from Physical Models*
- Hasselmann et al., *JONSWAP*, 1973（频率谱）
- Mitsuyasu et al., 1975（方向扩散）
- [gasgiant / FFT-Ocean](https://github.com/gasgiant/FFT-Ocean)：频谱与 IFFT 框架的参考实现（Built-in 管线），
  本工程将其移植到 URP 17 Render Graph，并重写了泡沫链路与着色。
