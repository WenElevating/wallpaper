using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class WallpaperWindow : IWallpaperSurface
{
    private const string ClassName = "WallpaperSurface";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_PAINT = 0x000F;
    private const int HTTRANSPARENT = -1;
    private const string XAML_ISLAND = "XamlExplorerHostIslandWindow";
    private const string WORKERW = "WorkerW";

    private readonly FileLogger _logger;
    private IntPtr _hwnd;
    private bool _disposed;
    private static WndProcDelegate? _wndProcDelegate;
    private static bool _classRegistered;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IntPtr Handle => _hwnd;

    public WallpaperWindow(FileLogger logger)
    {
        _logger = logger;
        CreateWindow();
    }

    private void CreateWindow()
    {
        RegisterClassIfNeeded();

        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        Width = sw;
        Height = sh;

        var hInstance = NativeMethods.GetModuleHandleW(null);

        // PRIMARY PATH: create as a child of the desktop wallpaper WorkerW. A
        // window parented to WorkerW sits permanently behind the desktop icons
        // and is immune to Z-order reshuffling / Win+D — a real wallpaper. The
        // DXGI flip-model swap chain (DxgiRenderer) hands its back buffer
        // directly to DWM for composition every frame, so it displays reliably
        // on this child (unlike an ID2D1HwndRenderTarget, whose present gets
        // stranded on the child's redirect surface).
        var workerW = FindWallpaperWorkerW();
        if (workerW != IntPtr.Zero)
        {
            _hwnd = NativeMethods.CreateWindowExW(
                (uint)NativeMethods.WS_EX_NOACTIVATE | (uint)NativeMethods.WS_EX_TOOLWINDOW,
                ClassName, null,
                WS_CHILD | WS_VISIBLE,
                0, 0, sw, sh,
                workerW,
                IntPtr.Zero, hInstance, IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
                _logger.Info($"Wallpaper embedded under WorkerW {workerW}: {_hwnd}");
                LogWindowDiagnostics(workerW);
                return;
            }
            _logger.Warn("CreateWindowEx as WorkerW child failed; using top-level fallback");
        }
        else
        {
            _logger.Warn("Desktop WorkerW not found; using top-level fallback");
        }

        // FALLBACK PATH: top-level popup (only when WorkerW cannot be located).
        _hwnd = NativeMethods.CreateWindowExW(
            (uint)NativeMethods.WS_EX_TOOLWINDOW | (uint)NativeMethods.WS_EX_NOACTIVATE,
            ClassName, null,
            WS_POPUP | WS_VISIBLE,
            0, 0, sw, sh,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _logger.Error("Failed to create wallpaper window");
            return;
        }

        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetWindowPos(
            _hwnd, NativeMethods.HWND_BOTTOM,
            0, 0, sw, sh,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        LogWindowDiagnostics(IntPtr.Zero);
    }

    private void RegisterClassIfNeeded()
    {
        if (_classRegistered) return;

        var hInstance = NativeMethods.GetModuleHandleW(null);
        _wndProcDelegate = WndProc;
        var wndClass = new NativeMethods.WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = (IntPtr)1,
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero
        };

        if (NativeMethods.RegisterClassExW(ref wndClass) == 0)
        {
            _logger.Error("Failed to register wallpaper window class");
            return;
        }
        _classRegistered = true;
    }

    // Finds the desktop wallpaper layer: the WorkerW that sits *behind* the
    // desktop icons. We send 0x052C to Progman to guarantee the wallpaper WorkerW
    // exists, then locate it relative to the window hosting SHELLDLL_DefView.
    //
    // IMPORTANT: the desktop window hierarchy CHANGED in Windows 11 24H2/25H2.
    //   - Pre-24H2: the wallpaper WorkerW is a top-level SIBLING after the icon
    //     host (find via FindWindowEx(NULL, host, "WorkerW")).
    //   - 24H2/25H2: the wallpaper WorkerW is a CHILD of the icon host/Progman
    //     (find via FindWindowEx(host, NULL, "WorkerW")).
    // We try the 24H2 layout first, then fall back to the legacy layout, so the
    // lookup works on Windows 10, pre-24H2 Win11, and 24H2/25H2 Win11. A window
    // parented into this WorkerW renders under the icons; the taskbar is a
    // separate top-level window and is never covered. (Ref: lively #2074.)
    private IntPtr FindWallpaperWorkerW()
    {
        var progman = NativeMethods.FindWindowExHandle(IntPtr.Zero, IntPtr.Zero, "Progman", null);
        if (progman == IntPtr.Zero)
        {
            _logger.Warn("Progman window not found");
            return IntPtr.Zero;
        }

        // Ask the shell to spawn/ensure the wallpaper WorkerW.
        NativeMethods.SendMessageW(progman, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

        // Enumerate top-level windows to find the one hosting the desktop icons
        // (SHELLDLL_DefView as a child — normally Progman itself).
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.FindWindowExHandle(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;

            // Windows 11 24H2/25H2: WorkerW is a CHILD of the icon host.
            found = NativeMethods.FindWindowExHandle(hwnd, IntPtr.Zero, "WorkerW", null);
            if (found != IntPtr.Zero)
            {
                _logger.Info($"Found WorkerW as child of icon host {hwnd} (24H2+ layout)");
                return false;
            }

            // Pre-24H2 (Win10 / older Win11): WorkerW is a top-level sibling after the host.
            found = NativeMethods.FindWindowExHandle(IntPtr.Zero, hwnd, "WorkerW", null);
            if (found != IntPtr.Zero)
                _logger.Info($"Found WorkerW as sibling after icon host {hwnd} (legacy layout)");
            return false;
        }, IntPtr.Zero);

        if (found == IntPtr.Zero)
            _logger.Warn("Wallpaper WorkerW not found after spawning");
        return found;
    }

    private bool InsertAboveDesktopBackground()
    {
        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        // Standard wallpaper Z-order technique for a TOP-LEVEL window:
        //  1. Find Progman (the desktop window that hosts the icon ListView).
        //  2. Send WM_SPAWN_WORKERW (0x052C) so the shell moves the static
        //     wallpaper image onto a separate WorkerW. This makes the icon host
        //     (SHELLDLL_DefView) draw transparent, so a window behind it shows
        //     through between the icons and the system wallpaper.
        //  3. SetWindowPos(hwnd, progman) places our window immediately BEHIND
        //     Progman in the Z order (MSDN: the positioned window goes behind
        //     hWndInsertAfter). That puts it below the icon host AND the
        //     taskbar, and above the system wallpaper layer — i.e. a wallpaper.
        IntPtr progman = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) == "Progman")
            {
                progman = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (progman == IntPtr.Zero)
        {
            _logger.Warn("Progman not found; placing wallpaper at HWND_BOTTOM");
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_BOTTOM,
                0, 0, sw, sh,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_SHOWWINDOW);
            return false;
        }

        NativeMethods.SendMessageW(progman, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

        // SetWindowPos(hwnd, X) places hwnd immediately in FRONT of (above) X
        // in the Z order. To land our window BEHIND Progman — below the desktop
        // icons and the taskbar, above the system wallpaper — anchor on the
        // top-level window immediately beneath Progman (GW_HWNDNEXT).
        var below = NativeMethods.GetWindow(progman, NativeMethods.GW_HWNDNEXT);
        var insertAfter = below != IntPtr.Zero ? below : NativeMethods.HWND_BOTTOM;
        NativeMethods.SetWindowPos(
            _hwnd, insertAfter,
            0, 0, sw, sh,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_SHOWWINDOW);

        var above = NativeMethods.GetWindow(_hwnd, NativeMethods.GW_HWNDPREV);
        var belowSelf = NativeMethods.GetWindow(_hwnd, NativeMethods.GW_HWNDNEXT);
        _logger.Info($"Wallpaper placed (insert after {insertAfter}); Z above={above}({GetClassName(above)}) below={belowSelf}({GetClassName(belowSelf)}): {_hwnd}");
        return true;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        NativeMethods.GetClassNameW(hwnd, buf, 256);
        var end = Array.IndexOf(buf, '\0');
        return end >= 0 ? new string(buf, 0, end) : new string(buf);
    }

    // Diagnostic: log the created window's visibility, parent, and rect so we
    // can tell whether a non-displaying wallpaper is a layering/visibility
    // problem vs. a painting problem.
    private void LogWindowDiagnostics(IntPtr expectedParent)
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var visible = NativeMethods.IsWindowVisible(_hwnd);
            var isWin = NativeMethods.IsWindow(_hwnd);
            var parent = NativeMethods.GetParent(_hwnd);
            NativeMethods.GetWindowRect(_hwnd, out var rect);
            _logger.Info(
                $"Window diag hwnd={_hwnd}: isWindow={isWin} visible={visible} " +
                $"parent={parent}(expected {expectedParent}) " +
                $"rect=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Window diag failed: {ex.Message}");
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                return (IntPtr)HTTRANSPARENT;
            case WM_ERASEBKGND:
                return (IntPtr)1;
            default:
                return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
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
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
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

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
