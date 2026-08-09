using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class GpuAdapterSelectionTests
{
    private static readonly GpuAdapterInfo IntelIntegrated =
        new(0x8086, 128L * 1024 * 1024, false, "Intel(R) UHD Graphics");
    private static readonly GpuAdapterInfo NvidiaDiscrete =
        new(0x10DE, 8L * 1024 * 1024 * 1024, false, "NVIDIA GeForce RTX 4060 Laptop GPU");
    private static readonly GpuAdapterInfo AmdDiscrete =
        new(0x1002, 16L * 1024 * 1024 * 1024, false, "AMD Radeon RX 7900 XTX");
    private static readonly GpuAdapterInfo Warp =
        new(0x1414, 0, true, "Microsoft Basic Render Driver");

    [Fact]
    public void PickDiscrete_NoAdapters_ReturnsNull()
    {
        Assert.Null(GpuAdapterSelection.PickDiscrete(Array.Empty<GpuAdapterInfo>()));
    }

    [Fact]
    public void PickDiscrete_OnlyIntegratedGraphics_ReturnsNull()
    {
        Assert.Null(GpuAdapterSelection.PickDiscrete(new[] { IntelIntegrated }));
    }

    [Fact]
    public void PickDiscrete_PrefersDiscreteOverIntegrated()
    {
        var pick = GpuAdapterSelection.PickDiscrete(new[] { IntelIntegrated, NvidiaDiscrete });

        Assert.Equal(NvidiaDiscrete, pick);
    }

    [Fact]
    public void PickDiscrete_PrefersHigherMemoryDiscrete()
    {
        var pick = GpuAdapterSelection.PickDiscrete(new[] { NvidiaDiscrete, AmdDiscrete });

        Assert.Equal(AmdDiscrete, pick);
    }

    [Fact]
    public void PickDiscrete_IgnoresSoftwareAdapter()
    {
        var pick = GpuAdapterSelection.PickDiscrete(new[] { Warp, NvidiaDiscrete });

        Assert.Equal(NvidiaDiscrete, pick);
    }

    [Fact]
    public void PickDiscrete_OnlySoftwareAdapters_ReturnsNull()
    {
        Assert.Null(GpuAdapterSelection.PickDiscrete(new[] { Warp }));
    }

    [Fact]
    public void PickDiscrete_AcceptsAmdDiscrete()
    {
        var pick = GpuAdapterSelection.PickDiscrete(new[] { IntelIntegrated, AmdDiscrete });

        Assert.Equal(AmdDiscrete, pick);
    }
}
