using WallpaperApp.Models;

namespace WallpaperApp.Tests;

public class HotkeyConfigTests
{
    [Fact]
    public void DisplayString_CombinesModifiersAndKey()
    {
        var hk = new HotkeyConfig(ModifierKeys.Ctrl | ModifierKeys.Alt, VirtualKey.W);
        Assert.Equal("Ctrl+Alt+W", hk.DisplayString);
    }

    [Fact]
    public void None_HasEmptyDisplayString()
    {
        Assert.Equal("", HotkeyConfig.None.DisplayString);
        Assert.True(HotkeyConfig.None.IsNone);
    }

    [Fact]
    public void Equality_ByValue()
    {
        var a = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W);
        var b = new HotkeyConfig(ModifierKeys.Ctrl, VirtualKey.W);
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Parse_AcceptsPlusDelimitedString()
    {
        var ok = HotkeyConfig.TryParse("Ctrl+Alt+W", out var hk);
        Assert.True(ok);
        Assert.Equal(ModifierKeys.Ctrl | ModifierKeys.Alt, hk.Modifiers);
        Assert.Equal(VirtualKey.W, hk.Key);
    }

    [Fact]
    public void Parse_RejectsEmpty()
    {
        Assert.False(HotkeyConfig.TryParse("", out _));
        Assert.False(HotkeyConfig.TryParse("   ", out _));
    }
}
