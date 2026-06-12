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

    public Task PauseAsync(CancellationToken ct = default) => _backend.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct = default) => _backend.ResumeAsync(ct);

    private async Task RenderLoopAsync(CancellationToken ct)
    {
        await _backend.PlayAsync(ct);
        var lastPts = -1L;
        var sw = new Stopwatch();

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
                await _backend.SeekAsync(TimeSpan.Zero, ct);
                await _backend.PlayAsync(ct);
                lastPts = -1;
                continue;
            }

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

            var ok = _renderer.Present(frame);
            frame.Dispose();

            if (!ok)
            {
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
