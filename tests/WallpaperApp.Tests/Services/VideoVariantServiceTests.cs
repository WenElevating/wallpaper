using WallpaperApp.Models;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Tests.Services;

public sealed class VideoVariantServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VideoVariantTests_" + Guid.NewGuid().ToString("N"));
    private readonly FileLogger _logger;

    public VideoVariantServiceTests()
    {
        Directory.CreateDirectory(_root);
        _logger = new FileLogger(Path.Combine(_root, "logs"));
    }

    [Fact]
    public async Task FailedGeneration_LeavesNoFormalOrTemporaryVariant()
    {
        var source = Path.Combine(_root, "ABC.mp4");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var service = new VideoVariantService(_logger, Path.Combine(_root, "missing-ffmpeg.exe"));

        await service.GenerateAsync(source, _root);

        var variantDir = VideoVariantService.ResolveVariantDirectory(_root, source);
        Assert.False(File.Exists(Path.Combine(variantDir, "balanced.mp4")));
        Assert.False(File.Exists(Path.Combine(variantDir, "saver.mp4")));
        Assert.Empty(Directory.Exists(variantDir)
            ? Directory.GetFiles(variantDir, "*.tmp-*", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>());
    }

    [Fact]
    public async Task Gif_IsNotScheduledForProxyGeneration()
    {
        var source = Path.Combine(_root, "ABC.gif");
        await File.WriteAllBytesAsync(source, new byte[] { 1 });
        var service = new VideoVariantService(_logger, Path.Combine(_root, "missing-ffmpeg.exe"));

        await service.GenerateAsync(source, _root);

        Assert.False(Directory.Exists(VideoVariantService.ResolveVariantDirectory(_root, source)));
    }

    public void Dispose()
    {
        _logger.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
