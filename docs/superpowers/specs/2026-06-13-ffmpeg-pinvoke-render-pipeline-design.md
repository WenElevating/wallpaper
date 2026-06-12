# FFmpeg P/Invoke + Direct2D 渲染管线设计

## 问题

当前 `FfmpegBackend` 使用 CLI 子进程（`ffmpeg.exe -f rawvideo -pix_fmt bgra -`）向管道输出裸帧，导致：
- 数据走进程间管道，额外拷贝带宽浪费
- 帧精确 seek 需重启子进程，延迟高
- 多显示器场景 N 个子进程并行浪费资源
- 依赖外部 ffmpeg.exe，部署脆弱

需要替换为进程内 FFmpeg P/Invoke 调用 + Direct2D HWND 渲染。

## 架构总览

```
PlaybackManager → PlaybackSession (per monitor)
                    ├── FfmpegBackend (P/Invoke 解码)
                    │    └── av_format_ctx + av_codec_ctx + sws_ctx
                    └── D2dRenderer (IFrameRenderer)
                         └── ID2D1HwndRenderTarget + ID2D1Bitmap

DesktopHost → WallpaperWindow (per monitor)
               └── STATIC HWND (WorkerW child)
```

### 新增文件

| 文件 | 职责 |
|---|---|
| `Services/Playback/IFrameRenderer.cs` | 渲染器接口 |
| `Services/Playback/D2dRenderer.cs` | D2D 实现 |
| `Interop/D2D1.cs` | Direct2D COM vtable 声明（~200 行） |
| `Interop/FfmpegOffsets.cs` | FFmpeg struct 字段偏移常量 |

### 改动文件

| 文件 | 改动 |
|---|---|
| `FfmpegNative.cs` | 补充 `av_find_best_stream`, `av_seek_frame`, `avformat_version` |
| `FfmpegBackend.cs` | 完全重写为 P/Invoke 解码管线 |
| `PlaybackSession.cs` | 帧不再丢弃，调用 `_renderer.Present()` + PTS 帧定时 |
| `WallpaperWindow.cs` | 暴露 HWND 供 D2D RenderTarget 创建 |
| `IPlaybackBackend.cs` | 无需变更 |
| `PlaybackManager.cs` | 无需变更 |
| `DesktopHost.cs` | 无需变更 |
| `App.xaml.cs` | DI 注册（D2dRenderer 是 PerSession，不注册 Singleton） |

## 解码管线

### 状态机

```
Closed → Opened → Playing ↔ Paused
                     ↓
                   Closed
```

### OpenAsync

```
avformat_open_input(&fmtCtx, url, null, null)
avformat_find_stream_info(fmtCtx, null)

videoIdx = av_find_best_stream(fmtCtx, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0)
if videoIdx < 0 → return false

// 手动读取 AVFormatContext->streams[videoIdx]->codecpar
IntPtr streamsPtr = Marshal.ReadIntPtr(fmtCtx, STREAMS_OFFSET)
IntPtr streamPtr  = Marshal.ReadIntPtr(streamsPtr, videoIdx * IntPtr.Size)
IntPtr codecPar   = Marshal.ReadIntPtr(streamPtr, CODECPAR_OFFSET)

codecCtx = avcodec_alloc_context3(decoder)
avcodec_parameters_to_context(codecCtx, codecPar)
avcodec_open2(codecCtx, decoder, null)

// 获取宽高（从 codecCtx 通过 P/Invoke 无法读取，用 codecPar 手动读）
width  = Marshal.ReadInt32(codecPar, CODECPAR_WIDTH_OFFSET)
height = Marshal.ReadInt32(codecPar, CODECPAR_HEIGHT_OFFSET)

swsCtx = sws_getContext(width, height, AV_PIX_FMT_YUV420P,
                        width, height, AV_PIX_FMT_BGRA,
                        SWS_BILINEAR, null, null, null)

avFrame  = av_frame_alloc()
avPacket = av_packet_alloc()

// 双缓冲分配
stride    = width * 4
frameSize = height * stride
_bufferA  = av_malloc((ulong)frameSize)
_bufferB  = av_malloc((ulong)frameSize)
```

