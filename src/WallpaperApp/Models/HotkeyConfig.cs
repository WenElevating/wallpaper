namespace WallpaperApp.Models;

// 热键修饰键(位标志)。取值与 Win32 MOD_* 常量一致,便于直接位或传 RegisterHotKey。
// 注意:本类型位于 WallpaperApp.Models 命名空间,刻意不与 System.Windows.Input.ModifierKeys
// 混用——本模型用于持久化与显示,Win32 注册时由 GlobalHotkeyService.MapModifiers 转换。
[Flags]
public enum ModifierKeys
{
    None = 0,
    Alt = 0x0001,
    Ctrl = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

// Windows 虚拟键码子集(只列热键常用的字母/数字/功能键)。
public enum VirtualKey
{
    None = 0,
    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Space = 0x20,
    Pause = 0x13,
}

// 一个热键 = 修饰键 + 虚拟键。readonly record struct 提供值相等。
// 用于 AppSettings 序列化(JSON),字段均为简单枚举,默认序列化即可。
public readonly record struct HotkeyConfig(ModifierKeys Modifiers, VirtualKey Key)
{
    public static readonly HotkeyConfig None = new(ModifierKeys.None, VirtualKey.None);

    public bool IsNone => Modifiers == ModifierKeys.None && Key == VirtualKey.None;

    // "Ctrl+Alt+W" 形式的显示串。修饰键按固定顺序输出,便于稳定显示与冲突比对。
    public string DisplayString
    {
        get
        {
            if (IsNone) return "";
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Ctrl)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Win)) parts.Add("Win");
            if (Key != VirtualKey.None) parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    // 解析 "Ctrl+Alt+W" → HotkeyConfig。不区分大小写;未知段返回 false。
    public static bool TryParse(string text, out HotkeyConfig result)
    {
        result = None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = ModifierKeys.None;
        var key = VirtualKey.None;
        foreach (var raw in text.Split('+'))
        {
            var seg = raw.Trim();
            if (seg.Length == 0) continue;
            switch (seg.ToUpperInvariant())
            {
                case "CTRL": mods |= ModifierKeys.Ctrl; break;
                case "ALT": mods |= ModifierKeys.Alt; break;
                case "SHIFT": mods |= ModifierKeys.Shift; break;
                case "WIN": mods |= ModifierKeys.Win; break;
                default:
                    if (Enum.TryParse<VirtualKey>(seg, ignoreCase: true, out var vk))
                        key = vk;
                    else
                        return false;
                    break;
            }
        }
        if (key == VirtualKey.None) return false;
        result = new HotkeyConfig(mods, key);
        return true;
    }
}

// 所有可配置热键的槽位。新增动作(下一张/静音)时在此追加键。
// 未绑定的槽位保持 HotkeyConfig.None,GlobalHotkeyService 不会为其调用 RegisterHotKey。
public sealed class HotkeyBindings
{
    public HotkeyConfig TogglePause { get; init; } = new(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.W);
    // 以下槽位本期注册为 None(不绑定),F1(播放列表)/F4(声音)实现后由各自计划填充默认值。
    public HotkeyConfig SkipNext { get; init; } = HotkeyConfig.None;
    public HotkeyConfig SkipPrevious { get; init; } = HotkeyConfig.None;
    public HotkeyConfig ToggleMute { get; init; } = HotkeyConfig.None;
}
