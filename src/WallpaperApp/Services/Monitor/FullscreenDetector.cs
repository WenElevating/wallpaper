using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

public sealed class FullscreenDetector : IDisposable
{
    private readonly FileLogger _logger;
    // Reserved for future per-monitor pause rules (pause only the screen a
    // fullscreen app covers, vs the current all-screens PauseAll). The current
    // detection uses MonitorFromWindow + the OS notification state instead.
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
        // SAME hwnd but changes its geometry/state, and the old "handle
        // unchanged => bail" early-return silently missed those transitions
        // (stayed "playing" under a real fullscreen app). The handle is tracked
        // only to make the state-change log show when the foreground switched.
        if (wasFullscreen != IsFullscreen)
        {
            _logger.Info($"Fullscreen state changed: {IsFullscreen} (foreground={foreground:X}, fgChanged={fgChanged})");
            FullscreenStateChanged?.Invoke(this, IsFullscreen);
        }
    }

    // Detects whether a fullscreen app is active using two complementary methods:
    //
    //  (A) SHQueryUserNotificationState — the OS's own "is a fullscreen app
    //      active?" signal (the one Windows uses to auto-hide the taskbar and
    //      suppress notifications). This is the primary source because it catches
    //      cases pure geometry can't: D3D exclusive-fullscreen games have no
    //      normal window to measure, and it reflects the system's authoritative
    //      notion of fullscreen. Matches QUNS_BUSY / QUNS_RUNNING_D3D_FULL_SCREEN
    //      / QUNS_PRESENTATION_MODE / QUNS_APP. (Microsoft Q&A notes some games
    //      report QUNS_BUSY instead of the D3D state, so both are accepted.)
    //
    //  (B) Geometry fallback — if the notification state API fails or reports
    //      "not fullscreen", fall back to measuring the foreground window: a
    //      window whose rect is exactly the monitor's rcMonitor is fullscreen.
    //      Uses GetWindowRect (NOT DwmGetWindowAttribute's extended frame bounds,
    //      which include the invisible shadow and don't equal the monitor rect
    //      for many fullscreen apps — that mismatch is why the old code missed
    //      real fullscreen). Shell windows (Progman/WorkerW/taskbar) are excluded
    //      so Win+D / clicking the desktop never counts as fullscreen.
    private bool IsBorderlessFullscreen(IntPtr hWnd)
    {
        // (A) OS notification state — authoritative when available.
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0)
        {
            if (state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY ||
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN ||
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE ||
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_APP)
            {
                return true;
            }
            // QUNS_ACCEPTS_NOTIFICATIONS / QUNS_QUIET_TIME / QUNS_NOT_PRESENT =>
            // not fullscreen per the OS. Still fall through to geometry, because
            // some windowed-fullscreen UWP/Edge cases don't flip the OS state.
        }

        // (B) Geometry fallback — exact window-rect == monitor-rect.
        if (hWnd == IntPtr.Zero) return false;

        // Exclude the system shell: Win+D ("Show desktop") brings Progman /
        // WorkerW to the foreground; they cover the whole monitor and would
        // otherwise match. Same for the taskbar and its multi-monitor sibling.
        var cls = GetClassName(hWnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        // The monitor the window is mostly on (primary on failure).
        var hMon = NativeMethods.MonitorFromWindow(hWnd, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfoW(hMon, ref mi)) return false;

        if (!NativeMethods.GetWindowRect(hWnd, out var rect)) return false;

        // Exact equality against the monitor's full rect (rcMonitor, not the
        // smaller work area). This mirrors the Windows Terminal maintainer's
        // canonical implementation. Fullscreen apps set their window rect to
        // exactly the monitor rect; any border/titlebar delta disqualifies it.
        return rect.Left == mi.rcMonitor.Left &&
               rect.Top == mi.rcMonitor.Top &&
               rect.Right == mi.rcMonitor.Right &&
               rect.Bottom == mi.rcMonitor.Bottom;
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
