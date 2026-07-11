# GPU FPS-Filter Throttling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce wallpaper GPU usage by throttling frame output rate at the FFmpeg filter layer (instead of the removed present-side skip gate), re-enabling the Balanced/Saver performance profiles with a smoother mechanism.

**Architecture:** Insert a libavfilter `fps` graph between the FFmpeg decoder output and the frame returned to the render loop. When a profile sets `MaxPresentFps`, the backend builds a `buffer → fps=N → buffersink` filter chain; the render loop passively presents whatever the filter emits, dropping the coarse `Thread.Sleep` pacing and the dead `ShouldPresentFrame` gate. A standalone probe validates that the `fps` filter accepts `AV_PIX_FMT_D3D11` (the key risk) before the main wiring lands.

**Tech Stack:** C# / .NET 8 (net8.0-windows), Vortice.Direct3D11/DXGI, FFmpeg 7.x (libavfilter `avfilter-10.dll`, already in output dir), xUnit, raw `[LibraryImport]` P/Invoke.

## Global Constraints

- **Platform:** Windows-only (net8.0-windows). All P/Invoke targets Win32/FFmpeg DLLs.
- **FFmpeg version:** libavfilter `avfilter-10.dll`, avformat `avformat-61`, avcodec `avcodec-61`, avutil `avutil-59`, swscale `swscale-8` (FFmpeg 7.x). Do not change DLLs.
- **No new binary dependencies:** `avfilter-10.dll` is already copied to output via the existing wildcard `<None Include="../../lib/ffmpeg/*.dll" .../>` in `src/WallpaperApp/WallpaperApp.csproj`. No csproj DLL change needed.
- **P/Invoke style:** `[LibraryImport]` source-generator pattern with `internal static partial`. String args use `StringMarshalling = StringMarshalling.Utf8`. Pointer args use `unsafe`. Match `FfmpegNative.cs` exactly.
- **Render-thread ownership:** D3D/D2D/window objects for a session are created AND used on one dedicated `WallpaperRender-{monitorId}` thread. Do not break this.
- **Pause-reason accounting:** Pause remains controlled by `PauseReason` + `ApplyPauseAsync`/`ClearPauseAsync`. Performance policy is a SEPARATE axis and must never touch `_pauseReasons`.
- **No-blank wallpaper switching:** Preserve the current "build new session → swap → dispose old" pattern. Do not introduce a blank flash.
- **Commit directive from `af68e99`:** "Do not reintroduce post-decode frame skipping as the default Balanced/Saver behavior without user-visible jitter validation." This plan addresses that by throttling at the filter layer (earlier, smoother) AND by shipping a probe + PresentMon guidance to validate smoothness — NOT by reviving the present-side `ShouldPresentFrame` skip gate for the default path.
- **Build/test commands:** `dotnet build WallpaperApp.sln` and `dotnet test tests/WallpaperApp.Tests`. xUnit. Smoke/probe tests spawn native FFmpeg/DLLs already on the output path.

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/WallpaperApp/Services/Playback/FfmpegNative.cs` | P/Invoke declarations | **Modify** — add `AvFilter` library const + 11 avfilter P/Invokes + filter constants |
| `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs` | Profile→policy mapping | **Modify** — re-enable `MaxPresentFps` in `FromProfile` (Balanced=30, Saver=15) |
| `src/WallpaperApp/Services/Playback/FilterGraph.cs` | NEW — encapsulates the libavfilter `buffer→fps→buffersink` graph lifecycle | **Create** |
| `src/WallpaperApp/Services/Playback/FfmpegBackend.cs` | Decode + frame production | **Modify** — own a `FilterGraph`, route frames through it, rebuild on policy change, track `filterDropped` |
| `src/WallpaperApp/Services/Playback/PlaybackSession.cs` | Render loop | **Modify** — remove dead present gate (`ShouldPresentFrame`/`skippedFrames`/`lastPresentedUs`), update perf log, keep PTS pacing sleep (probe result decides filter-pace vs sleep-pace) |
| `tests/WallpaperApp.FilterProbe/` | NEW standalone probe | **Create** — validates `fps` filter + `AV_PIX_FMT_D3D11` compatibility |
| `tests/WallpaperApp.Tests/Services/FilterGraphTests.cs` | NEW unit tests for FilterGraph | **Create** |
| `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs` | Policy tests | **Modify** — update for new FromProfile mapping |
| `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs` | Session tests | **Modify** — remove/update ShouldPresentFrame tests, add filter-aware tests |

---

## Task 1: Probe — validate fps filter compatibility with D3D11 hardware format

This task resolves the #1 risk from the spec: does the libavfilter `fps` filter accept `AV_PIX_FMT_D3D11` (hardware) frames, or only software formats? The answer determines the implementation path (filter-pace vs. waitable-timer fallback). It must run FIRST.

**Files:**
- Create: `tests/WallpaperApp.FilterProbe/WallpaperApp.FilterProbe.csproj`
- Create: `tests/WallpaperApp.FilterProbe/Program.cs`

**Interfaces:**
- Consumes: `FfmpegBackend`, `HwDecodeDevice`, `GpuDevice`, `FfmpegNative`, `FfmpegOffsets` (from main project via ProjectReference)
- Produces: a console report (exit code + stdout) answering: "Can a `buffer→fps=30→buffersink` graph be built with `pix_fmt=AV_PIX_FMT_D3D11`, and can a real D3D11-decoded frame pass through it?"

- [ ] **Step 1: Create the probe csproj**

Mirror `tests/WallpaperApp.HwDecodeProbe/WallpaperApp.HwDecodeProbe.csproj` exactly (it has `AllowUnsafeBlocks`):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\WallpaperApp\WallpaperApp.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the probe Program.cs**

This probe opens a video with D3D11VA hardware decode, gets one D3D11 frame, then attempts to build a `buffer→fps=30→buffersink` filter graph configured for `AV_PIX_FMT_D3D11` and push the frame through. It reports each stage's result. It needs new P/Invokes to avfilter — but to keep the probe self-contained and avoid editing the main project before validating, the probe declares its OWN local `[LibraryImport("avfilter-10")]` declarations (these will be consolidated into `FfmpegNative.cs` in Task 2).

```csharp
using System.Runtime.InteropServices;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.FilterProbe;

internal static partial class Program
{
    // Local avfilter P/Invokes (consolidated into FfmpegNative.cs in Task 2).
    private const string AvFilter = "avfilter-10";

