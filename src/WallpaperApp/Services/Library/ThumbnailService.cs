using System.Diagnostics;
using System.IO;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class ThumbnailService
{
    private readonly FileLogger _logger;

    public ThumbnailService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> GenerateThumbnailAsync(string videoFilePath, string outputDir, CancellationToken ct = default)
    {
        if (!File.Exists(videoFilePath))
        {
            _logger.Error($"Video file not found: {videoFilePath}");
            return null;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
            var thumbFileName = $"{Path.GetFileNameWithoutExtension(videoFilePath)}_thumb.jpg";
            var thumbPath = Path.Combine(outputDir, thumbFileName);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{videoFilePath}\" -vframes 1 -q:v 2 \"{thumbPath}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.Error("Failed to start ffmpeg for thumbnail");
                return null;
            }

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || !File.Exists(thumbPath))
            {
                _logger.Error($"ffmpeg thumbnail failed for: {videoFilePath}");
                return null;
            }

            _logger.Debug($"Generated thumbnail: {thumbPath}");
            return thumbPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"Thumbnail generation failed: {videoFilePath}", ex);
            return null;
        }
    }
}
