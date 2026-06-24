namespace WallpaperApp.Services.Playback;

public interface IPlaybackBackend : IDisposable
{
    bool IsPlaying { get; }
    bool IsPaused { get; }
    bool IsHardwareDecoding { get; }
    int VideoWidth { get; }
    int VideoHeight { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    void UpdatePerformancePolicy(PlaybackPerformancePolicy policy);
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
    // CPU path: a pointer into FfmpegBackend's BGRA double-buffer pool.
    public IntPtr Buffer { get; }
    public int Stride { get; }

    // GPU zero-copy path: the decoded NV12 ID3D11Texture2D* (on the shared
    // device) and its array slice index. Renderer blits it via a shader — no
    // CPU/system-RAM round-trip.
    public IntPtr Texture { get; }
    public int TextureIndex { get; }
    public bool IsGpu => Texture != IntPtr.Zero;

    public int Width { get; }
    public int Height { get; }
    public long PtsUs { get; }
    private bool _disposed;

    // CPU (BGRA buffer) frame.
    public FrameData(IntPtr buffer, int width, int height, int stride, long ptsUs)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PtsUs = ptsUs;
    }

    // GPU (NV12 texture, zero-copy) frame. Static factory — the param types
    // would clash with the CPU constructor, so it gets its own name.
    public static FrameData Gpu(IntPtr texture, int textureIndex, int width, int height, long ptsUs)
        => new FrameData(texture, textureIndex, width, height, ptsUs, gpu: true);

    private FrameData(IntPtr texture, int textureIndex, int width, int height, long ptsUs, bool gpu)
    {
        Texture = texture;
        TextureIndex = textureIndex;
        Width = width;
        Height = height;
        PtsUs = ptsUs;
    }

    // Marks the frame consumed. CPU buffers are owned by FfmpegBackend's pool
    // (not freed here); GPU textures are owned by FFmpeg's frame pool (kept
    // alive by the backend until the next NextFrameAsync call).
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
