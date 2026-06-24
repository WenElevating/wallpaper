using System.IO;
using WallpaperApp.Models;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public class PlaybackManager : IDisposable, IPlaybackPauseController
{
    private readonly FileLogger _logger;
    private readonly DesktopHost _desktopHost;
    private readonly Dictionary<Guid, PlaybackSession> _sessions = new();
    private readonly Func<int, int, int, int, IWallpaperSurface?> _createSurface;
    private readonly Func<IntPtr, int, int, FileLogger, IFrameRenderer> _createRenderer;
    private readonly Func<IPlaybackBackend> _createBackend;
    private readonly Func<IPlaybackBackend> _createFallbackBackend;
    private readonly object _lock = new();
    private PlaybackPerformancePolicy _performancePolicy =
        PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
    private bool _disposed;

    // Shared GPU device for zero-copy hardware decode + render. Set by App after
    // construction. When null/unavailable, decode+render use a per-session device
    // and the CPU color pipeline (the proven fallback path).
    public GpuDevice? Gpu { get; set; }

    public PlaybackManager(
        FileLogger logger,
        DesktopHost desktopHost,
        Func<int, int, int, int, IWallpaperSurface?>? createSurface = null,
        Func<IntPtr, int, int, FileLogger, IFrameRenderer>? createRenderer = null,
        Func<IPlaybackBackend>? createBackend = null,
        Func<IPlaybackBackend>? createFallbackBackend = null)
    {
        _logger = logger;
        _desktopHost = desktopHost;
        _createSurface = createSurface ?? ((x, y, width, height) => _desktopHost.CreateForMonitor(x, y, width, height));
        _createRenderer = createRenderer ?? ((hwnd, width, height, fileLogger) => new DxgiRenderer(hwnd, width, height, fileLogger, Gpu));
        _createBackend = createBackend ?? CreateBackend;
        _createFallbackBackend = createFallbackBackend ?? CreateFallbackBackend;
    }

    public bool IsPlaying(Guid monitorId)
    {
        lock (_lock)
            return _sessions.TryGetValue(monitorId, out var s) && s.IsPlaying;
    }

    // Returns the wallpaper currently shown on the given monitor, or null if no
    // session is active. Used by the tray "shuffle" command to avoid picking the
    // wallpaper that is already on screen.
    public virtual Guid? GetActiveWallpaperId(Guid monitorId)
    {
        lock (_lock)
            return _sessions.TryGetValue(monitorId, out var s) ? s.WallpaperId : null;
    }

    // True when at least one wallpaper session is active (drives the pause
    // button's enabled state in the UI).
    public bool HasActiveSessions
    {
        get { lock (_lock) return _sessions.Count > 0; }
    }

    public event EventHandler? SessionsChanged;

    private void RaiseSessionsChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    public virtual async Task<bool> SetWallpaperAsync(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int monitorX,
        int monitorY,
        int monitorWidth,
        int monitorHeight,
        CancellationToken ct = default)
    {
        if (_disposed) return false;

        // F6: build the new session FIRST, then tear down the old one. The old
        // code removed the existing session before creating the replacement,
        // which left a gap where no child window covered the desktop WorkerW —
        // the user briefly saw the underlying static system wallpaper on every
        // switch. By starting the new session (which only resolves after its
        // first frame is presented) before disposing the old, the desktop is
        // never uncovered: the transition is old-frame → new-frame with no
        // intermediate blank.
        //
        // Capture the existing session BY REFERENCE (not via the dictionary key):
        // once the new session succeeds we overwrite the key, so a subsequent
        // RemoveWallpaperInternalAsync(monitorId) would dispose the NEW session.
        // We dispose oldSession directly off the captured reference instead.
        PlaybackSession? oldSession;
        PlaybackPerformancePolicy performancePolicy;
        lock (_lock)
        {
            _sessions.TryGetValue(monitorId, out oldSession);
            performancePolicy = _performancePolicy;
        }

        // The session owns the full pipeline (window + renderer + backend) and
        // runs it on a dedicated render thread so the D2D HWND render target
        // shares one thread with its window.
        var session = new PlaybackSession(
            monitorId,
            wallpaperId,
            filePath,
            monitorX, monitorY, monitorWidth, monitorHeight,
            _createSurface,
            _createRenderer,
            _createBackend,
            _createFallbackBackend,
            _logger,
            performancePolicy);

        bool started;
        try
        {
            // StartAsync resolves only after the first frame renders, so on
            // success the new window is already visibly covering the desktop.
            started = await session.StartAsync(ct);
        }
        catch (Exception ex)
        {
            // Defensive: PlaybackSession.Run swallows its own exceptions and
            // resolves false, but guard against anything thrown before the
            // render thread takes over (e.g. thread start). Never let a half-
            // built session leak; the old session is untouched.
            _logger.Error($"Exception starting wallpaper playback for monitor {monitorId}: {filePath}", ex);
            session.Dispose();
            return false;
        }

        if (!started)
        {
            // New wallpaper failed to load/render. Dispose just the failed
            // session and leave the old one in place and playing — the user
            // keeps seeing the previous wallpaper instead of a blank desktop.
            session.Dispose();
            _logger.Error($"Failed to start wallpaper playback for monitor {monitorId}: {filePath}");
            return false;
        }

        // New session is live and rendering. Swap it into the dictionary,
        // evicting the old reference, then dispose the old session by reference.
        lock (_lock)
            _sessions[monitorId] = session;
        RaiseSessionsChanged();

        if (oldSession != null)
        {
            try { await oldSession.StopAsync(ct); }
            catch (Exception ex) { _logger.Warn($"Failed to stop previous session on {monitorId}: {ex.Message}"); }
            try { oldSession.Dispose(); }
            catch (Exception ex) { _logger.Warn($"Failed to dispose previous session on {monitorId}: {ex.Message}"); }
        }

        _logger.Info($"Wallpaper set on monitor {monitorId}: {Path.GetFileName(filePath)}");
        return true;
    }

    public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
    {
        PlaybackSession[] sessions;
        lock (_lock)
        {
            _performancePolicy = policy;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
            session.UpdatePerformancePolicy(policy);
    }

    internal PlaybackPerformancePolicy? GetPerformancePolicyForTests(Guid monitorId)
    {
        lock (_lock)
            return _sessions.TryGetValue(monitorId, out var session)
                ? session.PerformancePolicyForTests
                : null;
    }

    public virtual async Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        await RemoveWallpaperInternalAsync(monitorId, ct);
    }

    private async Task RemoveWallpaperInternalAsync(Guid monitorId, CancellationToken ct)
    {
        PlaybackSession? session;
        lock (_lock)
            _sessions.Remove(monitorId, out session);

        if (session != null)
        {
            await session.StopAsync(ct);
            session.Dispose();
            RaiseSessionsChanged();
        }
    }

    // Pauses every active session for the given reason (default User, the
    // manual/tray pause). Each session tracks reasons independently: a session
    // is paused while ANY reason is present, and only resumes once its last
    // reason clears — so an auto-resume can't clobber a pause held for another
    // reason. Reason accounting lives in PlaybackSession.ApplyPauseAsync.
    public Task PauseAllAsync(CancellationToken ct = default) => PauseAllAsync(PauseReason.User, ct);
    public async Task PauseAllAsync(PauseReason reason, CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
            if (s.IsPlaying)
                await s.ApplyPauseAsync(reason, ct);
    }

    public Task ResumeAllAsync(CancellationToken ct = default) => ResumeAllAsync(PauseReason.User, ct);
    public async Task ResumeAllAsync(PauseReason reason, CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
            if (s.IsPlaying)
                await s.ClearPauseAsync(reason, ct);
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var s in sessions)
        {
            await s.StopAsync(ct);
            s.Dispose();
        }
        RaiseSessionsChanged();
    }

    private IPlaybackBackend CreateBackend() => new FfmpegBackend(_logger, AcquireHwDevice);

    // Returns a D3D11VA device for the decoder: the shared GPU device when
    // available (enables zero-copy), else a fresh per-session device.
    private IntPtr AcquireHwDevice()
        => Gpu is { IsAvailable: true } ? HwDecodeDevice.CreateForDevice(Gpu.DevicePointer) : HwDecodeDevice.CreateNew();
    private IPlaybackBackend CreateFallbackBackend()
    {
        _logger.Warn("FfmpegBackend failed, falling back to MfBackend");
        return new MfBackend(_logger);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PlaybackSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }
        foreach (var s in sessions)
            s.Dispose();
    }
}
