# FFT-sea-wave

Unity **6000.3.10f1** / URP **17.3**（Render Graph）。基于**海洋波谱 + 二维逆 FFT** 的实时海面：
每帧在 GPU 上用 Compute Shader 求解一次波动方程的解，着色器端做曲面细分位移、斜率重建法线、菲涅尔反射与含跨帧累积的白浪泡沫。

## FFT 海面技术

### 1. 技术方案

海面不是靠"模拟流体"做出来的，而是**用统计海洋学的方式描述一片统计意义上的海**：把水面看成大量正弦波的叠加，用风浪频率谱（JONSWAP）× 方向扩散函数（Mitsuyasu）给出每个波数 `k` 上的能量，就得到了初始的复振幅 `H0(k)`；再按深水色散关系 `ω = √(g|k|)` 让每个波随时间转相位，得到 `h(k,t)` 的频域场；对这个场做一次**二维逆 FFT**，就同时得到了整张网格上的高度、水平位移和它们的各阶偏导数。关键收益是**成本与波数无关**：`N²` 个波只需要 `O(N log N)` 的蝶形运算，4 级级联 × 512² ≈ 52 万个独立的波，每帧 4 次 dispatch 就全算完了。最后在着色器里用曲面细分补出顶点密度，domain 阶段按世界坐标查谱做位移，片元阶段用 FFT 顺带导出的偏导数直接算法线、算挤压、合成泡沫，再用一套**自定义光照（不是 PBR）**把天空反射、水体散射和太阳高光拼成海水的颜色。

整条链路除了最初的一次谱生成，全部在 GPU 上完成，CPU 每帧只发 4 条 dispatch + 十来个 `SetGlobal`。

### 2. 技术步骤

整体数据流

```
风浪参数（风速 U / 风向 / L0 / 级间比 / 循环周期 T）
  └─ CS_InitializeSpectrum（一次性，参数变化或 H0 被清空时重算）
       └─ _InitialSpectrumTexture  512² × 4 层 RGBA32F
            │   每层 = float4( H0(k) , conj(H0(−k)) )，一个像素两个复场
            ▼
        每帧 4 段 compute：
          ① CS_Spectrumtime     h(k,t) = H0 e^{iωt} + conj(H0(−k)) e^{−iωt}
                                 → 位移 / 导数共 8 个复场，打包成 8 层
          ② CS_FFTHorizontal    按行 1D IFFT（每行一个线程组，256 线程）
          ③ CS_FFTVertical      按列 1D IFFT，写回补 (−1)^{x+y}
          ④ CS_UpdateFoam       雅可比 J → 浪脊遮罩 → 跨帧递推
            ▼
        _OceanSpectrum 512² × 8 层（= 32 个实场）/ _OceanFoam 512² × 4 层
            ▼
        seashader：
          vert   只做坐标变换
          hull   只算细分因子（距离越远越小）
          domain 用「位移前」的世界 XZ 查谱 → 位移，按距离整体压平
          frag   斜率 → 法线 / 粗糙度 → 泡沫（换档 + 距离衰减 + 絮状遮罩）
                 → 菲涅尔 / 天空反射 / 太阳高光 / 浪尖透光
            ▼
        屏幕上出现一片起伏、会在浪尖起白沫、远处融进天空的海
```

#### 2.1 场景对象一览

| 对象                          | 组件                            | 关键配置                                                                                                                               |
| --------------------------- | ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `Ocean`                     | Transform                     | 放在原点，水平面在 `y = 0`（着色器按世界 XZ 采样，网格不能旋转）                                                                                             |
|                             | `OceanGrid`                   | `sizeMeters = 500`、`cellSizeMeters = 0.5`；实际分辨率被 `MAX_RESOLUTION` 钳到 512²，等效格边长 0.98 m                                             |
|                             | `MeshFilter` / `MeshRenderer` | Mesh 资产 `Ocean Grid`（263,169 顶点 / 524,288 三角形）；材质 `Ocean.mat`                                                                      |
|                             | `OceanFFTController`          | `basePatchSize = 1000`、`cascadeRatio = 4`、`windSpeed = 15`、`windDirection = (1,0)`、`spreadPower = 11.5`；引用 `CS_H0.compute` 与 H0 纹理 |
| `Main Camera`               | `FreeFlyCamera`               | 右键拖动转视角、`WASD` 平移、`Q/E` 升降、`Shift` 加速                                                                                              |
| `Directional Light`         | Light                         | 低角度太阳，海面的高光与浪尖透光都靠它                                                                                                                |
| `Global Volume`             | Volume                        | `SampleSceneProfile`：Bloom / Vignette / Tonemapping 启用（Motion Blur 未启用）。水面是 HDR 的，靠 Bloom 出"太阳在海面上的那条光路"                           |
| RenderSettings              | Skybox                        | `SunsetSky.mat`（程序化天空，反射探针缺失时 `GlossyEnvironmentReflection` 会退回这张天空盒生成的立方图）                                                        |
| `PC_Renderer.asset`         | `OceanFFTRenderFeature`       | 频谱/泡沫参数、循环周期 200 s（**不在场景里，挂在 Renderer 资产上**）                                                                                      |
| `InitialSpectrum_Tex.asset` | RenderTexture                 | 512² × 4 层、ARGBFloat、开启 Random Write，H0 的落点                                                                                        |

#### 2.2 初始频谱：从风浪参数到 H0

`Assets/CS/CS_H0.compute` 里的 `ComputeH0` 是整条链路的唯一输入。它做的事情是：给定像素对应的波数 `k`、风速、风向，算出这个波应该有多大的振幅。

```hlsl
float2 ComputeH0(uint2 pid, uint cascade)
{
    float L = _LengthScale[cascade];
    float deltaK = 2.0f * PI / L;
    float2 p = float2(pid) - (_Resolution / 2.0f);      // 中心化：k = 0 落在像素 (N/2, N/2)
    float2 k = p * deltaK;
    float kLength = length(k);
    if (kLength < 1e-5) return float2(0.0, 0.0);

    float omega       = Dispersion(kLength);            // ω = √(g|k|)
    float kAngle      = atan2(k.y, k.x);
    float dOmegadk    = DispersionDerivative(kLength);

    float peakOmega   = 0.877 * _Gravity / _WindSpeed;  // JONSWAP 峰值频率只由风速决定
    float S_omega     = JONSWAP(omega, peakOmega, 3.3, 0.0081);
    float windAngle   = atan2(_WindDir.y, _WindDir.x);
    float D_theta     = DirectionSpectrum(kAngle, windAngle, omega, peakOmega, spreadPower);

    float k_nyquist   = PI * _Resolution / L;
    float fadeStart   = k_nyquist * 0.8f;
    float shortWaveFade = exp(-pow(kLength / fadeStart, 2.0));   // 短波截断

    int   ci       = (int)cascade;
    float prevL    = _LengthScale[max(ci - 1, 0)];
    float kBandMin = (ci > 0) ? (PI * _Resolution / prevL) : 0.0;
    float bandPass = (kBandMin > 0.0) ? smoothstep(kBandMin * 0.75, kBandMin * 1.25, kLength) : 1.0;

    float JONSSpectrum = S_omega * D_theta * shortWaveFade * bandPass;
    float jacobian     = abs(dOmegadk) / kLength;   // 频率谱 → 波数谱的雅可比 |dω/dk| / k
    float areaTerm     = deltaK * deltaK;           // 离散积分的权重
    float energyTerm   = JONSSpectrum * jacobian * areaTerm;

    float2 gauss = GaussianRandom(pid + uint2(cascade * 100, cascade * 200));
    return gauss * sqrt(energyTerm / 2.0);
}
```

