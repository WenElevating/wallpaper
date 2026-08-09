using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// The single shared D3D11 device used by BOTH the hardware decoder (FFmpeg
// D3D11VA, via HwDecodeDevice.CreateForDevice) and the wallpaper renderer
// (DxgiRenderer swap chain + NV12 shader). Sharing one device is what makes
// zero-copy possible: the decoder's output NV12 texture lives on the same
// device as the swap chain, so the renderer can blit it directly with a shader
// — no CPU/system-RAM round-trip (no av_hwframe_transfer_data, no sws_scale,
// no CopyFromMemory).
//
// Created with VideoSupport (required by D3D11VA) + BgraSupport (required by
// the BGRA swap chain) and made multithread-protected so the decode threads and
// the render thread can both drive the immediate context safely.
//
// Hardware-specific: by default the device prefers a DISCRETE NVIDIA/AMD GPU
// (see GpuAdapterSelection) instead of the OS default (the display adapter —
// the weaker iGPU on hybrid laptops). DWM composites the swap chain to the
// display, so a dGPU device presents normally. Device creation is LAZY (first
// property access) so App can apply the PreferDiscreteGpu setting after
// settings load and before any wallpaper session starts.
//
// Failure is graceful: if the device can't be created (or lacks VideoSupport),
// IsAvailable is false and the system falls back to software decode.
public sealed class GpuDevice : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private readonly FileLogger _logger;
    private readonly object _createLock = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private bool _videoOk;
    private bool _creationAttempted;
    private bool _disposed;
    private string? _adapterDescription;

    // Set by App after settings load, BEFORE any wallpaper session starts (the
    // device is created lazily on first access). Default true: decode+render
    // on a discrete GPU (NVDEC is several times faster at 4K than the iGPU).
    public bool PreferDiscreteGpu { get; set; } = true;

    public ID3D11Device Device => GetDevice();
    public ID3D11DeviceContext Context => GetContext();
    public IntPtr DevicePointer => GetDevice().NativePointer;
    public bool IsAvailable { get { EnsureCreated(); return _device != null; } }
    public bool SupportsVideo { get { EnsureCreated(); return _videoOk; } }

    // True only once creation has been attempted (used by tests to assert
    // creation is deferred).
    internal bool IsCreationAttempted { get { lock (_createLock) return _creationAttempted; } }

    // Description of the adapter the device was actually created on (e.g.
    // "NVIDIA GeForce RTX 4060 Laptop GPU"). Used by the render probe and logs.
    public string? AdapterDescription { get { EnsureCreated(); return _adapterDescription; } }

    public GpuDevice(FileLogger logger)
    {
        _logger = logger;
    }

    private ID3D11Device GetDevice()
    {
        EnsureCreated();
        return _device ?? throw new ObjectDisposedException(nameof(GpuDevice));
    }

    private ID3D11DeviceContext GetContext()
    {
        EnsureCreated();
        return _context ?? throw new ObjectDisposedException(nameof(GpuDevice));
    }

    private void EnsureCreated()
    {
        lock (_createLock)
        {
            if (_creationAttempted || _disposed) return;
            _creationAttempted = true;
            TryCreateCore();
        }
    }

    private void TryCreateCore()
    {
        string? description = null;
        using IDXGIAdapter? preferred = PreferDiscreteGpu
            ? GpuAdapterSelection.EnumeratePreferred(_logger, out description)
            : null;
        if (preferred is not null)
            _logger.Info($"Preferring discrete GPU: {description}");

        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        if (!TryCreateHardware(flags, out var dev, out var ctx, out var videoOk, preferred))
        {
            // The preferred discrete GPU failed to open (e.g. driver state):
            // fall back to the OS default adapter.
            if (preferred is not null)
                _logger.Warn("Discrete GPU device creation failed; falling back to the default adapter");
            if (!TryCreateHardware(flags, out dev, out ctx, out videoOk))
            {
                // VideoSupport is rare to fail, but if it does, fall back to a
                // plain device so the renderer's CPU-upload path still works
                // (decode goes sw).
                if (!TryCreateHardware(DeviceCreationFlags.BgraSupport, out dev, out ctx, out videoOk))
                {
                    // Last-resort WARP (software rasterizer) — never has
                    // VideoSupport, but keeps something on screen if the
                    // hardware driver is unavailable.
                    videoOk = false;
                    if (!D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                            FeatureLevels, out dev, out _, out ctx).Success)
                    {
                        _logger.Error("GpuDevice: D3D11 device creation failed entirely");
                        return;
                    }
                }
                _logger.Warn("GpuDevice created without VideoSupport (zero-copy hw decode unavailable; will use software decode)");
            }
        }

        try
        {
            using var mt = dev.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);
        }
        catch (Exception ex)
        {
            _logger.Warn($"GpuDevice: ID3D11Multithread setup failed: {ex.Message}");
        }

        _adapterDescription = ReadAdapterDescription(dev);
        _device = dev;
        _context = ctx;
        _videoOk = videoOk;
        _logger.Info($"GpuDevice created (VideoSupport={videoOk}, flags={dev.CreationFlags}, adapter={_adapterDescription})");
    }

    // Hardware device creation on the given adapter (null = OS default). Does
    // NOT fall back to WARP — the caller chains fallbacks explicitly so a
    // failed discrete GPU still tries the default hardware adapter before the
    // software rasterizer.
    private static bool TryCreateHardware(
        DeviceCreationFlags flags,
        out ID3D11Device dev,
        out ID3D11DeviceContext ctx,
        out bool videoOk,
        IDXGIAdapter? preferred = null)
    {
        videoOk = (flags & DeviceCreationFlags.VideoSupport) != 0;
        return D3D11CreateDevice(preferred, DriverType.Hardware, flags, FeatureLevels, out dev, out _, out ctx).Success;
    }

    private static string? ReadAdapterDescription(ID3D11Device dev)
    {
        try
        {
            using var dxgiDevice = dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
            return adapter1.Description1.Description;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_createLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _context?.Dispose();
        _device?.Dispose();
    }
}
