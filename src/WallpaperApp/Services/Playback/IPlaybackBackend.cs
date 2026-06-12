namespace WallpaperApp.Services.Playback;

public interface IPlaybackBackend : IDisposable
{
    bool IsPlaying { get; }
    bool IsPaused { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    Task<bool> OpenAsync(string filePath, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);
    Task<FrameData?> NextFrameAsync(CancellationToken ct = default);
    event EventHandler? EndOfStream;
}

public sealed class FrameData : IDisposable
{
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public long PtsUs { get; }
    private bool _disposed;

    public FrameData(IntPtr buffer, int width, int height, int stride, long ptsUs)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PtsUs = ptsUs;
    }

    // Memory owned by FfmpegBackend double-buffer pool — Dispose marks consumed
    // so renderer doesn't reuse stale references.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
