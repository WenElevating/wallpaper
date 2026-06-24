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

    // F6: switching wallpaper on the same monitor must start the new session
    // BEFORE disposing the old one, so the desktop is never uncovered. These
    // tests pin that contract: the old session is disposed only after the new
    // one succeeds, and is KEPT when the new one fails.
    //
    // The fakes below are factory-style (fresh instance per create* call) with
    // disposal tracking, because the original single-instance fakes would alias
    // across two concurrent sessions and mask exactly the bugs this guards.

    [Fact]
    public async Task Switch_DisposesOldSession_WhenNewSucceeds()
    {
        // Track every backend/renderer/surface we hand out so we can assert on
        // the first session's disposal after the second switch succeeds.
        var backends = new List<FakePlaybackBackend>();
        var renderers = new List<FakeRenderer>();
        var surfaces = new List<FakeWallpaperSurface>();

        using var desktopHost = new DesktopHost(_logger);
        using var manager = new PlaybackManager(
            _logger,
            desktopHost,
            createSurface: (_, _, _, _) =>
            {
                var s = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
                surfaces.Add(s);
                return s;
            },
            createRenderer: (_, _, _, _) =>
            {
                var r = new FakeRenderer(true);
                renderers.Add(r);
                return r;
            },
            createBackend: () =>
            {
                var b = new FakePlaybackBackend(CreateFrame());
                backends.Add(b);
                return b;
            },
            createFallbackBackend: () => new FakePlaybackBackend());

        var monitorId = Guid.NewGuid();
        var wp1 = Guid.NewGuid();
        var wp2 = Guid.NewGuid();

        var ok1 = await manager.SetWallpaperAsync(monitorId, wp1, "a.mp4", 0, 0, 1, 1);
        Assert.True(ok1);
        Assert.Equal(wp1, manager.GetActiveWallpaperId(monitorId));
        Assert.Single(backends);
        Assert.False(backends[0].IsDisposed);

        var ok2 = await manager.SetWallpaperAsync(monitorId, wp2, "b.mp4", 0, 0, 1, 1);

        Assert.True(ok2);
        // New session is the active one.
        Assert.Equal(wp2, manager.GetActiveWallpaperId(monitorId));
        // Old session's backend/renderer/surface were disposed (by reference).
        Assert.True(backends[0].IsDisposed);
        Assert.True(renderers[0].IsDisposed);
        Assert.True(surfaces[0].IsDisposed);
        // New session's are alive.
        Assert.False(backends[1].IsDisposed);
        Assert.False(renderers[1].IsDisposed);
    }

    [Fact]
    public async Task Switch_KeepsOldSession_WhenNewFails()
    {
        // First session renders fine; second session's renderer fails to present
        // the first frame → new session is discarded, old session stays live.
        var backends = new List<FakePlaybackBackend>();
        var renderers = new List<FakeRenderer>();
        var renderResults = new Queue<bool>(new[] { true, false }); // 1st ok, 2nd fails

        using var desktopHost = new DesktopHost(_logger);
        using var manager = new PlaybackManager(
            _logger,
            desktopHost,
            createSurface: (_, _, _, _) => new FakeWallpaperSurface(new IntPtr(1), 1, 1),
            createRenderer: (_, _, _, _) =>
            {
                var r = new FakeRenderer(renderResults.Dequeue());
                renderers.Add(r);
                return r;
            },
            createBackend: () =>
            {
                var b = new FakePlaybackBackend(CreateFrame());
                backends.Add(b);
                return b;
            },
            createFallbackBackend: () => new FakePlaybackBackend());

        var monitorId = Guid.NewGuid();
        var wp1 = Guid.NewGuid();
        var wp2 = Guid.NewGuid();

        var ok1 = await manager.SetWallpaperAsync(monitorId, wp1, "a.mp4", 0, 0, 1, 1);
        Assert.True(ok1);

        var ok2 = await manager.SetWallpaperAsync(monitorId, wp2, "broken.mp4", 0, 0, 1, 1);

        // New wallpaper failed → reported false, old wallpaper still active.
        Assert.False(ok2);
        Assert.Equal(wp1, manager.GetActiveWallpaperId(monitorId));
        // Old session's backend untouched.
        Assert.False(backends[0].IsDisposed);
    }

    [Fact]
    public async Task SetWallpaperAsync_PassesCurrentPerformancePolicyToSession()
    {
        var backend = new FakePlaybackBackend(CreateFrame());
        var monitorId = Guid.NewGuid();
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var desktopHost = new DesktopHost(_logger);
        using var manager = new PlaybackManager(
            _logger,
            desktopHost,
            createSurface: (_, _, _, _) => surface,
            createRenderer: (_, _, _, _) => new FakeRenderer(true),
            createBackend: () => backend,
            createFallbackBackend: () => new FakePlaybackBackend());

        manager.UpdatePerformancePolicy(new PlaybackPerformancePolicy(15));

        var ok = await manager.SetWallpaperAsync(monitorId, Guid.NewGuid(), "sample.mp4", 0, 0, 1, 1);

        Assert.True(ok);
        Assert.Equal(15, manager.GetPerformancePolicyForTests(monitorId)?.MaxPresentFps);
    }

    [Fact]
    public async Task UpdatePerformancePolicy_UpdatesActiveSessionsWithoutStoppingPlayback()
    {
        var backend = new FakePlaybackBackend(CreateFrame(), CreateFrame());
        var monitorId = Guid.NewGuid();
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var desktopHost = new DesktopHost(_logger);
        using var manager = new PlaybackManager(
            _logger,
            desktopHost,
            createSurface: (_, _, _, _) => surface,
            createRenderer: (_, _, _, _) => new FakeRenderer(true),
            createBackend: () => backend,
            createFallbackBackend: () => new FakePlaybackBackend());

        var ok = await manager.SetWallpaperAsync(monitorId, Guid.NewGuid(), "sample.mp4", 0, 0, 1, 1);
        Assert.True(ok);

        manager.UpdatePerformancePolicy(new PlaybackPerformancePolicy(15));

        Assert.True(backend.IsPlaying);
        Assert.Equal(15, manager.GetPerformancePolicyForTests(monitorId)?.MaxPresentFps);
    }

    [Fact]
    public void PlaybackSession_CurrentPerformancePolicy_ReturnsUpdatedSnapshot()
    {
        using var session = new PlaybackSession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "sample.mp4",
            0,
            0,
            1,
            1,
            (_, _, _, _) => new FakeWallpaperSurface(new IntPtr(1), 1, 1),
            (_, _, _, _) => new FakeRenderer(true),
            () => new FakePlaybackBackend(),
            () => new FakePlaybackBackend(),
            _logger,
            new PlaybackPerformancePolicy(30));

        session.UpdatePerformancePolicy(new PlaybackPerformancePolicy(15));

        Assert.Equal(15, session.CurrentPerformancePolicy.MaxPresentFps);
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
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeRenderer : IFrameRenderer
    {
        private readonly bool _result;

        public FakeRenderer(bool result)
        {
            _result = result;
        }

        public bool IsDisposed { get; private set; }
        public int PresentCalls { get; private set; }

        public bool Present(FrameData frame)
        {
            PresentCalls++;
            return _result;
        }

        public void Resize(int width, int height)
        {
        }

        public void Dispose() => IsDisposed = true;
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
        public bool IsDisposed { get; private set; }
        public bool IsHardwareDecoding => false;
        public int VideoWidth => 0;
        public int VideoHeight => 0;
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
            IsDisposed = true;
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
