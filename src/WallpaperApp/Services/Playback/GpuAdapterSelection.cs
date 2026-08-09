using WallpaperApp.Services.Logging;
using Vortice.DXGI;

namespace WallpaperApp.Services.Playback;

// Summary of one DXGI adapter used by the selection decision.
public readonly record struct GpuAdapterInfo(
    int VendorId,
    long DedicatedVideoMemory,
    bool IsSoftware,
    string Description);

// Picks a discrete GPU for the D3D11 device. The app's device would otherwise
// be created on the OS default adapter (the display adapter — the weaker Intel
// iGPU on hybrid laptops) while the discrete GPU sits idle: measured on this
// machine the wallpaper burns ~16-20% VideoDecode + ~14% 3D on the iGPU with
// the RTX 4060 at 0%. Decoding + rendering on the discrete GPU (NVDEC is
// several times faster at 4K) cuts the iGPU load to ~0; DWM composites the
// swap chain to the display, so presentation keeps working.
public static class GpuAdapterSelection
{
    private const int VendorNvidia = 0x10DE;
    private const int VendorAmd = 0x1002;

    // Prefers the discrete NVIDIA/AMD GPU with the most dedicated video memory.
    // Returns null when no discrete GPU exists — the caller then uses the OS
    // default adapter. Pure decision logic, unit-tested with fabricated lists.
    public static GpuAdapterInfo? PickDiscrete(IReadOnlyList<GpuAdapterInfo> adapters)
    {
        GpuAdapterInfo? best = null;
        foreach (var adapter in adapters)
        {
            if (adapter.IsSoftware) continue;
            if (adapter.VendorId != VendorNvidia && adapter.VendorId != VendorAmd) continue;
            if (best is null || adapter.DedicatedVideoMemory > best.Value.DedicatedVideoMemory)
                best = adapter;
        }
        return best;
    }

    // Enumerates DXGI adapters and returns the preferred discrete GPU as a COM
    // object the caller MUST dispose, plus its description (null when no
    // discrete GPU exists or enumeration fails — the caller falls back to the
    // OS default adapter).
    public static IDXGIAdapter? EnumeratePreferred(FileLogger? logger, out string? description)
    {
        description = null;
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            var all = new List<(GpuAdapterInfo Info, IDXGIAdapter Adapter)>();
            for (int i = 0; ; i++)
            {
                var result = factory.EnumAdapters(i, out IDXGIAdapter? adapter);
                if (result.Failure) break;   // DXGI_ERROR_NOT_FOUND: end of list
                if (adapter is null) break;
                all.Add((Describe(adapter), adapter));
            }

            var pick = PickDiscrete(all.Select(a => a.Info).ToArray());
            if (pick is null)
            {
                foreach (var a in all) a.Adapter.Dispose();
                return null;
            }

            IDXGIAdapter? chosen = null;
            foreach (var a in all)
            {
                if (chosen is null && a.Info == pick.Value)
                {
                    chosen = a.Adapter;
                    description = a.Info.Description;
                }
                else
                {
                    a.Adapter.Dispose();
                }
            }
            return chosen;
        }
        catch (Exception ex)
        {
            logger?.Warn($"GPU adapter enumeration failed: {ex.Message}");
            return null;
        }
    }

    private static GpuAdapterInfo Describe(IDXGIAdapter adapter)
    {
        using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
        var desc = adapter1.Description1;
        return new GpuAdapterInfo(
            desc.VendorId,
            (long)desc.DedicatedVideoMemory,
            (desc.Flags & AdapterFlags.Software) != 0,
            desc.Description);
    }
}