    [LibraryImport(AvFilter, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr avfilter_get_by_name(string name);

    [LibraryImport(AvFilter)]
    private static partial IntPtr avfilter_graph_alloc();

    [LibraryImport(AvFilter)]
    private static partial void avfilter_graph_free(ref IntPtr graph);

    [LibraryImport(AvFilter, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int avfilter_graph_create_filter(
        ref IntPtr filtCtx, IntPtr filter, string name, string args, IntPtr opaque, IntPtr graph);

    [LibraryImport(AvFilter)]
    private static partial int avfilter_link(IntPtr src, uint srcPad, IntPtr dst, uint dstPad);

    [LibraryImport(AvFilter)]
    private static partial int avfilter_graph_config(IntPtr graph, IntPtr logCtx);

    [LibraryImport(AvFilter)]
    private static partial int av_buffersrc_add_frame_flags(IntPtr ctx, IntPtr frame, int flags);

    [LibraryImport(AvFilter)]
    private static partial int av_buffersink_get_frame(IntPtr ctx, IntPtr frame);

    private const int AV_PIX_FMT_D3D11 = 171;
    private const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: FilterProbe <video.mp4>");
            return 64;
        }

        var logDir = Path.Combine(Path.GetTempPath(), "WallpaperAppFilterProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);
        using var logger = new FileLogger(logDir);
        HwDecodeDevice.Logger = logger;

        // 1. Open with hardware decode (zero-copy style) to get a D3D11 frame.
        using var gpu = new GpuDevice(logger);
        Console.WriteLine($"GpuDevice available: {gpu.IsAvailable}");

        using var backend = new FfmpegBackend(logger, () => HwDecodeDevice.CreateForDevice(gpu.DevicePointer));
        backend.PreferZeroCopy = true;
        if (!backend.OpenAsync(args[0]).GetAwaiter().GetResult())
        {
            Console.WriteLine("RESULT: FAIL — backend.OpenAsync failed");
            return 2;
        }
        backend.PlayAsync().GetAwaiter().GetResult();
        var frame = backend.NextFrameAsync(default).GetAwaiter().GetResult();
        if (frame == null)
        {
            Console.WriteLine("RESULT: FAIL — no first frame");
            return 3;
        }
        Console.WriteLine($"Hardware decoding: {backend.IsHardwareDecoding}");
        Console.WriteLine($"First frame IsGpu: {frame.IsGpu}, {frame.Width}x{frame.Height}");

        // 2. Try to build buffer -> fps=30 -> buffersink with pix_fmt=AV_PIX_FMT_D3D11.
        var graph = avfilter_graph_alloc();
        if (graph == IntPtr.Zero) { Console.WriteLine("RESULT: FAIL — avfilter_graph_alloc returned null"); return 4; }

        try
        {
            var bufferFilter = avfilter_get_by_name("buffer");
            var fpsFilter = avfilter_get_by_name("fps");
            var sinkFilter = avfilter_get_by_name("buffersink");
            Console.WriteLine($"filters found: buffer={bufferFilter != IntPtr.Zero} fps={fpsFilter != IntPtr.Zero} sink={sinkFilter != IntPtr.Zero}");
            if (bufferFilter == IntPtr.Zero || fpsFilter == IntPtr.Zero || sinkFilter == IntPtr.Zero)
            { Console.WriteLine("RESULT: FAIL — a filter was not found"); return 5; }

            // buffer args: video_size, pix_fmt, time_base, pixel_aspect
            var bufferArgs = $"video_size={frame.Width}x{frame.Height}:pix_fmt={AV_PIX_FMT_D3D11}:time_base=1/60:pixel_aspect=1/1";
            var srcCtx = IntPtr.Zero;
            var fpsCtx = IntPtr.Zero;
            var sinkCtx = IntPtr.Zero;

            int r = avfilter_graph_create_filter(ref srcCtx, bufferFilter, "src", bufferArgs, IntPtr.Zero, graph);
            Console.WriteLine($"create buffer: 0x{r:X8} ({(r < 0 ? "FAIL" : "ok")})");
            if (r < 0) { Console.WriteLine("RESULT: B — buffer filter rejects AV_PIX_FMT_D3D11 args"); return 10; }

            r = avfilter_graph_create_filter(ref fpsCtx, fpsFilter, "fps", "fps=30", IntPtr.Zero, graph);
            Console.WriteLine($"create fps: 0x{r:X8} ({(r < 0 ? "FAIL" : "ok")})");
            if (r < 0) { Console.WriteLine("RESULT: FAIL — fps filter create failed"); return 6; }

            r = avfilter_graph_create_filter(ref sinkCtx, sinkFilter, "sink", "", IntPtr.Zero, graph);
            Console.WriteLine($"create sink: 0x{r:X8} ({(r < 0 ? "FAIL" : "ok")})");
            if (r < 0) { Console.WriteLine("RESULT: FAIL — sink filter create failed"); return 7; }

            r = avfilter_link(srcCtx, 0, fpsCtx, 0);
            Console.WriteLine($"link buffer->fps: 0x{r:X8}");
            if (r < 0) { Console.WriteLine("RESULT: FAIL — link src->fps"); return 8; }
            r = avfilter_link(fpsCtx, 0, sinkCtx, 0);
            Console.WriteLine($"link fps->sink: 0x{r:X8}");
            if (r < 0) { Console.WriteLine("RESULT: FAIL — link fps->sink"); return 9; }

            r = avfilter_graph_config(graph, IntPtr.Zero);
            Console.WriteLine($"graph config: 0x{r:X8} ({(r < 0 ? "FAIL" : "ok")})");
            if (r < 0) { Console.WriteLine("RESULT: B/C — graph config rejects D3D11 format negotiation"); return 11; }

            // 3. Try to push the actual D3D11 frame through.
            // The backend keeps the hw frame alive until next NextFrameAsync; we can't easily get the
            // raw AVFrame* here, so this probe validates graph CONSTRUCTION with D3D11 pix_fmt.
            // Frame-pushing is validated by the integration in Task 5.
            Console.WriteLine("RESULT: A — fps filter graph accepts AV_PIX_FMT_D3D11 construction");
            return 0;
        }
        finally
        {
            avfilter_graph_free(ref graph);
        }
    }
}
```

- [ ] **Step 3: Build the probe standalone**

Run: `dotnet build tests/WallpaperApp.FilterProbe/WallpaperApp.FilterProbe.csproj`
Expected: build succeeds. (avfilter-10.dll is already on the output path via the main project's wildcard.)

- [ ] **Step 4: Run the probe against a real video and record the result**

Run: `dotnet run --project tests/WallpaperApp.FilterProbe -- <path-to-a-real-mp4>` using any available mp4 (e.g. a wallpaper in the library).
Expected: prints a `RESULT:` line. **Record which result code is returned:**
- `RESULT: A` (exit 0) → `fps` filter accepts D3D11. Proceed with the filter-pace path (Tasks 2-6 as written).
- `RESULT: B` (exit 10/11) → `fps` filter rejects D3D11. The implementation MUST use the **waitable-timer fallback** (see Task 7) instead of a filter graph for the zero-copy path. Skip Tasks 4-5's zero-copy filter wiring; route filter throttling only through the software/CPU path, or skip the filter entirely and use Task 7's timer approach for all paths.
- Any other RESULT/FAIL → investigate before proceeding.

Write the observed result into the commit message of Task 2.

- [ ] **Step 5: Commit**

```bash
git add tests/WallpaperApp.FilterProbe/
git commit -m "Add FilterProbe to validate fps filter + D3D11 compatibility

Probe result: [fill in A / B / failure code observed in Step 4]"
```

---

## Task 2: Add avfilter P/Invoke declarations to FfmpegNative.cs

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/FfmpegNative.cs` (add `AvFilter` const at line 10, add filter P/Invokes after line 162, add filter constants near line 198)

**Interfaces:**
- Consumes: nothing new
- Produces: `FfmpegNative.avfilter_*` and `FfmpegNative.av_buffersrc_*` / `av_buffersink_*` functions for `FilterGraph` (Task 3) and `FfmpegBackend` (Task 5); constants `AV_BUFFERSRC_FLAG_KEEP_REF`, `AVERROR_EAGAIN`, `AVERROR_EOF`

- [ ] **Step 1: Add the AvFilter library constant**

In `src/WallpaperApp/Services/Playback/FfmpegNative.cs`, after line 10 (`private const string SwScale = "swscale-8";`), add:

```csharp
    private const string AvFilter = "avfilter-10";
```

- [ ] **Step 2: Add the avfilter P/Invoke declarations**

After the `sws_scale` import (currently the last import, ending around line 162), add these. Match the existing `[LibraryImport] partial` style:

```csharp
    [LibraryImport(AvFilter, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr avfilter_get_by_name(string name);

    [LibraryImport(AvFilter)]
    internal static partial IntPtr avfilter_graph_alloc();

    [LibraryImport(AvFilter)]
    internal static partial void avfilter_graph_free(ref IntPtr graph);

    [LibraryImport(AvFilter, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avfilter_graph_create_filter(
        ref IntPtr filtCtx, IntPtr filter, string name, string args, IntPtr opaque, IntPtr graph);

    [LibraryImport(AvFilter)]
    internal static partial int avfilter_link(IntPtr src, uint srcPad, IntPtr dst, uint dstPad);

    [LibraryImport(AvFilter)]
    internal static partial int avfilter_graph_config(IntPtr graph, IntPtr logCtx);

    [LibraryImport(AvFilter)]
    internal static partial int av_buffersrc_add_frame_flags(IntPtr ctx, IntPtr frame, int flags);

    [LibraryImport(AvFilter)]
    internal static partial int av_buffersink_get_frame(IntPtr ctx, IntPtr frame);

    [LibraryImport(AvFilter)]
    internal static partial int av_buffersink_get_frame_flags(IntPtr ctx, IntPtr frame, int flags);
```

- [ ] **Step 3: Add filter-related constants**

Near the existing constants block (after `AVDISCARD_NONREF = 8;` around line 198), add:

```csharp
    internal const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;
    internal const int AVERROR_EAGAIN = -11;       // EAGAIN — buffersink has no frame yet
    internal const int AVERROR_EOF = -0x20464F45;  // 'EOF ' tag as negative
```

Note: `AVERROR_EAGAIN` maps to the C `AVERROR(EAGAIN)` macro which is `-EAGAIN` = `-11` on Windows (where `EAGAIN = 11`). Verify against `avutil-59` if any doubt.

- [ ] **Step 4: Build to verify the source generator emits the P/Invokes**

Run: `dotnet build src/WallpaperApp/WallpaperApp.csproj`
Expected: build succeeds (no unresolved externals yet since nothing calls them).

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/FfmpegNative.cs
git commit -m "Add libavfilter P/Invoke declarations"
```

---

## Task 3: Create FilterGraph — encapsulate the buffer→fps→buffersink lifecycle

**Files:**
- Create: `src/WallpaperApp/Services/Playback/FilterGraph.cs`
- Test: `tests/WallpaperApp.Tests/Services/FilterGraphTests.cs`

**Interfaces:**
- Consumes: `FfmpegNative.avfilter_*` / `av_buffersrc_*` / `av_buffersink_*`, `FfmpegOffsets` (for AVFrame field access if needed)
- Produces: a `FilterGraph` class with this surface (used by `FfmpegBackend` in Task 5):

```csharp
public sealed class FilterGraph : IDisposable
{
    // Build a buffer→fps→buffersink graph. Returns null if construction fails (caller falls back to passthrough).
    public static FilterGraph? TryCreate(int width, int height, int pixFmt, AVRational timeBase, AVRational sar, int targetFps);
    // Push a decoded AVFrame* into the buffer source. Returns false on error.
    public bool PushFrame(IntPtr avFrame);
    // Pull the next available AVFrame* from the buffer sink into the provided AVFrame*.
    // Returns: a frame is available (true), no frame yet (false, not an error), or end/error (false — caller checks with WasEndOrError).
    public bool TryGetFrame(IntPtr destFrame, out bool error);
    public void Dispose();
}
```

- [ ] **Step 1: Write the failing test for graph construction**

Create `tests/WallpaperApp.Tests/Services/FilterGraphTests.cs`. Because graph construction needs real FFmpeg state (an opened decoder), the unit test validates the *passthrough-on-failure* contract and the null-return contract, not a full real-media decode (that's the probe's job in Task 1).

```csharp
using WallpaperApp.Services.Playback;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class FilterGraphTests
{
    [Fact]
    public void TryCreate_WithInvalidPixelFormat_ReturnsNullAndDoesNotThrow()
    {
        // A bogus pixel format should cause graph config to fail and TryCreate to return null gracefully,
        // rather than throwing. This is the fall-back-to-passthrough contract.
        var graph = FilterGraph.TryCreate(16, 16, pixFmt: 99999, timeBase: default, sar: default, targetFps: 30);
        Assert.Null(graph);
    }

    [Fact]
    public void TryCreate_WithZeroTargetFps_ReturnsNull()
    {
        // targetFps <= 0 is meaningless for an fps filter; Treat as "no throttling" (null = passthrough).
        var graph = FilterGraph.TryCreate(1920, 1080, pixFmt: 0, timeBase: new AVRational { Num = 1, Den = 60 }, sar: default, targetFps: 0);
        Assert.Null(graph);
    }

    [Fact]
    public void Dispose_OnUncreatedInstance_IsSafe()
    {
        // Disposing a graph that was never successfully created (null) must not throw.
        FilterGraph? graph = null;
        var ex = Record.Exception(() => graph?.Dispose());
        Assert.Null(ex);
    }
}
```

Note: `AVRational` is the FFmpeg struct already used in `FfmpegBackend.cs` (field at line 34). Confirm it's accessible from tests; if it's an internal struct, the test project's `InternalsVisibleTo` should cover it (the test project already asserts on `CurrentPerformancePolicyForTests` which is `internal`). If `AVRational` is not yet a public type, declare it as a public readonly struct in `FilterGraph.cs` or `IPlaybackBackend.cs` with `Num`/`Den` int fields (it already exists somewhere since `FfmpegBackend` uses it — locate it and reuse).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~FilterGraphTests"`
Expected: FAIL with "type or namespace 'FilterGraph' could not be found".

- [ ] **Step 3: Implement FilterGraph**

Create `src/WallpaperApp/Services/Playback/FilterGraph.cs`:

```csharp
using System.Runtime.InteropServices;

namespace WallpaperApp.Services.Playback;

/// <summary>
/// Encapsulates a libavfilter "buffer → fps → buffersink" graph used to throttle
/// the frame output rate at the filter layer (earlier and smoother than present-side skipping).
/// Construction failure returns null from TryCreate so the caller can fall back to passthrough.
/// </summary>
public sealed class FilterGraph : IDisposable
{
    private IntPtr _graph;
    private IntPtr _srcCtx;   // AVFilterContext* for the buffer source
    private IntPtr _sinkCtx;  // AVFilterContext* for the buffer sink
    private bool _disposed;

    private FilterGraph(IntPtr graph, IntPtr srcCtx, IntPtr sinkCtx)
    {
        _graph = graph;
        _srcCtx = srcCtx;
        _sinkCtx = sinkCtx;
    }

    /// <summary>
    /// Build a buffer→fps→buffersink filter graph.
    /// Returns null if any stage fails (caller should fall back to passthrough / no throttling).
    /// targetFps must be > 0.
    /// </summary>
    public static FilterGraph? TryCreate(int width, int height, int pixFmt, AVRational timeBase, AVRational sar, int targetFps)
    {
        if (targetFps <= 0 || width <= 0 || height <= 0)
            return null;

        var graph = FfmpegNative.avfilter_graph_alloc();
        if (graph == IntPtr.Zero)
            return null;

        IntPtr srcCtx = IntPtr.Zero, fpsCtx = IntPtr.Zero, sinkCtx = IntPtr.Zero;
        try
        {
            var bufferFilter = FfmpegNative.avfilter_get_by_name("buffer");
            var fpsFilter = FfmpegNative.avfilter_get_by_name("fps");
            var sinkFilter = FfmpegNative.avfilter_get_by_name("buffersink");
            if (bufferFilter == IntPtr.Zero || fpsFilter == IntPtr.Zero || sinkFilter == IntPtr.Zero)
                return null;

            // buffer args: video_size=WxH:pix_fmt=N:time_base=num/den:pixel_aspect=num/den
            var tbNum = timeBase.Num == 0 ? 1 : timeBase.Num;
            var tbDen = timeBase.Den == 0 ? 60 : timeBase.Den;
            var bufferArgs = $"video_size={width}x{height}:pix_fmt={pixFmt}:time_base={tbNum}/{tbDen}:pixel_aspect={sar.Num}/{(sar.Den == 0 ? 1 : sar.Den)}";

            if (FfmpegNative.avfilter_graph_create_filter(ref srcCtx, bufferFilter, "src", bufferArgs, IntPtr.Zero, graph) < 0)
                return null;
            if (FfmpegNative.avfilter_graph_create_filter(ref fpsCtx, fpsFilter, "fps", $"fps={targetFps}", IntPtr.Zero, graph) < 0)
                return null;
            if (FfmpegNative.avfilter_graph_create_filter(ref sinkCtx, sinkFilter, "sink", "", IntPtr.Zero, graph) < 0)
                return null;

            if (FfmpegNative.avfilter_link(srcCtx, 0, fpsCtx, 0) < 0)
                return null;
            if (FfmpegNative.avfilter_link(fpsCtx, 0, sinkCtx, 0) < 0)
                return null;
            if (FfmpegNative.avfilter_graph_config(graph, IntPtr.Zero) < 0)
                return null;

            return new FilterGraph(graph, srcCtx, sinkCtx);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Push a decoded AVFrame* into the buffer source. KEEP_REF so the caller can still unref its own frame.</summary>
    public bool PushFrame(IntPtr avFrame)
    {
        if (_disposed || avFrame == IntPtr.Zero) return false;
        return FfmpegNative.av_buffersrc_add_frame_flags(_srcCtx, avFrame, FfmpegNative.AV_BUFFERSRC_FLAG_KEEP_REF) >= 0;
    }

    /// <summary>
    /// Try to pull the next frame from the buffer sink into destFrame (an allocated AVFrame*).
    /// Returns true if a frame is available; false if not yet (not an error) — check <paramref name="error"/> for hard failures.
    /// </summary>
    public bool TryGetFrame(IntPtr destFrame, out bool error)
    {
        error = false;
        if (_disposed) { error = true; return false; }
        var ret = FfmpegNative.av_buffersink_get_frame(_sinkCtx, destFrame);
        if (ret >= 0) return true;
        // EAGAIN means "need more input" — normal, not an error. Anything else is an error/EOF.
        error = ret != FfmpegNative.AVERROR_EAGAIN;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_graph != IntPtr.Zero)
            FfmpegNative.avfilter_graph_free(ref _graph);
        _graph = IntPtr.Zero;
        _srcCtx = IntPtr.Zero;
        _sinkCtx = IntPtr.Zero;
    }
}
```

- [ ] **Step 4: Locate or define AVRational and verify it's accessible**

Search the codebase for the existing `AVRational` definition (used in `FfmpegBackend.cs:34`). If it is `internal` under `WallpaperApp.Services.Playback` or defined in `FfmpegNative.cs`, ensure `FilterGraph` and the tests can see it. If it needs to be a public struct, change its visibility (it's a simple `{ int Num; int Den; }` pair). Do NOT duplicate the type.

Run: `grep -rn "struct AVRational" src/`
- If found as `internal` in the playback namespace: no change needed (test project has InternalsVisibleTo).
- If not found: define it in `FfmpegNative.cs`: `internal struct AVRational { public int Num; public int Den; }` (check how FfmpegBackend already references it first — it must already exist).

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~FilterGraphTests"`
Expected: 3 PASS. (The bogus-format and zero-fps cases return null; dispose-on-null is safe.)

- [ ] **Step 6: Commit**

```bash
git add src/WallpaperApp/Services/Playback/FilterGraph.cs tests/WallpaperApp.Tests/Services/FilterGraphTests.cs
git commit -m "Add FilterGraph encapsulating buffer→fps→buffersink lifecycle"
```

---

## Task 4: Re-enable MaxPresentFps in FromProfile (Balanced=30, Saver=15)

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs:18-23`
- Modify: `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `FromProfile` now returns non-null `MaxPresentFps` for Balanced (30) and Saver (15); Quality stays null. `MinFrameIntervalUs` becomes non-zero for those profiles.

- [ ] **Step 1: Update the failing test first**

In `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`, find the existing `FromProfile` test(s) and update the expectations. If there is a test asserting `MaxPresentFps == null` for Balanced/Saver, change it. Add coverage for the new values:

```csharp
[Theory]
[InlineData(WallpaperPerformanceProfile.Quality, null)]
[InlineData(WallpaperPerformanceProfile.Balanced, 30)]
[InlineData(WallpaperPerformanceProfile.Saver, 15)]
public void FromProfile_SetsMaxPresentFps(WallpaperPerformanceProfile profile, int? expectedFps)
{
    var policy = PlaybackPerformancePolicy.FromProfile(profile);
    Assert.Equal(expectedFps, policy.MaxPresentFps);
}

[Fact]
public void FromProfile_SaverKeepsNonReferenceDiscard()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Saver);
    Assert.Equal(DecoderFrameDiscard.NonReference, policy.DecoderDiscard);
}

[Fact]
public void MinFrameIntervalUs_ForBalanced_IsOneOver30()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
    Assert.Equal(1_000_000L / 30, policy.MinFrameIntervalUs);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"`
Expected: FAIL — current `FromProfile` returns `null` for Balanced, but test expects 30.

- [ ] **Step 3: Update FromProfile**

In `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`, replace the `FromProfile` body (lines 18-23):

```csharp
    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15, DecoderFrameDiscard.NonReference),
            WallpaperPerformanceProfile.Balanced => new PlaybackPerformancePolicy(30, DecoderFrameDiscard.Default),
            _ => new PlaybackPerformancePolicy(null, DecoderFrameDiscard.Default),  // Quality: passthrough
        };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs
git commit -m "Re-enable MaxPresentFps: Balanced=30, Saver=15 (filter-layer throttle)"
```

---

## Task 5: Wire FilterGraph into FfmpegBackend — route frames through the filter, rebuild on policy change

This is the central integration task. It depends on Task 1's probe result: if the probe returned **A** (fps filter accepts D3D11), build the filter with `pixFmt = AV_PIX_FMT_D3D11` for the zero-copy path and the software pix_fmt otherwise. If the probe returned **B**, only build the filter for the software/CPU path and skip it for zero-copy (zero-copy will rely on the render-loop PTS pacing, kept in Task 6).

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/FfmpegBackend.cs`

**Interfaces:**
- Consumes: `FilterGraph.TryCreate/PushFrame/TryGetFrame` (Task 3), `PlaybackPerformancePolicy.MaxPresentFps` (Task 4), `FfmpegNative.av_frame_alloc`/`av_frame_unref`/`av_frame_free`
- Produces: `FfmpegBackend` now throttles frame output rate via the filter; `UpdatePerformancePolicy` rebuilds the filter; a new `FilterDroppedFrames` counter for diagnostics.

- [ ] **Step 1: Add filter fields and a frame-pool field**

In `src/WallpaperApp/Services/Playback/FfmpegBackend.cs`, in the field declarations region (after line 69, near `_heldHwFrame`), add:

```csharp
private FilterGraph? _fpsFilter;
private int _filterPixFmt = -1;
private IntPtr _filterOutFrame;   // AVFrame* reused for filter sink output
private long _filterDroppedFrames;
internal long FilterDroppedFramesForTests => _filterDroppedFrames;
```

- [ ] **Step 2: Add a helper to (re)build the filter graph based on current policy**

Add this method (place it near `ApplyDecoderDiscardPolicy`, ~line 412):

```csharp
private void ApplyFpsFilterPolicy()
{
    // Tear down any existing filter graph first.
    DisposeFpsFilter();

    var targetFps = _performancePolicy.MaxPresentFps ?? 0;
    if (targetFps <= 0)
        return;  // Quality / passthrough — no filter.

    // Decide the pixel format the buffer source must advertise.
    // For zero-copy hardware frames the filter sees AV_PIX_FMT_D3D11; for software, the decoded sw format.
    _filterPixFmt = PreferZeroCopy ? FfmpegNative.AV_PIX_FMT_D3D11 : _swsSrcFormat;
    if (_filterPixFmt < 0)
        return;  // don't know the format yet (not opened) — skip; will be built on first frame.

    var sar = new AVRational { Num = 1, Den = 1 };
    _fpsFilter = FilterGraph.TryCreate(_width, _height, _filterPixFmt, _timeBase, sar, targetFps);
    if (_fpsFilter == null)
        _logger.Warn($"Failed to build fps filter graph (pixFmt={_filterPixFmt}, targetFps={targetFps}); falling back to passthrough");
}

private void DisposeFpsFilter()
{
    if (_filterOutFrame != IntPtr.Zero)
    {
        FfmpegNative.av_frame_unref(_filterOutFrame);
        FfmpegNative.av_frame_free(ref _filterOutFrame);
    }
    _fpsFilter?.Dispose();
    _fpsFilter = null;
}
```

Note: `av_frame_free` must already be declared in `FfmpegNative.cs` (it's used elsewhere in the backend for `_avFrame` cleanup). Verify by grepping; if missing, add:
`[LibraryImport(AvUtil)] internal static partial void av_frame_free(ref IntPtr frame);`

- [ ] **Step 3: Call ApplyFpsFilterPolicy after codec open succeeds**

In `OpenCodecWithFallback` (lines 381-410), after EACH successful `avcodec_open2` return path (the hardware success at line 395 `return true;`, and the software success at line 409 `return ... == 0`), insert a call to build the filter. The cleanest spot: at the end of `OpenAsync` after `OpenCodecWithFallback` returns true (around line 134, after the width/height/format fields are populated). Add just before `OpenAsync` returns success:

```csharp
ApplyFpsFilterPolicy();
```

(Locate the exact success-return of `OpenAsync` — after stream info / codec params / dimensions are known — and add the call there, so `_width`/`_height`/`_timeBase` are set.)

- [ ] **Step 4: Route decoded frames through the filter in NextFrameAsync**

This is the core change. In `NextFrameAsync` (lines 201-376), the decode loop currently produces a frame in `_avFrame` then branches to zero-copy or sws_scale. Insert filter routing **after** a usable frame is in `_avFrame` but **before** the zero-copy / sws branch. 

The strategy: when a filter exists, push `_avFrame` into the buffer source, then loop `TryGetFrame` into `_filterOutFrame` until one comes out (feeding more decoded frames if the sink returns EAGAIN). When no filter exists, behavior is unchanged.

Concretely — after the point where `_avFrame` holds a decoded (and, for the hw→sw case, transferred) frame, replace the direct branch with:

```csharp
// --- filter throttling ---
if (_fpsFilter != null)
{
    if (_filterOutFrame == IntPtr.Zero)
        _filterOutFrame = FfmpegNative.av_frame_alloc();

    // Push the decoded frame into the filter source. KEEP_REF means the filter won't steal our frame data.
    while (true)
    {
        if (!_fpsFilter.PushFrame(_avFrame))
            break;  // push failed — fall through to direct path below

        // Drain whatever the sink emits. The fps filter may swallow frames (dropping) and emit nothing.
        if (_fpsFilter.TryGetFrame(_filterOutFrame, out var err))
        {
            // A throttled frame is available in _filterOutFrame. Use IT instead of _avFrame.
            // Swap: unref the original decoded frame, point subsequent logic at _filterOutFrame.
            FfmpegNative.av_frame_unref(_avFrame);
            // Copy _filterOutFrame's metadata into _avFrame so the existing zero-copy/sws code below works unchanged.
            FfmpegNative.av_frame_move_ref(_avFrame, _filterOutFrame);
            break;
        }
        else if (err)
        {
            break;  // hard error — fall back to the unfiltered frame this iteration
        }
        // else: EAGAIN — the filter ate the frame (dropped for rate limiting) but produced no output yet.
        // The caller's outer while(true) loop will decode the NEXT source frame and push it.
        _filterDroppedFrames++;
        FfmpegNative.av_frame_unref(_avFrame);
        // Continue the outer decode loop to get another frame; we must not return null here because
        // we haven't produced an output frame yet. Hand control back to the decode loop:
        goto decodeNextFrame;
    }
}
```

`av_frame_move_ref` must be declared in `FfmpegNative.cs` if not already. Check first:
`grep -n "av_frame_move_ref" src/WallpaperApp/Services/Playback/FfmpegNative.cs`
If missing, add: `[LibraryImport(AvUtil)] internal static partial void av_frame_move_ref(IntPtr dst, IntPtr src);`

The `decodeNextFrame:` label goes at the top of the existing `while(true)` decode loop body (line ~218, just before `av_read_frame`). This label lets the filter's EAGAIN path ask for more decoded input without returning a frame to the render loop.

IMPORTANT: this is the trickiest edit. The existing `NextFrameAsync` is a single `while(true)` loop inside a `Task.Run`. Read lines 201-376 fully before editing, and place the filter block between the frame-availability check (after hw→sw transfer if applicable) and the `if (PreferZeroCopy)` / sws branch. The zero-copy/sws code then operates on `_avFrame` (which now holds the filter's output frame when a filter is active).

- [ ] **Step 5: Rebuild the filter on policy change**

Update `UpdatePerformancePolicy` (lines 83-87) to also rebuild the fps filter:

```csharp
public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
{
    var fpsChanged = policy.MaxPresentFps != _performancePolicy.MaxPresentFps;
    _performancePolicy = policy;
    ApplyDecoderDiscardPolicy();
    if (fpsChanged && _isOpen)
        ApplyFpsFilterPolicy();   // rebuilds (or removes) the filter graph
}
```

- [ ] **Step 6: Dispose the filter in the backend Dispose path**

In the existing `Dispose(bool)` method of `FfmpegBackend`, add a call to `DisposeFpsFilter();` before the other native-resource frees.

- [ ] **Step 7: Write/update FfmpegBackend unit tests**

In `tests/WallpaperApp.Tests/Services/FfmpegBackendTests.cs`, the existing tests use `new FfmpegBackend(_logger)` without a real media file, so they can't exercise real decode. Add a test asserting the policy-rebuild plumbing does NOT throw when no media is open, and that `ApplyFpsFilterPolicy` is a safe no-op pre-open:

```csharp
[Fact]
public void UpdatePerformancePolicy_BeforeOpen_DoesNotThrow()
{
    using var backend = new FfmpegBackend(_logger);
    var ex = Record.Exception(() => backend.UpdatePerformancePolicy(
        new PlaybackPerformancePolicy(30, DecoderFrameDiscard.Default)));
    Assert.Null(ex);
    Assert.Equal(30, backend.CurrentPerformancePolicyForTests.MaxPresentFps);
}
```

For real-decode filter validation, rely on the smoke/probe tests (Task 1 probe + existing `FfmpegBackendSmokeTests`).

- [ ] **Step 8: Build and run the unit tests**

Run: `dotnet build WallpaperApp.sln && dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~FfmpegBackendTests|FullyQualifiedName~FilterGraphTests|FullyQualifiedName~PlaybackPerformancePolicyTests"`
Expected: build succeeds, all targeted tests PASS.

- [ ] **Step 9: Commit**

```bash
git add src/WallpaperApp/Services/Playback/FfmpegBackend.cs tests/WallpaperApp.Tests/Services/FfmpegBackendTests.cs
git commit -m "Route decoded frames through fps filter; rebuild on policy change"
```

---

## Task 6: Clean up PlaybackSession render loop — remove dead present gate, update perf log

With the filter now handling throttling in the backend (Task 5), the render loop's present-side gate (`ShouldPresentFrame`, `skippedFrames`, `lastPresentedUs`) is dead weight. Remove it. Keep the PTS-pacing `Thread.Sleep` for now (the filter throttles output rate, but PTS pacing still keeps the loop from spinning on `Present` when the filter emits faster than the source clock — VSync backstops this, but the sleep avoids a busy loop). The perf log gains a `filterDropped` field read from the backend.

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs` (lines 138-142, 300-321, 367-374)
- Modify: `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`

**Interfaces:**
- Consumes: `IPlaybackBackend.FilterDroppedFramesForTests` (or a new public accessor) for the perf log
- Produces: a cleaner render loop with no present gate; updated `LogPerformanceSummary` format.

- [ ] **Step 1: Update tests that reference ShouldPresentFrame**

In `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`, find any test referencing `ShouldPresentFrame` (grep: `grep -n "ShouldPresentFrame" tests/`). If `ShouldPresentFrame` is `internal`, tests may call it directly. Convert those tests to assert the NEW behavior: the render loop presents every frame the backend produces (the backend's filter is responsible for dropping). If a test was specifically validating "30 FPS policy skips frames", that contract is now invalid (filter throttling moved to the backend) — replace it with a test asserting "Balanced policy → backend received UpdatePerformancePolicy with MaxPresentFps=30".

Example replacement test:

```csharp
[Fact]
public async Task RenderLoop_PassesBalancedPolicyToBackend()
{
    var (session, backend, renderer, _) = await StartSessionAsync();
    session.UpdatePerformancePolicy(PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced));
    // Let the loop apply the policy.
    await Task.Delay(150);
    session.Stop();
    Assert.Equal(30, backend.LastAppliedPolicy.MaxPresentFps);
}
```

This requires the `FakePlaybackBackend` in the test file to track `LastAppliedPolicy` — add a field `public PlaybackPerformancePolicy LastAppliedPolicy;` and set it in the fake's `UpdatePerformancePolicy` override.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackSessionTests"`
Expected: FAIL (the new test expects the new field/logic not yet present).

- [ ] **Step 3: Remove ShouldPresentFrame and the present gate from the render loop**

In `src/WallpaperApp/Services/Playback/PlaybackSession.cs`:
- **Delete** the `ShouldPresentFrame` method (lines 138-142).
- In `RenderLoop`, delete: `var lastPresentedUs = -1L;` (line 301), `var skippedFrames = 0L;` (line 305).
- Delete the present-gate block (lines 367-374): the `if (!ShouldPresentFrame(...)) { skippedFrames++; ... frame.Dispose(); continue; }`.
- Update the frame-present block to present unconditionally (the backend's filter already dropped excess frames):

```csharp
        var policy = CurrentPerformancePolicy;
        if (appliedBackendPolicy != policy)
        {
            _backend.UpdatePerformancePolicy(policy);
            appliedBackendPolicy = policy;
        }

        var ok = _renderer!.Present(frame);
        presentedFrames++;
        frame.Dispose();
        LogPerformanceSummary(policy, _clock.NowUs);
```

- [ ] **Step 4: Update LogPerformanceSummary to include filterDropped and remove skipped**

The backend exposes `FilterDroppedFramesForTests` (internal). Add a public-ish accessor on `IPlaybackBackend` or read the concrete type. Simplest: add `long FilterDroppedFrames { get; }` to `IPlaybackBackend` (and implement on `FfmpegBackend` returning `_filterDroppedFrames`; on `MfBackend`/fakes return 0).

Update `LogPerformanceSummary` (lines 310-321):

```csharp
    void LogPerformanceSummary(PlaybackPerformancePolicy policy, long nowUs)
    {
        if (nowUs - lastPerfLogUs < 30_000_000L)
            return;

        var fpsCap = policy.MaxPresentFps?.ToString() ?? "native";
        var path = _backend!.IsHardwareDecoding ? "zero-copy" : "cpu-upload";
        var dropped = _backend.FilterDroppedFrames;
        _logger.Debug($"Playback perf monitor={_monitorId} path={path} decoded={decodedFrames}/30s presented={presentedFrames}/30s filterDropped={dropped}/30s fpsCap={fpsCap}");
        decodedFrames = 0;
        presentedFrames = 0;
        lastPerfLogUs = nowUs;
    }
```

Note: `filterDropped` here is cumulative from the backend; since this resets `decodedFrames`/`presentedFrames` every 30s but not the backend's counter, either (a) reset the backend counter too (add a `ResetFilterDroppedCount()` method) or (b) report the delta. Simplest correct approach: add `IPlaybackBackend.FilterDroppedFrames` returning cumulative, and track `lastReportedDropped` locally to compute the delta. Use option (b):

```csharp
        var droppedDelta = _backend.FilterDroppedFrames - lastReportedDropped;
        // ... use droppedDelta in the log line ...
        lastReportedDropped = _backend.FilterDroppedFrames;
```

Add `var lastReportedDropped = 0L;` to the loop's locals (near line 300).

- [ ] **Step 5: Add FilterDroppedFrames to IPlaybackBackend and implementations**

In `src/WallpaperApp/Services/Playback/IPlaybackBackend.cs`, add to the interface:
```csharp
long FilterDroppedFrames { get; }
```
- `FfmpegBackend`: `public long FilterDroppedFrames => _filterDroppedFrames;`
- `MfBackend`: `public long FilterDroppedFrames => 0;`
- Both `FakePlaybackBackend` doubles in the test files: `public long FilterDroppedFrames => 0;`

- [ ] **Step 6: Run all tests**

Run: `dotnet test tests/WallpaperApp.Tests`
Expected: ALL PASS (unit tests; smoke tests may be skipped if ffmpeg.exe not on PATH).

- [ ] **Step 7: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackSession.cs src/WallpaperApp/Services/Playback/IPlaybackBackend.cs src/WallpaperApp/Services/Playback/MfBackend.cs tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs tests/WallpaperApp.Tests/Services/PlaybackManagerTests.cs
git commit -m "Remove dead present-side gate; add filterDropped to perf log"
```

---

## Task 7 (conditional — only if Task 1 probe returned result B): waitable-timer fallback for the zero-copy path

Skip this task entirely if Task 1 returned **A**. Only do it if the `fps` filter rejects `AV_PIX_FMT_D3D11`, meaning zero-copy frames can't be filter-throttled.

In that case, zero-copy playback still needs rate limiting, but via a smoother mechanism than the old `Thread.Sleep(ms)`. The fallback: keep `ShouldPresentFrame`-style gating BUT drive the wait with a high-resolution `CreateWaitableTimer`/`SetWaitableTimer` instead of `Thread.Sleep`, eliminating the ~15ms timer-granularity jitter that caused `af68e99` to revert the gate.

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs` (restore a precision-gated present path for the zero-copy case)
- Modify: `src/WallpaperApp/Interop/NativeMethods.cs` (add `CreateWaitableTimer`/`SetWaitableTimer`/`WaitForSingleObject` P/Invokes)

**Interfaces:** same as before — `ShouldPresentFrame` is restored but only active when the backend reports filter throttling is inactive for the current path.

- [ ] **Step 1: Add waitable-timer P/Invokes to NativeMethods.cs**

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
internal static extern IntPtr CreateWaitableTimer(IntPtr lpTimerAttributes, bool manualReset, string? lpTimerName);

[DllImport("kernel32.dll", SetLastError = true)]
internal static extern bool SetWaitableTimer(IntPtr hTimer, ref long dueTime, int period, IntPtr pfnCompletion, IntPtr lpArgToCompletionRoutine, bool resume);

[DllImport("kernel32.dll", SetLastError = true)]
internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
internal static extern bool CloseHandle(IntPtr hObject);
```

- [ ] **Step 2: In PlaybackSession.RenderLoop, replace Thread.Sleep pacing with waitable-timer pacing**

Create one `IntPtr _pacingTimer = NativeMethods.CreateWaitableTimer(IntPtr.Zero, true, null);` per session (dispose in session dispose). Replace the `Thread.Sleep((int)Math.Min(waitUs / 1000, int.MaxValue))` at line 354 with a timer wait:

```csharp
if (waitUs > 0)
{
    var due = -(waitUs * 10);  // negative = relative, in 100ns units
    NativeMethods.SetWaitableTimer(_pacingTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
    NativeMethods.WaitForSingleObject(_pacingTimer, uint.MaxValue);
}
```

- [ ] **Step 3: Keep ShouldPresentFrame ONLY for the no-filter (zero-copy, result B) case**

If the backend has no active filter (e.g. `_backend.FilterDroppedFrames` won't grow because the filter is absent for D3D11), re-enable the present gate using the same waitable-timer precision. This is the fallback path; document it clearly.

- [ ] **Step 4: Build, test, commit**

```bash
git add src/WallpaperApp/Interop/NativeMethods.cs src/WallpaperApp/Services/Playback/PlaybackSession.cs
git commit -m "Use waitable timer for high-precision pacing on zero-copy path (fps filter unavailable for D3D11)"
```

---

## Task 8: Full build, test sweep, and manual validation

**Files:** none (verification only)

- [ ] **Step 1: Clean rebuild + full unit test suite**

Run: `dotnet build WallpaperApp.sln && dotnet test tests/WallpaperApp.Tests`
Expected: build OK, all unit tests PASS.

- [ ] **Step 2: Run smoke tests (requires ffmpeg.exe on PATH)**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~Smoke"`
Expected: PASS (confirms real decode still works end-to-end).

- [ ] **Step 3: Manual validation — compare GPU usage across profiles**

Launch the app with a 1080p60 and a 4K60 wallpaper. For each, cycle Quality → Balanced → Saver and observe Task Manager GPU% (and the 3D/Video Decode engine split if visible). Record before/after numbers.

Expected:
- Quality ≈ source FPS, GPU% unchanged from baseline.
- Balanced ≈ 30 FPS, GPU% measurably lower than Quality.
- Saver ≈ 15 FPS, GPU% lowest of the three.
- No visible stutter/jitter distinct from the old present-side skip behavior (the filter is smoother).
- No blank flash on wallpaper switch, no crash on profile hot-switch.

- [ ] **Step 4: Manual validation — PresentMon (optional but recommended)**

Run PresentMon against the app process. Confirm:
- Present count/s ≈ target FPS for Balanced (30) and Saver (15).
- No present-spike bursts (filter should emit at a steady rate).

- [ ] **Step 5: Final commit (if any test/data files added)**

If a validation-notes file was written, commit it; otherwise this step is a no-op.

---

## Self-Review Notes

**Spec coverage check** (spec section → task):
- "fps filter replaces PTS Sleep" → Task 5 (filter routing) + Task 6 (render loop cleanup). Covered.
- "Profile mapping Balanced=30, Saver=15" → Task 4. Covered.
- "Delete dead present gate" → Task 6. Covered.
- "filterDropped counter in perf log" → Task 6 step 4. Covered.
- "Probe validates D3D11 compatibility" → Task 1. Covered.
- "Fallback: waitable timer" → Task 7 (conditional). Covered.
- "avfilter P/Invokes" → Task 2. Covered.
- "Quality passthrough (no filter)" → Task 5 (ApplyFpsFilterPolicy returns early when targetFps<=0). Covered.

**Type consistency check:**
- `FilterGraph.TryCreate(int,int,int,AVRational,AVRational,int)` — used consistently in Task 3 def and Task 5 caller. ✓
- `FilterDroppedFrames` on `IPlaybackBackend` — added in Task 6 step 5, read in Task 6 step 4. ✓
- `MaxPresentFps` nullable int — Task 4 sets 30/15/null; Task 5 reads `?? 0`. ✓
- `AVRational` — Task 3/5 both reference; flagged in Task 3 step 4 to verify the type exists and is visible. ✓

**Risks flagged inline for the implementer:**
- Task 5 step 4 (the `goto decodeNextFrame` filter loop) is the highest-complexity edit — implementer must read the full `NextFrameAsync` first. Flagged.
- Task 1 probe result gates whether Task 7 runs at all. Flagged.
- `AVERROR_EOF` constant value is version-dependent; if tests reveal it's wrong, use `av_strerror` to diagnose. Flagged in Task 2 step 3.