几个容易写错、但错了画面立刻不对的步骤：

| 步骤   | 做法                                            | 为什么                                                                          |
| ---- | --------------------------------------------- | ---------------------------------------------------------------------------- |
| 谱的变量 | JONSWAP 给的是 `S(ω)`，要的是 `S(k)`                 | 换变量要乘雅可比 `abs(dω/dk) / k`（二维极坐标的面积元 `k dk dθ`），漏了它波高的频率分布整条歪掉                |
| 振幅   | `H0 = gauss · √(energyTerm / 2)`              | 高斯随机数的实部/虚部各是 `N(0,1)`，两个自由度平分能量，所以每轴开方后要除 2；**没有额外的 ×2**，H0 里已经算的是"每个实场的能量" |
| 短波截断 | `exp(−(k/k_nyquist/0.8)²)`                    | 网格采样不到的高频必须有能量但渲染不出来，硬留着就会在 Nyquist 附近闪烁（`sinc` 旁瓣），指数窗把能量平滑压掉               |
| 级联带通 | `smoothstep(kBandMin·0.75, kBandMin·1.25, k)` | 4 级级联的波段必须首尾相接且**不重叠**。重叠会让同一段波长被两个级联各算一遍，总方差被放大 → 浪高整体偏高、泡沫阈值全部失效           |
| 随机相位 | `GaussianRandom(pid + 级联偏移)`                  | 相位必须是逐像素确定的伪随机：重算 H0 要逐位可复现（见下面的自愈说明），而每级联加不同偏移是为了让 4 个级联不共用同一张随机图           |

最后一步把 `H0(k)` 和 `conj(H0(−k))` 一起写进同一个像素，靠镜像像素 `(N,N) − id` 取共轭：

```hlsl
[numthreads(8,8,1)]
void CS_InitializeSpectrum(uint3 id : SV_DispatchThreadID)
{
    // ... 若当前像素是镜像像素，就把刚刚算出的那一半翻转过来，省一次 ComputeH0 ...
    uint2 mirror = (uint2(N, N) - id.xy) % uint2(N, N);
}
```

> **注意：H0 存在 RenderTexture 里，而 RT 的内容不落盘。** 域重载、脚本重编译、`.compute` 或这张纹理资产被重新导入，都会把它清成全 0，表现是海面变成一块没有起伏的蓝布，而且**不报任何错**。工程里做了四处自愈（控制器 `Start`、建资源时、非播放模式录命令前、资源导入后），原理都是"发现全 0 就重算"——因为 H0 的随机相位来自像素哈希，重算是逐位确定的，多算几次不会让画面抖。

#### 2.3 时间演化与打包：一次 IFFT 出 4 个实场

`CS_Spectrumtime` 只做两件事：让每个波按色散关系转相位，然后把**需要逆变换的实场两两打包成复场**。

```hlsl
// [公式] 基频 w0 = 2π / T。把 ω 量化成 w0 的整数倍，
// 保证 t = T 时每个波都正好绕回整数圈，海面能首尾无缝循环。
float w0 = 2.0f * PI / max(_RepeatTime, 1e-4);

float dispersion = floor(sqrt(_Gravity * kLength) / w0) * w0 * _FrameTime;
float2 exponent  = EnlerFormula(dispersion);          // e^{iωt}

// [公式] h(k,t) = H0(k) e^{iωt} + conj(H0(−k)) e^{−iωt}
float2 htime = ComplexMul(h0, exponent)
             + ComplexMul(h0conju, float2(exponent.x, -exponent.y));
float2 ihtime = float2(-htime.y, htime.x);            // i · htime

// [公式] 位移谱（水平位移按 k 方向分配）
float2 displacementX = ihtime * k.x * kLengthRcp;
float2 displacementY = htime;
float2 displacementZ = ihtime * k.y * kLengthRcp;

// [公式] 偏导数谱：频域求导 = 时域乘 i k
float2 displacementY_dx = ihtime * k.x;
float2 displacementY_dz = k.y * ihtime;
float2 displacementX_dx = -htime * k.x * k.x * kLengthRcp;
float2 displacementZ_dz = -htime * k.y * k.y * kLengthRcp;
float2 displacementZ_dx = -htime * k.x * k.y * kLengthRcp;

// 打包成 (a + i·b)：逆变换后 实部 → a，虚部 → b
float2 htimeDisplacementX = float2(displacementX.x - displacementZ.y, displacementX.y + displacementZ.x);
float2 htimeDisplacementZ = float2(displacementY.x - displacementZ_dx.y, displacementY.y + displacementZ_dx.x);
float2 htimeSlopeX = float2(displacementY_dx.x - displacementY_dz.y, displacementY_dx.y + displacementY_dz.x);
float2 htimeSlopeZ = float2(displacementX_dx.x - displacementZ_dz.y, displacementX_dx.y + displacementZ_dz.x);

_SpectrumTexture[uint3(id.xy, i * 2)]     = float4(htimeDisplacementX, htimeDisplacementZ);
_SpectrumTexture[uint3(id.xy, i * 2 + 1)] = float4(htimeSlopeX, htimeSlopeZ);
```

- **`ω` 量化是循环的前提。** `floor(ω/w0)·w0` 把每个波的频率吸附到 `2π/200 s` 的整数倍，于是 200 s 之后每个波都绕回了整数圈，海面无缝。代价是长波的色散关系有百分之几的偏差（周期 14 s 的涌浪会被吸到 14.3 s），肉眼不可见。
- **打包的关键是实场的数目。** 一个 `float4` 装两个复场 = 4 个实场，8 层 = **32 个实场**（4 个级联 × 每个级联 8 个量）。通道排布是：第 `i*2` 层 `R = 位移 X, G = 位移 Z, B = 高度 Y, A = dZ/dx`；第 `i*2+1` 层 `R = dY/dx, G = dY/dz, B = dX/dx, A = dZ/dz`。注意第 `i*2` 层的顺序是 X、Z、Y，和 `float3` 的 XYZ 不一致，容易看错。
- **`i k` 求导一次拿全。** 高度场的法线要 `∂h/∂x`、`∂h/∂z`，泡沫要水平位移的散度和交叉项；这些在频域里都只是把谱乘以一个 `i k` 的线性组合，不用额外采样，也不用在片元里现算差分。

#### 2.4 二维逆 FFT

按行做一遍 1D 逆变换、再按列做一遍，就是二维逆变换。每行（每列）交给一个线程组，数据放在 `groupshared`，256 个线程协同，`stride` 循环支持到 512 点。

```hlsl
void FFTInPlace(uint tid, uint N, uint logN)
{
    // 1) 位反转置换：DIT 要求输入先按比特反转排序
    if (tid < N)
    {
        uint j = reversebits(tid) >> (32u - logN);
        if (tid < j) { /* 交换 gFFT[tid] 和 gFFT[j] */ }
    }
    GroupMemoryBarrierWithGroupSync();

    // 2) 蝶形：len 从 2 一路翻倍到 N，两路复数并行
    for (uint len = 2u; len <= N; len <<= 1u)
    {
        uint halfLen = len >> 1u;
        float angStep = 2.0f * PI / float(len);         // 逆变换取 +i
        for (uint b = tid; b < (N >> 1u); b += FFT_THREADS)
        {
            uint i = (b / halfLen) * len;
            uint j = b % halfLen;
            float2 w = float2(cos(angStep * j), sin(angStep * j));
            float4 u = gFFT[i + j];
            float4 t = gFFT[i + j + halfLen];
            float4 v = float4(cmul(t.xy, w), cmul(t.zw, w));
            gFFT[i + j]           = u + v;
            gFFT[i + j + halfLen] = u - v;
        }
        GroupMemoryBarrierWithGroupSync();
    }
}
```

