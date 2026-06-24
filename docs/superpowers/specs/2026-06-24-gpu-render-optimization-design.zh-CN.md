# GPU 渲染优化设计

日期：2026-06-24

## 问题

壁纸应用已经通过 FFmpeg D3D11VA 硬件解码和 DXGI 渲染器降低了 CPU 占用。但用户仍然会看到较高的 GPU 占用，有时约为 30-40%。原因是：只要视频壁纸处于可见状态，应用仍然需要持续执行逐帧 GPU 工作。

当前零拷贝路径的整体结构是合理的：

- `App.xaml.cs` 创建共享的 `GpuDevice`，并交给 `PlaybackManager`。
- `PlaybackManager` 创建带 D3D11VA 硬件设备提供器的 `FfmpegBackend`。
- `PlaybackSession` 初始化 `DxgiRenderer.TryInitZeroCopy()`，并设置 `FfmpegBackend.PreferZeroCopy`。
- `FfmpegBackend` 在硬件解码成功时返回 D3D11 纹理帧。
- `DxgiRenderer` 复制解码出的 NV12 纹理，运行全屏 NV12 到 RGB 的 shader，然后通过 flip-discard swap chain present 到屏幕。

这条路径避免了旧的 CPU 密集型颜色转换，但 GPU 仍然要对每个源视频帧、每个活跃显示器执行硬件视频解码、纹理复制、shader 转换和 present/合成工作。

## 目标

降低 GPU 占用，同时保留：

- 低 CPU 占用，
- 稳定的壁纸播放，
- 现有暂停行为，
- 渲染线程所有权规则，
- 可用时继续使用零拷贝硬件解码，
- 切换壁纸时不出现空白间隙。

第一版实现应优先选择成功概率最高、风险最低的方向：性能测量、帧率调度、用户可选性能档位。

## 非目标

- 不替换 FFmpeg。
- 不移除现有 DXGI/NV12 shader 路径。
- 不引入新依赖。
- 不重写桌面 WorkerW 集成。
- 第一版不把 D3D11 VideoProcessor 作为默认路径。
- 本 spec 不改变媒体库导入或转码行为。

## 设计原则

1. 先测量，再宣称性能收益。
2. 先减少提交帧数，再考虑重写渲染内部。
3. 保留当前可工作的零拷贝路径作为默认渲染基础。
4. 将性能取舍明确暴露给用户。
5. 暂停原因和降频策略分离。

## 推荐方案

以分阶段方式实现 GPU 优化：

1. 添加播放/渲染计数器和基准测试指引。
2. 添加全局壁纸帧率限制。
3. 添加映射到帧率行为的性能档位。
4. 分辨率预算和 D3D11 VideoProcessor 暂时作为后续 spec，除非第一阶段测量证明必须立即处理。

这个方案刻意收敛第一版范围。当前最可控的成本是应用执行 copy/shader/present 路径的频率。帧率上限可以减少这部分工作，同时不扰动解码器初始化、swap chain 创建或桌面挂载逻辑。

## 用户可见行为

设置页新增一个性能相关区域。

性能档位：

- 画质优先：使用原生/源视频帧率。
- 平衡：限制为 30 FPS。
- 省电：限制为 15 FPS。

默认值：平衡。

理由：视频壁纸是背景氛围，不是前台媒体播放器。平衡模式应能降低 60 FPS 壁纸的 GPU 负载，同时保持普通桌面使用下足够顺滑。偏好极致流畅的用户仍可选择画质优先。

现有设置保持不变：

- 全屏时暂停，
- 使用电池时暂停，
- 远程会话时暂停。

电池行为：

- 如果启用 `PauseOnBattery`，保持当前暂停行为。
- 如果禁用 `PauseOnBattery`，仍然应用用户选择的性能档位。

切换性能档位后，应影响正在运行的会话，不要求重启应用。如果实时更新最简单且安全，就原地更新活跃会话。如果必须重启会话，则复用当前“先启动新会话再释放旧会话”的无空白切换模式，而不是先撕掉正在显示的壁纸。

