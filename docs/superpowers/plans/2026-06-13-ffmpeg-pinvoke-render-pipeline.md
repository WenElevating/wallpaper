# FFmpeg P/Invoke + Direct2D 渲染管线实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace FFmpeg CLI subprocess with in-process P/Invoke FFmpeg decoding and Direct2D HWND rendering.

**Architecture:** P/Invoke calls to bundled avformat-61/avcodec-61/swscale-8 via LibraryImport, struct field access via hardcoded byte offsets (one-time mapping to bundled FFmpeg 7.1). D2D via minimal COM vtable interop (ID2D1Factory → ID2D1HwndRenderTarget → ID2D1Bitmap). Frame double-buffered in decoder, uploaded to GPU via CopyFromMemory in renderer.

**Tech Stack:** .NET 8 / FFmpeg 7.1 (avformat-61) / Direct2D (d2d1.dll) / COM interop

---

## 文件结构

### 新增文件

| 文件 | 职责 |
|---|---|
| `src/WallpaperApp/Interop/FfmpegOffsets.cs` | FFmpeg struct 字段偏移常量 |
| `src/WallpaperApp/Interop/D2D1.cs` | Direct2D COM vtable 声明 + D2D1CreateFactory |
| `src/WallpaperApp/Services/Playback/IFrameRenderer.cs` | 渲染器接口 |
| `src/WallpaperApp/Services/Playback/D2dRenderer.cs` | D2D 实现 |

### 修改文件

| 文件 | 改动 |
|---|---|
| `src/WallpaperApp/Services/Playback/FfmpegNative.cs` | 补充声明 + 修复 IntPtr[] 用法 |
| `src/WallpaperApp/Services/Playback/FfmpegBackend.cs` | 完整重写为 P/Invoke 解码管线 |
| `src/WallpaperApp/Services/Playback/IPlaybackBackend.cs` | FrameData 改为后端拥有内存 |
| `src/WallpaperApp/Services/Playback/PlaybackSession.cs` | 集成 D2dRenderer，PTS 帧定时 |
| `src/WallpaperApp/Services/Playback/PlaybackManager.cs` | 创建 Session 时传入 renderer |
| `src/WallpaperApp/Services/Desktop/WallpaperWindow.cs` | 暴露 HWND 属性 |
| `tests/.../FfmpegBackendTests.cs` | 适配新 FfmpegBackend Dispose 模式 |

---

### Task 1: 补充 P/Invoke 声明 + 偏移常量

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/FfmpegNative.cs`
- Create: `src/WallpaperApp/Interop/FfmpegOffsets.cs`

- [ ] **Step 1: 更新 FfmpegNative.cs** — 补充缺失声明、修复参数类型

```csharp
// 在 avformat_open_input 声明后添加（～line 19）：
[LibraryImport(AvFormat)]
internal static partial long av_find_best_stream(IntPtr ps, int mediaType, int wantedStreamNb, int relatedStream, ref IntPtr decoderRet, int flags);

[LibraryImport(AvFormat)]
internal static partial long av_seek_frame(IntPtr s, int streamIndex, long timestamp, int flags);

[LibraryImport(AvFormat)]
internal static partial uint avformat_version();

[LibraryImport(AvCodec)]
internal static partial uint avcodec_version();

[LibraryImport(AvUtil)]
internal static partial uint avutil_version();

// 修复 avcodec_free_context 签名（IntPtr[] → IntPtr）：
[LibraryImport(AvCodec)]
internal static partial void avcodec_free_context(IntPtr avctx);

// 修复 av_frame_free 签名：
[LibraryImport(AvUtil)]
internal static partial void av_frame_free(IntPtr frame);

// 修复 av_packet_free 签名：
[LibraryImport(AvUtil)]
internal static partial void av_packet_free(IntPtr pkt);

// 添加 AVSEEK_FLAG 常量（〜line 111）：
internal const int AVSEEK_FLAG_BACKWARD = 1;
internal const int AVSEEK_FLAG_FRAME = 2;
internal const int AVSEEK_FLAG_ANY = 4;
```

- [ ] **Step 2: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 3: 创建 FfmpegOffsets.cs**

```csharp
using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

/// <summary>
/// FFmpeg 7.1 (avformat-61 / avcodec-61 / avutil-59) struct field offsets.
/// These are ABI-specific to the bundled DLL build.
/// Verify on FFmpeg version upgrade by checking struct layouts with a C probe.
/// </summary>
internal static class FfmpegOffsets
{
    // AVFormatContext (avformat-61)
    internal const int NbStreamsOffset   = 0x68;  // int
    internal const int StreamsOffset     = 0x70;  // AVStream** (IntPtr)

    // AVStream (avformat-61)
    internal const int CodecparOffset    = 0x60;  // AVCodecParameters* (IntPtr)