三个必须记住的点：

1. **要补 `(−1)^{x+y}`。** 谱是按"中心化"存的（`k = 0` 在像素 `(N/2, N/2)`），而标准 FFT 要求下标 0 就是 `k = 0`；频率下标整体平移 `N/2` 等价于空域乘 `(−1)^n`。漏了这一项，海面会逐像素正负交替，看起来像棋盘噪点。所以列变换写回时统一乘符号。
2. **不做 `1/N` 归一化。** `H0` 里已经乘过 `Δk²`（`areaTerm`），它就是离散积分 `Σ h(k) e^{ikx}` 的权重，这里要的就是"不除 N 的原始求和"。
3. **屏障不能放在分支里。** `GroupMemoryBarrierWithGroupSync()` 必须在整个线程组都走得到的位置，否则行为未定义（`N < 512` 时多余线程照样要进屏障）。

#### 2.5 泡沫：雅可比 + 浪脊遮罩 + 跨帧累积

泡沫的全部逻辑在 `CS_UpdateFoam` 里，按级联纹素算，结果直接写回同一张 `_OceanFoam`（就地读写：每个线程只碰自己那一个纹素，不需要双缓冲）。

```hlsl
// 合计梯度：只把「本级 + 所有更粗的级」加起来
[loop]
for (int j = 0; j <= level; j++)
{
    float2 uv = worldXZ / CascadeLengthCS(j);
    // ... 手动双线性取 4 邻居（RWTexture 没有 Sample），累加 dXdx / dZdz / dZdx / dYdx / dYdz ...
}

// 位移和它的导数乘同一个缩放：x = s·dX，所以 J = det(I + s·∇d)
dXdx *= _FoamChoppiness;
dZdz *= _FoamChoppiness;
dZdx *= _FoamChoppiness;

float jacobian = (1.0f + dXdx) * (1.0f + dZdz) - dZdx * dZdx;

// 逐纹素抖动阈值：把 J 的平滑等值线打碎，再靠跨帧累积磨圆
jacobian += (Hash(id.xy) - 0.5f) * _FoamDetail;

float prev      = _FoamReset > 0.5f ? 0.0f : _FoamTexture[id].r;
float crush     = saturate(_FoamThreshold - jacobian);   // 压过头多少（J 可以为负）
float generated = pow(crush, _FoamJPower);

// 浪脊遮罩：坡度在浪脊处最大、在浪谷和缓坡处接近 0
float steepness = length(float2(dYdx, dYdz)) * _FoamChoppiness;
float crest     = saturate((steepness - _FoamCrestBias) / max(_FoamCrestRange, 1e-4f));
generated *= crest;

_FoamTexture[id] = float4(saturate(prev * _FoamDecay + _FoamAdd * generated), 0, 0, 0);
```

- **`J = det(I + s·∇d)`** 是"水平位移把水面的面积压缩了多少"：`J < 1` 是被挤到一起，`J < 0` 是网格翻了过来——那正是浪要破碎的地方。
- **级联的 `J` 必须是"和的雅可比"。** 水面高度是 4 级位移相加的结果，所以第 `level` 级的泡沫要把 `0..level` 级的梯度**先在空间上采样出来再相加**，而不是把 4 级的 `J` 各自算完再相加。后者是"先非线性再相加"，和真实几何不一致（表现为泡沫和实际浪形的错位）。
- **跨帧累积**：`retention^dt` 衰减、`rate·dt` 增量，指数移到每帧的系数里算（在 C# 侧），波峰过去之后泡沫还在、慢慢褪，比逐帧乘常数稳定。
- **`_FoamReset`** 时从 0 开始：暂停/切场景回来、或者相机参数大改之后，避免把上一段历史当成这一帧的初值。

#### 2.6 曲面细分与 domain 位移

```hlsl
[domain("tri")]
[partitioning("fractional_odd")]
[outputtopology("triangle_cw")]
[outputcontrolpoints(3)]
[patchconstantfunc("PatchConstantFunc")]
Varyings HullProgram(InputPatch<Varyings, 3> patch, uint id : SV_OutputControlPointID)
{
    return patch[id];                      // hull 只负责「原样传下去」，细分因子由 PatchConstantFunc 给
}

[domain("tri")]
Varyings DomainProgram(TessFactors factors, OutputPatch<Varyings, 3> patch, float3 bary : SV_DomainLocation)
{
    float3 positionWS = patch[0].positionWS * bary.x
                      + patch[1].positionWS * bary.y
                      + patch[2].positionWS * bary.z;

    // 用「位移前」的世界 XZ 去查谱：频谱定义在静止平面上，拿位移后的坐标查会自己追自己
    float3 displacement = SampleDisplacement(positionWS) * _DisplacementScale;

    // 远处的位移整体收掉，地平线才是一条水平线而不是一串浪尖
    positionWS += displacement * DisplacementFade(positionWS);
    OUT.positionWS = positionWS;
    OUT.positionCS = TransformWorldToHClip(positionWS);
    return OUT;
}
```

| 决定               | 选择                                              | 为什么                                                                                                |
| ---------------- | ----------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| 位移发生在哪           | **domain** 阶段                                   | vert 阶段只有一个大平面三角形，先细分再位移，浪尖才不会因为顶点太稀被拉成三角形；在 hull 里改控制点则是错的——位移量会被 domain 再插值一遍，等于摊平，patch 接缝还会对不上 |
| 边因子怎么取           | 每条边取**两端点的最小值**，内部取三者最小值                        | 相邻 patch 共享的那条边必须算出同一个因子，否则细分等级交界处会裂开、闪动                                                           |
| 分区模式             | `fractional_odd`                                | 细分等级连续变化时顶点数量平滑增减，不会整级跳变导致"呼吸"                                                                     |
| 采样坐标             | **位移前**的世界 XZ，且必须用 `SAMPLE_TEXTURE2D_ARRAY_LOD` | domain 阶段没有屏幕空间导数，隐式求 LOD 的采样无法工作                                                                  |
| `#pragma target` | **4.6**                                         | 低于这个值 hull/domain 会被编译器直接丢掉，shader 静默退回顶点着色器——表现是"位移完全没效果"，且不报错                                    |
| 距离衰减             | `DisplacementFade` 把位移整体压平                      | 远处的浪在屏幕上只有几像素，保持满幅只会把地平线堆成一列锯齿状的山                                                                  |

级联的换算关系也在这里：`CascadeUV(worldXZ, i) = worldXZ / L_i`，也就是说**每个级联就是一张 `L_i` 米见方的周期贴图**，平铺由采样模式（Repeat）完成，不需要另外存偏移。

