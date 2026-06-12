using System.IO;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackManager : IDisposable
{
    private readonly FileLogger _logger;
    private readonly DesktopHost _desktopHost;
    private readonly Dictionary<Guid, PlaybackSession> _sessions = new();
    private readonly Dictionary<Guid, IFrameRenderer> _renderers = new();
    private readonly object _lock = new();
    private bool _disposed;

    public PlaybackManager(FileLogger logger, DesktopHost desktopHost)
    {
        _logger = logger;
        _desktopHost = desktopHost;
    }

    public bool IsPlaying(Guid monitorId)
    {
        lock (_lock)
            return _sessions.TryGetValue(monitorId, out var s) && s.IsPlaying;
    }

    public async Task SetWallpaperAsync(Guid monitorId, Guid wallpaperId, string filePath, CancellationToken ct = default)
    {
        if (_disposed) return;

        // Stop existing session
        await RemoveWallpaperInternalAsync(monitorId, ct);

        // Find wallpaper window for this monitor
        var wallpaperWindow = _desktopHost.WallpaperWindows.FirstOrDefault();
        if (wallpaperWindow == null)
        {
            _logger.Error($"No wallpaper window available for monitor {monitorId}");
            return;
        }

        var backend = CreateBackend();
        IFrameRenderer renderer = new D2dRenderer(
            wallpaperWindow.Handle,
            wallpaperWindow.Width > 0 ? wallpaperWindow.Width : 1920,
            wallpaperWindow.Height > 0 ? wallpaperWindow.Height : 1080,
            _logger);

        var session = new PlaybackSession(monitorId, backend, renderer, _logger);
        var loaded = await session.LoadFileAsync(filePath, ct);

        if (!loaded)
        {
            backend.Dispose();
            renderer.Dispose();
            _logger.Warn("FfmpegBackend failed, trying MfBackend fallback");
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
            _renderers[monitorId] = renderer;
        }

        await session.StartAsync(ct);
        _logger.Info($"Wallpaper set on monitor {monitorId}: {Path.GetFileName(filePath)}");
    }

    public async Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        await RemoveWallpaperInternalAsync(monitorId, ct);
    }

    private async Task RemoveWallpaperInternalAsync(Guid monitorId, CancellationToken ct)
    {
        PlaybackSession? session;
        IFrameRenderer? renderer;
        lock (_lock)
        {
            _sessions.Remove(monitorId, out session);
            _renderers.Remove(monitorId, out renderer);
        }
        if (session != null)
        {
            await session.StopAsync(ct);
            session.Dispose();
        }
        renderer?.Dispose();
    }

    public async Task PauseAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
            if (s.IsPlaying && !s.IsPaused)
                await s.PauseAsync(ct);
    }

    public async Task ResumeAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
            if (s.IsPlaying && s.IsPaused)
                await s.ResumeAsync(ct);
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        IFrameRenderer[] renderers;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            renderers = _renderers.Values.ToArray();
            _renderers.Clear();
        }
        foreach (var s in sessions)
        {
            await s.StopAsync(ct);
            s.Dispose();
        }
        foreach (var r in renderers)
            r.Dispose();
    }

    private IPlaybackBackend CreateBackend() => new FfmpegBackend(_logger);
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
        IFrameRenderer[] renderers;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            renderers = _renderers.Values.ToArray();
            _renderers.Clear();
        }
        foreach (var s in sessions) s.Dispose();
        foreach (var r in renderers) r.Dispose();
    }
}