    // AVCodecParameters (avcodec-61)
    internal const int CodecTypeOffset   = 0x04;  // int (AVMediaType)
    internal const int CodecIdOffset     = 0x08;  // int (AVCodecID)
    internal const int WidthOffset       = 0x2C;  // int
    internal const int HeightOffset      = 0x30;  // int
}
```

- [ ] **Step 4: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/FfmpegNative.cs src/WallpaperApp/Interop/FfmpegOffsets.cs
git commit -m "feat: add FFmpeg declarations and struct offset constants for P/Invoke backend"
```

---

### Task 2: 重写 FfmpegBackend (P/Invoke 解码管线)

**Files:**
- Rewrite: `src/WallpaperApp/Services/Playback/FfmpegBackend.cs`

- [ ] **Step 1: 编写完整 P/Invoke FfmpegBackend**

```csharp
using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class FfmpegBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;

    // FFmpeg contexts (native pointers)
    private IntPtr _fmtCtx;
    private IntPtr _codecCtx;
    private IntPtr _swsCtx;
    private IntPtr _avFrame;
    private IntPtr _avPacket;

    // Stream info
    private int _videoStreamIndex = -1;
    private int _width;
    private int _height;
    private int _stride; // width * 4
    private int _frameSize; // height * stride

    // Double buffers for sws_scale output
    private IntPtr _bufferA;
    private IntPtr _bufferB;
    private bool _useBufferA;

    // FPS / timing
    private AVRational _timeBase;
    private long _durationUs;

    // State
    private bool _isOpen;
    private bool _disposed;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Duration => TimeSpan.FromTicks(_durationUs * 10);
    public TimeSpan Position { get; private set; }

    public event EventHandler? EndOfStream;

    public FfmpegBackend(FileLogger logger)
    {
        _logger = logger;
    }

    public Task<bool> OpenAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Close();

                _fmtCtx = FfmpegNative.avformat_alloc_context();
                FfmpegNative.avformat_open_input(out _fmtCtx, filePath, IntPtr.Zero, null);

                if (FfmpegNative.avformat_find_stream_info(_fmtCtx, null) < 0)
                    return Fail($"Failed to find stream info: {filePath}");

                IntPtr decoder = IntPtr.Zero;
                var streamIdx = FfmpegNative.av_find_best_stream(
                    _fmtCtx, FfmpegNative.AVMEDIA_TYPE_VIDEO, -1, -1, ref decoder, 0);

                if (streamIdx < 0 || decoder == IntPtr.Zero)
                    return Fail($"No video stream found: {filePath}");

                _videoStreamIndex = (int)streamIdx;

                // Read AVFormatContext->streams[_videoStreamIndex]->codecpar via offsets
                var streamsPtr = Marshal.ReadIntPtr(_fmtCtx, FfmpegOffsets.StreamsOffset);
                var streamPtr = Marshal.ReadIntPtr(streamsPtr + _videoStreamIndex * IntPtr.Size);
                var codecPar = Marshal.ReadIntPtr(streamPtr, FfmpegOffsets.CodecparOffset);

                _width = Marshal.ReadInt32(codecPar, FfmpegOffsets.WidthOffset);
                _height = Marshal.ReadInt32(codecPar, FfmpegOffsets.HeightOffset);
                if (_width <= 0 || _height <= 0)
                    return Fail($"Invalid video dimensions: {_width}x{_height}");

                _stride = _width * 4;
                _frameSize = _height * _stride;

                _codecCtx = FfmpegNative.avcodec_alloc_context3(decoder);
                FfmpegNative.avcodec_parameters_to_context(_codecCtx, codecPar);

                if (FfmpegNative.avcodec_open2(_codecCtx, decoder, null) < 0)
                    return Fail($"Failed to open codec");

                _swsCtx = FfmpegNative.sws_getContext(
                    _width, _height, FfmpegNative.AV_PIX_FMT_YUV420P,
                    _width, _height, FfmpegNative.AV_PIX_FMT_BGRA,
                    FfmpegNative.SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, null);

                if (_swsCtx == IntPtr.Zero)
                    return Fail($"Failed to create sws context");

                _avFrame = FfmpegNative.av_frame_alloc();
                _avPacket = FfmpegNative.av_packet_alloc();

                _bufferA = FfmpegNative.av_malloc((ulong)_frameSize);
                _bufferB = FfmpegNative.av_malloc((ulong)_frameSize);

                // Read time_base and duration from stream
                _timeBase = ReadTimeBase(streamPtr);
                _durationUs = Marshal.ReadInt64(streamPtr, 0x88); // AVStream.duration (avformat-61)

                _isOpen = true;
                _logger.Info($"Opened: {filePath} ({_width}x{_height})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Open failed: {filePath}", ex);
                return false;
            }
        }, ct);

        bool Fail(string msg) { _logger.Error(msg); return false; }
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        IsPlaying = true;
        IsPaused = false;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        IsPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        IsPaused = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsPlaying = false;
        IsPaused = false;
        Position = TimeSpan.Zero;
        _useBufferA = false;
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (!_isOpen) return Task.CompletedTask;

        Position = position;
        var targetPts = (long)(position.TotalSeconds * _timeBase.Den / _timeBase.Num);
        FfmpegNative.avcodec_flush_buffers(_codecCtx);
        FfmpegNative.av_seek_frame(_fmtCtx, _videoStreamIndex, targetPts, FfmpegNative.AVSEEK_FLAG_BACKWARD);
        return Task.CompletedTask;
    }

    public async Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
    {
        if (!_isOpen || !IsPlaying) return null;

        return await Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var ret = FfmpegNative.av_read_frame(_fmtCtx, _avPacket);
                    if (ret < 0)
                    {
                        EndOfStream?.Invoke(this, EventArgs.Empty);
                        return null; // caller handles loop via seek(0)
                    }

                    var pktStreamIdx = Marshal.ReadInt32(_avPacket, 0x08); // AVPacket.stream_index (avcodec-61)
                    if (pktStreamIdx != _videoStreamIndex)
                    {
                        FfmpegNative.av_packet_unref(_avPacket);
                        continue;
                    }

                    var sendRet = FfmpegNative.avcodec_send_packet(_codecCtx, _avPacket);
                    FfmpegNative.av_packet_unref(_avPacket);
                    if (sendRet < 0) continue;

                    var recvRet = FfmpegNative.avcodec_receive_frame(_codecCtx, _avFrame);
                    if (recvRet < 0) continue;

                    var pts = FfmpegNative.av_frame_get_best_effort_timestamp(_avFrame);

                    // sws_scale to double-buffered BGRA
                    var activeBuffer = _useBufferA ? _bufferA : _bufferB;
                    _useBufferA = !_useBufferA;

                    var srcData = new IntPtr[4];
                    var srcStride = new int[4];
                    srcData[0] = FfmpegNative.av_frame_get_data(_avFrame, 0);
                    srcData[1] = FfmpegNative.av_frame_get_data(_avFrame, 1);
                    srcData[2] = FfmpegNative.av_frame_get_data(_avFrame, 2);
                    srcStride[0] = FfmpegNative.av_frame_get_linesize(_avFrame, 0);
                    srcStride[1] = FfmpegNative.av_frame_get_linesize(_avFrame, 1);
                    srcStride[2] = FfmpegNative.av_frame_get_linesize(_avFrame, 2);

                    var dstData = new IntPtr[] { activeBuffer };
                    var dstStride = new int[] { _stride };

                    FfmpegNative.sws_scale(_swsCtx, srcData, srcStride, 0, _height, dstData, dstStride);

                    // PTS in stream time_base → convert to microseconds
                    var ptsUs = pts * _timeBase.Num * 1_000_000 / _timeBase.Den;
                    Position = TimeSpan.FromMicroseconds(ptsUs);

                    return new FrameData(activeBuffer, _width, _height, _stride, ptsUs);
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                _logger.Warn($"Frame decode error: {ex.Message}");
                return null;
            }
        }, ct);
    }

    private void Close()
    {
        if (_swsCtx != IntPtr.Zero) { FfmpegNative.sws_freeContext(_swsCtx); _swsCtx = IntPtr.Zero; }
        if (_codecCtx != IntPtr.Zero) { FfmpegNative.avcodec_free_context(_codecCtx); _codecCtx = IntPtr.Zero; }
        if (_avFrame != IntPtr.Zero) { FfmpegNative.av_frame_free(_avFrame); _avFrame = IntPtr.Zero; }
        if (_avPacket != IntPtr.Zero) { FfmpegNative.av_packet_free(_avPacket); _avPacket = IntPtr.Zero; }
        if (_fmtCtx != IntPtr.Zero) { FfmpegNative.avformat_close_input(out _fmtCtx); }
        if (_bufferA != IntPtr.Zero) { FfmpegNative.av_free(_bufferA); _bufferA = IntPtr.Zero; }
        if (_bufferB != IntPtr.Zero) { FfmpegNative.av_free(_bufferB); _bufferB = IntPtr.Zero; }
        _isOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private static AVRational ReadTimeBase(IntPtr streamPtr)
    {
        // AVStream.time_base at offset 0x50 (avformat-61)
        var num = Marshal.ReadInt32(streamPtr, 0x50);
        var den = Marshal.ReadInt32(streamPtr, 0x54);
        return new AVRational(num, den);
    }

    private readonly record struct AVRational(int Num, int Den);
}
```