| 级联  | 物理边长 `L` | 纹素尺寸（N = 512） | 最短波长 `2L/N`                   |
| --- | -------- | ------------- | ----------------------------- |
| c0  | 1000 m   | 1.95 m        | 3.9 m                         |
| c1  | 250 m    | 0.49 m        | 0.98 m                        |
| c2  | 62.5 m   | 0.12 m        | 0.24 m                        |
| c3  | 15.625 m | 3.05 cm       | 6.1 cm（ω ≈ 31.7 rad/s ≈ 5 Hz） |

#### 2.7 着色与光照：自定义光照，不走 PBR

**这一节是刻意的技术选择，不是"没写完的 PBR"。** URP 的标准路径是 `UniversalFragmentPBR(inputData, surfaceData)`，它假设材质是"有漫反射底色 + 有镜面"的固体表面，用 albedo/smoothness/metallic 控制两者比例。水不是这种材质：

1. 水的漫反射几乎为 0，颜色主要来自"看到了什么"（天空反射）和"光在身体里散射了多久"；
2. 水的反射率随视角剧烈变化（`F0 ≈ 0.02`，掠射角趋近 1）——这是**菲涅尔**，不是 roughness；
3. 直接套 PBR，水体颜色会被当成 albedo 参与漫反射，海面看起来像一块**蓝绿色塑料**，远处的海也不会变成天空色。

所以这里只借 URP 的**积木**（`GlossyEnvironmentReflection`、`DirectBRDF`、`SampleSH`、`GetMainLight`），不借 `UniversalFragmentPBR` 的组合方式——沿用离线渲染和教程里那套更老的配方：

```
color = (1 − F) · body(水体散射) + F · sky(环境反射) + specular(太阳高光)
```

| 量     | 标准 PBR 的做法             | 这里的做法                                                         | 为什么                                         |
| ----- | ---------------------- | ------------------------------------------------------------- | ------------------------------------------- |
| 水体颜色  | `albedo × NdotL` 参与漫反射 | `body = _OceanColor·(ambient + light·NdotL·_SunBodyStrength)` | 直接光对水体颜色的贡献要很小（0.35），否则就是塑料                 |
| 反射占比  | 高光项和 diffuse 相加        | `lerp(body, skyReflection, fresnel)`                          | 水的"亮"是被反射的天空**占满**，不是高光项变大                  |
| `F0`  | 由 metallic/albedo 推出   | 写死 `WATER_F0 = 0.02`                                          | 水的 IOR ≈ 1.33，`((1.33−1)/(1.33+1))² ≈ 0.02` |
| 太阳高光  | 完整 BRDF                | `DirectBRDF`，且 **diffuse 传 0**                                | 只要镜面那一瓣，水体颜色已经自己算了                          |
| 朝下的法线 | 不处理                    | `reflectDir.y = max(reflectDir.y, 0.02)`                      | 否则浪背面的法线会去采样天空盒的"地面"半边，满屏棕灰色斑块              |
| 远处细节  | 靠 mip                  | 被淡掉的斜率方差折算进 roughness                                         | 起伏没有消失，只是采样不出来，换成更宽的镜面瓣                     |

```hlsl
// ---- 5. 菲涅尔（Schlick）----
float fresnel = WATER_F0 + (1.0 - WATER_F0) * pow(1.0 - NdotV, 5.0);
fresnel = saturate(fresnel * _FresnelStrength);

// ---- 6. 天空反射 ----
float3 reflectDir = reflect(-viewDir, normalWS);
reflectDir.y = max(reflectDir.y, 0.02);      // 水下没有可以反射的东西
reflectDir = normalize(reflectDir);
half3 skyReflection = GlossyEnvironmentReflection(reflectDir, roughness, 1.0) * _EnvStrength;
skyReflection *= saturate(shadow + 0.5);     // 在阴影里天空还在，只是暗一点

// ---- 7. 太阳高光：借 URP 的 GGX 高光瓣，diffuse 设 0 ----
BRDFData brdfData;
InitializeBRDFData(half3(0, 0, 0), 0.0, half3(WATER_F0, WATER_F0, WATER_F0), 1.0 - roughness, 1.0, brdfData);
half3 specular = DirectBRDF(brdfData, normalWS, lightDir, viewDir) * lightColor * saturate(shadow + 0.3);

// ---- 8. 水体（散射）：浪尖逆光透青绿 ----
float crest = saturate(IN.positionWS.y / max(_CrestHeight, 1e-3));   // 位移只在竖直方向，y 就是波高
float backLight = pow(saturate(dot(lightDir, -viewDir)), 4.0);
half3 body = _OceanColor.rgb * (SampleSH(normalWS) + lightColor * NdotL * _SunBodyStrength)
           + _ScatterColor.rgb * (backLight * crest * crest * _ScatterStrength);

// ---- 9. 合成 / 10. 泡沫盖上去 ----
float3 color = lerp(body, skyReflection, fresnel) + specular;
color = lerp(color, _FoamColor.rgb * (lightColor * NdotL + ambient), saturate(foam));
```

法线本身不来自法线贴图，而是从 FFT 顺带导出的斜率直接算：

```hlsl
// 高度场 y = h(x, z) 的法线是 normalize(-dh/dx, 1, -dh/dz)
float4 detail = od.derivatives * _DisplacementScale;
float2 slope  = detail.xy * DisplacementFade(IN.positionWS);   // 远处的法线和几何一起压平
float3 normalWS = normalize(float3(-slope.x * _NormalStrength, 1.0, -slope.y * _NormalStrength));
```

#### 2.8 泡沫的显示侧：换档、距离衰减、絮状遮罩

CS 算完的泡沫还是"按级联纹素"的，片元里要做三件事才能变成看上去对的泡沫：

```hlsl
float w = CascadeFade(i, worldPos) * LayerWeight(i);

// ① 由粗到细「换档」，不是相加：第 i 级的泡沫在 CS 里已经把 0..i 级全算进去了
o.foam = (i == 0) ? f : lerp(o.foam, f, CascadeFade(i, worldPos));

// ② 距离衰减：泡沫只在近处几十米有分辨率，远处全是亚像素噪声，衰减到看不见才是对的
float clipDepth = saturate(1.0 - dist / max(_DisplaceFadeDistance, 1e-3));
foam = od.foam * pow(clipDepth, max(_FoamDepthAttenuation, 1e-3));

// ③ 幂次收紧：从「一片薄雾」变成「一条边缘清楚的带子」
foam = pow(saturate(foam), _FoamEdgePower);

// ④ 絮状遮罩：三层 fBm，先用低频噪声把 uv 扭一下（domain warp）
float2 warp = float2(FoamValueNoise(baseUV * 0.61f + 7.3f), FoamValueNoise(baseUV * 0.61f - 3.9f)) - 0.5f;
float2 noiseUV = baseUV + warp * 1.6f;
float foamNoise = FoamValueNoise(noiseUV) * 0.55f
                + FoamValueNoise(noiseUV * 3.17f + 19.3f) * 0.30f
                + FoamValueNoise(noiseUV * 9.73f - 5.10f) * 0.15f;
foam *= saturate(0.45f + 0.60f * foamNoise);
```

`CascadeFade` 是这里最不显眼却最关键的一个函数，它同时承担了抗锯齿和"泡沫该不该存在"两个职责：

```hlsl
float CascadeFade(int cascade, float3 worldPos)
{
    float texel     = CascadeLength(cascade) / max(_OceanResolution, 1.0);
    float footprint = distance(_WorldSpaceCameraPos, worldPos) * DETAIL_FOOTPRINT_PER_METER;
    return 1.0 - saturate((footprint - texel) / texel);   // 足迹到一个纹素开始淡，到两个纹素淡光
}
```

