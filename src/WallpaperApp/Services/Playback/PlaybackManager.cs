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
    private readonly Dictionary<Guid, SemaphoreSlim> _monitorGates = new();
    private readonly Func<int, int, int, int, IWallpaperSurface?> _createSurface;
    private readonly Func<IntPtr, int, int, FileLogger, IFrameRenderer> _createRenderer;
    private readonly Func<IPlaybackBackend> _createBackend;
    private readonly Func<IPlaybackBackend> _createFallbackBackend;
    private readonly object _lock = new();
    private readonly object _policyTasksLock = new();
    private readonly List<Task> _policyTasks = new();
    private readonly Dictionary<Guid, SessionDescriptor> _sessionDescriptors = new();
    private readonly SemaphoreSlim _policySwitchGate = new(1, 1);
    private PlaybackPerformancePolicy _performancePolicy =
        PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
    private bool _disposed;

    // Set by the composition root once LibraryService has applied the active
    // library root. Null preserves the existing direct-file behavior for tests
    // and non-library integrations.
    public Func<string, WallpaperPerformanceProfile, string>? PlaybackPathResolver { get; set; }

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

    public virtual Task<bool> SetWallpaperAsync(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int monitorX,
        int monitorY,
        int monitorWidth,
        int monitorHeight,
        CancellationToken ct = default)
    {
        var sourcePath = filePath;
        var path = PlaybackPathResolver?.Invoke(sourcePath, _performancePolicy.Profile) ?? sourcePath;
        return SetWallpaperCoreAsync(monitorId, wallpaperId, path, monitorX, monitorY, monitorWidth, monitorHeight, ct, sourcePath, TimeSpan.Zero, null);
    }

    private SemaphoreSlim GetMonitorGate(Guid monitorId)
    {
        lock (_lock)
        {
            if (!_monitorGates.TryGetValue(monitorId, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _monitorGates[monitorId] = gate;
            }
            return gate;
        }
    }

    private async Task<bool> SetWallpaperCoreAsync(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int monitorX,
        int monitorY,
        int monitorWidth,
        int monitorHeight,
        CancellationToken ct,
        string sourcePath,
        TimeSpan initialPosition,
        IReadOnlyCollection<PauseReason>? pauseReasons)
    {
        var monitorGate = GetMonitorGate(monitorId);
        await monitorGate.WaitAsync(ct);
        try
        {
            return await SetWallpaperCoreSerializedAsync(
                monitorId, wallpaperId, filePath, monitorX, monitorY, monitorWidth, monitorHeight,
                ct, sourcePath, initialPosition, pauseReasons);
        }
        finally
        {
            monitorGate.Release();
        }
    }

    private async Task<bool> SetWallpaperCoreSerializedAsync(
        Guid monitorId,
        Guid wallpaperId,
        string filePath,
        int monitorX,
        int monitorY,
        int monitorWidth,
        int monitorHeight,
        CancellationToken ct,
        string sourcePath,
        TimeSpan initialPosition,
        IReadOnlyCollection<PauseReason>? pauseReasons)
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
            performancePolicy,
            initialPosition);

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
        {
            _sessions[monitorId] = session;
            _sessionDescriptors[monitorId] = new SessionDescriptor(
                sourcePath, monitorX, monitorY, monitorWidth, monitorHeight);
        }
        RaiseSessionsChanged();

        if (pauseReasons != null)
            foreach (var reason in pauseReasons)
                await session.ApplyPauseAsync(reason, ct);

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
            if (_disposed) return;
            _performancePolicy = policy;
            sessions = _sessions.Values.ToArray();
        }

        foreach (var session in sessions)
            session.UpdatePerformancePolicy(policy);

        if (PlaybackPathResolver != null)
        {
            Task task;
            lock (_policyTasksLock)
            {
                if (_disposed) return;
                task = SwitchActiveSessionsForPolicyAsync(policy, sessions);
                _policyTasks.Add(task);
            }
            _ = task.ContinueWith(
                completed =>
                {
                    lock (_policyTasksLock)
                        _policyTasks.Remove(completed);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task SwitchActiveSessionsForPolicyAsync(PlaybackPerformancePolicy policy, PlaybackSession[] snapshot)
    {
        await _policySwitchGate.WaitAsync();
        try
        {
            foreach (var oldSession in snapshot)
            {
                SessionDescriptor descriptor;
                lock (_lock)
                {
                    if (!_sessions.TryGetValue(oldSession.MonitorId, out var current) || !ReferenceEquals(current, oldSession) ||
                        !_sessionDescriptors.TryGetValue(oldSession.MonitorId, out descriptor!))
                        continue;
                }

                var path = PlaybackPathResolver?.Invoke(descriptor.SourcePath, policy.Profile) ?? descriptor.SourcePath;
                if (string.Equals(path, oldSession.FilePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var started = await SetWallpaperCoreAsync(
                    oldSession.MonitorId,
                    oldSession.WallpaperId,
                    path,
                    descriptor.X,
                    descriptor.Y,
                    descriptor.Width,
                    descriptor.Height,
                    CancellationToken.None,
                    descriptor.SourcePath,
                    oldSession.Position,
                    oldSession.ActivePauseReasons);
                if (started)
                    _logger.Info($"Playback proxy switched monitor={oldSession.MonitorId} profile={policy.Profile} path={path}");
            }
        }
        finally { _policySwitchGate.Release(); }
    }

    internal PlaybackPerformancePolicy? GetPerformancePolicyForTests(Guid monitorId)
    {
        lock (_lock)
            return _sessions.TryGetValue(monitorId, out var session)
                ? session.PerformancePolicyForTests
                : null;
    }

    public async Task RecreateActiveSessionsAsync(CancellationToken ct = default)
    {
        (PlaybackSession session, SessionDescriptor descriptor)[] active;
        lock (_lock)
        {
            active = _sessions
                .Where(pair => _sessionDescriptors.ContainsKey(pair.Key))
                .Select(pair => (pair.Value, _sessionDescriptors[pair.Key]))
                .ToArray();
        }

        foreach (var (session, descriptor) in active)
        {
            var path = PlaybackPathResolver?.Invoke(descriptor.SourcePath, _performancePolicy.Profile)
                ?? descriptor.SourcePath;
            await SetWallpaperCoreAsync(
                session.MonitorId,
                session.WallpaperId,
                path,
                descriptor.X,
                descriptor.Y,
                descriptor.Width,
                descriptor.Height,
                ct,
                descriptor.SourcePath,
                session.Position,
                session.ActivePauseReasons);
        }
    }

    public virtual async Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        await RemoveWallpaperInternalAsync(monitorId, ct);
    }

    private async Task RemoveWallpaperInternalAsync(Guid monitorId, CancellationToken ct)
    {
        var monitorGate = GetMonitorGate(monitorId);
        await monitorGate.WaitAsync(ct);
        try
        {
            await RemoveWallpaperSerializedAsync(monitorId, ct);
        }
        finally
        {
            monitorGate.Release();
        }
    }

    private async Task RemoveWallpaperSerializedAsync(Guid monitorId, CancellationToken ct)
    {
        PlaybackSession? session;
        lock (_lock)
            _sessions.Remove(monitorId, out session);
        lock (_lock)
            _sessionDescriptors.Remove(monitorId);

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
            _sessionDescriptors.Clear();
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
        Task[] policyTasks;
        lock (_policyTasksLock)
        {
            if (_disposed) return;
            _disposed = true;
            policyTasks = _policyTasks.ToArray();
        }

        foreach (var task in policyTasks)
        {
            try { task.GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.Warn($"Policy switch did not complete during shutdown: {ex.Message}"); }
        }

        PlaybackSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _sessionDescriptors.Clear();
        }
        foreach (var s in sessions)
            s.Dispose();
        foreach (var gate in _monitorGates.Values)
            gate.Dispose();
        _policySwitchGate.Dispose();
    }

    private sealed record SessionDescriptor(
        string SourcePath, int X, int Y, int Width, int Height);
}