## 架构

### 设置模型

扩展 `AppSettings`，加入渲染性能设置。

推荐结构：

```csharp
public enum WallpaperPerformanceProfile
{
    Quality,
    Balanced,
    Saver
}
```

`AppSettings` 增加：

```csharp
public WallpaperPerformanceProfile PerformanceProfile { get; init; } = WallpaperPerformanceProfile.Balanced;
```

运行时将档位映射到 FPS 上限：

- Quality：不额外限制，沿用源视频时序。
- Balanced：30 FPS。
- Saver：15 FPS。

第一版使用 enum，而不是直接暴露数字 FPS。档位更容易本地化、说明、测试和后续演进。如果之后用户确实需要自定义 FPS，再增加原始数字配置。

### 播放策略

引入一个小的值对象表示有效播放限制。

建议概念：

```csharp
public readonly record struct PlaybackPerformancePolicy(int? MaxPresentFps);
```

职责：

- 将设置/档位转换为运行时限制，
- 给 `PlaybackManager` 一个统一对象传给新会话，
- 允许活跃会话更新策略。

该策略不包含暂停原因。暂停仍然由 `PauseReason` 和 `PlaybackSession.ApplyPauseAsync` / `ClearPauseAsync` 控制。

### PlaybackManager

`PlaybackManager` 持有当前性能策略，并传递给 `PlaybackSession`。

需要行为：

- 新创建的会话拿到当前策略，
- 设置变化时可以更新活跃会话，
- 策略更新不会恢复用户手动暂停的会话，
- 策略更新不会停止播放。

最简单的公开接口：

```csharp
public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy);
```

### PlaybackSession

`PlaybackSession.RenderLoop` 当前会解码下一帧、根据源 PTS sleep，然后 present 每一帧。需要在一帧已经准备好之后、调用 `Present` 之前添加额外的 present gate。

需要计数器：

- 已解码帧数，
- 已 present 帧数，
- 已跳过帧数，
- 上次摘要日志时间。

帧率限制行为：

- Quality/native：行为与当前一致。
- Balanced/Saver：只有距离上一帧 present 已经过了足够 wall-clock 时间，才 present 当前帧。
- 被跳过的帧仍然必须 dispose。
- End-of-stream 循环行为保持不变。
- 暂停状态仍然 sleep，并且不解码。

重要的 GPU 帧生命周期规则：

`FfmpegBackend` 会让一个硬件帧保持存活直到下一次 `NextFrameAsync` 调用。跳过一个 GPU `FrameData` 时，不能让该帧永久被 pin 住。现有生命周期会在下一次解码调用开始时释放上一帧，因此被跳过的帧应在当前循环正常 dispose，并由下一次解码释放 native held frame。测试中应使用 fake frame 覆盖该路径，即使真实 COM 生命周期在 native 层。

### 诊断

从播放会话中添加周期性 Debug 日志：

```text
Playback perf monitor=<id> path=zero-copy decoded=60/s presented=30/s skipped=30/s fpsCap=30
```

日志间隔：30 秒。

这样不需要新增依赖，也不要求每次都运行外部工具，就能获得轻量的内置性能信号。

### 设置 UI

在 `SettingsView` 中新增一行紧凑设置：

- 标签：壁纸性能
- 控件：ComboBox，或符合现有 WPF 风格的分段控件
- 选项：画质优先、平衡、省电

使用现有本地化模式向 `Strings.cs` 资源添加文案。

现有设置页已经有性能相关开关。新行应放在全屏/电池/远程会话暂停设置附近。

## 数据流

1. 应用启动时加载 `AppSettings`。
2. `MainViewModel.Settings` 暴露 `PerformanceProfile`。
3. `App.xaml.cs` 或 `MainViewModel` 将 profile 映射为 `PlaybackPerformancePolicy`。
4. `PlaybackManager` 保存当前策略。
5. `PlaybackManager.SetWallpaperAsync` 将策略传给每个新 `PlaybackSession`。
6. `PlaybackSession.RenderLoop` 使用策略决定 present 或跳过已解码帧。
7. 设置变更后保存 JSON，并调用 `PlaybackManager.UpdatePerformancePolicy`。
8. 活跃会话在下一次 render loop 迭代中使用新策略。

