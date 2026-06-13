using System.Diagnostics;
using System.Threading;
using WallpaperApp.Interop;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Owns the entire per-monitor render pipeline on a SINGLE dedicated thread:
// the wallpaper window, the D2D render target, the decode backend, and the
// frame loop. Everything is created AND used on that one thread.
//
// Why one thread: Direct2D's HWND render target must be created and driven
// from the same thread that owns the window. If the window lives on the UI
// thread while the render target lives on another thread, EndDraw() returns
// S_OK but the BitBlt never reaches the screen — the wallpaper stays blank
// with zero error logs. Creating the window here (on the render thread) keeps
// window + render target + present on one thread, which actually displays.
public sealed class PlaybackSession : IDisposable
{
    private readonly FileLogger _logger;
    private readonly Guid _monitorId;
    private readonly string _filePath;
    private readonly int _x, _y, _width, _height;
    private readonly Func<int, int, int, int, IWallpaperSurface?> _createSurface;
    private readonly Func<IntPtr, int, int, FileLogger, IFrameRenderer> _createRenderer;
    private readonly Func<IPlaybackBackend> _createBackend;
    private readonly Func<IPlaybackBackend> _createFallbackBackend;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private TaskCompletionSource<bool>? _readyTcs;
    private bool _disposed;

    // Owned by the render thread only.
    private IWallpaperSurface? _surface;
    private IFrameRenderer? _renderer;
    private IPlaybackBackend? _backend;
    private IntPtr _hwnd;

    public Guid MonitorId => _monitorId;
    public bool IsPlaying => _backend?.IsPlaying ?? false;
    public bool IsPaused => _backend?.IsPaused ?? false;

    public PlaybackSession(
        Guid monitorId,
        string filePath,
        int x, int y, int width, int height,
        Func<int, int, int, int, IWallpaperSurface?> createSurface,
        Func<IntPtr, int, int, FileLogger, IFrameRenderer> createRenderer,
        Func<IPlaybackBackend> createBackend,
        Func<IPlaybackBackend> createFallbackBackend,
        FileLogger logger)
    {
        _monitorId = monitorId;
        _filePath = filePath;
        _x = x;
        _y = y;
        _width = width;
        _height = height;
        _createSurface = createSurface;
        _createRenderer = createRenderer;
        _createBackend = createBackend;
        _createFallbackBackend = createFallbackBackend;
        _logger = logger;
    }

    public Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult(false);
        _cts?.Cancel();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = $"WallpaperRender-{_monitorId}",
        };
        _thread.Start();

        return _readyTcs.Task.WaitAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        var thread = _thread;
        if (thread != null && thread.IsAlive)
        {
            // Wait for the render thread to fully exit before disposing the
            // window / renderer / backend it owns.
            try { thread.Join(); } catch { }
        }
        _thread = null;

        if (_backend != null)
        {
            try { await _backend.StopAsync(ct); } catch { }
        }
        _logger.Info($"Session stopped for monitor {_monitorId}");
    }

    public Task PauseAsync(CancellationToken ct = default)
        => _backend?.PauseAsync(ct) ?? Task.CompletedTask;

    public Task ResumeAsync(CancellationToken ct = default)
        => _backend?.ResumeAsync(ct) ?? Task.CompletedTask;

    // Runs entirely on the dedicated render thread.
    private void Run(CancellationToken ct)
    {
        try
        {
            // 1. Create the window ON THIS THREAD so it shares the render
            //    thread's ownership (required for the D2D HWND render target).
            _surface = _createSurface(_x, _y, _width, _height);
            if (_surface == null || _surface.Handle == IntPtr.Zero)
            {
                _logger.Error($"No wallpaper window available for monitor {_monitorId}");
                _readyTcs?.TrySetResult(false);
                return;
            }
            _hwnd = _surface.Handle;

            _renderer = _createRenderer(
                _hwnd,
                _surface.Width > 0 ? _surface.Width : _width,
                _surface.Height > 0 ? _surface.Height : _height,
                _logger);

            // 2. Open the file (with fallback backend if the primary fails).
            _backend = _createBackend();
            if (!_backend.OpenAsync(_filePath, ct).GetAwaiter().GetResult())
            {
                _logger.Warn("Primary backend failed to open file, trying fallback");
                _backend.Dispose();
                _backend = _createFallbackBackend();
                if (!_backend.OpenAsync(_filePath, ct).GetAwaiter().GetResult())
                {
                    _logger.Error($"Failed to load wallpaper for monitor {_monitorId}: {_filePath}");
                    _readyTcs?.TrySetResult(false);
                    return;
                }
            }

            _backend.PlayAsync(ct).GetAwaiter().GetResult();
            _logger.Info($"Session started for monitor {_monitorId}");

            RenderLoop(ct);
        }
        catch (OperationCanceledException)
        {
            // Stop/cancel path.
        }
        catch (Exception ex)
        {
            _logger.Warn($"Session error for monitor {_monitorId}: {ex.Message}");
            _readyTcs?.TrySetResult(false);
        }
        finally
        {
            _readyTcs?.TrySetResult(false);
        }
    }

    private void RenderLoop(CancellationToken ct)
    {
        var lastPts = -1L;
        var sw = new Stopwatch();

        while (!ct.IsCancellationRequested && _backend!.IsPlaying)
        {
            // Pump window messages on the render thread so WM_NCHITTEST
            // (click-through) and WM_PAINT are handled on the window's owner.
            PumpMessages();

            if (_backend.IsPaused)
            {
                Thread.Sleep(50);
                continue;
            }

            var frame = _backend.NextFrameAsync(ct).GetAwaiter().GetResult();
            if (frame == null)
            {
                _backend.SeekAsync(TimeSpan.Zero, ct).GetAwaiter().GetResult();
                _backend.PlayAsync(ct).GetAwaiter().GetResult();
                lastPts = -1L;
                sw.Restart();
                continue;
            }

            if (lastPts > 0 && frame.PtsUs > lastPts)
            {
                var frameDurationUs = frame.PtsUs - lastPts;
                var elapsedUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                var waitUs = Math.Max(0L, frameDurationUs - elapsedUs);
                if (waitUs > 0)
                    Thread.Sleep((int)Math.Min(waitUs / 1000, int.MaxValue));
            }

            sw.Restart();
            lastPts = frame.PtsUs;

            var ok = _renderer!.Present(frame);
            frame.Dispose();

            // Signal readiness from the first frame's result so StartAsync
            // reports success only once the pipeline has actually rendered
            // end-to-end. Idempotent: the first TrySetResult wins.
            _readyTcs?.TrySetResult(ok);

            if (!ok)
            {
                _logger.Warn($"Render failed for monitor {_monitorId}, stopping");
                break;
            }
        }
    }

    private void PumpMessages()
    {
        if (_hwnd == IntPtr.Zero) return;
        while (NativeMethods.PeekMessageW(out var msg, _hwnd, 0, 0, NativeMethods.PM_REMOVE))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        try { _thread?.Join(); } catch { }
        _cts?.Dispose();

        // Owned by the render thread; safe to release after it has joined.
        _renderer?.Dispose();
        _backend?.Dispose();
        _surface?.Dispose();
    }
}
