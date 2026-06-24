using System.Diagnostics;
using System.Threading;
using WallpaperApp.Interop;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Why a playback session is paused. Multiple independent sources can pause a
// session at once (the user via the tray, a fullscreen app, battery); the
// session stays paused while ANY reason is present and only resumes once the
// LAST reason is cleared. This prevents an auto-resume (e.g. leaving a
// fullscreen app) from clobbering a pause the user set manually, and vice versa.
public enum PauseReason
{
    User,
    Fullscreen,
    Power,
    Occluded, // wallpaper fully covered by other windows (not visible)
    RemoteDesktop, // RDP or Miracast session active (bandwidth saver)
}

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
    private readonly Guid _wallpaperId;
    private readonly string _filePath;
    private readonly int _x, _y, _width, _height;
    private readonly Func<int, int, int, int, IWallpaperSurface?> _createSurface;
    private readonly Func<IntPtr, int, int, FileLogger, IFrameRenderer> _createRenderer;
    private readonly Func<IPlaybackBackend> _createBackend;
    private readonly Func<IPlaybackBackend> _createFallbackBackend;
    private readonly object _performancePolicyLock = new();
    private readonly IClock _clock;
    private PlaybackPerformancePolicy _performancePolicy;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private TaskCompletionSource<bool>? _readyTcs;
    private bool _disposed;

    // Pause reasons currently active on this session. Guarded by _pauseLock
    // because Pause/Resume are driven from the PlaybackManager (threadpool),
    // not the render thread. Empty = playing; non-empty = paused.
    private readonly HashSet<PauseReason> _pauseReasons = new();
    private readonly object _pauseLock = new();

    // Owned by the render thread only.
    private IWallpaperSurface? _surface;
    private IFrameRenderer? _renderer;
    private IPlaybackBackend? _backend;
    private IntPtr _hwnd;

    public Guid MonitorId => _monitorId;
    public Guid WallpaperId => _wallpaperId;
    public bool IsPlaying => _backend?.IsPlaying ?? false;
    public bool IsPaused => _backend?.IsPaused ?? false;
    internal PlaybackPerformancePolicy CurrentPerformancePolicy
    {
        get
        {
            lock (_performancePolicyLock)
                return _performancePolicy;
        }
    }

    internal PlaybackPerformancePolicy PerformancePolicyForTests => CurrentPerformancePolicy;

    public PlaybackSession(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int x, int y, int width, int height,
        Func<int, int, int, int, IWallpaperSurface?> createSurface,
        Func<IntPtr, int, int, FileLogger, IFrameRenderer> createRenderer,
        Func<IPlaybackBackend> createBackend,
        Func<IPlaybackBackend> createFallbackBackend,
        FileLogger logger,
        PlaybackPerformancePolicy performancePolicy = default)
        : this(
            monitorId,
            wallpaperId,
            filePath,
            x,
            y,
            width,
            height,
            createSurface,
            createRenderer,
            createBackend,
            createFallbackBackend,
            logger,
            performancePolicy,
            null)
    {
    }

    internal PlaybackSession(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int x, int y, int width, int height,
        Func<int, int, int, int, IWallpaperSurface?> createSurface,
        Func<IntPtr, int, int, FileLogger, IFrameRenderer> createRenderer,
        Func<IPlaybackBackend> createBackend,
        Func<IPlaybackBackend> createFallbackBackend,
        FileLogger logger,
        PlaybackPerformancePolicy performancePolicy,
        IClock? clock)
    {
        _monitorId = monitorId;
        _wallpaperId = wallpaperId;
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
        _performancePolicy = performancePolicy;
        _clock = clock ?? new StopwatchClock();
    }

    internal static bool ShouldPresentFrame(long nowUs, long lastPresentedUs, PlaybackPerformancePolicy policy)
    {
        var minIntervalUs = policy.MinFrameIntervalUs;
        return minIntervalUs <= 0 || lastPresentedUs < 0 || nowUs - lastPresentedUs >= minIntervalUs;
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

    public Task PauseAsync(CancellationToken ct = default) => ApplyPauseAsync(PauseReason.User, ct);
    public Task ResumeAsync(CancellationToken ct = default) => ClearPauseAsync(PauseReason.User, ct);

    public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
    {
        lock (_performancePolicyLock)
            _performancePolicy = policy;
    }

    // Adds a pause reason. Calls the backend's PauseAsync only on the empty->
    // non-empty transition (no-op if already paused for any reason), so a
    // second reason never re-issues a redundant pause.
    public async Task ApplyPauseAsync(PauseReason reason, CancellationToken ct = default)
    {
        IPlaybackBackend? backend;
        lock (_pauseLock)
        {
            if (!_pauseReasons.Add(reason))
                return; // this reason already active; nothing to transition
            if (_pauseReasons.Count != 1)
                return; // was already paused by another reason; backend already paused
            backend = _backend;
        }
        if (backend != null)
        {
            try { await backend.PauseAsync(ct); } catch { }
        }
    }

    // Removes a pause reason. Calls the backend's ResumeAsync only when the
    // last reason is cleared — so an auto-resume (e.g. leaving fullscreen)
    // never clobbers a pause still held for another reason.
    public async Task ClearPauseAsync(PauseReason reason, CancellationToken ct = default)
    {
        IPlaybackBackend? backend;
        lock (_pauseLock)
        {
            if (!_pauseReasons.Remove(reason))
                return; // this reason wasn't active; nothing to transition
            if (_pauseReasons.Count != 0)
                return; // still paused by another reason; stay paused
            backend = _backend;
        }
        if (backend != null)
        {
            try { await backend.ResumeAsync(ct); } catch { }
        }
    }

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

            // Decide zero-copy once per session: if the renderer can set up the
            // NV12 shader path, the backend hands frames as GPU textures (no CPU
            // color convert); otherwise the backend decodes to CPU BGRA and the
            // renderer uploads it. Either way Present() handles the frame type.
            var zeroCopy = _renderer is DxgiRenderer dx && dx.TryInitZeroCopy(_backend.VideoWidth, _backend.VideoHeight);
            if (_backend is FfmpegBackend fb)
                fb.PreferZeroCopy = zeroCopy;
            _logger.Info(zeroCopy
                ? $"Monitor {_monitorId}: zero-copy GPU render enabled (NV12 shader)"
                : $"Monitor {_monitorId}: using CPU color pipeline");

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
        var lastPresentedUs = -1L;
        var lastPerfLogUs = _clock.NowUs;
        var decodedFrames = 0L;
        var presentedFrames = 0L;
        var skippedFrames = 0L;
        var sw = new Stopwatch();
        var loggedMode = false;

        void LogPerformanceSummary(PlaybackPerformancePolicy policy, long nowUs)
        {
            if (nowUs - lastPerfLogUs < 30_000_000L)
                return;

            var fpsCap = policy.MaxPresentFps?.ToString() ?? "native";
            _logger.Debug($"Playback perf monitor={_monitorId} decoded={decodedFrames}/30s presented={presentedFrames}/30s skipped={skippedFrames}/30s fpsCap={fpsCap}");
            decodedFrames = 0;
            presentedFrames = 0;
            skippedFrames = 0;
            lastPerfLogUs = nowUs;
        }

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
                lastPresentedUs = -1L;
                sw.Restart();
                continue;
            }

            decodedFrames++;

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

            var policy = CurrentPerformancePolicy;
            var nowUs = _clock.NowUs;
            if (!ShouldPresentFrame(nowUs, lastPresentedUs, policy))
            {
                skippedFrames++;
                LogPerformanceSummary(policy, nowUs);
                frame.Dispose();
                continue;
            }

            var ok = _renderer!.Present(frame);
            presentedFrames++;
            lastPresentedUs = nowUs;
            frame.Dispose();
            LogPerformanceSummary(policy, nowUs);

            // Signal readiness from the first frame's result so StartAsync
            // reports success only once the pipeline has actually rendered
            // end-to-end. Idempotent: the first TrySetResult wins.
            _readyTcs?.TrySetResult(ok);

            // One-time diagnostic: report whether this session is actually
            // decoding on the GPU (D3D11VA) or fell back to software. Software
            // decode keeps H.264 reference frames in system RAM (~hundreds of
            // MB) and burns a CPU core, so this is the key thing to watch.
            if (!loggedMode)
            {
                loggedMode = true;
                _logger.Info($"Monitor {_monitorId}: {_backend!.VideoWidth}x{_backend.VideoHeight} decoding via {(_backend.IsHardwareDecoding ? "D3D11VA (hardware)" : "SOFTWARE (fallback)")}");
            }

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