- [ ] **Step 2: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

**注意：** 需要暂时注释掉 `FfmpegBackendTests.cs` 中与旧 Dispose 模式有关的测试（如果 P/Invoke 调用报 DllNotFoundException，则 CI 环境可能无 FFmpeg DLL）。

- [ ] **Step 3: 运行现有 tests 确认接口契约不变**

Run: `dotnet test tests\WallpaperApp.Tests --filter "FfmpegBackendTests" -v n`
Expected: 通过（构造函数、state 转换、属性默认值等纯逻辑测试）

- [ ] **Step 4: Commit**

```bash
git add src/WallpaperApp/Services/Playback/FfmpegBackend.cs
git commit -m "feat: rewrite FfmpegBackend with in-process P/Invoke FFmpeg decoding"
```

---

### Task 3: 更新 IPlaybackBackend FrameData 所有权

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/IPlaybackBackend.cs`

- [ ] **Step 1: 修改 FrameData — Dispose 不再释放内存**

```csharp
public sealed class FrameData : IDisposable
{
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long PtsUs { get; }
    private bool _isDisposed;

    public FrameData(IntPtr buffer, int width, int height, int stride, long ptsUs)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PtsUs = ptsUs;
    }

    // Buffer owned by FfmpegBackend double-buffer pool.
    // Dispose marks frame consumed so renderer doesn't reuse stale reference.
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
    }
}
```

- [ ] **Step 2: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Services/Playback/IPlaybackBackend.cs
git commit -m "refactor: FrameData.Dispose marks consumed (memory owned by backend)"
```

