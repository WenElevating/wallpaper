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
        // Reset the cached foreground window so the first Poll() logs the
        // foreground handle it sees. (Not needed for correctness — Poll
        // recomputes geometry every tick regardless of handle changes.)
        _lastForegroundWindow = IntPtr.Zero;
        Poll();
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
        var fgChanged = foreground != _lastForegroundWindow;
        _lastForegroundWindow = foreground;

        var wasFullscreen = IsFullscreen;
        IsFullscreen = IsBorderlessFullscreen(foreground);

        // Recompute every tick even when the foreground handle is unchanged: a
        // windowed app toggling to fullscreen (F11 / game fullscreen) keeps the
        // SAME hwnd but changes its geometry, and the old "handle unchanged =>
        // bail" early-return silently missed those transitions (stayed "playing"
        // under a real fullscreen app). The handle is tracked only to make the
        // state-change log show when the foreground actually switched.
        if (wasFullscreen != IsFullscreen)
        {
            _logger.Info($"Fullscreen state changed: {IsFullscreen} (foreground={foreground:X}, fgChanged={fgChanged})");
            FullscreenStateChanged?.Invoke(this, IsFullscreen);
        }
    }

    private bool IsBorderlessFullscreen(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        // Exclude the system shell windows. Win+D ("Show desktop") brings
        // Progman / WorkerW to the foreground; those are popup-style windows
        // with no caption that cover the whole monitor, so the geometry test
        // below would flag them as fullscreen and pause the wallpaper — even
        // though the user just peeked at the desktop. Same for the taskbar
        // (Shell_TrayWnd) and the multi-monitor "Shell_SecondaryTrayWnd".
        var cls = GetClassName(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        // Hidden / minimized foregrounds can't be fullscreen.
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        var style = NativeMethods.GetWindowLongW(hWnd, NativeMethods.GWL_STYLE);

        // A fullscreen app has dropped its titlebar/border: no WS_CAPTION.
        // (We no longer require WS_POPUP: modern fullscreen UWP / Edge / games
        // toggle fullscreen by restyling an overlapped window, so the popup
        // bit is an unreliable signal and caused false negatives.)
        bool hasCaption = (style & 0x00C00000) != 0; // WS_CAPTION
        if (hasCaption) return false;

        if (NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var rect, System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) != 0)
        {
            return false;
        }

        // Check if the window covers any monitor fully.
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

    private static string GetClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        NativeMethods.GetClassNameW(hwnd, buf, 256);
        var end = Array.IndexOf(buf, '\0');
        return end >= 0 ? new string(buf, 0, end) : new string(buf);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
