using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class WallpaperWindow : IDisposable
{
    private readonly FileLogger _logger;
    private IntPtr _hwnd;
    private bool _isFallback;
    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IntPtr Handle => _hwnd;
    public bool IsFallback => _isFallback;

    public WallpaperWindow(FileLogger logger)
    {
        _logger = logger;
    }

    public bool TryAttachToDesktop()
    {
        try
        {
            var progman = FindWindowByClass("Progman");
            if (progman == IntPtr.Zero)
            {
                _logger.Warn("Progman window not found");
                return false;
            }

            NativeMethods.SendMessageW(progman, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

            var workerW = FindWorkerW();
            if (workerW == IntPtr.Zero)
            {
                _logger.Warn("WorkerW not found after 0x052C, using fallback");
                return CreateFallbackWindow();
            }

            CreateWallpaperHwnd(workerW);
            _isFallback = false;
            _logger.Info("Attached to desktop via WorkerW");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Desktop attachment failed", ex);
            return CreateFallbackWindow();
        }
    }

    public void Resize(int x, int y, int width, int height)
    {
        Width = width;
        Height = height;
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero,
            x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
        IntPtr workerW = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            var buf = new char[256];
            NativeMethods.GetClassNameW(hWnd, buf, 256);
            var name = new string(buf, 0, Array.IndexOf(buf, '\0'));
            if (name == "WorkerW")
            {
                var child = FindChildByClass(hWnd, "SHELLDLL_DefView");
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

    private static IntPtr FindChildByClass(IntPtr parent, string className)
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

    private bool CreateFallbackWindow()
    {
        try
        {
            var exStyle = NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            var hInstance = NativeMethods.GetModuleHandleW(null);

            _hwnd = NativeMethods.CreateWindowExW(
                (uint)exStyle, "STATIC", null,
                NativeMethods.WS_CHILD,
                0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _logger.Error("Failed to create fallback window");
                return false;
            }

            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_BOTTOM,
                0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            _isFallback = true;
            _logger.Info("Created fallback WS_EX_TOOLWINDOW overlay");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Fallback window creation failed", ex);
            return false;
        }
    }

    private void CreateWallpaperHwnd(IntPtr parent)
    {
        var hInstance = NativeMethods.GetModuleHandleW(null);

        _hwnd = NativeMethods.CreateWindowExW(
            0, "STATIC", null,
            NativeMethods.WS_CHILD,
            0, 0, 0, 0,
            parent, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            _logger.Info($"Created wallpaper HWND: {_hwnd} parented to {parent}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
