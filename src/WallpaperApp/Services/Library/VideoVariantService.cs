using System.Diagnostics;
using System.IO;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

// Generates rebuildable, content-addressed playback proxies. The source file
// name is already a SHA-256 hash because LibraryService owns deduplication.
public sealed class VideoVariantService
{
    public const string VariantDirectoryName = "variants";

    private readonly FileLogger _logger;
    private readonly string? _ffmpegPath;
    private readonly SemaphoreSlim _generationGate = new(1, 1);

    public VideoVariantService(FileLogger logger, string? ffmpegPath = null)
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath;
    }

    public static string ResolveVariantDirectory(string libraryDirectory, string sourcePath)
        => Path.Combine(libraryDirectory, VariantDirectoryName, Path.GetFileNameWithoutExtension(sourcePath));

    public static string ResolveVariantPath(string libraryDirectory, string sourcePath, WallpaperPerformanceProfile profile)
    {
        var name = profile switch
        {
            WallpaperPerformanceProfile.Balanced => "balanced.mp4",
            WallpaperPerformanceProfile.Saver => "saver.mp4",
            _ => string.Empty,
        };
        return string.IsNullOrEmpty(name)
            ? sourcePath
            : Path.Combine(ResolveVariantDirectory(libraryDirectory, sourcePath), name);
    }

    public async Task GenerateAsync(string sourcePath, string libraryDirectory, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath) || !IsVideoPath(sourcePath)) return;
        await _generationGate.WaitAsync(ct);
        try
        {
            foreach (var profile in new[] { WallpaperPerformanceProfile.Balanced, WallpaperPerformanceProfile.Saver })
            {
                var output = ResolveVariantPath(libraryDirectory, sourcePath, profile);
                if (File.Exists(output) && new FileInfo(output).Length > 0)
                {
                    _logger.Debug($"Playback proxy hit profile={profile} source={sourcePath} path={output}");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    var temp = output + $".tmp-{Guid.NewGuid():N}";
                    try
                    {
                        await RunFfmpegAsync(sourcePath, temp, profile, ct);
                        File.Move(temp, output, overwrite: true);
                        _logger.Info($"Playback proxy generated profile={profile} source={sourcePath} path={output}");
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _logger.Warn($"Playback proxy generation cancelled profile={profile} source={sourcePath}");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Playback proxy generation failed profile={profile} source={sourcePath}: {ex.Message}");
                }
            }
        }
        finally { _generationGate.Release(); }
    }

    private async Task RunFfmpegAsync(string source, string output, WallpaperPerformanceProfile profile, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath ?? ResolveFfmpegPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        var max = profile == WallpaperPerformanceProfile.Saver ? "1280:720" : "1920:1080";
        var fps = profile == WallpaperPerformanceProfile.Saver ? "30" : "30";
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add($"scale={max}:force_original_aspect_ratio=decrease:force_divisible_by=2,fps={fps}");
        psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("medium");
        psi.ArgumentList.Add("-crf"); psi.ArgumentList.Add("23");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-movflags"); psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(output);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}");
    }

    private static bool IsVideoPath(string path)
        => !string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    private static string ResolveFfmpegPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(local) ? local : "ffmpeg";
    }
}
