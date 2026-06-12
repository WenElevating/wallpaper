using System.IO;

namespace WallpaperApp.Services.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _currentDate;

    public FileLogger(string? logDir = null)
    {
        _logDir = logDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "logs");
        Directory.CreateDirectory(_logDir);
        RotateIfNeeded();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex.Message}\n{ex.StackTrace}" : message;
        Write("ERROR", msg);
    }
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            RotateIfNeeded();
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _writer?.WriteLine($"[{timestamp}] [{level}] {message}");
            _writer?.Flush();
        }
    }

    private void RotateIfNeeded()
    {
        var today = DateTime.UtcNow.Date;
        if (_currentDate != today || _writer == null)
        {
            _writer?.Dispose();
            _currentDate = today;
            var path = Path.Combine(_logDir, $"wallpaper-{today:yyyy-MM-dd}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = false };
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }
}