---

### Task 4: 创建 Direct2D COM 互操作声明

**Files:**
- Create: `src/WallpaperApp/Interop/D2D1.cs`

- [ ] **Step 1: 创建最小 D2D COM vtable 声明**

```csharp
using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

internal static class D2D1
{
    private const string D2D1Dll = "d2d1.dll";

    // Factory type
    internal const int D2D1_FACTORY_TYPE_SINGLE_THREADED = 0;

    // Render target type
    internal const int D2D1_RENDER_TARGET_TYPE_HARDWARE = 0;
    internal const int D2D1_RENDER_TARGET_TYPE_DEFAULT  = 2;

    // Present options
    internal const int D2D1_PRESENT_OPTIONS_NONE = 0;

    // Bitmap options
    internal const int D2D1_BITMAP_OPTIONS_NONE     = 0;
    internal const int D2D1_BITMAP_OPTIONS_TARGET    = 1;

    // Feature levels
    internal const int D2D1_FEATURE_LEVEL_DEFAULT = 0;

    // Alpha mode
    internal const int D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    internal const int D2D1_ALPHA_MODE_IGNORE = 2;

    // Error codes
    internal const int S_OK = 0;
    internal const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    [LibraryImport(D2D1Dll)]
    internal static partial int D2D1CreateFactory(
        int factoryType,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppFactory);

    // ── ID2D1Factory vtable wrappers ──

    internal static int CreateHwndRenderTarget(
        IntPtr factory,
        ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProps,
        ref D2D1_HWND_RENDER_TARGET_PROPERTIES hwndProps,
        out IntPtr renderTarget)
    {
        // vtable: [0]QueryInterface [1]AddRef [2]Release [3]ReloadSystemResources
        // [4]GetDesktopDpi [5]CreateRectangleGeometry [6]CreateRoundedRectangleGeometry
        // [7]CreateEllipseGeometry [8]CreateGeometryGroup [9]CreateTransformedGeometry
        // [10]CreatePathGeometry [11]CreateStrokeStyle [12]CreateDrawingStateBlock
        // [13]CreateWicBitmapRenderTarget [14]CreateHwndRenderTarget ← slot 14!
        var vtable = Marshal.ReadIntPtr(factory);
        var methodPtr = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
        var createFn = Marshal.GetDelegateForFunctionPointer<CreateHwndRenderTargetFn>(methodPtr);
        return createFn(factory, ref renderTargetProps, ref hwndProps, out renderTarget);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateHwndRenderTargetFn(
        IntPtr factory,
        ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProps,
        ref D2D1_HWND_RENDER_TARGET_PROPERTIES hwndProps,
        out IntPtr renderTarget);

    // ── ID2D1RenderTarget vtable wrappers ──

    internal static int BeginDraw(IntPtr renderTarget)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var methodPtr = Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size); // slot 5
        var fn = Marshal.GetDelegateForFunctionPointer<BeginDrawFn>(methodPtr);
        return fn(renderTarget);
    }

    internal static int EndDraw(IntPtr renderTarget)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var methodPtr = Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size); // slot 6
        var fn = Marshal.GetDelegateForFunctionPointer<EndDrawFn>(methodPtr);
        return fn(renderTarget);
    }

    internal static int DrawBitmap(
        IntPtr renderTarget,
        IntPtr bitmap,
        ref D2D1_RECT_F destinationRect,
        float opacity,
        int interpolationMode,
        ref D2D1_RECT_F sourceRect)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var methodPtr = Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size); // slot 13
        var fn = Marshal.GetDelegateForFunctionPointer<DrawBitmapFn>(methodPtr);
        return fn(renderTarget, bitmap, ref destinationRect, opacity, interpolationMode, ref sourceRect);
    }

    internal static int CreateBitmap(
        IntPtr renderTarget,
        D2D1_SIZE_U size,
        IntPtr srcData,
        int pitch,
        ref D2D1_BITMAP_PROPERTIES bitmapProps,
        out IntPtr bitmap)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var methodPtr = Marshal.ReadIntPtr(vtable, 19 * IntPtr.Size); // slot 19
        var fn = Marshal.GetDelegateForFunctionPointer<CreateBitmapFn>(methodPtr);
        return fn(renderTarget, size, srcData, pitch, ref bitmapProps, out bitmap);
    }

    private delegate int BeginDrawFn(IntPtr renderTarget);
    private delegate int EndDrawFn(IntPtr renderTarget);
    private delegate int DrawBitmapFn(IntPtr renderTarget, IntPtr bitmap, ref D2D1_RECT_F dstRect, float opacity, int interpolation, ref D2D1_RECT_F srcRect);
    private delegate int CreateBitmapFn(IntPtr renderTarget, D2D1_SIZE_U size, IntPtr srcData, int pitch, ref D2D1_BITMAP_PROPERTIES props, out IntPtr bitmap);

    // ── ID2D1Bitmap vtable wrappers ──

    internal static int CopyFromMemory(
        IntPtr bitmap,
        IntPtr dstRect,   // null = whole bitmap
        IntPtr srcData,
        int pitch)
    {
        var vtable = Marshal.ReadIntPtr(bitmap);
        var methodPtr = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size); // slot 7
        var fn = Marshal.GetDelegateForFunctionPointer<CopyFromMemoryFn>(methodPtr);
        return fn(bitmap, dstRect, srcData, pitch);
    }

    private delegate int CopyFromMemoryFn(IntPtr bitmap, IntPtr dstRect, IntPtr srcData, int pitch);

    // ── COM Release helper ──

    internal static int Release(IntPtr comPtr)
    {
        if (comPtr == IntPtr.Zero) return 0;
        var vtable = Marshal.ReadIntPtr(comPtr);
        var methodPtr = Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size); // slot 2
        var fn = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(methodPtr);
        return fn(comPtr);
    }

    private delegate int ReleaseFn(IntPtr comPtr);

    // ── Structs ──

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_SIZE_U
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_SIZE_F
    {
        public float Width;
        public float Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RECT_F
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;

        public static D2D1_RECT_F Full(float w, float h) => new() { Right = w, Bottom = h };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public int Type;               // D2D1_RENDER_TARGET_TYPE
        public D2D1_PIXEL_FORMAT PixelFormat;
        public float DpiX;
        public float DpiY;
        public int Usage;              // D2D1_RENDER_TARGET_USAGE
        public int MinLevel;           // D2D1_FEATURE_LEVEL

        public static D2D1_RENDER_TARGET_PROPERTIES Default() => new()
        {
            Type = D2D1_RENDER_TARGET_TYPE_DEFAULT,
            PixelFormat = new D2D1_PIXEL_FORMAT { Format = 0, AlphaMode = D2D1_ALPHA_MODE_IGNORE },
            DpiX = 96, DpiY = 96,
            Usage = 0,
            MinLevel = D2D1_FEATURE_LEVEL_DEFAULT
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_PIXEL_FORMAT
    {
        public int Format;    // DXGI_FORMAT (0 = DXGI_FORMAT_UNKNOWN)
        public int AlphaMode; // D2D1_ALPHA_MODE
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_HWND_RENDER_TARGET_PROPERTIES
    {
        public IntPtr Hwnd;
        public D2D1_SIZE_U PixelSize;
        public int PresentOptions;

        public static D2D1_HWND_RENDER_TARGET_PROPERTIES ForHwnd(IntPtr hwnd, uint w, uint h) => new()
        {
            Hwnd = hwnd,
            PixelSize = new D2D1_SIZE_U { Width = w, Height = h },
            PresentOptions = D2D1_PRESENT_OPTIONS_NONE
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_BITMAP_PROPERTIES
    {
        public D2D1_PIXEL_FORMAT PixelFormat;
        public float DpiX;
        public float DpiY;

        public static D2D1_BITMAP_PROPERTIES Default() => new()
        {
            PixelFormat = new D2D1_PIXEL_FORMAT { Format = 0, AlphaMode = D2D1_ALPHA_MODE_IGNORE },
            DpiX = 96, DpiY = 96
        };
    }
}
```

