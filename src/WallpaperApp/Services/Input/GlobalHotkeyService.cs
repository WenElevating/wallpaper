using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Input;

// 全局热键服务。拥有一个 message-only HWND,在其 WndProc 接收 WM_HOTKEY 并
// 分发到槽位对应的回调。设计要点:
//
//  - 独立窗口(不复用 TrayIcon):保证主窗口隐藏/托盘模式下热键仍生效,且
//    生命周期与设置变更解耦。
//  - 单线程:HWND 必须在其创建线程上被驱动;本服务假设所有方法都在 WPF
//    Dispatcher 线程调用(与 TrayIcon 一致)。
//  - 回调注入:动作(暂停/下一张等)由 MainViewModel 在启动时通过 SetHandler
//    注入,服务本身不依赖 ViewModel。
//  - WndProc 静态分发:与 TrayIcon 一样用 static _instance 让静态 WndProc
//    找回当前实例。生产环境只有一个实例,故可接受。测试只测 ComputeDiff,
//    不触发 WndProc,多实例构造不受影响。
public sealed class GlobalHotkeyService : IDisposable
{
    private const string ClassName = "WallpaperHotkey";

    // 槽位 → 注册 id(传给 RegisterHotKey 的 id 参数,WM_HOTKEY 的 wParam 回传它)。
    // internal(非 private)因为 HotkeyDiff(测试可见)的元组包含 Slot。
    internal enum Slot { TogglePause = 1, SkipNext = 2, SkipPrevious = 3, ToggleMute = 4 }

    private readonly FileLogger _logger;
    private readonly IntPtr _hwnd;
    private bool _disposed;

    // 当前已注册的绑定(用于 Apply 时 diff)。slot -> HotkeyConfig。
    private readonly Dictionary<Slot, HotkeyConfig> _registered = new();

    // 槽位 → 回调。由 MainViewModel 注入。
    private readonly Dictionary<Slot, Action> _handlers = new();

    private static readonly object ClassRegisterLock = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProc;
    private static GlobalHotkeyService? _instance; // 供静态 WndProc 找回实例(同 TrayIcon 模式)

    public GlobalHotkeyService(FileLogger logger)
    {
        _logger = logger;
        _instance = this;
        _hwnd = CreateWindow();
        if (_hwnd == IntPtr.Zero)
            _logger.Error("Hotkey message window creation failed");
    }

    // 注入某槽位的回调。slotName 必须与 Slot 枚举名匹配(TogglePause/SkipNext/...)。
    public void SetHandler(string slotName, Action handler)
    {
        if (Enum.TryParse<Slot>(slotName, ignoreCase: true, out var slot))
            _handlers[slot] = handler;
    }

    // 测试可见的 diff 计算:返回新旧绑定之间的注册/反注册差异。
    internal HotkeyDiff ComputeDiff(HotkeyBindings oldBindings, HotkeyBindings newBindings)
    {
        var diff = new HotkeyDiff();
        var slots = new[] { Slot.TogglePause, Slot.SkipNext, Slot.SkipPrevious, Slot.ToggleMute };
        foreach (var slot in slots)
        {
            var oldHk = GetSlot(oldBindings, slot);
            var newHk = GetSlot(newBindings, slot);
            if (oldHk == newHk) continue;
            if (!oldHk.IsNone) diff.ToUnregister.Add(slot);
            if (!newHk.IsNone) diff.ToRegister.Add((slot, newHk));
        }
        return diff;
    }

    internal sealed class HotkeyDiff
    {
        public List<(Slot slot, HotkeyConfig hk)> ToRegister { get; } = new();
        public List<Slot> ToUnregister { get; } = new();
    }

    private static HotkeyConfig GetSlot(HotkeyBindings b, Slot s) => s switch
    {
        Slot.TogglePause => b.TogglePause,
        Slot.SkipNext => b.SkipNext,
        Slot.SkipPrevious => b.SkipPrevious,
        Slot.ToggleMute => b.ToggleMute,
        _ => HotkeyConfig.None,
    };

