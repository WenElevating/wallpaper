using System.Windows;
using System.Windows.Interop;
using WallpaperApp.Interop;

namespace WallpaperApp.UI;

// Applies a Windows 11 Mica backdrop and a dark title bar to a top-level
// window. Mica needs build 22000+; on older Windows this is a no-op (the window
// keeps its explicit dark background). Always enabled the dark immersive title
// bar so the chrome matches the dark theme regardless of Mica.
public static class WindowBackdrop
{
    public static bool TryApply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // Dark title bar works on Win10 20H1+ (build 19041+) and all Win11.
        int dark = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (Environment.OSVersion.Version.Build < 22000) return false;

        int corner = 2; // DWMWCP_ROUND — force rounded window corners
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        int backdrop = NativeMethods.DWMSBT_MAINWINDOW; // Mica
        int hr = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        return hr == 0; // S_OK
    }
}