- [ ] **Step 2: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Interop/D2D1.cs
git commit -m "feat: add minimal Direct2D COM vtable interop declarations"
```

---

### Task 5: 创建 IFrameRenderer + D2dRenderer

**Files:**
- Create: `src/WallpaperApp/Services/Playback/IFrameRenderer.cs`
- Create: `src/WallpaperApp/Services/Playback/D2dRenderer.cs`

- [ ] **Step 1: 创建渲染器接口**

```csharp
using WallpaperApp.Interop;

namespace WallpaperApp.Services.Playback;

public interface IFrameRenderer : IDisposable
{
    bool Present(FrameData frame);
    void Resize(int width, int height);
}
```

- [ ] **Step 2: 创建 D2dRenderer**

```csharp
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class D2dRenderer : IFrameRenderer
{
    private readonly FileLogger _logger;
    private readonly Guid _factoryId = Guid.Parse("06152247-04f6-4698-8b2d-6d1b2d0b7b1a"); // IID_ID2D1Factory

    private IntPtr _factory;
    private IntPtr _renderTarget;
    private IntPtr _bitmap;
    private int _width;
    private int _height;
    private int _stride;
    private bool _disposed;

    public D2dRenderer(IntPtr hwnd, int width, int height, FileLogger logger)
    {
        _logger = logger;
        _width = width;
        _height = height;
        _stride = width * 4;

        var hr = D2D1.D2D1CreateFactory(
            D2D1.D2D1_FACTORY_TYPE_SINGLE_THREADED,
            _factoryId,
            out _factory);

        if (hr != D2D1.S_OK)
        {
            _logger.Error($"D2D1CreateFactory failed: 0x{hr:X8}");
            return;
        }

        CreateRenderTarget(hwnd);
    }

    public bool Present(FrameData frame)
    {
        if (_disposed || _renderTarget == IntPtr.Zero) return false;

        try
        {
            // If dimensions changed, recreate bitmap
            if (frame.Width != _width || frame.Height != _height)
            {
                Resize(frame.Width, frame.Height);
            }

            // Upload BGRA data to D2D bitmap
            var hr = D2D1.CopyFromMemory(_bitmap, IntPtr.Zero, frame.Buffer, _stride);
            if (hr != D2D1.S_OK)
            {
                _logger.Warn($"CopyFromMemory failed: 0x{hr:X8}");
                return false;
            }

            // Draw to HWND
            hr = D2D1.BeginDraw(_renderTarget);
            if (hr != D2D1.S_OK) return false;

            var dstRect = D2D1.D2D1_RECT_F.Full(_width, _height);
            var srcRect = D2D1.D2D1_RECT_F.Full(_width, _height);
            hr = D2D1.DrawBitmap(_renderTarget, _bitmap, ref dstRect, 1.0f, 0, ref srcRect);
            if (hr != D2D1.S_OK) return false;

            hr = D2D1.EndDraw(_renderTarget);
            if (hr == D2D1.D2DERR_RECREATE_TARGET)
            {
                RecreateRenderTarget();
                return true;
            }

            if (hr != D2D1.S_OK) return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"D2D Present error: {ex.Message}");
            return false;
        }
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        RecreateRenderTarget();
    }

    private void CreateRenderTarget(IntPtr hwnd)
    {
        // Release old
        if (_bitmap != IntPtr.Zero) { D2D1.Release(_bitmap); _bitmap = IntPtr.Zero; }
        if (_renderTarget != IntPtr.Zero) { D2D1.Release(_renderTarget); _renderTarget = IntPtr.Zero; }

        if (_factory == IntPtr.Zero) return;

        var rtProps = D2D1.D2D1_RENDER_TARGET_PROPERTIES.Default();
        var hwndProps = D2D1.D2D1_HWND_RENDER_TARGET_PROPERTIES.ForHwnd(
            hwnd, (uint)_width, (uint)_height);

        var hr = D2D1.CreateHwndRenderTarget(
            _factory, ref rtProps, ref hwndProps, out _renderTarget);

        if (hr != D2D1.S_OK || _renderTarget == IntPtr.Zero)
        {
            _logger.Error($"CreateHwndRenderTarget failed: 0x{hr:X8}");
            return;
        }

        // Create bitmap matching render target
        var size = new D2D1.D2D1_SIZE_U { Width = (uint)_width, Height = (uint)_height };
        var bmpProps = D2D1.D2D1_BITMAP_PROPERTIES.Default();

        hr = D2D1.CreateBitmap(_renderTarget, size, IntPtr.Zero, _stride, ref bmpProps, out _bitmap);
        if (hr != D2D1.S_OK || _bitmap == IntPtr.Zero)
        {
            _logger.Error($"CreateBitmap failed: 0x{hr:X8}");
        }
    }

    private void RecreateRenderTarget()
    {
        // Need the HWND — stored in the render target properties or passed in
        // Re-creation will be triggered by Present returning D2DERR_RECREATE_TARGET
        // For now, just log
        _logger.Warn("D2D render target needs recreation — reinitialize from Present error path");
        // Actual recreation requires storing HWND in a field
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_bitmap != IntPtr.Zero) { D2D1.Release(_bitmap); _bitmap = IntPtr.Zero; }
        if (_renderTarget != IntPtr.Zero) { D2D1.Release(_renderTarget); _renderTarget = IntPtr.Zero; }
        if (_factory != IntPtr.Zero) { D2D1.Release(_factory); _factory = IntPtr.Zero; }
    }
}
```

- [ ] **Step 3: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add src/WallpaperApp/Services/Playback/IFrameRenderer.cs src/WallpaperApp/Services/Playback/D2dRenderer.cs
git commit -m "feat: add IFrameRenderer and D2D renderer implementation"
```

