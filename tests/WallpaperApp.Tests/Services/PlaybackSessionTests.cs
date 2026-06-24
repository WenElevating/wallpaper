using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PlaybackSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public PlaybackSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PlaybackSessionTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    [Fact]
    public async Task StartAsync_ReturnsFalse_WhenFirstRenderFails()
    {
        using var backend = new FakePlaybackBackend(CreateFrame());
        using var renderer = new FakeRenderer(false);
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var session = new PlaybackSession(
            Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
            (_, _, _, _) => surface,
            (_, _, _, _) => renderer,
            () => backend,
            () => throw new NotImplementedException(),
            _logger);

        var started = await session.StartAsync();

        Assert.False(started);
        Assert.Equal(1, renderer.PresentCalls);
    }

    [Fact]
    public async Task StartAsync_ReturnsTrue_WhenFirstRenderSucceeds()
    {
        using var backend = new FakePlaybackBackend(CreateFrame());
        using var renderer = new FakeRenderer(true);
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var session = new PlaybackSession(
            Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
            (_, _, _, _) => surface,
            (_, _, _, _) => renderer,
            () => backend,
            () => throw new NotImplementedException(),
            _logger);

        var started = await session.StartAsync();
        await session.StopAsync();

        Assert.True(started);
        Assert.True(renderer.PresentCalls >= 1);
    }

    [Fact]
    public async Task PerformancePolicy_CappedMode_PresentsFewerFramesThanDecoded()
    {
        using var backend = new FakePlaybackBackend(
            CreateFrame(0),
            CreateFrame(1_000),
            CreateFrame(2_000),
            CreateFrame(40_000));
        using var renderer = new FakeRenderer(true);
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var session = new PlaybackSession(
            Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
            (_, _, _, _) => surface,
            (_, _, _, _) => renderer,
            () => backend,
            () => throw new NotImplementedException(),
            _logger,
            new PlaybackPerformancePolicy(30),
            new FakeClock(0, 1_000, 2_000, 40_000));

        var started = await session.StartAsync();
        await session.StopAsync();

        Assert.True(started);
        Assert.True(backend.NextFrameCalls >= 2);
        Assert.True(renderer.PresentCalls < backend.NextFrameCalls);
    }

    [Fact]
    public async Task PerformancePolicy_QualityMode_PresentsEveryDecodedFrame()
    {
        using var backend = new FakePlaybackBackend(
            CreateFrame(0),
            CreateFrame(1_000),
            CreateFrame(2_000));
        using var renderer = new FakeRenderer(true);
        using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        using var session = new PlaybackSession(
            Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
            (_, _, _, _) => surface,
            (_, _, _, _) => renderer,
            () => backend,
            () => throw new NotImplementedException(),
            _logger,
            new PlaybackPerformancePolicy(null),
            new FakeClock(0, 1_000, 2_000));

        var started = await session.StartAsync();
        await session.StopAsync();

        Assert.True(started);
        Assert.Equal(backend.NextFrameCalls, renderer.PresentCalls);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // A session is paused while ANY reason is present and only resumes once the
    // last reason clears. These cover the reason-coordination contract that lets
    // auto-pause (fullscreen/battery) coexist with the user's manual pause
    // without one clobbering the other.
    [Fact]
    public async Task PauseReason_AutoResume_DoesNotClobberManualPause()
    {
        var (session, backend) = await StartSessionAsync();

        // User manually pauses, then a fullscreen app pauses for its own reason.
        await session.PauseAsync();                              // User
        await session.ApplyPauseAsync(PauseReason.Fullscreen);   // Fullscreen
        Assert.True(backend.IsPaused);

        // Leaving fullscreen clears ONLY the Fullscreen reason. The user's pause
        // is still active, so the session must STAY paused.
        await session.ClearPauseAsync(PauseReason.Fullscreen);
        Assert.True(backend.IsPaused);

        // Only once the user resumes too does playback actually resume.
        await session.ResumeAsync();
        Assert.False(backend.IsPaused);

        await session.StopAsync();
    }

    [Fact]
    public async Task PauseReason_SecondReason_DoesNotRedundantlyPause()
    {
        var (session, backend) = await StartSessionAsync();

        await session.PauseAsync();                              // User -> paused
        await session.ApplyPauseAsync(PauseReason.Power);        // Power added
        Assert.True(backend.IsPaused);

        // User resumes while Power still holds -> stays paused.
        await session.ResumeAsync();
        Assert.True(backend.IsPaused);

        // Clearing the last reason (Power) finally resumes.
        await session.ClearPauseAsync(PauseReason.Power);
        Assert.False(backend.IsPaused);

        await session.StopAsync();
    }

    [Fact]
    public async Task PauseReason_ClearingInactiveReason_IsNoOp()
    {
        var (session, backend) = await StartSessionAsync();
        await session.PauseAsync();                              // User -> paused
        // Clearing a reason that was never set must not resume the session.
        await session.ClearPauseAsync(PauseReason.Fullscreen);
        Assert.True(backend.IsPaused);
        await session.ResumeAsync();
        Assert.False(backend.IsPaused);
        await session.StopAsync();
    }

    // Starts a session (so its _backend is assigned) with a renderer that keeps
    // it "playing" long enough to drive the reason methods, then returns it.
    private async Task<(PlaybackSession session, FakePlaybackBackend backend)> StartSessionAsync()
    {
        var backend = new FakePlaybackBackend(CreateFrame());
        var renderer = new FakeRenderer(true);
        var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
        var session = new PlaybackSession(
            Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
            (_, _, _, _) => surface,
            (_, _, _, _) => renderer,
            () => backend,
            () => throw new NotImplementedException(),
            _logger);
        await session.StartAsync();
        return (session, backend);
    }

    private static FrameData CreateFrame()
        => CreateFrame(0);

    private static FrameData CreateFrame(long ptsUs)
    {
        var size = 4 * 4;
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        return new FrameData(buffer, 1, 1, 4, ptsUs);
    }

    private sealed class FakeClock : IClock
    {
        private readonly Queue<long> _timestamps;
        private long _lastTimestamp;

        public FakeClock(params long[] timestamps)
        {
            _timestamps = new Queue<long>(timestamps);
            _lastTimestamp = timestamps.Length > 0 ? timestamps[^1] : 0;
        }

        public long NowUs
        {
            get
            {
                if (_timestamps.Count > 0)
                    _lastTimestamp = _timestamps.Dequeue();
                return _lastTimestamp;
            }
        }
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

    private sealed class FakePlaybackBackend : IPlaybackBackend
    {
        private readonly Queue<FrameData?> _frames;

        public FakePlaybackBackend(params FrameData?[] frames)
        {
            _frames = new Queue<FrameData?>(frames);
        }

        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsHardwareDecoding => false;
        public int VideoWidth => 0;
        public int VideoHeight => 0;
        public TimeSpan Duration => TimeSpan.Zero;
        public TimeSpan Position => TimeSpan.Zero;
        public int NextFrameCalls { get; private set; }
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
            {
                var frame = _frames.Dequeue();
                if (frame != null)
                    NextFrameCalls++;
                return Task.FromResult(frame);
            }

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

    private sealed class FakeRenderer : IFrameRenderer
    {
        private readonly bool _presentResult;

        public FakeRenderer(bool presentResult)
        {
            _presentResult = presentResult;
        }

        public int PresentCalls { get; private set; }

        public bool Present(FrameData frame)
        {
            PresentCalls++;
            return _presentResult;
        }

        public void Resize(int width, int height)
        {
        }

        public void Dispose()
        {
        }
    }
}
