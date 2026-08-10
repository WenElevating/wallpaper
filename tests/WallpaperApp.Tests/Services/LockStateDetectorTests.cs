using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class LockStateDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public LockStateDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LockStateDetectorTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    // Locking throttles every live session to the 5 FPS scene interval; the
    // base profile's 30 FPS floor is restored on unlock. Uses the real
    // PlaybackManager (with fakes) so the whole detector -> manager -> session
    // wiring is exercised.
    [Fact]
    public async Task SetLocked_ThrottlesSessionsAndRestoresOnUnlock()
    {
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var backend = new FakePlaybackBackend(CreateFrame());
        using var renderer = new FakeRenderer(true);
        using var desktopHost = new DesktopHost(_logger);
        using var manager = new PlaybackManager(
            _logger, desktopHost,
            createSurface: (_, _, _, _) => surface,
            createRenderer: (_, _, _, _) => renderer,
            createBackend: () => backend,
            createFallbackBackend: () => new FakePlaybackBackend());
        using var detector = new LockStateDetector(_logger, manager, pollIntervalMs: 5000);

        var monitorId = Guid.NewGuid();
        Assert.True(await manager.SetWallpaperAsync(monitorId, Guid.NewGuid(), "sample.mp4", 0, 0, 1, 1));

        detector.SetLocked(true);
        Assert.True(detector.IsLocked);
        Assert.Equal(200_000, manager.GetPerformancePolicyForTests(monitorId)!.Value.MinFrameIntervalUs);

        detector.SetLocked(false);
        Assert.False(detector.IsLocked);
        Assert.Equal(33_333, manager.GetPerformancePolicyForTests(monitorId)!.Value.MinFrameIntervalUs);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static FrameData CreateFrame()
    {
        var size = 4 * 4;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        return new FrameData(buffer, 1, 1, 4, 0);
    }

    private sealed class FakeWallpaperSurface : IWallpaperSurface
    {
        public FakeWallpaperSurface(IntPtr handle, int width, int height)
        {
            Handle = handle;
            Width = width;
            Height = height;
        }

        public IntPtr Handle { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRenderer : IFrameRenderer
    {
        private readonly bool _result;

        public FakeRenderer(bool result) => _result = result;

        public int PresentCalls { get; private set; }

        public bool Present(FrameData frame)
        {
            PresentCalls++;
            return _result;
        }

        public void Resize(int width, int height)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        private FrameData? _firstFrame;

        public FakePlaybackBackend(FrameData? firstFrame = null) => _firstFrame = firstFrame;

        public bool IsPlaying { get; private set; }
        public bool IsPaused => false;
        public bool IsHardwareDecoding => false;
        public int VideoWidth => 1;
        public int VideoHeight => 1;
        public TimeSpan Duration => TimeSpan.Zero;
        public TimeSpan Position => TimeSpan.Zero;
        public event EventHandler? EndOfStream;

        public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
        {
        }

        public Task<bool> OpenAsync(string filePath, CancellationToken ct = default) => Task.FromResult(true);

        public Task PlayAsync(CancellationToken ct = default)
        {
            IsPlaying = true;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
        {
            var frame = _firstFrame;
            _firstFrame = null;
            if (frame != null)
                return Task.FromResult<FrameData?>(frame);

            IsPlaying = false;
            EndOfStream?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<FrameData?>(null);
        }

        public void Dispose()
        {
        }
    }
}
