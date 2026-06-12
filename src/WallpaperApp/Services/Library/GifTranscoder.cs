using System.Diagnostics;
using System.IO;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class GifTranscoder
{
    private readonly FileLogger _logger;

    public GifTranscoder(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<string?> TranscodeAsync(string gifFilePath, string outputDir, CancellationToken ct = default)
    {
        if (!File.Exists(gifFilePath))
        {
            _logger.Error($"GIF file not found: {gifFilePath}");
            return null;
        }

        try
        {
            Directory.CreateDirectory(outputDir);
            var outputFileName = $"{Path.GetFileNameWithoutExtension(gifFilePath)}.mp4";
            var outputPath = Path.Combine(outputDir, outputFileName);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{gifFilePath}\" -movflags +faststart -pix_fmt yuv420p -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" \"{outputPath}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.Error("Failed to start ffmpeg for GIF transcode");
                return null;
            }

            var timeout = TimeSpan.FromMinutes(5);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                _logger.Error($"GIF transcode timed out: {gifFilePath}");
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                _logger.Error($"GIF transcode failed: {gifFilePath}");
                return null;
            }

            _logger.Info($"Transcoded GIF: {gifFilePath} -> {outputPath}");
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"GIF transcode failed: {gifFilePath}", ex);
            return null;
        }
    }
}