### NextFrameAsync

```
do {
    int ret = av_read_frame(fmtCtx, avPacket)
    if (ret < 0) { EndOfStream 事件 → return null }
    if (packet->stream_index != videoIdx) { av_packet_unref(avPacket); continue }
    break
} while (true)

avcodec_send_packet(codecCtx, avPacket)
av_packet_unref(avPacket)

// 可能 receive 多次（内部帧缓冲区）
int gotFrame = 0
do {
    ret = avcodec_receive_frame(codecCtx, avFrame)
    if (ret == 0) { gotFrame = 1; break }
    if (ret == AVERROR(EAGAIN)) { return null }  // 需要更多包
    if (ret < 0) { return null }                  // 错误
} while (false)

if (gotFrame == 0) return null

// 色彩空间转换到双缓冲区轮换
IntPtr activeBuffer = _useBufferA ? _bufferA : _bufferB
int[] srcStride = [frame->linesize[0], frame->linesize[1], frame->linesize[2], 0]
IntPtr srcSlice = ...  // frame->data 数组指针

sws_scale(swsCtx, frame->data, srcStride, 0, height, &activeBuffer, &stride)

pts = av_frame_get_best_effort_timestamp(avFrame)
_useBufferA = !_useBufferA

return new FrameData(activeBuffer, width, height, stride, pts)
```

### SeekAsync

```
avcodec_flush_buffers(codecCtx)
av_seek_frame(fmtCtx, videoIdx, targetPts, AVSEEK_FLAG_BACKWARD)
```

### Play / StopAsync

```
PlayAsync:  _isPlaying = true; _isPaused = false
PauseAsync: _isPaused = true
StopAsync:  _isPlaying = false; _isPaused = false; _position = 0;
            avcodec_flush_buffers(codecCtx)
```

### Dispose

```
av_frame_free(avFrame)
av_packet_free(avPacket)
sws_freeContext(swsCtx)
avcodec_free_context(codecCtx)
avformat_close_input(fmtCtx)
av_free(_bufferA)
av_free(_bufferB)
```

## 字段偏移常量 (`FfmpegOffsets.cs`)

针对打包的 FFmpeg 7.1 (avformat-61 | avcodec-61 | avutil-59)：

| 字段 | 偏移 | 类型 |
|---|---|---|
| `AVFormatContext.nb_streams` | 0x68 | int |
| `AVFormatContext.streams` | 0x70 | IntPtr (AVStream**) |
| `AVStream.codecpar` | 0x60 | IntPtr (AVCodecParameters*) |
| `AVCodecParameters.codec_type` | 0x04 | int |
| `AVCodecParameters.codec_id` | 0x08 | int |
| `AVCodecParameters.width` | 0x2C | int |
| `AVCodecParameters.height` | 0x30 | int |

这些偏移量在换 FFmpeg 版本时必须同步更新。验证方式：运行时调用 `avformat_version()` + `avcodec_version()` 记录版本号到日志。

## 渲染管线

### IFrameRenderer 接口

```csharp
public interface IFrameRenderer : IDisposable
{
    bool Present(FrameData frame);    // 返回 false → HWND 失效
    void Resize(int width, int height);
}
```

### D2dRenderer

```
class D2dRenderer : IFrameRenderer
{
    IntPtr _factory;         // ID2D1Factory*
    IntPtr _renderTarget;    // ID2D1HwndRenderTarget*
    IntPtr _bitmap;          // ID2D1Bitmap*
    int _width, _height;

    Create(IntPtr hwnd, int w, int h):
        D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, ref _factory)
        _factory.CreateHwndRenderTarget(RTP(), HwndRTP(hwnd, size), out _renderTarget)
        _renderTarget.CreateBitmap(size, stride, bmpProps, out _bitmap)

    Present(FrameData frame):
        _bitmap.CopyFromMemory(null, frame.Buffer, frame.Stride)
        hr = _renderTarget.BeginDraw()
        _renderTarget.DrawBitmap(_bitmap)
        hr = _renderTarget.EndDraw()
        if (hr == D2DERR_RECREATE_TARGET) → Recreate()
        frame.Dispose()
        return hr >= 0
}
```

