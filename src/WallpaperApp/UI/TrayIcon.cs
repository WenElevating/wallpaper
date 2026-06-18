using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using WallpaperApp.Interop;
using WallpaperApp.Localization;
using WallpaperApp.Services.Logging;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI;

// Hand-rolled system-tray icon using Shell_NotifyIcon + a message-only window.
//
// Why not Hardcodet.NotifyIcon: the 1.0.5 build previously used here shows its
// WPF ContextMenu via PlacementMode.MousePoint (wrong under per-monitor DPI) and
// never performs the SetForegroundWindow / PostMessage(WM_NULL) activation that a
// shell notify-icon menu needs. On Windows 10/11 that manifested exactly as the
// reported bugs — the menu opened at the wrong spot and clicks on its items were
// silently dropped. The Win32 pattern below (foreground activate, then
// TrackPopupMenuEx at the cursor, then WM_NULL) is the documented fix and both
// positions and dispatches the menu correctly.
//
// Menu labels are read from Strings.* on every right-click, so the tray menu
// follows the current UI language without needing a restart.
public sealed class TrayIcon : IDisposable
{
    private const string ClassName = "WallpaperTray";
    private const uint CallbackMessage = 0x8000; // WM_APP

    // Mouse messages delivered in the callback message's lParam.
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint CmdOpen = 1001;
    private const uint CmdPause = 1002;
    private const uint CmdResume = 1003;
    private const uint CmdExit = 1004;
    private const uint CmdLangZh = 1005;
    private const uint CmdLangEn = 1006;
    private const uint CmdShuffle = 1007;

    private static readonly object ClassRegisterLock = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProc;
    private static TrayIcon? _instance;

    private readonly MainViewModel _viewModel;
    private readonly FileLogger _logger;
    private readonly Icon _icon;
    private readonly MainWindow _mainWindow;

    private IntPtr _hwnd;
    private bool _disposed;
    // True once a real app exit has begun; lets the Closing handler below stop
    // hiding and actually allow the window (and the process) to close.
    private bool _isExiting;

