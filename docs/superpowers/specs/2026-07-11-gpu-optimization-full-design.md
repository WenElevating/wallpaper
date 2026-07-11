# GPU 全面优化设计

Date: 2026-07-11

## 背景与现状

本应用已有一份 GPU 优化 spec（`2026-06-24-gpu-render-optimization-design.md`），其第一阶段（帧率限制 / 三档性能档位 / 计数器）已实现。但后续提交 `af68e99`（"Move saver policy to decoder-side throttling"）**撤掉了呈现侧帧率限制**，原因是"解码后再跳帧让画面抖，且没降低解码工作量"。当前状态：

- `MaxPresentFps` 对所有档位都是 `null`（`PlaybackPerformancePolicy.cs:21-22`），`ShouldPresentFrame` 是死代码。
- Saver 档唯一生效的优化是 `skip_frame=NONREF`，但对 H.264/H.265 这类编码，非参考帧本来就少，省得有限。
- 60fps 源每秒跑满 60 次整条管线（解码→拷贝→着色器→Present）。
- 零 GPU 计量基础设施，只有文本日志的帧计数。

用户目标：**全面优化**，覆盖所有场景（日常、多显/4K、游戏时），分阶段实施。用户选择阶段 1 做"解码侧帧率限制"，Saver 档保持"仅限帧率"力度（不切软件解码，不加新档位）。

## 问题诊断

结合代码探查与网上调研，当前 GPU 高占用的四个根因：

1. **3D 引擎被无谓点亮**。当前 NV12→RGB 像素着色器走 3D 管线（VS+PS+Draw，`DxgiRenderer.cs:270-277`）。StackOverflow 实测（`q/71110226`）：同样视频，Chrome/Edge 走视频固定功能管线时 **3D 引擎占用 0%**，自定义着色器路径 3D 引擎持续工作。壁纸永远在背景跑，3D 引擎每帧都亮 = GPU 进不了低功耗状态。

2. **DWM 合成开销**。交换链用 `Scaling.Stretch`（`DxgiRenderer.cs:419-438`），尺寸是视频分辨率而非显示器分辨率，DWM 每帧拉伸合成。若交换链尺寸 = 显示器分辨率且用 flip-discard，OS 有机会走 DirectFlip/MPO，让显示控制器直接翻转、跳过 DWM（微软文档 `for-best-performance--use-dxgi-flip-model`）。

3. **帧率限制被撤掉后无替代**。`af68e99` 撤呈现侧限制是对的（会抖），但没补替代方案。壁纸作为背景氛围，15-30fps 完全够用。

4. **4K/多显放大效应**。每显示器一条独立解码+渲染线程，4K 源每帧拷贝/着色工作量是 1080p 的 4 倍。

## 总体路线图

4 个阶段，每阶段独立可交付、可回滚。阶段 1 完成后，其计量数据决定阶段 2-4 的优先级。

| 阶段 | 内容 | 预期 GPU 收益 | 风险 | 状态 |
|---|---|---|---|---|
| **1** | 解码侧 fps filter 节流 + 计量埋点 + 删死代码 | 中（降帧率直接降所有引擎负载） | 低 | 本 spec 详述 |
| **2** | 分辨率预算（4K→1440p/1080p 内部渲染） | 中高（拷贝/着色/显存线性降） | 低-中 | 后续 spec |
| **3** | VideoProcessor 渲染器（`ID3D11VideoProcessorBlt` 替代像素着色器，走固定功能） | 高（砍掉 3D 引擎这条线） | 中 | 后续 spec |
| **4** | MPO/DirectFlip + 按显示器可见度节流 | 高（跳过 DWM 合成） | 中 | 后续 spec |

## 阶段 1 详细设计

### 设计原则（延续旧 spec）

1. 测量优先于声称性能提升。
2. 在管线最上游（解码后立即）丢帧，比下游（呈现前）丢帧更省。
3. 保留现有零拷贝路径作为渲染基础，不重写渲染器（留给阶段 3）。
4. 性能权衡对用户可见、可逆。
5. 节流策略与暂停原因分离。

### 关于"解码侧帧率限制"的技术现实

用户选择"解码侧帧率限制"作为阶段 1。需要先澄清一个技术现实，避免错误预期：

