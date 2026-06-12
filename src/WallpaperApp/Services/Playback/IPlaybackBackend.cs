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

public record FrameData(IntPtr Buffer, int Width, int Height, int Stride, long PresentationTimestampUs);
