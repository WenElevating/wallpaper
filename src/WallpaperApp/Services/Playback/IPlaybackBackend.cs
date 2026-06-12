using System.Runtime.InteropServices;

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

public sealed class FrameData(IntPtr buffer, int width, int height, int stride, long presentationTimestampUs) : IDisposable
{
    public IntPtr Buffer { get; } = buffer;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public long PresentationTimestampUs { get; } = presentationTimestampUs;

    public void Dispose()
    {
        if (Buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(Buffer);
    }
}