**真正"解前少解"（编码层跳帧）在通用视频上做不到。** FFmpeg 的 `skip_frame=NONREF` 只丢非参考帧，对 H.264/H.265 这类编码非参考帧本就稀少。真正能在任意视频上稳定降帧率的机制是 **`fps` filter（libavfilter）**——它在解码后丢帧，但比呈现侧跳帧好：

- filter 跑在解码线程池上，丢帧后**渲染线程根本不被唤醒**，省掉整条渲染管线（拷贝→着色器→Present）的开销。
- filter 的丢帧决策比"解完→传回主线程→判断时间→跳过"更早、更廉价。
- 对于零拷贝路径，被 filter 丢掉的帧不会触发 `CopySubresourceRegion` + 着色器 Draw + `Present`——**这才是真正的省 GPU**，而不是省解码本身。

所以阶段 1 的命名虽沿用"解码侧"，但准确说是"管线早期节流（filter 级）"而非"编码层少解"。这与 `af68e99` 撤掉呈现侧跳帧的方向一致：把节流点上移。

### 依赖确认

`lib/ffmpeg/avfilter-10.dll` 已存在且会被复制到输出目录（验证见 `obj/.../FileListAbsolute.txt`）。当前代码无任何 `libavfilter` P/Invoke。阶段 1 需新增 P/Invoke 声明，**不新增二进制依赖**。

### 核心机制：FFmpeg `fps` filter 替代 PTS Sleep 节流

#### 当前节流（要替换的）

`PlaybackSession.cs:335`（同步阻塞等解码）+ `348-357`（粗粒度 `Thread.Sleep` PTS 节流）：
- 解码器以源帧率（60fps）全速出帧
- 渲染线程每帧同步阻塞 `.GetAwaiter().GetResult()`
- 再用 `Thread.Sleep(ms)`（Windows 默认计时器分辨率 ~15.6ms）节流，30fps（33ms）误差 ±50%

#### 新机制

在 FFmpeg 解码后挂 `fps` filter，让解码+滤镜管线以目标帧率稳定输出。渲染线程变成"filter 出一帧就 Present 一帧"，**不再自己 Sleep 节流**。

```
源 60fps → D3D11VA 解码 → fps=30 filter → 渲染线程（被动响应，不主动 Sleep）
```

**为什么不再需要呈现侧 Sleep**：`fps` filter 内部维护精确时间基准，按目标帧率输出，渲染线程被动跟随。filter 出帧节奏由 FFmpeg 内部时钟保证，比 `Thread.Sleep` 精密得多。VSync（`Present(1)`）仍作为最终帧率上限兜底。

**边界情况：filter 偶尔出帧过快**。理论上 filter 严格按目标帧率出帧，但若解码突发完成多帧，filter 可能在短时间内连发。此时 VSync（`Present(1, None)`）会把实际呈现速率钳制到刷新率，渲染线程会短暂在 `Present` 上阻塞（这是 DXGI 的正常 back-pressure，非忙等）。可接受——比粗粒度 Sleep 的抖动小。

#### filter graph 构造

在 `FfmpegBackend.OpenAsync` 成功打开解码器后，构造 filter graph：

```
buffer (args: video_size, pix_fmt, time_base, sar 来自解码器)
  → fps=<target>
  → buffersink
```

- 用 `avfilter_graph_alloc` + `avfilter_graph_create_filter` 构造 `buffer` → `fps` → `buffersink` 三节点链。
- `avfilter_graph_parse_ptr` 或手动 `avfilter_link` 连接。
- `avfilter_graph_config` 完成。
- 每帧：`av_buffersrc_add_frame` 喂入解码帧，循环 `av_buffersink_get_frame` 取出（可能 0 帧也可能多帧）。
- 档位切换时：`avfilter_graph_free` + 重建（target 帧率变了）。
- Quality 档（`MaxPresentFps == null`）：不构造 filter graph，解码帧直通（行为同今天）。

#### 档位映射（重新启用 MaxPresentFps，作用在 filter）

```csharp
public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile) => profile switch
{
    WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15, DecoderFrameDiscard.NonReference),
    WallpaperPerformanceProfile.Balanced => new PlaybackPerformancePolicy(30, DecoderFrameDiscard.Default),
    _ => new PlaybackPerformancePolicy(null, DecoderFrameDiscard.Default),  // Quality: 直通
};
```

