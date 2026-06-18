using System.IO;

namespace WallpaperApp.Services.Logging;

// File logger that degrades gracefully when the log file can't be opened.
//
// The previous version threw if the day's log file was locked (e.g. another
// process still held it open — a leftover instance, an AV scanner, a crash
// dump). Since the logger is constructed during DI setup, that throw aborted
// the whole app startup, so a transient file lock became "app won't launch".
//
// Now: opening the log file never throws. If the preferred path is locked we
// fall back to a numbered variant (wallpaper-YYYY-MM-DD-1.log, -2, ...), and if
// even those fail we log in-memory only (writes become no-ops). The app always
// starts; logging is best-effort, never a hard dependency.
public sealed class FileLogger : IDisposable
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _currentDate;
    // Set when we gave up opening any file this run; further writes are no-ops
    // (better silent than crashing the host on every log call).
    private bool _loggingDisabled;

    public FileLogger(string? logDir = null)
    {
        _logDir = logDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "logs");
        try { Directory.CreateDirectory(_logDir); }
        catch { /* can't even create the dir — degrade to no-op logging */ }
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
            if (_loggingDisabled) return;
            RotateIfNeeded();
            if (_writer == null) return;
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            try
            {
                _writer.WriteLine($"[{timestamp}] [{level}] {message}");
                _writer.Flush();
            }
            catch
            {
                // The writer went bad mid-run (file deleted/locked under us).
                // Drop it and let RotateIfNeeded try to reopen next time.
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }
        }
    }

    private void RotateIfNeeded()
    {
        var today = DateTime.UtcNow.Date;
        if (_currentDate == today && _writer != null) return;

        _writer?.Dispose();
        _writer = null;
        _currentDate = today;
        _writer = OpenLogWriter(today);
    }

    // Tries wallpaper-<date>.log, then -1, -2, ... until one opens. Returns null
    // if none can be opened (caller then logs in-memory only).
    private StreamWriter? OpenLogWriter(DateTime date)
    {
        var baseName = $"wallpaper-{date:yyyy-MM-dd}";
        for (var i = 0; i < 64; i++)
        {
            var suffix = i == 0 ? "" : "-" + i;
            var path = Path.Combine(_logDir, baseName + suffix + ".log");
            try
            {
                return new StreamWriter(path, append: true) { AutoFlush = false };
            }
            catch (IOException)
            {
                // Sharing violation / locked — try the next suffix.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                // No write permission to this path — try the next suffix once,
                // but if it's a perms issue on the dir, all will fail.
                continue;
            }
        }
        // All attempts failed: give up on file logging for this run.
        _loggingDisabled = true;
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
