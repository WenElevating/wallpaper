using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class MfBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;
    public bool IsPlaying => false;
    public bool IsPaused => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public TimeSpan Position => TimeSpan.Zero;
    public event EventHandler? EndOfStream;
    public MfBackend(FileLogger logger) { _logger = logger; _logger.Info("MF backend stub initialized"); }
    public Task<bool> OpenAsync(string filePath, CancellationToken ct = default) { _logger.Warn($"MF stub: cannot open {filePath}"); return Task.FromResult(false); }
    public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;
    public Task<FrameData?> NextFrameAsync(CancellationToken ct = default) => Task.FromResult<FrameData?>(null);
    public void Dispose() { }
}
