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
    private bool _isAttached;
    private bool _disposed;

    public bool IsAttached => _isAttached;
    public IReadOnlyList<WallpaperWindow> WallpaperWindows => _wallpaperWindows.AsReadOnly();

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
        if (_isAttached) return true;

        // Attach() marks the host ready and starts the retry timer only. It must
        // NOT create a wallpaper window: App startup (and playback) create windows
        // per-monitor via CreateForMonitor(). Creating one here left an orphan
        // window (no renderer, static background) embedded in the desktop WorkerW.
        _isAttached = true;
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
            _wallpaperWindows.Add(window);
            _logger.Info($"Created wallpaper window at ({x},{y}) {width}x{height}");
            return window;
        }

        window.Dispose();
        return null;
    }

    public void ResizeMainWindow(int x, int y, int width, int height)
    {
        foreach (var w in _wallpaperWindows)
            w.Resize(x, y, width, height);
    }

    public void Detach()
    {
        if (!_isAttached) return;
        _retryTimer.Stop();

        foreach (var w in _wallpaperWindows)
            w.Dispose();
        _wallpaperWindows.Clear();

        _isAttached = false;
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