`DETAIL_FOOTPRINT_PER_METER = 0.002` 的意思是"每 1 m 距离对应约 2 mm 的像素足迹"，约相当于 1080p + 垂直 FOV 90°（桌面常见的 60° 会更小，因此这个取值偏保守）。代入 `L = (1000, 250, 62.5, 15.6)`、`N = 512`：

| 级联  | 纹素      | 淡出区间（距离）                        |
| --- | ------- | ------------------------------- |
| c0  | 1.95 m  | 976 – 1953 m（在 500 m 的平面内永远全有效） |
| c1  | 0.49 m  | 244 – 488 m                     |
| c2  | 0.12 m  | 61 – 122 m                      |
| c3  | 3.05 cm | **15 – 31 m**                   |

也就是近处保留全部细节、30 m 外自动退回 c2、120 m 外退回 c1——远处的海看起来"糊"，但不会闪。

#### 2.9 参数速查

**风浪**（`Ocean` → `OceanFFTController`）

| 参数              | 值      | 说明                                  |
| --------------- | ------ | ----------------------------------- |
| Base Patch Size | 1000   | 最大级联边长 `L0`，其余三级由它和级间比推出            |
| Cascade Ratio   | 4      | 相邻级联的 `L` 比值（也是波段分割比，上限 `N/2`）      |
| Wind Speed      | 15     | 只通过 `ωp = 0.877g/U` 影响谱的形状，波高约 ∝ U² |
| Wind Direction  | (1, 0) | 谱峰方向                                |
| Spread Power    | 11.5   | Mitsuyasu 扩散指数上限，谱峰处最窄；调大 → 方向更集中   |

**频谱/泡沫**（`PC_Renderer.asset` → `OceanFFTRenderFeature`）

| 参数                      | 值          | 说明                                                    |
| ----------------------- | ---------- | ----------------------------------------------------- |
| Repeat Time             | 200 s      | `ω` 量化的基频周期，决定循环有多长                                   |
| Foam Retention          | 0.35       | 泡沫每秒保留比例（`pow(retention, dt)`）                        |
| Foam Add                | 3.5        | 泡沫每秒生成速率（`rate · dt`）                                 |
| Foam Threshold          | 0.55       | `crush = saturate(T − J)` 的阈值，越大泡沫越多                  |
| Foam J Power            | 2          | `pow(crush, ·)`，越大越只保留深折叠                             |
| Foam Detail             | 0.2        | 逐纹素抖动幅度，用来打碎 `J` 的平滑等值线                               |
| Foam Choppiness         | 0.45       | `J` 里用的水平位移缩放（**应与材质 `_DisplacementScale` 一致**，见 5.1） |
| Foam Crest Bias / Range | 0.22 / 0.4 | 浪脊遮罩的偏移与范围                                            |

**材质**（`Ocean.mat`）

| 参数                                                       | 值             | 说明                           |
| -------------------------------------------------------- | ------------- | ---------------------------- |
| `_DisplacementScale`                                     | 0.42          | 位移整体的缩放，直接决定浪高               |
| `_Roughness` / `_RoughnessBoost`                         | 0.16 / 1.5    | 基础粗糙度 / 丢失细节折算成粗糙度的系数        |
| `_TessMax` / `_TessMin` / `_TessDistance`                | 7.4 / 1 / 408 | 细分因子随距离从 7.4 线性降到 1 的距离      |
| `_DisplaceFadeDistance` / `Falloff`                      | 400 / 1.2     | 远处几何压平的距离与幂次                 |
| `_FoamEdgePower` / `_FoamDepthAttenuation`               | 2.77 / 17.8   | 泡沫边缘收紧 / 距离衰减幂次              |
| `_FoamNoiseScale` / `_FoamNoiseContrast`                 | 1.54 / 1.31   | 絮状遮罩的频率与对比度                  |
| `_FresnelStrength` / `_EnvStrength`                      | 0.63 / 1      | 菲涅尔强度 / 环境反射强度               |
| `_CrestHeight` / `_SunBodyStrength` / `_ScatterStrength` | 3 / 0.35 / 2  | 浪尖透光的高度尺度 / 直接光对水体的贡献 / 散射强度 |

#### 2.10 运行

1. 用 Unity **6000.3.10f1**（URP 17.3）打开工程；
2. 打开 `Assets/Scenes/SampleScene.unity`，确认 `PC_Renderer` 上挂着 `OceanFFTRenderFeature`（`Project Settings → Graphics` 里指定的就是它）；
3. Play。相机：右键拖动转视角、`WASD` 平移、`Q/E` 升降、`Shift` 加速、滚轮调速。

#### 2.11 附：目录结构

```
Assets/
├── CS/
│   └── CS_H0.compute            谱生成、时间演化、IFFT、泡沫累积（4 个 kernel + 1 个初始化）
├── 脚本/
│   ├── OceanFFTRenderFeature.cs 建纹理、发全局变量、录 4 段 dispatch（RenderGraph）
│   ├── OceanFFTController.cs    风浪参数 → 调度 H0，含四处自愈逻辑
│   ├── OceanGrid.cs             生成/复用静态海面网格（尺寸、格边长、顶点色）
│   └── FreeFlyCamera.cs         自由飞行相机（新输入系统）
├── 材质/
│   ├── seashader.shader         Hull / Domain / Fragment，位移与着色
│   ├── Ocean.mat
│   └── SunsetSky.mat            程序化天空（反射的回退来源）
├── Editor/
│   ├── OceanSpectrumAutoRepair.cs  H0 清空后自动补算
│   └── InitialSpectrumViewer.cs    查看初始频谱
├── Texture/InitialSpectrum_Tex.asset   H0 的 RenderTexture 资产
├── Scenes/SampleScene.unity
└── Settings/PC_Renderer.asset     挂了 OceanFFTRenderFeature
```

### 3. 踩坑记录

这一节集中记录**泡沫**这一段。它是整条链路里唯一"每一步都算得出来、画面里也确实有泡沫、但看起来就是不对"的地方；而且下面三个坑的现象完全不像同一个原因——一个是方块，一个是全白，一个是闪烁。它们的共同点只有一条：**代码不报错，只有眼睛能发现**。

#### 3.1 泡沫是一块块肉眼可见的方块

现象：泡沫团块的边缘是**笔直的、和世界坐标轴平行的方块**，近处像马赛克，越靠近相机越明显；怎么调阈值、调噪声频率都改不掉"方块"这个形状，只会改变方块的大小。

定位过程：

1. **先把 `J` 直接当颜色输出**（把 `_FoamTexture` 的 R 通道当 albedo 贴上水面）。方块已经在 `J` 里了，不是渲染侧的问题。
2. **再看 `CS_UpdateFoam` 的采样**：`_SpectrumTexture[uint3(p0, ...)]` 是**最近邻**取值。`j == level` 时纹素一一对应，没问题；但 `j < level` 时，粗级纹素盖住的是几十个细级纹素——c0 的纹素 1.95 m 盖住 c3 的 64 个纹素（3.05 cm）——于是 `J` 在每个粗级纹素内部是个**常量**，出了边界跳一个台阶。
3. **改完双线性，方块变小了但还在**，这次是"絮状团块边缘的直角"。第二处是渲染侧的 value noise：格点轴和世界坐标轴对齐，`_FoamNoiseContrast` 又把它往 0/1 两端推，格子边界被推成了硬边。

