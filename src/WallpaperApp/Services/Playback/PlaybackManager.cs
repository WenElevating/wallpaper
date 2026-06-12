using System.IO;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackManager : IDisposable
{
    private readonly FileLogger _logger;
    private readonly Dictionary<Guid, PlaybackSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    public PlaybackManager(FileLogger logger)
    {
        _logger = logger;
    }

    public bool IsPlaying(Guid monitorId)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(monitorId, out var session) && session.IsPlaying;
        }
    }

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

        var backend = CreateBackend();
        var session = new PlaybackSession(monitorId, backend, _logger);
        var loaded = await session.LoadFileAsync(filePath, ct);
        if (!loaded)
        {
            // Try fallback backend
            backend.Dispose();
            _logger.Warn($"FfmpegBackend failed to load, trying MfBackend fallback");
            backend = CreateFallbackBackend();
            session = new PlaybackSession(monitorId, backend, _logger);
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

    public async Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        PlaybackSession? session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(monitorId, out session!)) return;
            _sessions.Remove(monitorId);
        }
        await session.StopAsync(ct);
        session.Dispose();
        _logger.Info($"Wallpaper removed from monitor {monitorId}");
    }

    public async Task PauseAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
        {
            if (s.IsPlaying && !s.IsPaused)
                await s.PauseAsync(ct);
        }
        _logger.Info("All sessions paused");
    }

    public async Task ResumeAllAsync(CancellationToken ct = default)
    {
        PlaybackSession[] sessions;
        lock (_lock) { sessions = _sessions.Values.ToArray(); }
        foreach (var s in sessions)
        {
            if (s.IsPlaying && s.IsPaused)
                await s.ResumeAsync(ct);
        }
        _logger.Info("All sessions resumed");
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
        _logger.Info("All sessions stopped");
    }

    private IPlaybackBackend CreateBackend()
    {
        return new FfmpegBackend(_logger);
    }

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
