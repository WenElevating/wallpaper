using WallpaperApp.Models;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PowerAwareControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public PowerAwareControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PowerAwareControllerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    // PauseOnBattery = true keeps the old hard-pause behavior. No scene
    // throttle is applied while paused (the gate is irrelevant when nothing
    // is presenting), so the scene flag must be OFF.
    [Fact]
    public void Poll_OnBattery_WithPauseEnabled_PausesOnly()
    {
        var settings = new AppSettings { PauseOnBattery = true };
        using var recorder = new RecordingPlaybackManager(_logger);
        using var controller = new PowerAwareController(_logger, recorder, () => settings, () => true);

        controller.PollOnce();

        Assert.Equal(1, recorder.PauseAllCalls);
        Assert.Equal(0, recorder.ResumeAllCalls);
        Assert.Equal(ScenePerformanceState.Battery, recorder.LastSceneState);
        Assert.False(recorder.LastSceneActive);
    }

    // PauseOnBattery = false: instead of pausing, the battery scene throttle
    // drops the present rate to 15 FPS so playback continues at reduced cost.
    [Fact]
    public void Poll_OnBattery_WithPauseDisabled_ThrottlesSceneInstead()
    {
        var settings = new AppSettings { PauseOnBattery = false };
        using var recorder = new RecordingPlaybackManager(_logger);
        using var controller = new PowerAwareController(_logger, recorder, () => settings, () => true);

        controller.PollOnce();

        Assert.Equal(0, recorder.PauseAllCalls);
        Assert.Equal(ScenePerformanceState.Battery, recorder.LastSceneState);
        Assert.True(recorder.LastSceneActive);
    }

    [Fact]
    public void Poll_OnAc_ClearsBatteryPauseAndThrottle()
    {
        var settings = new AppSettings { PauseOnBattery = true };
        using var recorder = new RecordingPlaybackManager(_logger);
        using var controller = new PowerAwareController(_logger, recorder, () => settings, () => false);

        controller.PollOnce();

        Assert.Equal(0, recorder.PauseAllCalls);
        Assert.Equal(ScenePerformanceState.Battery, recorder.LastSceneState);
        Assert.False(recorder.LastSceneActive);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Records the playback-manager calls the controller makes, so the test can
    // assert the pause/scene-throttle split without touching real sessions.
    private sealed class RecordingPlaybackManager : PlaybackManager
    {
        public int PauseAllCalls { get; private set; }
        public int ResumeAllCalls { get; private set; }
        public ScenePerformanceState? LastSceneState { get; private set; }
        public bool? LastSceneActive { get; private set; }

        public RecordingPlaybackManager(FileLogger logger)
            : base(logger, new DesktopHost(logger))
        {
        }

        public override Task PauseAllAsync(PauseReason reason, CancellationToken ct = default)
        {
            PauseAllCalls++;
            return Task.CompletedTask;
        }

        public override Task ResumeAllAsync(PauseReason reason, CancellationToken ct = default)
        {
            ResumeAllCalls++;
            return Task.CompletedTask;
        }

        public override void SetSceneState(ScenePerformanceState state, bool active)
        {
            LastSceneState = state;
            LastSceneActive = active;
        }
    }
}