根因：**两处采样率不匹配**——一处是空间的（粗级纹素 vs 细级纹素），一处是噪声的（格点 vs 世界），共同点是"用低分辨率的量去驱动高分辨率的结果，中间既没有插值也没有扭曲"。

改法（两处都要改，只改一处方块不会消失）：

```hlsl
// ① compute 侧：RWTexture 没有 Sample，手动双线性（4 邻居 + lerp）
uint2 p0 = (uint2)t;
uint2 p1 = (p0 + 1u) % res;                 // 级联贴图是周期的，取模就是 wrap
float2 f = t - (float2)p0;
float4 d00 = _SpectrumTexture[uint3(p0.x, p0.y, dispSlice)];
float4 d10 = _SpectrumTexture[uint3(p1.x, p0.y, dispSlice)];
float4 d01 = _SpectrumTexture[uint3(p0.x, p1.y, dispSlice)];
float4 d11 = _SpectrumTexture[uint3(p1.x, p1.y, dispSlice)];
float4 disp = lerp(lerp(d00, d10, f.x), lerp(d01, d11, f.x), f.y);

// ② 渲染侧：先用低频噪声把 uv 扭一下（domain warp），把格子边界揉开
float2 warp = float2(FoamValueNoise(baseUV * 0.61f + 7.3f),
                     FoamValueNoise(baseUV * 0.61f - 3.9f)) - 0.5f;
float2 noiseUV = baseUV + warp * 1.6f;
```

**这条坑的结论**：凡是"低分辨率的数据驱动高分辨率的结果"，中间必须有插值或扭曲。特别地，**`RWTexture` 没有采样器**——用 `RWTexture2DArray` 当数据源的时候，双线性这件事得自己写，编译器不会替你补。

#### 3.2 泡沫糊满全屏：一片均匀的雪原

现象：整片海均匀地糊着一层白，近处远处一个亮度，看不出"贴着浪脊、一条一条"的破碎带，像雪原或者牛奶。

定位过程：

1. **把 `J` 输出成灰度**，`J` 的分布本身是合理的（只在折叠区偏低），但读回来的泡沫处处饱和成 1 —— 说明问题在"从 `J` 到泡沫"这一步。
2. **检查累积**：4 级泡沫在片元里是被**相加**的。而第 `i` 级在 CS 里已经把 `0..i` 级全部算进去了，四级相加等于把同一片水算了 4 遍，直接饱和到 1。改成"由粗到细换档"（`lerp`）才对。
3. **换档之后仍有大片均匀白**，回到 compute 看阈值：写的是 `max(0, 阈值 − J²)`，而且 `J` 先被 `saturate` 到 `[0,1]`。`J` 折叠之后可以远小于 0，`saturate` 之后全部变成 0，`crush` 一律等于阈值——**所有折叠区的深度信息在这一步被抹平了**，深浅不分，只能给出同一份泡沫。
4. **改成 `saturate(T − J)` 后形状对了，但白带还是铺满整片海**、像一个从近到远的渐变。这是"只用 `J`"的必然结果：大片缓坡上 `J` 也略小于 1，`crush` 处处为正。加浪脊遮罩后，泡沫才收成"沿着浪脊的一条条"。
5. **最后远处那一条还是糊**，因为完全没有距离衰减。

根因：四个独立的错误叠在一起——相加而不是换档、`J²` + `saturate` 抹平深度、缺浪脊遮罩、缺距离衰减。每一个单独看都"像是泡沫"（有白、不报错），合起来就是"一片均匀的白"。

改法：

```hlsl
float crush     = saturate(_FoamThreshold - jacobian);   // 保留 J < 0 的深度，不是 max(0, T - J²)
float generated = pow(crush, _FoamJPower);
float crest     = saturate((length(float2(dYdx, dYdz)) * _FoamChoppiness - _FoamCrestBias) / _FoamCrestRange);
generated *= crest;

// 片元：换档而不是相加
o.foam = (i == 0) ? f : lerp(o.foam, f, CascadeFade(i, worldPos));
foam = od.foam * pow(clipDepth, _FoamDepthAttenuation);   // 距离衰减
```

**这条坑的结论**：泡沫是两个独立的量——**"挤得多深"**（`J`，而且必须保留负值才分得出深浅）和**"挤在哪里"**（浪脊遮罩，因为 `J` 在大片缓坡上也会略小于 1）。再加一条：多级级联的同一分量要么换档、要么分工，**绝不能相加**。

#### 3.3 远处满屏闪烁的雪花噪点

现象：十几米外开始，海面浮着一层细碎、随机跳动的亮点，相机一动就闪，越远越密，像老电视的雪花。

定位过程：

1. **关掉泡沫** → 干净；**关掉法线** → 还是闪。于是确定是泡沫，不是高光。
2. **把 4 级泡沫分别输出到 RGBA**（R = c0，G = c1，B = c2，A = c3），一眼看出来闪的是 **c3**，也就是最细的那一级（纹素 3.05 cm）。
3. **算一下像素足迹**：1080p、垂直 90° FOV 时，一个像素在距离 `d` 处覆盖的世界尺度约 `0.002·d`——100 m 处是 0.2 m，而 c3 的纹素只有 0.03 m。也就是说相邻两个像素读到的是**相距 6 个纹素**的值，而这些纹素每帧的相位都在变（c3 的频率约 5 Hz），屏幕上的结果自然就是逐帧随机跳变。

根因：**采样率不足（aliasing）**。不是泡沫算错了，是"在这个距离上这段频率根本不能被采样"。任何"算得对但频率太高"的量都会这样。

改法：按"像素足迹 vs 纹素尺寸"做带限，淡出超采样能力的级联。**关键是不能只是乘个权重归零**——被淡掉的那部分斜率不是噪声，是这个像素"本来应该看到的起伏"，直接丢掉远处会变成一块死镜面：

```hlsl
// ① 级联淡出（带限）
float texel     = CascadeLength(cascade) / max(_OceanResolution, 1.0);
float footprint = distance(_WorldSpaceCameraPos, worldPos) * DETAIL_FOOTPRINT_PER_METER;
float fade      = 1.0 - saturate((footprint - texel) / texel);

// ② 泡沫和位移共用同一个级联、同一套 uv、同一个淡出权重 —— 级联淡出本身就等于按足迹做带限
float f = SAMPLE_TEXTURE2D_ARRAY_LOD(_OceanFoam, sampler_OceanFoam, CascadeUV(worldPos.xz, i), i, 0).r;
o.foam = (i == 0) ? f : lerp(o.foam, f, fade);

// ③ 被淡掉的斜率不是消失了，折算成粗糙度（思路同 Toksvig 法线过滤）
o.lostSlope2 += (1.0 - w) * (s.r * s.r + s.g * s.g);
float roughness = sqrt(_Roughness * _Roughness + (od.lostSlope2 + lostFromGeo) * _RoughnessBoost);
```

**这条坑的结论**：高频细节的淡出必须伴随**能量转移**——从"法线起伏"转移到"粗糙度"。只做淡出会从"闪烁"变成"死板"，两种都不对；两者合起来，远处才是柔和的天空反射。

### 4. 技术总结

#### 4.1 方案的本质

把"模拟一片海"这个看起来偏物理的问题，转换成一个**纯信号的合成问题**：

> 用一张静态的谱 `H0(k)` 描述"这片海应该有哪些波、各有多大"，每帧只让它们转相位，然后靠一次 `O(N log N)` 的逆变换把频率域一次性搬回空间域。

