using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public class MfBackendTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public MfBackendTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MfBackendTests_" + Guid.NewGuid().ToString("N")[..8]);
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
    public void Constructor_DoesNotThrow()
    {
        var ex = Record.Exception(() => new MfBackend(_logger));
        Assert.Null(ex);
    }

    [Fact]
    public void IsPlaying_ReturnsFalse()
    {
        using var backend = new MfBackend(_logger);
        Assert.False(backend.IsPlaying);
    }

    [Fact]
    public void IsPaused_ReturnsFalse()
    {
        using var backend = new MfBackend(_logger);
        Assert.False(backend.IsPaused);
    }

    [Fact]
    public void Duration_ReturnsTimeSpanZero()
    {
        using var backend = new MfBackend(_logger);
        Assert.Equal(TimeSpan.Zero, backend.Duration);
    }

    [Fact]
    public void Position_ReturnsTimeSpanZero()
    {
        using var backend = new MfBackend(_logger);
        Assert.Equal(TimeSpan.Zero, backend.Position);
    }

    [Fact]
    public async Task OpenAsync_ReturnsFalse()
    {
        using var backend = new MfBackend(_logger);
        var result = await backend.OpenAsync("any_file.mp4");
        Assert.False(result);
    }

    [Fact]
    public async Task PlayAsync_DoesNotThrow()
    {
        using var backend = new MfBackend(_logger);
        var ex = await Record.ExceptionAsync(() => backend.PlayAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task PauseAsync_DoesNotThrow()
    {
        using var backend = new MfBackend(_logger);
        var ex = await Record.ExceptionAsync(() => backend.PauseAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task ResumeAsync_DoesNotThrow()
    {
        using var backend = new MfBackend(_logger);
        var ex = await Record.ExceptionAsync(() => backend.ResumeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task StopAsync_DoesNotThrow()
    {
        using var backend = new MfBackend(_logger);
        var ex = await Record.ExceptionAsync(() => backend.StopAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task SeekAsync_DoesNotThrow()
    {
        using var backend = new MfBackend(_logger);
        var ex = await Record.ExceptionAsync(() => backend.SeekAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(ex);
    }

    [Fact]
    public async Task NextFrameAsync_ReturnsNull()
    {
        using var backend = new MfBackend(_logger);
        var frame = await backend.NextFrameAsync();
        Assert.Null(frame);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var backend = new MfBackend(_logger);
        var ex = Record.Exception(() => backend.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void EndOfStream_Event_CanBeSubscribed()
    {
        using var backend = new MfBackend(_logger);
        var invoked = false;
        backend.EndOfStream += (_, _) => invoked = true;
        Assert.False(invoked);
    }
}
