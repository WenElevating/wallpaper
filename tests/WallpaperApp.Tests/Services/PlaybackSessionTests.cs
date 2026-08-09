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
    public async Task PerformancePolicy_BalancedMode_SkipsFramesCloserThanInterval()
    {
        // Balanced => MaxPresentFps=30 => MinFrameIntervalUs ≈ 33_333us.
        // Frames at clock-times 0, 1ms, 2ms, 40ms: first always presents, the
        // two at 1ms/2ms are skipped (< 33.3ms interval), the 40ms frame
        // presents. So of 4 frames, at most 2 can be presented regardless of
        // how many the loop actually consumed before StopAsync arrives — the
        // exact decode count is timing-dependent (precise pacing consumes frames
        // faster than the old coarse Thread.Sleep did), so we assert the gate's
        // invariant rather than a specific decode count.
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
            PlaybackPerformancePolicy.FromProfile(WallpaperApp.Models.WallpaperPerformanceProfile.Balanced),
            new FakeClock(0, 1_000, 2_000, 40_000));

        var started = await session.StartAsync();
        await session.StopAsync();

        Assert.True(started);
        // The present-side gate must cap presents at the interval-allowed count.
        // With a 33.3ms interval and frames at 0/1/2/40ms, at most 2 of the 4
        // decoded frames are allowed to present (frame 0 + the 40ms frame). The
        // exact present count is timing-dependent (how many frames the loop
        // consumed before StopAsync arrives), but it can NEVER exceed 2 if the
        // gate is wired — and at least 1 (StartAsync resolves on first present).
        // The boundary-by-boundary skip logic is pinned by the direct
        // ShouldPresentFrame_* unit tests below; this test proves the wiring.
        Assert.InRange(renderer.PresentCalls, 1, 2);
    }

    [Fact]
    public async Task PerformancePolicy_SaverMode_PushesDecoderDiscardToBackend()
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
            _logger,
            PlaybackPerformancePolicy.FromProfile(WallpaperApp.Models.WallpaperPerformanceProfile.Saver),
            new FakeClock(0));

        var started = await session.StartAsync();
        await session.StopAsync();

        Assert.True(started);
        Assert.Equal(DecoderFrameDiscard.NonReference, backend.CurrentPolicy.DecoderDiscard);
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

    // Direct unit tests of the ShouldPresentFrame gate (Task 5). These exercise
    // the pure gate logic with the real FromProfile policy values, independent
    // of the render loop's threading/timing. The integration tests above cover
    // the end-to-end path; these pin down the exact interval-boundary behavior
    // that the precision pacing makes effective (skip under interval, present at
    // interval). Balanced => MaxPresentFps=30 => MinFrameIntervalUs ≈ 33_333us;
    // Quality => MaxPresentFps=30 => MinFrameIntervalUs ≈ 33_333us (GPU opt:
    // a 60 FPS source is copied+drawn at most 30 times per second).
    [Fact]
    public void ShouldPresentFrame_Balanced_SkipsUnderInterval()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(
            WallpaperApp.Models.WallpaperPerformanceProfile.Balanced);
        var interval = policy.MinFrameIntervalUs; // 1_000_000 / 30 = 33_333us

        // First frame always presents (lastPresentedUs < 0 sentinel).
        Assert.True(PlaybackSession.ShouldPresentFrame(0, -1, policy));
        // Frame 1us after a present: well under the 33.3ms interval => skip.
        Assert.False(PlaybackSession.ShouldPresentFrame(1, 0, policy));
        // Frame exactly at the interval boundary => present (>= interval).
        Assert.True(PlaybackSession.ShouldPresentFrame(interval, 0, policy));
        // Frame one tick before the interval => still skip.
        Assert.False(PlaybackSession.ShouldPresentFrame(interval - 1, 0, policy));
        // Frame comfortably past the interval => present.
        Assert.True(PlaybackSession.ShouldPresentFrame(interval + 5_000, 0, policy));
    }

    [Fact]
    public void ShouldPresentFrame_Quality_CapsAtThirtyFps()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(
            WallpaperApp.Models.WallpaperPerformanceProfile.Quality);

        // Quality now caps presents at 30 FPS like the other profiles.
        Assert.Equal(33_333, policy.MinFrameIntervalUs);
        Assert.True(PlaybackSession.ShouldPresentFrame(0, -1, policy));
        Assert.False(PlaybackSession.ShouldPresentFrame(20_000, 0, policy));
        Assert.True(PlaybackSession.ShouldPresentFrame(40_000, 0, policy));
    }

    [Fact]
    public void ShouldPresentFrame_Saver_UsesContinuousPlaybackInterval()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(
            WallpaperApp.Models.WallpaperPerformanceProfile.Saver);

        // Saver uses a 30 FPS proxy so the cadence stays continuous rather than
        // forcing the old 15 FPS presentation pattern.
        Assert.Equal(33_333, policy.MinFrameIntervalUs);
        Assert.True(PlaybackSession.ShouldPresentFrame(0, -1, policy));
        Assert.False(PlaybackSession.ShouldPresentFrame(20_000, 0, policy));
        Assert.True(PlaybackSession.ShouldPresentFrame(40_000, 0, policy));
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

    [Fact]
    public async Task StopAsync_DoesNotWaitForeverForUncooperativeDecoder()
    {
        var backend = new BlockingPlaybackBackend(CreateFrame());
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
        await backend.Blocked;

        var stopTask = session.StopAsync();
        try
        {
            var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.Same(stopTask, completed);
            await Assert.ThrowsAsync<TimeoutException>(() => stopTask);
        }
        finally
        {
            backend.Release();
            await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));
            session.Dispose();
        }
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
        public PlaybackPerformancePolicy CurrentPolicy { get; private set; }
        public event EventHandler? EndOfStream;

        public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
        {
            CurrentPolicy = policy;
        }

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

    private sealed class BlockingPlaybackBackend : IPlaybackBackend
    {
        private readonly FrameData _firstFrame;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextFrameCalls;
        private bool _released;
        public Task Blocked => _blocked.Task;

        public BlockingPlaybackBackend(FrameData firstFrame) => _firstFrame = firstFrame;

        public bool IsPlaying { get; private set; }
        public bool IsPaused => false;
        public bool IsHardwareDecoding => false;
        public int VideoWidth => 1;
        public int VideoHeight => 1;
        public TimeSpan Duration => TimeSpan.Zero;
        public TimeSpan Position => TimeSpan.Zero;
        public event EventHandler? EndOfStream { add { } remove { } }
        public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy) { }
        public Task<bool> OpenAsync(string filePath, CancellationToken ct = default) => Task.FromResult(true);
        public Task PlayAsync(CancellationToken ct = default) { if (!_released) IsPlaying = true; return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) { IsPlaying = false; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _nextFrameCalls) == 1)
                return _firstFrame;
            _blocked.TrySetResult();
            await _release.Task;
            return null;
        }

        public void Release()
        {
            _released = true;
            IsPlaying = false;
            _release.TrySetResult();
        }
        public void Dispose() { }
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