整个方案里没有任何一处需要"逐一处理每一个波"的循环，也没有任何一处需要 CPU 介入。这个转换是整套设计成立的根本原因，也是它容易被扩展的原因——**任何能写进谱的东西，都会自动变成海面上的一个波**：主谱、次谱、船尾迹、局部风场，区别只在 `H0` 怎么算。

#### 4.2 关键设计决策

| 决策       | 选择                                  | 收益                                                              |
| -------- | ----------------------------------- | --------------------------------------------------------------- |
| 海面怎么来    | 统计波谱 + 二维逆 FFT                      | 成本 `O(N log N)` 且与波数无关；4 级 × 512² ≈ 52 万个波，每帧 4 次 dispatch      |
| 怎么做到无缝循环 | 把 `ω` 量化到 `2π/200 s` 的整数倍           | 200 s 后每个波绕回整数圈，不需要任何跨边界处理；代价是长波周期有百分之几的偏差                      |
| 频域里存什么   | 4 个复场（位移 X/Y/Z + 高度）打包成 2 层/级联      | 一个 `float4` = 2 个复场 = 4 个实场；8 层 = 32 个实场，`IFFT` 次数从 8 降到 2 次/级联 |
| 法线怎么来    | 频域乘 `i k` 得到解析导数                    | 不用在片元里做差分、不用法线贴图，且天然带 mip 无关的抗锯齿能力                              |
| 位移在哪做    | domain 阶段、用位移前的坐标查谱                 | 顶点密度可按距离自适应；远处理论上还能整块跳过                                         |
| 光照走哪条路   | **自定义合成，不走 `UniversalFragmentPBR`** | 水的高光主要来自天空反射而非漫反射；走 PBR 只会得到"蓝绿色塑料"                             |
| 泡沫怎么攒    | compute 里按级联纹素跨帧递推                  | 涨得快、落得慢，且历史天然按级联分档，可以跟着 `CascadeFade` 一起带限                      |

#### 4.3 各阶段的实际开销

| 阶段                      | 频率                   | 代价                                                                 |
| ----------------------- | -------------------- | ------------------------------------------------------------------ |
| `CS_InitializeSpectrum` | 初始化 / 参数变化 / H0 被清空时 | 512² × 4 层，一次填满 H0（16 MB）                                          |
| `CS_Spectrumtime`       | 每帧                   | `(64,64,1)` 个线程组 × 64 线程，4 级 × 512² 个格点的谱运算；写 8 层（32 MB）           |
| `CS_FFTHorizontal`      | 每帧                   | `(1,512,8)` 个线程组 × 256 线程，每组做一行 512 点 IFFT；读 32 MB + 写 32 MB       |
| `CS_FFTVertical`        | 每帧                   | 同上；读 32 MB + 写 32 MB                                               |
| `CS_UpdateFoam`         | 每帧                   | `(64,64,4)` 个线程组；最细一级每纹素 `4 级 × 8 次 load` = 32 次，读 16 MB + 写 16 MB |
| Domain（细分顶点）            | 每个细分后顶点              | 4 次 `ARRAY` 采样（每级 1 次），按 `CascadeFade` 加权                          |
| Fragment                | 每个像素                 | 8 次 `ARRAY` 采样（4 级 × 位移导数 + 泡沫）、1 次反射采样、3 次泡沫噪声                    |

结论：**每帧的显存流量约 190 MB**（32 + 64 + 64 + 32），与波数完全无关；显存占用约 96 MB（频谱 8 层 × 2 张 = 64 MB，泡沫 4 层 = 16 MB，H0 4 层 = 16 MB，均为 ARGBFloat 512²）。真正的开销大头不在 compute，在**几何**：512² 的网格（0.98 m 一格）再乘 7.4 级细分，等效顶点间距约 13 cm，三角形数量在 `10⁷` 量级——这也是 5.2 第一条要动的旋钮。

#### 4.4 收获

- **"算得对"和"看起来对"是两件事。** 泡沫那三个坑，`J` 的公式从头到尾都是对的；错的是"把 `J` 变成屏幕上像素"的那几步——相加还是换档、保留负值还是一律 `saturate`、要不要带限、带限之后丢掉的能量去哪。图形里大多数"效果不对"都属于这一类，而不是公式错。
- **采样率意识比公式意识更难建立。** 一个 3 cm 的纹素在 100 m 外就是 6 个像素跨过去的东西，这不是 bug，是 Nyquist。级联、mip、Toksvig 补偿、domain warp，本质都是同一件事的不同侧面：**让高频量在采样不了的地方体面地消失，并且把它的能量交接给一个低频量**。
- **频域里的"线性"是抢来的能力。** 位移、导数、法线、泡沫的输入全都出自同一次 IFFT；`i k` 求导、Hermitian 对称、打包复场，这些技巧把"要算的东西"从 8 个实场压到 2 次 IFFT/级联，这是这个方案能在 60 fps 下跑起来的核心。
- **Render Graph 下的全局纹理是个盲点。** 通过 `Shader.SetGlobalTexture` 发布、由任意材质自己采样的纹理，对 RenderGraph 的资源追踪是**不可见**的。pass 一旦被判定"没有输出"，就会被 culling 掉，表现为海面突然变成平的蓝布，而且不报错。这里用 `AllowPassCulling(false)` 直接关掉 culling 来解决（对应 `OceanFFTRenderPass.RecordRenderGraph`）。
- **资产化的 RenderTexture 内容不落盘。** H0 存在一张 `RenderTexture` 资产里，任何一次重导入都会把它清成全 0。工程里为此写了四处"发现全 0 就重算"的自愈逻辑——这类"看起来是 bug、其实是资产语义"的问题，只能靠对载体的理解提前规避。

#### 4.5 结果

- 4 级级联 × 512²，最短可表示的波长 6.1 cm（ω ≈ 31.7 rad/s ≈ 5 Hz），波高 / 法线 / 泡沫全部由同一套谱导出，没有一处手写噪声充当"浪"；
- `ω` 量化到 `2π/200 s`，海面 200 s 无缝循环，边界不需要任何处理；
- 500 m × 500 m 网格、26.3 万顶点 / 52.4 万三角形，细分因子随距离从 7.4 降到 1（408 m 处）；
- 纹理占用约 96 MB 显存，每帧约 190 MB 带宽，全部在 GPU 上，CPU 只发 dispatch 和 `SetGlobal`；
- 泡沫近处成条、贴着浪脊，30 m 外自动退回低频、120 m 外基本消失，看不出闪烁；
- 光照不走 PBR：掠射角下整片海是天空的颜色，垂直看下去露出水体的青绿，逆光时浪尖透光。

### 5. 改进方向

#### 5.1 当前方案的局限

1. **只有一个谱。** 现在等于假设"整片海是被同一个风场吹出来的"——方向固定、扩散参数固定，做不出"局地风生浪 + 远处涌浪"叠加的那种层次（这是 5.3 要解决的主要问题）。
2. **线性波谱没有破碎浪。** 浪尖不会翻卷、不会喷白，泡沫只是"挤压量"的贴图化表达，没有真正的几何破碎。
3. **只有深水。** 色散关系固定为 `ω² = gk`，没有 `tanh(kh)` 项，近岸的浅水变形、折射做不出来。
4. 
5. **没有 per-pass GPU 计时。** 上面的开销都是按代码路径和字节数推算的，没有用 RenderDoc / `ProfilerRecorder` 做过基线，所以 5.2 的优化没有量化依据。

