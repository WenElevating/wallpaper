using WallpaperApp.Models;
using WallpaperApp.Services.Input;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Tests.Services;

// 热键的注册/分发依赖真实的 HWND + 全局快捷键槽位,无法在无窗口的单元测试里
// 完整驱动 RegisterHotKey。这里只测可隔离的部分:绑定 diff 逻辑(ComputeDiff)。
public sealed class GlobalHotkeyServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public GlobalHotkeyServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HotkeyTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    // 配置变更(为某槽位新设一个热键)应被记录为"待注册"。
    [Fact]
    public void ComputeDiff_NewBinding_QueuesRegistration()
    {
        var svc = new GlobalHotkeyService(_logger);
        var diff = svc.ComputeDiff(
            new HotkeyBindings { TogglePause = HotkeyConfig.None },
            new HotkeyBindings { TogglePause = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.W) });

        Assert.Single(diff.ToRegister);
        Assert.Empty(diff.ToUnregister);
    }

    // 清空某槽位应被记录为"待反注册"。
    [Fact]
    public void ComputeDiff_ClearedBinding_QueuesUnregistration()
    {
        var svc = new GlobalHotkeyService(_logger);
        var diff = svc.ComputeDiff(
            new HotkeyBindings { TogglePause = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W) },
            new HotkeyBindings { TogglePause = HotkeyConfig.None });

        Assert.Empty(diff.ToRegister);
        Assert.Single(diff.ToUnregister);
    }

    // 未变化的槽位不出现在任一队列。
    [Fact]
    public void ComputeDiff_UnchangedBinding_NoDiff()
    {
        var svc = new GlobalHotkeyService(_logger);
        var same = new HotkeyBindings { TogglePause = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W) };
        var diff = svc.ComputeDiff(same, same);

        Assert.Empty(diff.ToRegister);
        Assert.Empty(diff.ToUnregister);
    }

    // 某槽位从热键 A 改为热键 B,应同时出现在 ToUnregister(A) 和 ToRegister(B)。
    [Fact]
    public void ComputeDiff_ChangedBinding_QueuesBoth()
    {
        var svc = new GlobalHotkeyService(_logger);
        var diff = svc.ComputeDiff(
            new HotkeyBindings { TogglePause = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W) },
            new HotkeyBindings { TogglePause = new HotkeyConfig(ModifierKeys.Alt, VirtualKey.W) });

        Assert.Single(diff.ToRegister);
        Assert.Single(diff.ToUnregister);
    }

    // 全部四个槽位同时绑定:验证 GetSlot 的 switch 对 SkipNext/SkipPrevious/ToggleMute
    // 三个非 TogglePause 分支也正确(此前测试只覆盖 TogglePause,这三个分支完全未测)。
    [Fact]
    public void ComputeDiff_AllSlotsBound_RegistersAllFour()
    {
        var svc = new GlobalHotkeyService(_logger);
        var allNone = new HotkeyBindings
        {
            TogglePause = HotkeyConfig.None,
            SkipNext = HotkeyConfig.None,
            SkipPrevious = HotkeyConfig.None,
            ToggleMute = HotkeyConfig.None,
        };
        var allBound = new HotkeyBindings
        {
            TogglePause = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.W),
            SkipNext = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.N),
            SkipPrevious = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.P),
            ToggleMute = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.M),
        };

        var diff = svc.ComputeDiff(allNone, allBound);

        Assert.Equal(4, diff.ToRegister.Count);
        Assert.Empty(diff.ToUnregister);
    }

    // 多槽位混合:一个改变、一个新增、一个不变 —— 验证循环无提前返回、计数正确。
    [Fact]
    public void ComputeDiff_MixedChanges_AggregatesCorrectly()
    {
        var svc = new GlobalHotkeyService(_logger);
        var old = new HotkeyBindings
        {
            TogglePause = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W),     // 将改变
            SkipNext = HotkeyConfig.None,                                         // 将新增
            ToggleMute = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.M),      // 不变
        };
        var next = new HotkeyBindings
        {
            TogglePause = new HotkeyConfig(ModifierKeys.Alt, VirtualKey.W),      // 改变 -> 1 unreg + 1 reg
            SkipNext = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.N),        // 新增 -> 1 reg
            ToggleMute = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.M),      // 不变 -> 0
        };

        var diff = svc.ComputeDiff(old, next);

        Assert.Equal(2, diff.ToRegister.Count);   // TogglePause(新) + SkipNext
        Assert.Single(diff.ToUnregister);          // TogglePause(旧)
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