Saver 档双保险：NONREF 先减解码量，`fps=15` 再限输出。Balanced 档 30fps。Quality 直通（不挂 filter）。

> 注意：这与旧 spec 的 Quality/Balanced/Saver = 无上限/30/15 数值一致，但实现机制不同——旧 spec 在呈现侧跳帧（已证明会抖并被撤），本 spec 在 filter 侧节流。

### P/Invoke 新增（FfmpegNative.cs）

新增 `AvFilter = "avfilter-10"` 库常量，声明以下函数（沿用现有 `[LibraryImport]` + `partial` 风格）：

```
avfilter_get_by_name
avfilter_graph_alloc
avfilter_graph_free
avfilter_graph_create_filter
avfilter_link
avfilter_graph_config
av_buffersrc_parameters_alloc
av_buffersrc_add_frame_flags
av_buffersink_get_frame
av_buffersink_get_frame_flags
```

`FfmpegOffsets.cs` 新增 filter 相关结构偏移（如需要）。注意 filter API 比 codec context 偏移更稳定（FFmpeg 的 filter 公开 API 很少变 ABI），但仍需按 `avfilter-10.dll`（FFmpeg 7.x）验证。

### 删除死代码

撤掉被 `af68e99` 搁置的呈现侧节流机制：
- `PlaybackSession.cs:138-142`（`ShouldPresentFrame`）
- `PlaybackSession.cs:367-374`（循环里的 present gate 调用）
- `PlaybackSession.cs` 中 `lastPresentedUs`、`StopwatchClock` 相关字段（若仅被 gate 使用）
- `LogPerformanceSummary` 里 `skippedFrames` 计数（filter 侧丢帧不再走渲染线程，无法在 session 层统计；改为 filter 层日志）

保留：`decodedFrames`、`presentedFrames` 计数。节流职责彻底移交 filter。

### 计量：GPU 引擎占用埋点

当前代码零 GPU 计量。这是"先测再改"的关键基础设施，决定阶段 2-4 优先级。

#### 实现方案（分层降级）

1. **首选**：D3D11 query 计数器（`ID3D11Device.CreateQuery` + `D3D11_COUNTER`）。问题：消费级 GPU/驱动支持参差（`D3D11_COUNTER` 在很多 NVIDIA/AMD 消费卡上不可用，仅 D3D11 功能级别有限支持）。启动时探测可用性，不可用则降级。
2. **降级 A**：进程级 GPU 占用，通过 `GPU_ENGINE` ETW（Event Tracing for Windows）或 `IDXGIAdapter3.QueryVideoMemory`。复杂度高。
3. **降级 B（务实推荐）**：不引入复杂 ETW，只增强现有帧计数日志 + 提供 PresentMon 手测指引。每 30s 输出：

```
Playback perf monitor=<id> path=zero-copy decoded=60/30s presented=30/30s filterDropped=30/30s profile=Balanced
```

新增 `filterDropped` 计数（filter 侧丢的帧数），让用户/开发者直观看到节流生效程度。

阶段 1 实现降级 B（零新依赖、零驱动兼容风险）。若后续阶段需要更精细的引擎级数据，再引入 D3D11 query 探测 + PresentMon 自动化。

#### 计量日志格式

`PlaybackSession.LogPerformanceSummary`（`PlaybackSession.cs:310-321`）改为：

```
Playback perf monitor=<id> path=zero-copy|cpu-upload decoded=N/30s presented=N/30s filterDropped=N/30s profile=Quality|Balanced|Saver targetFps=null|30|15 hwDecoding=true|false
```

`filterDropped` = 解码产出 - filter 输出。在 `FfmpegBackend` 内统计：每次 `av_buffersink_get_frame` 返回 EAGAIN / EOF 时，记录被 filter 吃掉但未输出的帧数。

### 数据流（阶段 1）

1. App 启动加载 `AppSettings.PerformanceProfile`。
2. `MainViewModel` → `PlaybackManager.UpdatePerformancePolicy(FromProfile(...))`。
3. `PlaybackSession` 新建时收到 policy，传给 `FfmpegBackend`。
4. `FfmpegBackend.OpenAsync` 成功后，若 `policy.MaxPresentFps != null`，构造 `fps=<target>` filter graph。
5. `FfmpegBackend.NextFrameAsync`：解码 → `av_buffersrc_add_frame` → 循环 `av_buffersink_get_frame` 取出一帧返回（其余被 filter 丢弃，计入 `filterDropped`）。
6. `PlaybackSession.RenderLoop`：不再 Sleep 节流，直接 Present filter 输出的帧。
7. 档位切换：`UpdatePerformancePolicy` → 检测 `MaxPresentFps` 变化 → `avfilter_graph_free` + 按新 target 重建。

