# FFT-sea-wave

基于 Unity URP 的实时海面演示项目，包含动态海浪、泡沫、水面反射和自由飞行相机。

本文说明如何获取工程、运行示例、调整配置，以及构建和部署 Windows 桌面程序。

## 环境要求

| 项目           | 要求                                                          |
| ------------ | ----------------------------------------------------------- |
| Unity Editor | **6000.3.10f1**，建议通过 Unity Hub 安装                           |
| 渲染管线         | URP **17.3.0**，由项目依赖自动恢复                                    |
| 输入系统         | Input System **1.18.0**，由项目依赖自动恢复                           |
| 显卡           | 支持 Compute Shader、纹理数组和硬件曲面细分；海面 Shader 使用 Shader Model 4.6 |
| 本指南的运行与部署目标  | Windows 64 位桌面程序，建议先使用 Direct3D 11                          |

其他平台需要单独验证。仓库中的 Mobile 配置不代表海面已经完成移动端适配。

## 快速开始

### 1. 获取工程

使用 Git 下载：

```bash
git clone https://github.com/lsx113/FFT-sea-wave.git
cd FFT-sea-wave
```

也可以在 GitHub 仓库页面选择 **Code > Download ZIP**，解压到本地。

确认工程根目录下能看到 `Assets`、`Packages` 和 `ProjectSettings` 三个目录。

### 2. 用 Unity 打开

1. 安装 Unity Hub 和 **Unity 6000.3.10f1**。
2. 在 Unity Hub 的项目列表中添加已有项目，选择刚下载的工程根目录。
3. 使用上述版本打开，等待依赖包恢复、资源导入和脚本编译结束。
4. 打开 **Window > General > Console**，确认没有阻止运行的编译错误。

依赖清单已保存在 [Packages/manifest.json](Packages/manifest.json)，正常情况下由 Unity 自动安装。首次打开需要联网下载依赖。

### 3. 运行示例

1. 在 Project 窗口打开 [Assets/Scenes/SampleScene.unity](Assets/Scenes/SampleScene.unity)。
2. 点击编辑器顶部的 **Play**。
3. 切换到 **Game** 窗口并点击画面，使窗口获得输入焦点。
4. 使用下方按键移动相机、观察海面。

示例场景已经配置海面、相机、灯光、天空和后处理，可以直接运行。首次使用建议保留现有场景配置。

## 相机操作

| 操作          | 按键              |
| ----------- | --------------- |
| 转动视角        | 按住鼠标右键并移动鼠标     |
| 前进 / 后退     | `W` / `S`       |
| 左移 / 右移     | `A` / `D`       |
| 沿相机局部上下方向移动 | `E` / `Q`       |
| 加速移动        | 按住左侧或右侧 `Shift` |
| 调整移动速度      | 鼠标滚轮            |
| 释放光标        | 松开鼠标右键          |

移动和滚轮调速不要求按住右键。在编辑器中结束演示时，再次点击 **Play**。

## 调整配置

建议先停止播放，再修改参数、保存场景与资产，最后重新进入 Play 检查效果。播放期间的场景修改通常不会保留，风浪设置也不保证全部实时更新。

| 想调整的内容             | 配置入口                                                                                              |
| ------------------ | ------------------------------------------------------------------------------------------------- |
| 风速、风向、海浪尺度         | 场景中 `Ocean` 对象的 `OceanFFTController > Settings`                                                   |
| 海面范围、基础网格密度        | `Ocean` 对象的 `OceanGrid`，调整 `Size Meters` 和 `Cell Size Meters`                                     |
| 波浪位移强度、海水颜色、反射、粗糙度 | [Assets/材质/Ocean.mat](Assets/材质/Ocean.mat)                                                        |
| 泡沫生成范围、生成量、保留时间    | [Assets/Settings/PC_Renderer.asset](Assets/Settings/PC_Renderer.asset) 中的 `OceanFFTRenderFeature` |
| 泡沫颜色、边缘强度、距离衰减     | `Ocean.mat` 的 Foam 参数组                                                                            |
| 近处细分与细分距离          | `Ocean.mat` 的 Tessellation 参数组                                                                    |
| 相机移动速度和鼠标灵敏度       | `Main Camera` 对象的 `FreeFlyCamera`                                                                 |
| 太阳方向、颜色与亮度         | 场景中的 `Directional Light`                                                                          |
| 天空和后处理效果           | `SunsetSky` 材质、场景中的 `Global Volume`                                                               |

泡沫的生成参数和显示参数分布在 Renderer 与材质两个入口中，调整时注意区分。修改位移强度后，也需要重新检查泡沫效果。

## 场景与渲染配置检查

如果复制示例场景或将海面接入其他场景，请检查以下配置。

### 海面对象

`Ocean` 对象需要包含：

- `OceanGrid`、`MeshFilter` 和 `MeshRenderer`。
- `OceanFFTController`。
- 海面材质 [Ocean.mat](Assets/材质/Ocean.mat)。

`OceanFFTController` 的两个资源引用应分别指向：

- Compute Shader：[Assets/CS/CS_H0.compute](Assets/CS/CS_H0.compute)。
- Initial Spectrum Texture：[Assets/Texture/InitialSpectrum_Tex.asset](Assets/Texture/InitialSpectrum_Tex.asset)。

建议直接复用已有的初始频谱纹理：512 × 512、2D Array、4 层、浮点格式，并开启 Random Write。不要随意更改它的尺寸和类型。

