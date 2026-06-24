# GPU 性能策略修正设计

## 背景

上一版性能档位通过限制 `PlaybackSession` 的 `Present()` 频率来减少 GPU 呈现次数。但用户实测后发现：切到平衡/省电后视频抖动更明显，GPU 占用没有明显下降。

根因是当前策略发生在解码之后：`FfmpegBackend.NextFrameAsync()` 仍然按源视频帧率读包、D3D11VA 硬解、产出 GPU NV12 纹理，随后 `PlaybackSession` 才决定是否丢弃该帧。这样无法降低硬件视频解码负载，还会破坏显示节奏。

## 目标

1. 平衡模式不再通过解码后丢帧制造抖动。
2. 省电模式把降载动作下推到 FFmpeg 解码侧，优先减少要产出的解码帧。
3. 保留现有质量模式、零拷贝渲染路径、暂停原因记账和渲染线程所有权。
4. 日志继续暴露解码/呈现统计，便于用户硬件上验证。

## 非目标

1. 本次不新增依赖。
2. 本次不实现 D3D11 VideoProcessor 替换 NV12 shader。
3. 本次不默认关闭硬件解码；如果省电模式仍不足，再做单独的“省 GPU / 省 CPU”高级策略。

## 设计

`PlaybackPerformancePolicy` 从单一 `MaxPresentFps` 扩展为两个概念：

- `MaxPresentFps`：只用于未来需要显式降低呈现频率的极端策略。本次平衡和省电默认不使用它。
- `DecoderFrameDiscard`：传递给 FFmpeg 解码器的丢弃策略。

档位映射：

- 质量：不限制呈现，不丢弃解码帧。
- 平衡：不限制呈现，不丢弃解码帧。目标是恢复流畅，避免“抖动但不省电”。
- 省电：不限制呈现，但设置解码器跳过非参考帧，减少部分编码流上的硬解输出量。

实现上新增一个小枚举 `DecoderFrameDiscard`，由 `PlaybackSession` 在每次循环中把当前策略同步到支持该能力的后端。`FfmpegBackend` 将其映射到 FFmpeg `skip_frame` option，避免新增脆弱的结构体偏移。

## 风险

FFmpeg 的 `skip_frame=NONREF` 对 B 帧较多的视频通常有效；对全参考帧、低帧率或某些硬件解码器可能效果有限。因此这次修正的最低成功标准是：平衡模式不再抖，省电模式至少把策略放到正确层级，并通过日志验证是否减少解码帧。

如果用户硬件上 GPU 仍高，下一步应实现“省 GPU 模式”：省电档位可选择关闭 D3D11VA，改走软件解码 + CPU BGRA 上传，把视频解码负载从 GPU 转移到 CPU。

## 验收

1. 平衡模式不再设置 `MaxPresentFps=30`。
2. 省电模式设置 `DecoderFrameDiscard=NonReference`。
3. `PlaybackSession` 不再用省电/平衡策略在解码后丢帧作为主要降载手段。
4. 单元测试覆盖策略映射、策略下发到后端、以及平衡模式呈现所有已解码帧。