    public TrayIcon(MainViewModel viewModel, FileLogger logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        _instance = this;

        _icon = TrayIconImage.CreateDefault();

        _mainWindow = new MainWindow(viewModel);
        // Minimize-to-tray: cancel Close and Hide instead, so the window stays
        // alive and can be reopened repeatedly from the tray. (Handling Closed
        // cannot prevent destruction — it fires too late — and a later Show()
        // then throws VerifyCanShow, which was silently swallowed.) When a real
        // exit is in progress (_isExiting) we let the close go through.
        _mainWindow.Closing += (_, e) =>
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                _mainWindow.Hide();
            }
        };

        CreateWindow();
    }

    private void CreateWindow()
    {
        RegisterClassIfNeeded();

        var hInstance = NativeMethods.GetModuleHandleW(null);
        _hwnd = NativeMethods.CreateWindowExW(
            0, ClassName, null,
            0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _logger.Error("Tray message window creation failed");
            return;
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle,
            szTip = "WallpaperApp",
        };

        if (!NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid))
            _logger.Error("Shell_NotifyIcon NIM_ADD failed");
        else
            _logger.Info("Tray icon added");
    }

    private static void RegisterClassIfNeeded()
    {
        lock (ClassRegisterLock)
        {
            if (_classRegistered) return;
            var hInstance = NativeMethods.GetModuleHandleW(null);
            _wndProc = WndProc;
            var wc = new NativeMethods.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = ClassName,
            };
            if (NativeMethods.RegisterClassExW(ref wc) == 0)
            {
                _instance?._logger.Error("Tray window class registration failed");
                return;
            }
            _classRegistered = true;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == CallbackMessage && _instance != null)
        {
            _instance.OnTrayCallback((int)lParam);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // Marshal a command off the native WndProc call stack onto the WPF
    // Dispatcher. Showing/activating a WPF Window synchronously from inside the
    // tray's Win32 message callback deadlocks the UI thread ("Not Responding"),
    // because window creation/activation must happen on a clean Dispatcher turn.
    // BeginInvoke lets the WndProc return first, then runs the command normally.
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) { action(); return; }
        dispatcher.BeginInvoke(action);
    }

    private void OnTrayCallback(int mouseMsg)
    {
        switch (mouseMsg)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                Dispatch(ShowMainWindow);
                break;
            case WM_RBUTTONUP:
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Required activation dance (KB135788): put our window in the foreground
        // before showing the menu, then post a no-op so the menu closes correctly
        // when the user clicks away. Without this, item clicks are dropped.
        NativeMethods.SetForegroundWindow(_hwnd);

        NativeMethods.GetCursorPos(out var pt);

        IntPtr menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING | NativeMethods.MF_ENABLED, CmdOpen, Strings.MenuOpen);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING | NativeMethods.MF_ENABLED, CmdPause, Strings.PauseAllText);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING | NativeMethods.MF_ENABLED, CmdResume, Strings.ResumeAllText);
        // F5: shuffle to a random wallpaper that isn't the current one (nor in the
        // recent-history window). Grayed out when the library is empty.
        var shuffleFlags = NativeMethods.MF_STRING |
            (_viewModel.Wallpapers.Count > 0 ? NativeMethods.MF_ENABLED : NativeMethods.MF_GRAYED);
        NativeMethods.AppendMenuW(menu, shuffleFlags, CmdShuffle, Strings.MenuShuffleWallpaper);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);

        // Language submenu (中文 / English), with a checkmark on the active one.
        var activeLang = LocalizationService.EffectiveCode(_viewModel.Settings.Language);
        IntPtr langMenu = NativeMethods.CreatePopupMenu();
        uint zhFlags = NativeMethods.MF_STRING | NativeMethods.MF_ENABLED;
        if (activeLang == LocalizationService.Chinese) zhFlags |= NativeMethods.MF_CHECKED;
        uint enFlags = NativeMethods.MF_STRING | NativeMethods.MF_ENABLED;
        if (activeLang == LocalizationService.English) enFlags |= NativeMethods.MF_CHECKED;
        NativeMethods.AppendMenuW(langMenu, zhFlags, CmdLangZh, "中文");
        NativeMethods.AppendMenuW(langMenu, enFlags, CmdLangEn, "English");
        NativeMethods.AppendMenuPopup(menu, NativeMethods.MF_POPUP | NativeMethods.MF_STRING, langMenu, Strings.LanguageLabel);

        NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, null);
        NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING | NativeMethods.MF_ENABLED, CmdExit, Strings.MenuExit);

        // Right + bottom align => the menu opens up-and-to-the-left of the cursor,
        // which is correct for a tray icon in the bottom-right corner ("above the
        // icon"). TPM_RETURNCMD returns the chosen command id directly.
        const uint flags =
            NativeMethods.TPM_RIGHTBUTTON |
            NativeMethods.TPM_RIGHTALIGN |
            NativeMethods.TPM_BOTTOMALIGN |
            NativeMethods.TPM_RETURNCMD |
            NativeMethods.TPM_NONOTIFY |
            NativeMethods.TPM_VERPOSANIMATION;

        int selected = NativeMethods.TrackPopupMenuEx(menu, flags, pt.X, pt.Y, _hwnd, IntPtr.Zero);

        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.DestroyMenu(menu);

        switch (selected)
        {
            case (int)CmdOpen: Dispatch(ShowMainWindow); break;
            case (int)CmdPause: Dispatch(() => _ = _viewModel.PauseAllAsync()); break;
            case (int)CmdResume: Dispatch(() => _ = _viewModel.ResumeAllAsync()); break;
            case (int)CmdShuffle: Dispatch(() => _ = _viewModel.ShuffleAllMonitorsAsync()); break;
            case (int)CmdLangZh: Dispatch(() => _ = _viewModel.SetLanguageAsync(LocalizationService.Chinese)); break;
            case (int)CmdLangEn: Dispatch(() => _ = _viewModel.SetLanguageAsync(LocalizationService.English)); break;
            case (int)CmdExit: Dispatch(ExitApplication); break;
        }
    }

    public void ShowMainWindow()
    {
        try
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
        catch (Exception ex)
        {
            // Surface the failure to the app log instead of letting it vanish into
            // the generic DispatcherUnhandledException swallow.
            _logger.Error("ShowMainWindow failed", ex);
        }
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            var nid = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _isExiting = true;
        _mainWindow.Close();
        _icon.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