## 错误处理

- 设置 JSON 缺失或无效时，继续回退到 `new AppSettings()`。
- 未知 enum 值应尽量回退到 Balanced。
- 日志失败不能影响播放。
- 策略更新与会话停止发生竞争时，忽略已停止会话。
- 帧被跳过时，在同一轮循环中 dispose。

## 测试策略

单元测试：

- `SettingsService` 能保存并重新加载 `PerformanceProfile`。
- `PlaybackManager` 会把当前策略传给新会话。
- `PlaybackManager.UpdatePerformancePolicy` 会更新活跃 fake session。
- `PlaybackSession` 在 30 FPS 策略下，面对更快的 fake PTS/wall-clock 数据时，present 帧数少于解码帧数。
- Native/Quality 模式保留当前每帧 present 行为。
- 暂停原因仍然阻止 decode/present，并且只有最后一个暂停原因清除后才恢复。
- 启用 FPS 上限时，End-of-stream 循环仍然工作。

手动性能验证：

- 测试 1080p60 和 4K60 壁纸。
- 记录优化前后的任务管理器 GPU 占用。
- 尽可能记录 GPU engine split。
- 可用时使用 PresentMon 记录进程级 present timing。
- 对比画质优先、平衡、省电。
- 验证遮挡、全屏、电池、远程会话暂停仍然工作。

验收目标：

- 60 FPS 源视频在平衡模式下约 present 30 FPS。
- 60 FPS 源视频在省电模式下约 present 15 FPS。
- 可见桌面场景下，平衡/省电相对画质优先能可测量地降低 GPU 占用。
- CPU 占用没有明显回退。
- 没有渲染失败、暂停卡死、壁纸空白间隙或无限日志。

## 风险

### 解码仍可能按源 FPS 运行

第一版跳过的是 present，而不是 decode。硬件视频解码仍可能按源 FPS 运行，因此总 GPU 降幅取决于用户机器上的主要瓶颈是 Video Decode，还是 copy/shader/present/composition。

缓解：添加计数器，并使用 PresentMon/GPU engine split。若 Video Decode 仍是主要成本，再创建后续 spec 处理 decode 级丢帧或导入时生成低 FPS 版本。

### 运动画面可能不够顺滑

15 FPS 和 30 FPS 都是取舍。

缓解：提供画质优先模式，并让选择可随时恢复。

### 时序测试可能不稳定

当前 render loop 使用 `Stopwatch` 和 `Thread.Sleep`。

缓解：尽可能把 FPS gate 设计成一个小而可测试的策略 helper，让单元测试使用确定性时间戳，而不是依赖真实 sleep。

### 设置变更需要会话管线

现有活跃会话除了暂停控制外，似乎没有太多实时设置更新入口。

缓解：添加一个窄的 `UpdatePerformancePolicy` 路径，而不是把完整设置对象注入到会话里。

## 后续 Specs

以下内容刻意不纳入第一版实现：

1. 分辨率预算档位：以 1440p/1080p 内部预算渲染 4K 壁纸。
2. D3D11 VideoProcessor 渲染器：原型验证 `ID3D11VideoProcessorBlt` 是否可替代 NV12 pixel shader。
3. 按显示器可见性降频：对几乎不可见的显示器降频，而不是全局暂停所有会话。
4. 导入时优化版本：为省电模式可选生成低 FPS/低分辨率壁纸副本。

## 待测决策

第一版实现应通过测量回答：

- 平衡模式是否足以降低用户观察到的 GPU 占用？
- 主要 GPU engine 成本来自 Video Decode、3D、Copy，还是 DWM composition？
- 30 FPS 作为后台壁纸默认体验是否可接受？

在这些问题完成测量之前，避免进行更深层的渲染器重写。
