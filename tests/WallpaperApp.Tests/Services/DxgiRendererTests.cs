using WallpaperApp.Interop;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class DxgiRendererTests
{
    [Fact]
    public void ComputeSwapChainSize_PrefersClientRect_WhenItIsValid()
    {
        var (w, h) = DxgiRenderer.ComputeSwapChainSize(
            3840, 2160, new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1600 });

        Assert.Equal(2560, w);
        Assert.Equal(1600, h);
    }

    [Fact]
    public void ComputeSwapChainSize_FallsBackToFrameSize_WhenNoClientRect()
    {
        var (w, h) = DxgiRenderer.ComputeSwapChainSize(3840, 2160, null);

        Assert.Equal(3840, w);
        Assert.Equal(2160, h);
    }

    [Fact]
    public void ComputeSwapChainSize_FallsBackToFrameSize_WhenClientRectIsDegenerate()
    {
        var (w, h) = DxgiRenderer.ComputeSwapChainSize(
            3840, 2160, new NativeMethods.RECT { Left = 0, Top = 0, Right = 0, Bottom = 0 });

        Assert.Equal(3840, w);
        Assert.Equal(2160, h);
    }
}
