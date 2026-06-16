using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

// Detects whether the wallpaper is NOT visible on any monitor — i.e. some
// combination of foreground windows fully covers the monitor where the
// wallpaper renders. When occluded, there's no point decoding/rendering, so
// this raises an event the host wires into the pause pipeline.
//
// Unlike FullscreenDetector (which keys off SHQueryUserNotificationState and
// looks for a SINGLE borderless fullscreen app), this is purely geometric:
// it walks top-level windows in Z order and subtracts each window's rect from
// the monitor's region. If a monitor's region ends up empty, ANYTHING — a
// fullscreen game, a maximized browser, or several overlapping windows tiling
// the screen — is covering the wallpaper there. This matches the user's goal:
// "don't render when the wallpaper can't be seen."
//
// (GetClipBox is NOT used: under DWM composition every window paints
// independently and GetClipBox doesn't reflect true occlusion — confirmed in
// the SO discussion this algorithm comes from.)
public sealed class WallpaperVisibilityDetector : IDisposable
{
    private readonly FileLogger _logger;
    private readonly MonitorManager _monitorManager;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;

    // Raised when visibility changes. true = wallpaper is visible (resumed);
    // false = wallpaper is covered on at least one monitor (paused).
    public event EventHandler<bool>? VisibilityChanged;

    // true when the wallpaper is currently considered visible.
    public bool IsVisible { get; private set; } = true;

    public WallpaperVisibilityDetector(FileLogger logger, MonitorManager monitorManager, int pollIntervalMs = 500)
    {
        _logger = logger;
        _monitorManager = monitorManager;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        _pollTimer.Start();
        Poll();
        _logger.Debug("Wallpaper visibility detector started");
    }

    public void Stop()
    {
        _pollTimer.Stop();
        _logger.Debug("Wallpaper visibility detector stopped");
    }

    private void Poll()
    {
        try
        {
            var wasVisible = IsVisible;
            IsVisible = IsWallpaperVisible();

            if (wasVisible != IsVisible)
            {
                _logger.Info($"Wallpaper visibility changed: {(IsVisible ? "visible" : "covered")}");
                VisibilityChanged?.Invoke(this, IsVisible);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Visibility poll failed: {ex.Message}");
        }
    }

    // Builds a region per monitor (initialized to the full monitor rect), then
    // subtracts every higher-Z top-level window's rect. If any monitor's region
    // becomes empty, the wallpaper on that monitor is fully covered.
    private bool IsWallpaperVisible()
    {
        // Project to just the rects (drop the MonitorInfo).
        var monitorRects = _monitorManager.GetMonitorRects().Select(r => r.Rect).ToList();
        using var monitors = new MonitorRegions(monitorRects);
        if (monitors.Count == 0) return true; // nothing to cover
        // EnumWindows yields top-level windows in Z order, topmost first. We
        // stop early once every monitor region is empty.
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (monitors.AllCovered) return false; // done

            // Skip invisible / minimized / shell windows — they don't paint.
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.IsIconic(hwnd)) return true;
            if (IsShellWindow(hwnd)) return true;

            if (!NativeMethods.GetWindowRect(hwnd, out var wr)) return true;

            monitors.Subtract(wr);
            return true;
        }, IntPtr.Zero);

        return monitors.AnyVisible;
    }

    // Shell windows (desktop, taskbar) don't occlude the wallpaper.
    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = GetClassName(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd" or "MsGEdge"; // MsGEdge = IME, ignore
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        NativeMethods.GetClassNameW(hwnd, buf, 256);
        var end = Array.IndexOf(buf, '\0');
        return end >= 0 ? new string(buf, 0, end) : new string(buf);
    }

    // Owns a set of GDI regions (one per monitor) and exposes region math.
    // IDisposable because GDI regions must be deleted to avoid handle leaks.
    private sealed class MonitorRegions : IDisposable
    {
        private readonly List<IntPtr> _regions = new();
        private readonly bool[] _covered;

        public int Count => _regions.Count;
        public bool AllCovered
        {
            get
            {
                foreach (var c in _covered) if (!c) return false;
                return true;
            }
        }
        public bool AnyVisible
        {
            get
            {
                foreach (var c in _covered) if (!c) return true;
                return false;
            }
        }

        public MonitorRegions(IEnumerable<NativeMethods.RECT> monitors)
        {
            foreach (var r in monitors)
            {
                var region = NativeMethods.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
                _regions.Add(region);
            }
            _covered = new bool[_regions.Count];
        }

        // Subtracts a window rect from every not-yet-covered monitor region.
        public void Subtract(NativeMethods.RECT rect)
        {
            var hole = IntPtr.Zero;
            try
            {
                hole = NativeMethods.CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
                for (var i = 0; i < _regions.Count; i++)
                {
                    if (_covered[i]) continue;
                    // CombineRgn(dst, src1, src2, RGN_DIFF): dst = src1 - src2.
                    var result = NativeMethods.CombineRgn(_regions[i], _regions[i], hole, NativeMethods.RGN_DIFF);
                    if (result == NativeMethods.NULLREGION)
                    {
                        _covered[i] = true; // region empty -> monitor covered
                    }
                }
            }
            finally
            {
                if (hole != IntPtr.Zero) NativeMethods.DeleteObject(hole);
            }
        }

        public void Dispose()
        {
            foreach (var r in _regions)
            {
                if (r != IntPtr.Zero) NativeMethods.DeleteObject(r);
            }
            _regions.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
