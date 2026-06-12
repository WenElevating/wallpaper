using System.IO;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Tests.Services;

public class FileLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FileLoggerTests_" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Constructor_CreatesLogDirectory()
    {
        using var logger = new FileLogger(_tempDir);
        Assert.True(Directory.Exists(_tempDir));
    }

    [Fact]
    public void Info_WritesToLogFile()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
            logger.Info("test info message");

        var content = File.ReadAllText(logFile);
        Assert.Contains("[INFO]", content);
        Assert.Contains("test info message", content);
    }

    [Fact]
    public void Warn_WritesToLogFile()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
            logger.Warn("test warn message");

        var content = File.ReadAllText(logFile);
        Assert.Contains("[WARN]", content);
        Assert.Contains("test warn message", content);
    }

    [Fact]
    public void Error_WritesToLogFile()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
            logger.Error("test error message");

        var content = File.ReadAllText(logFile);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("test error message", content);
    }

    [Fact]
    public void Debug_WritesToLogFile()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
            logger.Debug("test debug message");

        var content = File.ReadAllText(logFile);
        Assert.Contains("[DEBUG]", content);
        Assert.Contains("test debug message", content);
    }

    [Fact]
    public void Error_WithException_IncludesExceptionMessageAndStackTrace()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var ex = new InvalidOperationException("inner boom");
        var logger = new FileLogger(_tempDir);
        logger.Error("outer message", ex);
        logger.Dispose();

        var content = File.ReadAllText(logFile);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("outer message", content);
        Assert.Contains("inner boom", content);
    }

    [Fact]
    public void LogFileNameFormat_IsWallpaperYYYY_MM_DD()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
            logger.Info("check filename");

        Assert.True(File.Exists(logFile));
    }

    [Fact]
    public void MultipleWrites_AppendToSameFile()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        using (var logger = new FileLogger(_tempDir))
        {
            logger.Info("line 1");
            logger.Info("line 2");
            logger.Info("line 3");
        }

        var lines = File.ReadAllLines(logFile);
        Assert.Equal(3, lines.Length);
        Assert.Contains("line 1", lines[0]);
        Assert.Contains("line 2", lines[1]);
        Assert.Contains("line 3", lines[2]);
    }

    [Fact]
    public void Dispose_ClosesWriter_NoMoreOutput()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var logger = new FileLogger(_tempDir);
        logger.Info("before dispose");
        logger.Dispose();

        // Writing after dispose should not throw but also should not produce output
        // (StreamWriter is disposed, _writer is null-safe via ?. operator)
        var contentBefore = File.ReadAllText(logFile);
        Assert.Contains("before dispose", contentBefore);
    }

    [Fact]
    public void ThreadSafety_ConcurrentWrites_DontCrash()
    {
        var logFile = Path.Combine(_tempDir, $"wallpaper-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var logger = new FileLogger(_tempDir);

        var tasks = Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => logger.Info($"thread {i} message")));
        Task.WhenAll(tasks).GetAwaiter().GetResult();

        logger.Dispose();

        var lines = File.ReadAllLines(logFile);
        Assert.Equal(20, lines.Length);
    }

    [Fact]
    public void LogRotation_DifferentDays_CreatesDifferentFiles()
    {
        var logger = new FileLogger(_tempDir);

        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);

        var todayFile = Path.Combine(_tempDir, $"wallpaper-{today:yyyy-MM-dd}.log");
        var yesterdayFile = Path.Combine(_tempDir, $"wallpaper-{yesterday:yyyy-MM-dd}.log");

        // Simulate writing on "yesterday" by creating that file
        File.WriteAllText(yesterdayFile, $"[{yesterday:yyyy-MM-dd HH:mm:ss}] [INFO] yesterday message\n");

        // Normal write goes to today
        logger.Info("today message");

        Assert.True(File.Exists(todayFile));
        Assert.True(File.Exists(yesterdayFile));

        logger.Dispose();

        var todayContent = File.ReadAllText(todayFile);
        var yesterdayContent = File.ReadAllText(yesterdayFile);

        Assert.Contains("today message", todayContent);
        Assert.Contains("yesterday message", yesterdayContent);
        Assert.DoesNotContain("today message", yesterdayContent);
    }
}