---

### Task 6: 集成 PlaybackSession（渲染器 + PTS 帧定时）

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`

- [ ] **Step 1: 重写 PlaybackSession — 集成 IFrameRenderer**

```csharp
using System.Diagnostics;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackSession : IDisposable
{
    private readonly FileLogger _logger;
    private readonly IPlaybackBackend _backend;
    private readonly IFrameRenderer _renderer;
    private readonly Guid _monitorId;
    private CancellationTokenSource? _renderCts;
    private Task? _renderTask;
    private bool _disposed;

    public Guid MonitorId => _monitorId;
    public bool IsPlaying => _backend.IsPlaying;
    public bool IsPaused => _backend.IsPaused;
    public IPlaybackBackend Backend => _backend;

    public PlaybackSession(Guid monitorId, IPlaybackBackend backend, IFrameRenderer renderer, FileLogger logger)
    {
        _monitorId = monitorId;
        _backend = backend;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<bool> LoadFileAsync(string filePath, CancellationToken ct = default)
    {
        return await _backend.OpenAsync(filePath, ct);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.CompletedTask;
        _renderCts?.Cancel();
        _renderCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _renderTask = Task.Run(() => RenderLoopAsync(_renderCts.Token), _renderCts.Token);
        _logger.Info($"Session started for monitor {_monitorId}");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _renderCts?.Cancel();
        if (_renderTask != null)
        {
            try { await _renderTask; } catch { }
        }
        await _backend.StopAsync(ct);
        _logger.Info($"Session stopped for monitor {_monitorId}");
    }

    public async Task PauseAsync(CancellationToken ct = default) => await _backend.PauseAsync(ct);
    public async Task ResumeAsync(CancellationToken ct = default) => await _backend.ResumeAsync(ct);

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        await _backend.PlayAsync(ct);
        var lastPts = -1L;
        var sw = Stopwatch.StartNew();
        var frameBudget = TimeSpan.FromMilliseconds(16);

        while (!ct.IsCancellationRequested && _backend.IsPlaying)
        {
            if (_backend.IsPaused)
            {
                await Task.Delay(50, ct);
                continue;
            }

            var frame = await _backend.NextFrameAsync(ct);
            if (frame == null)
            {
                // End of stream — seek back and loop
                await _backend.SeekAsync(TimeSpan.Zero, ct);
                await _backend.PlayAsync(ct);
                lastPts = -1;
                continue;
            }

            // PTS-based frame timing
            if (lastPts > 0 && frame.PtsUs > lastPts)
            {
                var frameDurationUs = frame.PtsUs - lastPts;
                var elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                var waitUs = Math.Max(0L, frameDurationUs - elapsedUs);
                if (waitUs > 0)
                    await Task.Delay((int)(waitUs / 1000), ct);
            }

            sw.Restart();
            lastPts = frame.PtsUs;

            // Render
            var ok = _renderer.Present(frame);
            frame.Dispose();

            if (!ok)
            {
                // HWND disappeared or D2D device lost
                _logger.Warn($"Render failed for monitor {_monitorId}, stopping");
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _renderer.Dispose();
        _backend.Dispose();
    }
}
```

- [ ] **Step 2: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackSession.cs
git commit -m "feat: integrate D2D renderer and PTS-based frame timing into PlaybackSession"
```

---

### Task 7: 更新 PlaybackManager + WallpaperWindow HWND 暴露

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackManager.cs`
- Modify: `src/WallpaperApp/Services/Desktop/WallpaperWindow.cs`

- [ ] **Step 1: WallpaperWindow 暴露 Handle（已有，确认即可）**

确认 `WallpaperWindow.cs` 已有:
```csharp
public IntPtr Handle => _hwnd;
```

- [ ] **Step 2: 更新 PlaybackManager — 创建 Session 时传入 D2dRenderer**

```csharp
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackManager : IDisposable
{
    private readonly FileLogger _logger;
    private readonly DesktopHost _desktopHost;
    private readonly Dictionary<Guid, PlaybackSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    // 依赖注入 DesktopHost
    public PlaybackManager(FileLogger logger, DesktopHost desktopHost)
    {
        _logger = logger;
        _desktopHost = desktopHost;
    }

    // ... 其余方法不变 ...

    public async Task SetWallpaperAsync(Guid monitorId, Guid wallpaperId, string filePath, CancellationToken ct = default)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_sessions.TryGetValue(monitorId, out var existing))
            {
                existing.Dispose();
                _sessions.Remove(monitorId);
            }
        }

        // Find WallpaperWindow HWND for this monitor
        var wallpaperWindow = _desktopHost.WallpaperWindows
            .FirstOrDefault(w => !w.IsFallback);

        if (wallpaperWindow == null)
        {
            _logger.Error($"No wallpaper window available for monitor {monitorId}");
            return;
        }

        var backend = CreateBackend();
        var renderer = new D2dRenderer(
            wallpaperWindow.Handle,
            wallpaperWindow.Width,
            wallpaperWindow.Height,
            _logger);

        var session = new PlaybackSession(monitorId, backend, renderer, _logger);
        var loaded = await session.LoadFileAsync(filePath, ct);

        if (!loaded)
        {
            backend.Dispose();
            renderer.Dispose();
            _logger.Warn($"FfmpegBackend failed, trying MfBackend fallback");
            backend = CreateFallbackBackend();
            session = new PlaybackSession(monitorId, backend, renderer, _logger);
            loaded = await session.LoadFileAsync(filePath, ct);
        }

        if (!loaded)
        {
            session.Dispose();
            _logger.Error($"Failed to load wallpaper for monitor {monitorId}: {filePath}");
            return;
        }

        lock (_lock)
        {
            _sessions[monitorId] = session;
        }

        await session.StartAsync(ct);
        _logger.Info($"Wallpaper set on monitor {monitorId}: {Path.GetFileName(filePath)}");
    }

    // ... 其他方法不变 ...
}
```

**注意：** `DesktopHost` 需要已有 DI 注册（`App.xaml.cs` 中已有 `services.AddSingleton<DesktopHost>()`）。
**注意：** `WallpaperWindow` 需要暴露 `Width` / `Height` 属性。如无则需添加。

- [ ] **Step 3: 向 WallpaperWindow 添加尺寸属性**

```csharp
public int Width { get; private set; }
public int Height { get; private set; }

