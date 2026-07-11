using System.Runtime.InteropServices;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

// DEAD-END (probe result B/C, observed 2026-07-11):
// This probe validated whether FFmpeg's libavfilter `fps` filter can throttle
// AV_PIX_FMT_D3D11 (hardware-decoded) frames. Result: NO. The buffer source
// rejects HW pixel formats without a non-NULL hw_frames_ctx:
//   "Setting BufferSourceContext.pix_fmt to a HW format requires hw_frames_ctx
//    to be non-NULL!" -> graph config fails (0xFFFFFFEA).
// The fps-filter throttling approach is therefore dead for the zero-copy D3D11
// path (the GPU-dominant path). Phase 1 uses a waitable-timer pacing approach
// instead (see PrecisionTimer + PlaybackSession.RenderLoop). Do NOT reattempt
// fps-filter throttling for D3D11 without first plumbing hw_frames_ctx into the
// buffer source args.

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
