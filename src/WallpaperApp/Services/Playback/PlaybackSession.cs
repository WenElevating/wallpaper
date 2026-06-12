using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackSession : IDisposable
{
    private readonly FileLogger _logger;
    private readonly IPlaybackBackend _backend;
    private readonly Guid _monitorId;
    private CancellationTokenSource? _renderCts;
    private Task? _renderTask;
    private bool _disposed;

    public Guid MonitorId => _monitorId;
    public bool IsPlaying => _backend.IsPlaying;
    public bool IsPaused => _backend.IsPaused;
    public IPlaybackBackend Backend => _backend;

    public PlaybackSession(Guid monitorId, IPlaybackBackend backend, FileLogger logger)
    {
        _monitorId = monitorId;
        _backend = backend;
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

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await _backend.PauseAsync(ct);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _backend.ResumeAsync(ct);
    }

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        await _backend.PlayAsync(ct);
        while (!ct.IsCancellationRequested && _backend.IsPlaying)
        {
            if (!_backend.IsPaused)
            {
                var frame = await _backend.NextFrameAsync(ct);
                if (frame == null)
                {
                    break;
                }
            }
            await Task.Delay(16, ct);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderCts?.Cancel();
        _renderCts?.Dispose();
        _backend.Dispose();
    }
}
