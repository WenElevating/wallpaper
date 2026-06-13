using System.Diagnostics;

namespace WallpaperApp.Tests.Services;

public sealed class D2dRendererSmokeTests
{
    [Fact]
    public async Task Present_OnRealHwnd_SucceedsInProbeProcess()
    {
        var probePath = ResolveProbePath();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{probePath}\"",
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
            $"Render probe exited with code {process.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{stdout}{Environment.NewLine}stderr:{Environment.NewLine}{stderr}");
    }

    private static string ResolveProbePath()
    {
        var repoRoot = FindRepoRoot();
        var probePath = Path.Combine(
            repoRoot,
            "tests",
            "WallpaperApp.RenderProbe",
            "bin",
            "Debug",
            "net8.0-windows",
            "WallpaperApp.RenderProbe.dll");

        Assert.True(File.Exists(probePath), $"Render probe executable not found: {probePath}");
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