### D2D COM Interop (`D2D1.cs`)

Direct2D 通过 COM vtable 调用。按需声明最小方法集：

**ID2D1Factory** (vtable slot 4-29, 只用 slot 13):
- slot 13: `CreateHwndRenderTarget`

**ID2D1RenderTarget** (vtable slot 4-36, 只用 slot 8, 9, 16):
- slot 8:  `BeginDraw`
- slot 9:  `EndDraw`
- slot 16: `DrawBitmap`
- slot 22: `CreateBitmap`

**ID2D1Bitmap** (vtable slot 4-10, 只用 slot 7):
- slot 7:  `CopyFromMemory`

每个接口通过 `Marshal.GetDelegateForFunctionPointer` + `virtual method table` 调用。

**D2D1 structs** 用 `[StructLayout(LayoutKind.Sequential)]` 声明：
- `D2D1_SIZE_U` (UInt32 width, height)
- `D2D1_RENDER_TARGET_PROPERTIES`
- `D2D1_HWND_RENDER_TARGET_PROPERTIES`
- `D2D1_BITMAP_PROPERTIES`
- `D2D1_RECT_U`

## 帧交付与定时

### 数据流

```
解码线程: sws_scale → double buffer → FrameData → 返回给 session
Session:  FrameData → D2dRenderer.Present(frame)
D2DRender: CopyFromMemory(→GPU) → DrawBitmap ↓
           frame.Dispose() [标记消费，不释放内存]
内存所有权: 始终在 FfmpegBackend 双缓冲中
```

### 帧定时

```
_lastPts = 0
render loop:
    t0 = Stopwatch.GetTimestamp()
    frame = await backend.NextFrameAsync(ct)

    renderer.Present(frame)

    if _lastPts > 0 && frame.PtsUs > _lastPts:
        frameDuration = (frame.PtsUs - _lastPts) / 1_000_000.0  // us → sec
        elapsed = (GetTimestamp() - t0) / Frequency
        waitMs = max(0, (frameDuration - elapsed) * 1000)
        if waitMs > 0: delay(waitMs)

    _lastPts = frame.PtsUs
```

## 错误处理

| 故障场景 | 行为 |
|---|---|
| `avformat_open_input` 失败 | OpenAsync false → PlaybackManager 走 MfBackend |
| `av_read_frame` < 0 | EndOfStream → RenderLoop seek(0) 循环 |
| sws_scale/sws_getContext 失败 | NextFrameAsync return null (跳帧) |
| `EndDraw()` 返回 `D2DERR_RECREATE_TARGET` | 重建 `_renderTarget` + `_bitmap` |
| Present 返回 false (HWND 消失) | PlaybackSession 自动停止 |
| 解码异常 | catch → log Warn → return null (跳帧) |

## 多显示器

每个显示器创建独立的 `PlaybackSession`：
- `PlaybackManager.SetWallpaperAsync` 为每个 monitorId 创建 `FfmpegBackend` + `D2dRenderer` + `PlaybackSession`
- 各 session 独立解码、独立 D2D renderer、独立定时
- `WallpaperWindow.Handle` 传递 HWND 给 `D2dRenderer`
- N 显示器 = N 个 ffmpeg context 进程内并行解码（无子进程开销）

## 向后兼容性

- `IPlaybackBackend` 接口不变
- `PlaybackSession` 构造函数签名不变（仍取 `IPlaybackBackend`），新增可选 `IFrameRenderer` 参数
- 测试继续通过（对 MfBackend 无影响）

## 未覆盖（后续迭代）

- 硬件解码（可尝试 hwcontext 加速，但代码复杂度大增）
- 音频解码/混音（当前无桌面壁纸音频需求）
- 动态码率适应（壁纸为本地文件，无网络流）