#### 5.2 性能优化

1. **先动细分因子。** 52.4 万个网格三角形再乘 7.4² 的细分，是整个管线里最大的一笔；`_TessMax` 从 7.4 降到 4 就能砍掉约 70% 的三角形，视觉差别主要在近处浪尖的轮廓上。
2. **在 hull 阶段先剔除。** 现在所有 patch 都会先细分、再在 domain 里乘衰减权重；可以按 patch 的中心距离直接把远处的细分因子给到 1——等于把"先花钱再发现不用花"的顺序倒过来。
3. **泡沫历史纹理换成 R32F。** 只用到 R 通道，但现在按 ARGBFloat 声明；改格式能把 16 MB 降到 4 MB，也顺带少一半的读写带宽。

#### 5.3 加主谱 + 次谱（风生浪 + 涌浪）

**问题**：现实中的海面永远是两种浪的叠加——**风生浪（wind sea）**是局地风直接吹起来的，短、陡、方向散；**涌浪（swell）**是远处风暴生成后传播过来的，长、平、方向集中，而且几乎不衰减。单谱只能表达其中一种：把扩散调宽，涌浪那种又长又平的纹路就消失；调窄，风生浪就变成一排排平行的"木纹"。

**做法分两步：参数分开，然后在频域里合成。**

参数对照（风速 15 m/s 时，主谱峰值波长约 189 m）：

| 项    | 主谱（风生浪）                                      | 次谱（涌浪）                                    |
| ---- | -------------------------------------------- | ----------------------------------------- |
| 峰值 ω | `0.877g/U = 0.574 rad/s`（T ≈ 11 s，λ ≈ 189 m） | 直接指定，例如 `0.45 rad/s`（T ≈ 14 s，λ ≈ 306 m）  |
| 方向   | 风向                                           | 与风向偏开 30°–60°（真实涌浪的方向和局地风不一致，这正是"层次感"的来源） |
| 扩散指数 | 11.5（宽）                                      | 30–60（窄，接近单向）                             |
| 能量系数 | `α = 0.0081`（JONSWAP 标准值）                    | 0.3–0.6（传播过程中衰减过）                         |
| 波数分工 | `k > k_split`                                | `k < k_split`                             |

**波数分工是最关键的一步**：两个谱如果各自覆盖整个 `k` 空间，同一段波长就会被算两遍，总方差被放大——浪高整体偏高，泡沫阈值全部失效（和 2.2 里级联带通要防的是同一类错误）。

```hlsl
// CS_InitializeSpectrum / ComputeH0 里：两个谱在频域直接相加，仍只存 4 层
float kSplit      = 2.0 * PI / _SwellCutoffWavelength;    // 例如取 230 m
float swellWeight = 1.0 - smoothstep(kSplit * 0.8, kSplit * 1.25, kLength);

// 主谱：局地风生浪（现有那套）
float Sw = JONSWAP(omega, 0.877 * _Gravity / _WindSpeed, 3.3, 0.0081);
float Dw = DirectionSpectrum(kAngle, atan2(_WindDir.y, _WindDir.x), omega, peakOmega, spreadPower);

// 次谱：远处传播来的涌浪，换一套峰值频率 / 风向 / 扩散 / 能量
float Ss = JONSWAP(omega, _SwellPeakOmega, 3.3, 0.0081 * _SwellEnergy);
float Ds = DirectionSpectrum(kAngle, atan2(_SwellDir.y, _SwellDir.x), omega, _SwellPeakOmega, _SwellSpreadPower);

// 带权相加：dω/dk 和 Δk² 对两个谱是一样的，只在最后乘一次
float S = Sw * Dw * (1.0 - swellWeight) + Ss * Ds * swellWeight;
float energyTerm = S * shortWaveFade * bandPass * (abs(dOmegadk) / kLength) * (deltaK * deltaK);
return gaussian * sqrt(energyTerm / 2.0);
```

**为什么必须"频域相加"而不是"两个位移场在着色器里相加"**：泡沫和法线用的是**合成场**的导数，而 `J = det(I + s∇d)` 是 `∇d` 的非线性函数——先各自算 `J` 再相加，和"先相加场再算 `J`"不是一回事（`(a+b)² ≠ a²+b²`）。这一点和 2.5 里"级的 `J` 必须是和的雅可比"完全同源。频域相加只有一条代价极小的路：

| 方案                    | 每帧成本                                                        | 可调性                             |
| --------------------- | ----------------------------------------------------------- | ------------------------------- |
| **A. 在 `H0` 里合成（推荐）** | **完全不变**：`_InitialSpectrumTexture` 仍是 4 层，每帧仍是 4 次 dispatch | 改次谱参数要重算 `H0`（本来就要），两个谱不能独立缩放位移 |
| B. 存 8 层 H0、两次 IFFT   | 频谱带宽翻倍（+64 MB/帧）、dispatch 多 2 次                             | 两个谱可以各自缩放位移、各自做级别淡出             |

推荐 A：`H0` 是静态的，两个谱合成一次就够；如果之后需要"涌浪位移大一点"这种美术控制，再在 `CS_Spectrumtime` 里对两个分量分别乘系数——那时才需要 B 的 8 层结构。

**两个细节**：

- **循环周期不受影响。** 两个谱共用同一个 `w0 = 2π/200` 量化，200 s 仍然无缝。但涌浪周期长（14 s），量化到 `0.0314 rad/s` 的网格上误差约 2.4%，比风生浪大——在意物理精度就把 `RepeatTime` 提到 600 s，代价是相位更"锁"。
- **`_SwellCutoffWavelength` 要落在两个谱峰之间。** 主谱峰 189 m、次谱峰 306 m，取 230 m 左右；`smoothstep` 的过渡带（0.8×~1.25×）不要覆盖到任何一个峰，否则会把峰削平，浪就变成"平顶"的。

#### 5.4 其余可选方向

- **破碎浪**：`J < 0` 的区域就是"水面翻了过来"的地方，已经是一个现成的破碎判据。可以用它驱动一批粒子（`RWStructuredBuffer` + `DrawProceduralIndirect`）从浪尖喷出，或者把浪尖的顶点沿 `−∇h` 方向推向水平位移，做出真正的翻卷。
- **浅水**：把 `Dispersion` 换成 `ω² = gk·tanh(kh)`，`h` 由一张深度图给；顺带可以做折射和岸边涌高（shoaling）。
- **交互**：船、浮标的尾迹可以用一小块"局部波"叠加在 `H0` 之外（比如一个随时间衰减的 Kelvin 尾迹解析式），不需要改 FFT 本身。

---

### 参考

- Tessendorf, *Simulating Ocean Water*, SIGGRAPH course notes
- GPU Gems 1, Ch.1 *Effective Water Simulation from Physical Models*
- Hasselmann et al., *Measurements of Wind-Wave Growth and Swell Decay during the Joint North Sea Wave Project (JONSWAP)*, 1973
- Mitsuyasu et al., 1975（方向扩散函数）
- [gasgiant / FFT-Ocean](https://github.com/gasgiant/FFT-Ocean)：频谱与 IFFT 框架的参考实现（Built-in 管线）。本工程将其移植到 URP 17 Render Graph，并重写了泡沫链路（`CS_UpdateFoam`、级联换档、距离衰减）与着色/光照。
