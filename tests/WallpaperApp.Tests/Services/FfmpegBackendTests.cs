using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public class FfmpegBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public FfmpegBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FfmpegBackendTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_InitializesWithLogger()
    {
        var backend = new FfmpegBackend(_logger);
        Assert.NotNull(backend);
        backend.Dispose();
    }

    [Fact]
    public async Task OpenAsync_NonexistentFile_ReturnsFalse()
    {
        using var backend = new FfmpegBackend(_logger);
        var result = await backend.OpenAsync("C:\\nonexistent\\file.mp4");
        Assert.False(result);
    }

    [Fact]
    public async Task OpenAsync_InvalidFile_ReturnsFalse()
    {
        var fakeFile = Path.Combine(_tempDir, "fake.mp4");
        File.WriteAllText(fakeFile, "not a video");
        using var backend = new FfmpegBackend(_logger);
        var result = await backend.OpenAsync(fakeFile);
        Assert.False(result);
    }

    [Fact]
    public void IsPlaying_DefaultsToFalse()
    {
        using var backend = new FfmpegBackend(_logger);
        Assert.False(backend.IsPlaying);
    }

    [Fact]
    public void IsPaused_DefaultsToFalse()
    {
        using var backend = new FfmpegBackend(_logger);
        Assert.False(backend.IsPaused);
    }

    [Fact]
    public void Duration_DefaultsToTimeSpanZero()
    {
        using var backend = new FfmpegBackend(_logger);
        Assert.Equal(TimeSpan.Zero, backend.Duration);
    }

    [Fact]
    public void Position_DefaultsToTimeSpanZero()
    {
        using var backend = new FfmpegBackend(_logger);
        Assert.Equal(TimeSpan.Zero, backend.Position);
    }

    [Fact]
    public async Task PlayAsync_SetsIsPlayingTrue_IsPausedFalse()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.PlayAsync();
        Assert.True(backend.IsPlaying);
        Assert.False(backend.IsPaused);
    }

    [Fact]
    public async Task PauseAsync_SetsIsPausedTrue()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.PauseAsync();
        Assert.True(backend.IsPaused);
    }

    [Fact]
    public async Task ResumeAsync_SetsIsPausedFalse()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.PauseAsync();
        Assert.True(backend.IsPaused);
        await backend.ResumeAsync();
        Assert.False(backend.IsPaused);
    }

    [Fact]
    public async Task StopAsync_ResetsAllState()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.PlayAsync();
        await backend.StopAsync();
        Assert.False(backend.IsPlaying);
        Assert.False(backend.IsPaused);
        Assert.Equal(TimeSpan.Zero, backend.Position);
    }

    [Fact]
    public async Task NextFrameAsync_WithoutProcess_ReturnsNull()
    {
        using var backend = new FfmpegBackend(_logger);
        var frame = await backend.NextFrameAsync();
        Assert.Null(frame);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var backend = new FfmpegBackend(_logger);
        var ex = Record.Exception(() => backend.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public async Task PlayAsync_ThenPause_ThenResume_CorrectStates()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.PlayAsync();
        Assert.True(backend.IsPlaying);
        Assert.False(backend.IsPaused);

        await backend.PauseAsync();
        Assert.True(backend.IsPlaying);
        Assert.True(backend.IsPaused);

        await backend.ResumeAsync();
        Assert.True(backend.IsPlaying);
        Assert.False(backend.IsPaused);
    }

    [Fact]
    public async Task StopAsync_ResetsPosition()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.SeekAsync(TimeSpan.FromSeconds(5));
        await backend.StopAsync();
        Assert.Equal(TimeSpan.Zero, backend.Position);
    }

    [Fact]
    public void EndOfStream_Event_CanBeSubscribed()
    {
        using var backend = new FfmpegBackend(_logger);
        var invoked = false;
        backend.EndOfStream += (_, _) => invoked = true;
        // Event subscription doesn't throw
        Assert.False(invoked);
    }

    [Fact]
    public async Task SeekAsync_SetsPosition()
    {
        using var backend = new FfmpegBackend(_logger);
        await backend.SeekAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), backend.Position);
    }
}