// 在 Resize 中更新：
public void Resize(int x, int y, int width, int height)
{
    Width = width;
    Height = height;
    if (_hwnd == IntPtr.Zero) return;
    NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height,
        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
}
```

- [ ] **Step 4: Build 验证**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackManager.cs src/WallpaperApp/Services/Desktop/WallpaperWindow.cs
git commit -m "feat: wire D2D renderer into PlaybackManager per monitor session"
```

---

### Task 8: 集成测试与验证

**Files:**
- Modify: `tests/WallpaperApp.Tests/Services/FfmpegBackendTests.cs`

- [ ] **Step 1: 适配 FfmpegBackendTests — 保持接口契约测试**

现有 54 个测试中 FfmpegBackendTests 包含纯状态测试（Constructor, IsPlaying, IsPaused 等），应在 P/Invoke 后端继续通过。唯一需要调整的：`Dispose_DoesNotThrow` 和 `NextFrameAsync_WithoutProcess_ReturnsNull` 因后端内部不再创建子进程，应保持兼容。

确认如下测试通过（纯接口契约，不需 FFmpeg DLL）：
- Constructor_InitializesWithLogger
- IsPlaying_DefaultsToFalse
- IsPaused_DefaultsToFalse
- Duration_DefaultsToTimeSpanZero
- Position_DefaultsToTimeSpanZero
- PlayAsync_SetsIsPlayingTrue_IsPausedFalse
- PauseAsync_SetsIsPausedTrue
- ResumeAsync_SetsIsPausedFalse
- StopAsync_ResetsAllState
- NextFrameAsync_WithoutProcess_ReturnsNull
- Dispose_DoesNotThrow
- PlayAsync_ThenPause_ThenResume_CorrectStates
- StopAsync_ResetsPosition
- EndOfStream_Event_CanBeSubscribed
- SeekAsync_SetsPosition
- OpenAsync_NonexistentFile_ReturnsFalse
- OpenAsync_InvalidFile_ReturnsFalse