### 档位热切换

`af68e99` 已实现档位热切换的 plumbing（`PlaybackSession.RenderLoop:360-365` 检测 policy 变化推给 backend）。阶段 1 扩展 `FfmpegBackend.UpdatePerformancePolicy`：

- target 帧率不变：无操作。
- target 帧率变化（含 null↔非null）：销毁旧 filter graph，按新 target 重建（或 Quality 直通）。
- 重建在解码线程上同步完成，渲染线程下次 `NextFrameAsync` 自然用新 graph。无需停止播放。

### 错误处理

- filter graph 构造失败（如 `avfilter_graph_config < 0`）：日志警告，降级为直通（同 Quality 行为），不中断播放。
- filter graph 构造失败 + 零拷贝：filter 作用于软件帧格式，需确认 `buffer` filter 的 `pix_fmt` 对 NV12（`AV_PIX_FMT_NV12`）和 D3D11（`AV_PIX_FMT_D3D11`=171）的支持。**关键风险**：`fps` filter 可能不支持 D3D11 硬件像素格式，需要先 `hwdownload` 到 NV12 再 filter 再 `hwupload`——但这会引入 GPU↔CPU 往返，抵消零拷贝收益。**必须验证**：若 `fps` filter 不支持 D3D11 格式，阶段 1 方案需调整为"filter 仅作用于零拷贝关闭的路径"或"用 PTS-based 节流但改用高精度 waitable timer"。验证点写进测试策略。
- `av_buffersink_get_frame` 返回 EAGAIN：正常，表示 filter 还没输出下一帧，`NextFrameAsync` 继续喂下一解码帧。
- EOF：释放 filter graph，随 seek-to-zero 重建。
- 档位切换与 stop 竞争：忽略已 stop 的 session。

### 关键风险：fps filter 与 D3D11 硬件格式兼容性

这是阶段 1 最大的技术不确定性。`fps` filter 通过简单丢帧（不重采样/不插值）工作时，理论上对任意像素格式透明，但 libavfilter 的 filter 链在硬件像素格式（`AV_PIX_FMT_D3D11`）上的支持取决于 FFmpeg 编译配置和 `hwframe` 上下文。

**三种可能结果及对策**：

- **结果 A（理想）**：`fps` filter 直接吃 D3D11 格式。零拷贝 + filter 节流共存，最优。
- **结果 B**：`fps` filter 不吃 D3D11，但吃 NV12。需在 filter 链前插 `hwdownload`，但这破坏零拷贝。**对策**：零拷贝路径放弃 filter 节流，回退到"高精度 waitable timer 呈现侧节流"（见下），filter 节流仅用于 CPU 路径。
- **结果 C**：filter 完全不可用于此场景。**对策**：阶段 1 退化为"用 `CreateWaitableTimer` + `SetWaitableTimer` 替换 `Thread.Sleep` PTS 节流"（精度 ~1ms vs Sleep ~15ms），解决旧帧率限制"会抖"的根因，重新启用呈现侧 `MaxPresentFps`。这是纯呈现侧方案，收益小于 filter 方案但无依赖风险。

**阶段 1 实施顺序**：先写一个探针（类似 `WallpaperApp.HwDecodeProbe`）验证结果 A/B/C，再定实现路径。验证结果写进 spec 的实现记录。

### 降级方案：waitable timer 呈现侧节流（若 filter 路径不可行）

若 fps filter 与 D3D11 格式不兼容（结果 B/C），阶段 1 改为：

- 保留 `ShouldPresentFrame` 逻辑，但节流精度从 `Thread.Sleep` 升级为 `CreateWaitableTimer` + `SetWaitableTimer`（`duetime` 负值表示相对周期，精度 ~1ms）。
- 重新启用 `MaxPresentFps`（Balanced=30, Saver=15）。
- 这解决了 `af68e99` 撤帧率限制时的"抖"根因（粗粒度 Sleep），但收益小于 filter 方案（呈现侧丢帧仍跑了完整解码）。
- 此路径下 `filterDropped` 计数无意义，改为 `presentSkipped` 计数。

