using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

internal static partial class DesktopNative
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(IntPtr hWndParent, NativeMethods.EnumWindowsDelegate lpEnumFunc, IntPtr lParam);
}

public sealed class DesktopHost : IDisposable
{
    private readonly FileLogger _logger;
    private readonly System.Timers.Timer _retryTimer;
    private readonly List<WallpaperWindow> _wallpaperWindows = new();
    private readonly object _windowsLock = new();
    private bool _isAttached;
    private bool _disposed;

    public bool IsAttached
    {
        get { lock (_windowsLock) return _isAttached; }
    }

    public IReadOnlyList<WallpaperWindow> WallpaperWindows
    {
        get { lock (_windowsLock) return _wallpaperWindows.ToArray(); }
    }

    public event EventHandler? Attached;
    public event EventHandler? Detached;

    public DesktopHost(FileLogger logger)
    {
        _logger = logger;
        _retryTimer = new System.Timers.Timer(60_000);
        _retryTimer.Elapsed += (_, _) => RetryAttach();
    }

    public bool Attach()
    {
        lock (_windowsLock)
        {
            if (_disposed) return false;
            if (_isAttached) return true;

            // Attach() marks the host ready and starts the retry timer only. It
            // must NOT create a wallpaper window: playback creates windows per
            // monitor via CreateForMonitor().
            _isAttached = true;
        }
        _retryTimer.Start();
        _logger.Info("DesktopHost attached");
        Attached?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public WallpaperWindow? CreateForMonitor(int x, int y, int width, int height)
    {
        if (!_isAttached && !Attach()) return null;

        var window = new WallpaperWindow(_logger);
        if (window.Handle != IntPtr.Zero)
        {
            window.Resize(x, y, width, height);
            lock (_windowsLock)
            {
                if (!_isAttached || _disposed)
                {
                    window.Dispose();
                    return null;
                }
                _wallpaperWindows.Add(window);
            }
            _logger.Info($"Created wallpaper window at ({x},{y}) {width}x{height}");
            return window;
        }

        window.Dispose();
        return null;
    }

    public void ResizeMainWindow(int x, int y, int width, int height)
    {
        WallpaperWindow[] windows;
        lock (_windowsLock) windows = _wallpaperWindows.ToArray();
        foreach (var w in windows)
            w.Resize(x, y, width, height);
    }

    public void Detach()
    {
        WallpaperWindow[] windows;
        lock (_windowsLock)
        {
            if (!_isAttached) return;
            _isAttached = false;
            windows = _wallpaperWindows.ToArray();
            _wallpaperWindows.Clear();
        }
        _retryTimer.Stop();

        foreach (var w in windows)
            w.Dispose();
        _logger.Info("DesktopHost detached");
        Detached?.Invoke(this, EventArgs.Empty);
    }

    private void RetryAttach()
    {
        if (_isAttached) return;
        _logger.Debug("Retrying desktop attach...");
        Attach();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        _retryTimer.Dispose();
        Detach();
    }
}
