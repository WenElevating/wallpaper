using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class GpuDeviceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public GpuDeviceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GpuDeviceTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // Creation must be deferred so App can apply the PreferDiscreteGpu setting
    // after settings load and before the first wallpaper session starts. These
    // tests deliberately avoid touching IsAvailable/Device (that would create a
    // real D3D device in a unit test); the render probe covers creation.
    [Fact]
    public void Constructor_DefersDeviceCreation()
    {
        using var gpu = new GpuDevice(_logger);

        Assert.False(gpu.IsCreationAttempted);
    }

    [Fact]
    public void PreferDiscreteGpu_DefaultsToTrue_AndIsSettable()
    {
        using var gpu = new GpuDevice(_logger);

        Assert.True(gpu.PreferDiscreteGpu);

        gpu.PreferDiscreteGpu = false;

        Assert.False(gpu.PreferDiscreteGpu);
    }

    [Fact]
    public void Dispose_WithoutDeviceCreation_DoesNotThrow()
    {
        var gpu = new GpuDevice(_logger);

        gpu.Dispose();
    }
}
