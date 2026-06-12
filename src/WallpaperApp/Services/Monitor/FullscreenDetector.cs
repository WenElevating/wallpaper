using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

public sealed class FullscreenDetector : IDisposable
{
    private readonly FileLogger _logger;
    private readonly MonitorManager _monitorManager;
    private readonly System.Timers.Timer _pollTimer;
    private IntPtr _lastForegroundWindow;
    private bool _disposed;

    public event EventHandler<bool>? FullscreenStateChanged;

    public bool IsFullscreen { get; private set; }

    public FullscreenDetector(FileLogger logger, MonitorManager monitorManager, int pollIntervalMs = 500)
    {
        _logger = logger;
        _monitorManager = monitorManager;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        _pollTimer.Start();
        _logger.Debug("Fullscreen detector started");
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _logger.Debug("Fullscreen detector stopped");
    }

    private void Poll()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == _lastForegroundWindow) return;
        _lastForegroundWindow = foreground;

        var wasFullscreen = IsFullscreen;
        IsFullscreen = IsBorderlessFullscreen(foreground);

        if (wasFullscreen != IsFullscreen)
        {
            _logger.Info($"Fullscreen state changed: {IsFullscreen}");
            FullscreenStateChanged?.Invoke(this, IsFullscreen);
        }
    }

    private bool IsBorderlessFullscreen(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        var style = NativeMethods.GetWindowLongW(hWnd, NativeMethods.GWL_STYLE);
        var exStyle = NativeMethods.GetWindowLongW(hWnd, NativeMethods.GWL_EXSTYLE);

        bool hasCaption = (style & 0x00C00000) != 0;
        bool isPopup = (style & 0x80000000) != 0;

        if (hasCaption) return false;
        if (!isPopup) return false;

        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return false;
        }

        // Check if the window covers any monitor fully
        foreach (var (monRect, _) in _monitorManager.GetMonitorRects())
        {
            if (rect.Left <= monRect.Left && rect.Top <= monRect.Top &&
                rect.Right >= monRect.Right && rect.Bottom >= monRect.Bottom)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
