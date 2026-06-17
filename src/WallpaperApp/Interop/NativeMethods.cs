using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindWindowExW(
        IntPtr hWndParent, IntPtr hWndChildAfter,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszClass,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowExHandle(
        IntPtr hWndParent, IntPtr hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoExW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    internal const int GW_HWNDNEXT = 2;
    internal const int GW_HWNDPREV = 3;

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateSolidBrush(uint crColor);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    internal const int WM_PAINT = 0x000F;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    internal const uint PM_REMOVE = 0x0001;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    // Window backdrop (Win11 22000+) and dark title bar.
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    internal const int DWMSBT_MAINWINDOW = 2; // Mica
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    internal const uint WM_SPAWN_WORKERW = 0x052C;
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal static readonly IntPtr HWND_BOTTOM = new(1);
    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    // ---------- System tray (Shell_NotifyIcon) ----------

    // A message-only window parent. Passing this as hWndParent to CreateWindowExW
    // yields a window that has no UI but still receives window messages — ideal as
    // the owner of a tray icon's callback window.
    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    // Classic DllImport (not LibraryImport): the source generator cannot marshal a
    // struct containing ByValTStr fixed-size strings, but the runtime marshaler can.
    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // union: uTimeout / uVersion
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    internal const uint NIM_ADD = 0x00000000;
    internal const uint NIM_MODIFY = 0x00000001;
    internal const uint NIM_DELETE = 0x00000002;
    internal const uint NIF_MESSAGE = 0x00000001;
    internal const uint NIF_ICON = 0x00000002;
    internal const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    internal const uint WM_NULL = 0x0000;

    // ---------- Popup menu ----------

    [LibraryImport("user32.dll")]
    internal static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(IntPtr hMenu);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    // Same entry point as AppendMenuW, but uIDNewItem is pointer-sized so it can
    // hold an HMENU for MF_POPUP submenus (a uint would truncate a 64-bit handle).
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuPopup(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    internal const uint MF_STRING = 0x00000000;
    internal const uint MF_ENABLED = 0x00000000;
    internal const uint MF_GRAYED = 0x00000001;
    internal const uint MF_SEPARATOR = 0x00000800;
    internal const uint MF_POPUP = 0x00000010;
    internal const uint MF_CHECKED = 0x00000008;
    internal const uint TPM_LEFTBUTTON = 0x0000;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const uint TPM_LEFTALIGN = 0x0000;
    internal const uint TPM_RIGHTALIGN = 0x0008;
    internal const uint TPM_BOTTOMALIGN = 0x0020;
    internal const uint TPM_NONOTIFY = 0x0080;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_VERPOSANIMATION = 0x0400;

    // ---------- Power status (battery / AC) ----------

    // Reads the system power status. Returns false on failure (rare; treated as
    // "on AC" by callers). The struct is fully blittable so LibraryImport works.
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;       // 0 = offline (battery), 1 = online (AC), 255 = unknown
        public byte BatteryFlag;        // bit flags; 128 = no battery
        public byte BatteryLifePercent; // 0-100; 255 = unknown
        public byte SystemStatusFlag;   // 1 = battery saver active (Win10+)
        public uint BatteryLifeTime;    // seconds remaining; 0xFFFFFFFF = unknown
        public uint BatteryFullLifeTime;
    }

    internal const byte ACLineOffline = 0;
    internal const byte ACLineOnline = 1;
    internal const byte ACLineUnknown = 255;

    // ---------- Fullscreen detection ----------

    // SHQueryUserNotificationState is the OS's own "is a fullscreen app
    // active?" signal — the same one Windows uses to auto-hide the taskbar
    // and suppress notifications. It catches cases pure geometry misses:
    // D3D exclusive-fullscreen games (no normal window to measure), F11
    // fullscreen, presentation mode. The struct is a 4-byte int, blittable.
    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pqunsState);

    internal enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,                 // machine locked / screensaver / fast-user-switch
        QUNS_BUSY = 2,                        // a fullscreen app is running (F11 fullscreen, most games)
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,     // a Direct3D app is in exclusive fullscreen
        QUNS_PRESENTATION_MODE = 4,           // presentation mode (projector)
        QUNS_ACCEPTS_NOTIFICATIONS = 5,       // normal — no fullscreen
        QUNS_QUIET_TIME = 6,                  // quiet hours — no fullscreen
        QUNS_APP = 7,                         // a Windows Store app is running (treated as fullscreen)
    }

    // MONITOR_DEFAULTTOPRIMARY: if the window isn't on any monitor, fall back
    // to the primary. Returns the monitor the window is mostly on.
    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ---------- DXGI occlusion status event (kernel32 sync primitives) ----------
    // Used by DxgiRenderer to get a notification when its swap chain (the
    // wallpaper window) becomes occluded — see RegisterOcclusionStatusEvent.

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateEventW(IntPtr lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    // WaitForSingleObject: returns WAIT_OBJECT_0 (0) when signaled.
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 0x00000102;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ResetEvent(IntPtr hEvent);

    // ---------- Wallpaper visibility (Z-order region subtraction, GDI) ----------

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(IntPtr hWnd);

    // IsZoomed: window is maximized. Maximized windows cover the monitor's work
    // area, but their GetWindowRect is intentionally larger than the monitor
    // (the resize borders are drawn off-screen, ~8px each side) — so region math
    // against GetWindowRect is unreliable for them. IsZoomed is the robust test.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(IntPtr hWnd);

    // EnumWindows enumerates top-level windows in Z order, top to bottom.
    // (Declared earlier in this file; reused for the Z-order region subtraction.)

    // GDI region API for accumulating visible area: start with the monitor rect,
    // subtract each higher-Z window's rect; if the region is empty afterward,
    // the wallpaper on that monitor is fully covered.
    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [LibraryImport("gdi32.dll")]
    internal static partial int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [LibraryImport("gdi32.dll")]
    internal static partial int GetRgnBox(IntPtr hrgn, out RECT lprc);

    internal const int RGN_DIFF = 4;   // dst = src1 minus src2
    internal const int RGN_AND = 1;
    internal const int NULLREGION = 1; // region is empty
    internal const int SIMPLEREGION = 2;
    internal const int COMPLEXREGION = 3;
    internal const int ERROR_RGN = 0;

    // ---- RDP quick check (F2) ----
    // GetSystemMetrics 已在上方声明(用于全屏检测);这里只补充 SM_REMOTESESSION 常量。
    internal const int SM_REMOTESESSION = 0x1000;

    // ---- Miracast / indirect display detection (F2) ----
    // QueryDisplayConfig 枚举当前所有显示路径,用于发现无线投屏 / 间接显示器。
    // DISPLAYCONFIG_PATH_INFO.targetInfo.outputTechnology 指示输出技术类型。

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    // QDC_ALL_PATHS = 枚举所有路径(含未激活),确保投屏目标被覆盖。
    internal const uint QDC_ALL_PATHS = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public uint adapterId_Low;
        public uint adapterId_High;
        public uint id;
        public uint modeInfoIdx; // 联合体,取低 16 位为 source mode idx
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public uint adapterId_Low;
        public uint adapterId_High;
        public uint id;
        public uint modeInfoIdx; // 联合体;低 16 位 = target mode idx
        public uint outputTechnology; // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_*
        public uint outputTechnology_Reserved;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public uint targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public uint adapterId_Low;
        public uint adapterId_High;
        public DISPLAYCONFIG_MODE_INFO_Union modeInfo; // 联合体;此处不细分字段
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct DISPLAYCONFIG_MODE_INFO_Union
    {
        // targetMode 与 sourceMode 是联合体;我们只关心结构大小对齐,
        // 实际取值不读,所以用固定大小的占位缓冲。
        [FieldOffset(0)] public long _placeholder1;
        [FieldOffset(8)] public long _placeholder2;
        [FieldOffset(16)] public long _placeholder3;
        [FieldOffset(24)] public long _placeholder4;
    }

    // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_* 取值(用于判定投屏类型)。
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER = 0xFFFFFFFFu; // -1 unsigned
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI = 5;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DVI = 6;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST = 11;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED = 12;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL = 14;
    internal const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNELING = 15;
}