海面网格按水平 XZ 平面使用，建议保持示例中的朝向。修改网格设置后，如需手动重建，可在 `OceanGrid` 组件菜单中选择 **Regenerate Grid**。

### 渲染管线

1. 在 **Edit > Project Settings > Graphics** 中检查默认渲染管线资产为 [PC_RPAsset](Assets/Settings/PC_RPAsset.asset)。
2. 在 **Project Settings > Quality** 中检查当前质量档使用的管线，避免被其他质量档覆盖。
3. 确认 `PC_RPAsset` 使用 [PC_Renderer](Assets/Settings/PC_Renderer.asset)，且其中的 **OceanFFTRenderFeature** 已启用。
4. 保持 URP 的 **Render Graph** 开启，**Compatibility Mode** 关闭。

项目自带的 `Mobile_Renderer` 没有配置海面 Renderer Feature，不能直接作为海面演示的替代配置。

## 构建 Windows 程序

1. 在编辑器中停止 Play，并保存场景和修改过的资产。
2. 打开 **File > Build Profiles**。
3. 选择或创建 **Windows** 构建配置，使用 **Intel 64-bit / x86_64** 架构，并将它切换为当前配置。
4. 如果提示缺少目标平台模块，通过 Unity Hub 为当前 Editor 安装提示的模块。
5. 检查该配置的场景列表，确保 [SampleScene](Assets/Scenes/SampleScene.unity) 已勾选并放在第一位。项目当前的全局构建列表已包含此场景；自定义配置覆盖场景列表时需要再次检查。
6. 在 Player Settings 中检查 Windows 图形 API。建议首次验证使用 **Direct3D 11**；需要指定时，关闭该目标的 Auto Graphics API 并选择对应 API。
7. 点击 **Build**，选择独立输出目录，例如 `Builds/Windows`；也可使用 **Build and Run** 在本机启动。
8. 等待构建结束，检查 Console 中是否出现错误。

仓库目前使用编辑器构建流程，没有提供自动打包脚本。构建配置的操作说明可参考 [Unity 6.3 官方 Build Profiles 文档](https://docs.unity.com/en-us/engine/6000.3/manual/building-and-publishing/build-settings/create-build-profile)。

## 部署到其他电脑

1. 先在构建机器上运行生成的程序，确认海浪动画和相机操作正常。
2. 将**整个构建输出目录**复制或压缩后发送到目标 Windows 电脑。
3. 保持可执行文件、对应的 `_Data` 目录、`UnityPlayer.dll` 以及其他生成文件的相对位置不变。
4. 在目标电脑解压，运行目录中的 `.exe`，点击窗口后使用相机按键。
5. 在目标电脑上再次检查海浪动画、材质显示和相机操作。

生成文件会随构建后端与选项变化，以实际输出为准。分发时应包含完整运行文件，详见 [Unity 官方 Windows 构建输出说明](https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/windows/standalone-binaries)。

独立程序无需安装 Unity Editor，但目标电脑仍需满足图形功能要求。可以通过窗口关闭按钮结束程序。

## 常见问题

| 问题               | 建议检查                                                                                                          |
| ---------------- | ------------------------------------------------------------------------------------------------------------- |
| 首次打开一直等待，或依赖安装失败 | 检查网络与 Package Manager 报错，等待包恢复和资源导入完成，再运行场景                                                                   |
| 编译报错，无法进入 Play   | 先查看 Console 中的第一条编译错误；确认使用项目指定的 Unity 版本与依赖                                                                   |
| 海面是粉色            | 检查海面材质的 Shader、URP 配置以及当前显卡和图形 API 是否支持所需功能                                                                   |
| 海面平坦、不动或没有显示     | 检查当前质量档是否使用 PC 管线、Renderer Feature 是否启用、Render Graph 是否开启，以及 Controller 的资源引用和纹理 Random Write 设置；停止后重新进入 Play |
| 无法移动或转动相机        | 点击 Game 窗口或程序窗口使其获得焦点；确认 `FreeFlyCamera` 已启用、Input System 依赖已加载                                               |
| 画面卡顿             | 优先降低材质中的近处细分值，或增大 `OceanGrid` 的格子尺寸，再降低运行窗口分辨率                                                                |
| 修改风浪参数后看不到变化     | 停止播放后修改并保存，再重新 Play；不要依赖所有风浪参数在播放中即时更新                                                                        |
| 构建后找不到场景或启动画面不对  | 检查当前 Build Profile 的场景列表，确认 SampleScene 已勾选且是首场景                                                              |
| 程序拷到其他电脑后无法启动    | 检查是否完整复制了输出目录，保留 Data 目录和所有运行库，并确认目标显卡支持所需功能                                                                  |

## 主要文件入口

| 路径                                                                                   | 用途                  |
| ------------------------------------------------------------------------------------ | ------------------- |
| [Assets/Scenes/SampleScene.unity](Assets/Scenes/SampleScene.unity)                   | 默认演示场景              |
| [Assets/脚本](Assets/脚本)                                                               | 海面控制、网格生成、相机操作与渲染配置 |
| [Assets/材质](Assets/材质)                                                               | 海面与天空材质             |
| [Assets/Settings](Assets/Settings)                                                   | URP、Renderer 与后处理配置 |
| [Assets/Texture/InitialSpectrum_Tex.asset](Assets/Texture/InitialSpectrum_Tex.asset) | 初始频谱纹理资产            |
