using Vortice.Direct3D;
using Vortice.Direct3D11;
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
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private bool _disposed;

    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(GpuDevice));
    public ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(GpuDevice));
    public IntPtr DevicePointer => _device?.NativePointer ?? IntPtr.Zero;
    public bool IsAvailable => _device != null;
    // True only when the device supports D3D11VA decode (VideoSupport succeeded).
    public bool SupportsVideo { get; }

    public GpuDevice(FileLogger logger)
    {
        _logger = logger;

        if (!TryCreate(DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, out var dev, out var ctx, out var videoOk))
        {
            // VideoSupport is rare to fail, but if it does, fall back to a plain
            // device so the renderer's CPU-upload path still works (decode goes sw).
            if (!TryCreate(DeviceCreationFlags.BgraSupport, out dev, out ctx, out videoOk))
            {
                logger.Error("GpuDevice: D3D11 device creation failed entirely");
                return;
            }
            logger.Warn("GpuDevice created without VideoSupport (zero-copy hw decode unavailable; will use software decode)");
        }

        try
        {
            using var mt = dev.QueryInterface<ID3D11Multithread>();
            mt.SetMultithreadProtected(true);
        }
        catch (Exception ex)
        {
            logger.Warn($"GpuDevice: ID3D11Multithread setup failed: {ex.Message}");
        }

        _device = dev;
        _context = ctx;
        SupportsVideo = videoOk;
        logger.Info($"GpuDevice created (VideoSupport={SupportsVideo}, flags={dev.CreationFlags})");
    }

    private static bool TryCreate(DeviceCreationFlags flags, out ID3D11Device dev, out ID3D11DeviceContext ctx, out bool videoOk)
    {
        videoOk = (flags & DeviceCreationFlags.VideoSupport) != 0;
        if (D3D11CreateDevice(null, DriverType.Hardware, flags, FeatureLevels, out dev, out _, out ctx).Success)
            return true;
        // Last-resort WARP (software rasterizer) — never has VideoSupport, but
        // keeps something on screen if the hardware driver is unavailable.
        videoOk = false;
        return D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, FeatureLevels, out dev, out _, out ctx).Success;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context?.Dispose();
        _device?.Dispose();
    }
}