### 测试策略

#### 单元测试

- `PlaybackPerformancePolicy.FromProfile` 映射正确（Saver=15+NONREF, Balanced=30, Quality=null）。
- `FfmpegBackend` 构造 filter graph：Quality 档不构造，Balanced/Saver 构造且 target 正确。
- 档位热切换：target 变化时 graph 重建，不变时无操作。
- filter graph 构造失败时降级为直通，不抛异常。
- `PauseReason` 仍正确阻止 decode/present，且仅在最后一个 reason 清除时恢复。
- EOF 循环（seek to zero）时 filter graph 正确重建。

#### 探针测试（优先级最高）

新增 `tests/WallpaperApp.FilterProbe/`（仿 `HwDecodeProbe`）：
- 打开一个 D3D11VA 硬件解码的视频。
- 尝试构造 `buffer→fps=30→buffersink`，`buffer` 的 `pix_fmt` 设为 `AV_PIX_FMT_D3D11`。
- 报告：filter graph 是否构造成功？喂 D3D11 帧后 `av_buffersink_get_frame` 返回什么？
- 确定结果 A/B/C，决定主实现路径。

#### 手动性能验证

- 1080p60 + 4K60 源，Task Manager GPU% 对比 Quality / Balanced / Saver。
- PresentMon 确认呈现帧率匹配档位目标（Quality≈源，Balanced≈30，Saver≈15）。
- 验证全屏/电池/远程会话暂停仍正常。
- 验证壁纸切换无空白闪屏。
- 验证档位实时切换平滑（无卡顿、无崩溃）。

#### 验收目标

- Balanced（30fps filter）下，1080p60 源的 GPU% 明显低于 Quality，且画面无明显抖动（区别于旧呈现侧 30fps 限制）。
- Saver（15fps + NONREF）下，GPU% 进一步下降。
- CPU 不显著上升（filter 很轻）。
- `filterDropped` 计数能解释 decoded - presented 的差值。
- 无渲染失败、卡死暂停、空白壁纸间隙。

### 非目标（阶段 1）

- 不改渲染器内部（NV12 着色器、交换链配置）——留给阶段 3/4。
- 不改分辨率——留给阶段 2。
- 不加新依赖（avfilter-10.dll 已存在）。
- 不引入 ETW/D3D11 query（降级 B 计量足够阶段 1）。
- 不切软件解码（用户选择"仅限帧率"）。
- 不加新 UI（复用现有三档 ComboBox）。

## 阶段 2-4 概述（后续 spec，本档仅记录方向）

### 阶段 2：分辨率预算

4K 源内部渲染 1440p/1080p，交换链 `Scaling.Stretch` 上桌。显存、拷贝、着色器负载线性下降。需改 `Nv12Shader` 视口 + 交换链/纹理尺寸。风险：拉伸画质。

### 阶段 3：VideoProcessor 渲染器

用 `ID3D11VideoProcessorBlt` 替代自定义 NV12 像素着色器，走 GPU 固定功能视频管线（Video Processing 引擎），砍掉 3D 引擎这条线。SO 证据显示浏览器走此路径时 3D 引擎占用为 0%。Vortice.Direct3D11 支持 `ID3D11VideoProcessor` 相关 API。风险：VideoProcessor 对 NV12 分辨率对齐有要求（`q/1192424` 提到 height 非 16 倍数时出错）。

### 阶段 4：MPO/DirectFlip + 可见度节流

- 交换链尺寸 = 显示器分辨率 + flip-discard，争取 OS 走 DirectFlip/MPO（显示控制器直接翻转，跳过 DWM 合成）。需验证 WorkerW 子窗口场景下 MPO 是否可用。
- 不可见/被遮挡的显示器降到极低帧率（如 1fps）而非完全暂停，平衡"恢复响应"与"省电"。

## 开放问题（阶段 1 实施时回答）

1. `fps` filter 是否支持 `AV_PIX_FMT_D3D11`？（探针验证 → 结果 A/B/C）
2. 若结果 B/C，waitable timer 降级方案的抖动是否可接受？（PresentMon 验证）
3. Balanced=30 作为默认是否合适？（旧 spec 已设此默认，阶段 1 不改）
4. filter 重建在档位频繁切换时是否有可感知延迟？（实测）