    // 应用新绑定:diff 当前已注册集合,反注册移除项、注册新增项。
    public void Apply(HotkeyBindings bindings)
    {
        if (_hwnd == IntPtr.Zero) return;
        var oldBindings = CurrentAsBindings();
        var diff = ComputeDiff(oldBindings, bindings);

        foreach (var slot in diff.ToUnregister)
        {
            if (NativeMethods.UnregisterHotKey(_hwnd, (int)slot))
                _registered.Remove(slot);
            else
                _logger.Warn($"UnregisterHotKey failed for {slot}: {Marshal.GetLastWin32Error()}");
        }

        foreach (var (slot, hk) in diff.ToRegister)
        {
            var mods = MapModifiers(hk.Modifiers);
            if (NativeMethods.RegisterHotKey(_hwnd, (int)slot, mods | NativeMethods.MOD_NOREPEAT, (uint)hk.Key))
            {
                _registered[slot] = hk;
                _logger.Info($"Registered hotkey {slot}: {hk.DisplayString}");
            }
            else
            {
                var err = Marshal.GetLastWin32Error();
                _logger.Warn($"RegisterHotKey failed for {slot} ({hk.DisplayString}): Win32 error {err}");
            }
        }
    }

    private HotkeyBindings CurrentAsBindings() => new()
    {
        TogglePause = _registered.GetValueOrDefault(Slot.TogglePause),
        SkipNext = _registered.GetValueOrDefault(Slot.SkipNext),
        SkipPrevious = _registered.GetValueOrDefault(Slot.SkipPrevious),
        ToggleMute = _registered.GetValueOrDefault(Slot.ToggleMute),
    };

    private static uint MapModifiers(ModifierKeys m)
    {
        uint v = 0;
        if (m.HasFlag(ModifierKeys.Alt)) v |= NativeMethods.MOD_ALT;
        if (m.HasFlag(ModifierKeys.Ctrl)) v |= NativeMethods.MOD_CONTROL;
        if (m.HasFlag(ModifierKeys.Shift)) v |= NativeMethods.MOD_SHIFT;
        if (m.HasFlag(ModifierKeys.Win)) v |= NativeMethods.MOD_WIN;
        return v;
    }

    private IntPtr CreateWindow()
    {
        lock (ClassRegisterLock)
        {
            if (!_classRegistered)
            {
                _wndProc = WndProc;
                var hInstance = NativeMethods.GetModuleHandleW(null);
                var wc = new NativeMethods.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = hInstance,
                    lpszClassName = ClassName,
                };
                if (NativeMethods.RegisterClassExW(ref wc) == 0)
                {
                    _logger.Error("Hotkey window class registration failed");
                    return IntPtr.Zero;
                }
                _classRegistered = true;
            }
        }

        var hInstance2 = NativeMethods.GetModuleHandleW(null);
        return NativeMethods.CreateWindowExW(
            0, ClassName, null,
            0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero, hInstance2, IntPtr.Zero);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_HOTKEY && _instance != null)
        {
            var id = (Slot)(int)wParam;
            if (_instance._handlers.TryGetValue(id, out var handler))
            {
                // 转发到 WPF Dispatcher,避免在 WndProc 栈上直接触发 UI 操作。
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) handler();
                else dispatcher.BeginInvoke(handler);
            }
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var slot in _registered.Keys)
                NativeMethods.UnregisterHotKey(_hwnd, (int)slot);
            // UnregisterHotKey 是纯 P/Invoke,不会修改 _registered,故在循环内调用安全;
            // Clear 在循环后执行。
            _registered.Clear();
            NativeMethods.DestroyWindow(_hwnd);
        }
        // 清除静态回指:DestroyWindow 后 OS 不应再投递 WM_HOTKEY,但置空让 WndProc
        // 的 _instance != null 守卫也兜底,避免任何遗留消息解引用已释放实例。
        if (_instance == this) _instance = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
