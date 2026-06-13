using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PlaybackManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public PlaybackManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PlaybackManagerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    [Fact]
    public async Task SetWallpaperAsync_UsesMonitorBounds_WhenCreatingWallpaperSurface()
    {
        var observed = (x: -1, y: -1, width: -1, height: -1);
        using var surface = new FakeWallpaperSurface(new IntPtr(123), 1920, 1080);
        using var backend = new FakePlaybackBackend(CreateFrame());
        using var fallbackBackend = new FakePlaybackBackend();
        using var renderer = new FakeRenderer(true);
        using var desktopHost = new DesktopHost(_logger);

        using var manager = new PlaybackManager(
            _logger,
            desktopHost,
            createSurface: (x, y, width, height) =>
            {
                observed = (x, y, width, height);
                return surface;
            },
            createRenderer: (_, _, _, _) => renderer,
            createBackend: () => backend,
            createFallbackBackend: () => fallbackBackend);

        var ok = await manager.SetWallpaperAsync(Guid.NewGuid(), Guid.NewGuid(), "sample.mp4", 100, 200, 1600, 900);

        Assert.True(ok);
        Assert.Equal((100, 200, 1600, 900), observed);
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

        public FakeRenderer(bool result)
        {
            _result = result;
        }

        public bool Present(FrameData frame) => _result;

        public void Resize(int width, int height)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        private readonly Queue<FrameData?> _frames;

        public FakePlaybackBackend(params FrameData?[] frames)
        {
            _frames = new Queue<FrameData?>(frames);
        }

        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public TimeSpan Duration => TimeSpan.Zero;
        public TimeSpan Position => TimeSpan.Zero;
        public event EventHandler? EndOfStream;

        public Task<bool> OpenAsync(string filePath, CancellationToken ct = default) => Task.FromResult(true);

        public Task PlayAsync(CancellationToken ct = default)
        {
            IsPlaying = true;
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken ct = default)
        {
            IsPaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken ct = default)
        {
            IsPaused = false;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            IsPlaying = false;
            return Task.CompletedTask;
        }

        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;

        public Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
        {
            if (_frames.Count > 0)
                return Task.FromResult(_frames.Dequeue());

            IsPlaying = false;
            EndOfStream?.Invoke(this, EventArgs.Empty);
            return Task.FromResult<FrameData?>(null);
        }

        public void Dispose()
        {
            while (_frames.Count > 0)
            {
                var frame = _frames.Dequeue();
                if (frame != null)
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(frame.Buffer);
                    frame.Dispose();
                }
            }
        }
    }
}
