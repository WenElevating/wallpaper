using System.IO;
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
    private IntPtr _progmanHwnd;
    private IntPtr _workerWHwnd;
    private IntPtr _childHwnd;
    private bool _isAttached;
    private bool _disposed;

    public bool IsAttached => _isAttached;
    public IntPtr ChildHwnd => _childHwnd;

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
        _progmanHwnd = FindWindowByClass("Progman");
        if (_progmanHwnd == IntPtr.Zero)
        {
            _logger.Warn("Progman window not found");
            return false;
        }

        NativeMethods.SendMessageW(_progmanHwnd, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

        _workerWHwnd = FindWorkerW();
        if (_workerWHwnd == IntPtr.Zero)
        {
            _logger.Warn("WorkerW window not found, using fallback");
            _workerWHwnd = CreateFallbackWindow();
        }

        if (_workerWHwnd == IntPtr.Zero)
        {
            _logger.Error("Failed to find or create WorkerW");
            return false;
        }

        _isAttached = true;
        _retryTimer.Start();
        _logger.Info("DesktopHost attached");
        Attached?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public IntPtr CreateForMonitor(int x, int y, int width, int height)
    {
        if (!_isAttached) throw new InvalidOperationException("Not attached");

        var parentHwnd = _workerWHwnd != IntPtr.Zero ? _workerWHwnd : _progmanHwnd;
        var hInstance = NativeMethods.GetModuleHandleW(null);

        _childHwnd = NativeMethods.CreateWindowExW(
            0, "STATIC", null,
            NativeMethods.WS_CHILD,
            x, y, width, height,
            parentHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_childHwnd == IntPtr.Zero)
        {
            _logger.Error("Failed to create child HWND");
            return IntPtr.Zero;
        }

        NativeMethods.ShowWindow(_childHwnd, NativeMethods.SW_SHOW);
        _logger.Info($"Created child HWND: {_childHwnd} at ({x},{y}) {width}x{height}");
        return _childHwnd;
    }

    public void ResizeMainWindow(int x, int y, int width, int height)
    {
        if (_childHwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            _childHwnd, IntPtr.Zero,
            x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    public void Detach()
    {
        if (!_isAttached) return;
        _retryTimer.Stop();

        if (_childHwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_childHwnd);
            _childHwnd = IntPtr.Zero;
        }

        _isAttached = false;
        _logger.Info("DesktopHost detached");
        Detached?.Invoke(this, EventArgs.Empty);
    }

    private static IntPtr FindWindowByClass(string className)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var buf = new char[256];
            NativeMethods.GetClassNameW(hWnd, buf, 256);
            var name = new string(buf, 0, Array.IndexOf(buf, '\0'));
            if (name == className)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private IntPtr FindWorkerW()
    {
        var workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var className = new char[256];
            NativeMethods.GetClassNameW(hWnd, className, 256);
            var name = new string(className, 0, Array.IndexOf(className, '\0'));
            if (name == "WorkerW")
            {
                var child = FindWindowByClassIn(hWnd, "SHELLDLL_DefView");
                if (child != IntPtr.Zero)
                {
                    workerW = hWnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return workerW;
    }

    private static IntPtr FindWindowByClassIn(IntPtr parent, string className)
    {
        IntPtr found = IntPtr.Zero;
        DesktopNative.EnumChildWindows(parent, (hWnd, _) =>
        {
            var buf = new char[256];
            NativeMethods.GetClassNameW(hWnd, buf, 256);
            var name = new string(buf, 0, Array.IndexOf(buf, '\0'));
            if (name == className)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private IntPtr CreateFallbackWindow()
    {
        var exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        var hInstance = NativeMethods.GetModuleHandleW(null);

        var hwnd = NativeMethods.CreateWindowExW(
            (uint)exStyle, "STATIC", null,
            NativeMethods.WS_CHILD,
            0, 0, 0, 0,
            _progmanHwnd, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_BOTTOM,
                0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        return hwnd;
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