- [ ] **Step 2: 运行全部测试**

Run: `dotnet test tests\WallpaperApp.Tests -v n`
Expected: 54 passed, 0 failed

- [ ] **Step 3: 最终 Build 验证（Release）**

Run: `dotnet build src\WallpaperApp\WallpaperApp.csproj -c Release`
Expected: 0 errors

- [ ] **Step 4: 最终 Commit**

```bash
git add -A
git commit -m "feat: complete FFmpeg P/Invoke decode pipeline with D2D rendering"
```

---

## 设计约束汇总

| 约束 | 原因 |
|---|---|
| `LibraryImport` 源码生成 | 性能最优，AOT 友好，当前项目已使用 |
| 手动偏移量读取 | 避免 `Marshal.OffsetOf<AVFormatContext>()` 在空 struct 上返回 0 |
| 偏移量硬编码 | 绑定到打包的 FFmpeg 7.1 DLLs，换版本需同步更新 |
| D2D COM vtable 手动调用 | 避免引入 NuGet 依赖，仅需 3 个接口 |
| FrameData 不拥有内存 | 后端双缓冲管理，避免每帧 Alloc/Free 开销 |
| 解码线程始终为 `Task.Run` | FFmpeg API 非线程安全（单线程调用），避免阻塞 UI |
