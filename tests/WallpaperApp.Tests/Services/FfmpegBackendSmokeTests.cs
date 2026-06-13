using System.Diagnostics;

namespace WallpaperApp.Tests.Services;

public sealed class FfmpegBackendSmokeTests : IDisposable
{
    private readonly string _tempDir;

    public FfmpegBackendSmokeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FfmpegBackendSmokeTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task ValidMp4_CanOpenAndDecodeFirstFrame_InProbeProcess()
    {
        var samplePath = Path.Combine(_tempDir, "sample.mp4");
        await CreateSampleVideoAsync(samplePath);

        var probePath = ResolveProbePath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{probePath}\" \"{samplePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            $"Probe exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
    }

    [Fact]
    public async Task Bt709Video_DecodesFirstPixelLikeFfmpegReference()
    {
        var samplePath = Path.Combine(_tempDir, "bt709.mp4");
        await CreateBt709VideoAsync(samplePath);

        var expected = await DecodeFirstPixelWithFfmpegAsync(samplePath);
        var actual = await DecodeFirstPixelWithProbeAsync(samplePath);

        AssertPixelClose(expected, actual, tolerance: 2);
    }

    private async Task CreateSampleVideoAsync(string outputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-f lavfi -i color=c=black:s=32x32:r=5:d=1 -pix_fmt yuv420p -an -y -v error \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stderr = await process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"ffmpeg failed creating sample mp4:{Environment.NewLine}{stderr}");
        Assert.True(File.Exists(outputPath), "Sample mp4 was not created.");
    }

    private async Task CreateBt709VideoAsync(string outputPath)
    {
        var rawPath = Path.Combine(_tempDir, "bt709.yuv");
        await File.WriteAllBytesAsync(rawPath, CreateYuv420Frame(16, 16, y: 145, u: 54, v: 200));

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-f rawvideo -pixel_format yuv420p -video_size 16x16 -framerate 1 " +
                        "-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                        $"-i \"{rawPath}\" -frames:v 1 -c:v libx264 -crf 0 -preset ultrafast -pix_fmt yuv420p " +
                        "-color_range tv -colorspace bt709 -color_primaries bt709 -color_trc bt709 " +
                        $"-an -y -v error \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stderr = await process!.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"ffmpeg failed creating BT.709 sample:{Environment.NewLine}{stderr}");
        Assert.True(File.Exists(outputPath), "BT.709 sample video was not created.");
    }

    private static byte[] CreateYuv420Frame(int width, int height, byte y, byte u, byte v)
    {
        var ySize = width * height;
        var chromaSize = ySize / 4;
        var frame = new byte[ySize + chromaSize * 2];
        Array.Fill(frame, y, 0, ySize);
        Array.Fill(frame, u, ySize, chromaSize);
        Array.Fill(frame, v, ySize + chromaSize, chromaSize);
        return frame;
    }

    private async Task<byte[]> DecodeFirstPixelWithFfmpegAsync(string samplePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-i \"{samplePath}\" -frames:v 1 -f rawvideo -pix_fmt bgra -v error -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        using var stdout = new MemoryStream();
        var copyTask = process!.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copyTask;

        Assert.True(process.ExitCode == 0, $"ffmpeg failed decoding BT.709 sample:{Environment.NewLine}{stderr}");
        Assert.True(stdout.Length >= 4, "ffmpeg did not return a BGRA pixel.");

        return stdout.ToArray()[..4];
    }

    private async Task<byte[]> DecodeFirstPixelWithProbeAsync(string samplePath)
    {
        var probePath = ResolveProbePath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{probePath}\" \"{samplePath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        Assert.NotNull(process);

        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(
            process.ExitCode == 0,
            $"Probe exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");

        var prefix = "first_pixel_bgra=";
        var line = stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        Assert.NotNull(line);

        return Convert.FromHexString(line![prefix.Length..]);
    }

    private static void AssertPixelClose(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(4, expected.Length);
        Assert.Equal(4, actual.Length);

        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(expected[i] - actual[i]) <= tolerance,
                $"BGRA channel {i} differed. Expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}.");
        }
    }

    private static string ResolveProbePath()
    {
        var repoRoot = FindRepoRoot();
        var probePath = Path.Combine(
            repoRoot,
            "tests",
            "WallpaperApp.FfmpegProbe",
            "bin",
            "Debug",
            "net8.0-windows",
            "WallpaperApp.FfmpegProbe.dll");

        Assert.True(File.Exists(probePath), $"Probe executable not found: {probePath}");
        return probePath;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WallpaperApp.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root.");
    }
}
