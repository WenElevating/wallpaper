using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Creates FFmpeg D3D11VA hardware device contexts for decoders that want them.
// Hardware decode keeps decoded reference frames in GPU memory (the whole
// point — they don't live in system RAM).
//
// Two ways to obtain a device:
//   - CreateNew(): a FRESH device the caller owns. Use this for persistent
//     decoders. Sharing one D3D11 device across CONCURRENT decoders can make
//     FFmpeg's get_format silently fall back to SOFTWARE decode (reference
//     frames back in system RAM + a busy CPU core). Per-session devices avoid
//     that. The probe (tests/WallpaperApp.HwDecodeProbe) confirms it.
//   - Acquire(): a ref on a single shared device (kept for the shared-mode
//     probe comparison). Prefer CreateNew in production.
//
// Failure is non-fatal: if the GPU/driver can't provide D3D11VA, both return
// IntPtr.Zero and the caller transparently falls back to software decode
// (FfmpegBackend handles that).
public static class HwDecodeDevice
{
    private static readonly Lazy<IntPtr> _shared = new(CreateRaw, LazyThreadSafetyMode.ExecutionAndPublication);

    // Set at startup (App) for status logging; null => silent.
    public static FileLogger? Logger { get; set; }

    // Creates a FRESH D3D11VA device context the caller owns and MUST release
    // (FfmpegBackend assigns it to the codec context, which releases it via
    // avcodec_free_context). Returns IntPtr.Zero if unavailable.
    public static IntPtr CreateNew() => CreateRaw();

    // Wraps an EXISTING ID3D11Device as a D3D11VA device context the caller owns
    // (released via the codec context). Use this for ZERO-COPY rendering: by
    // decoding on the SAME device as the renderer's swap chain, the decoder's
    // output NV12 texture can be blitted to the screen via a shader with no CPU
    // round-trip. The caller passes GpuDevice.DevicePointer. Returns IntPtr.Zero
    // if the device can't be wrapped (caller falls back to software decode).
    public static IntPtr CreateForDevice(IntPtr d3d11Device)
    {
        if (d3d11Device == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            var type = FfmpegNative.av_hwdevice_find_type_by_name("d3d11va");
            if (type < 0) type = FfmpegNative.AV_HWDEVICE_TYPE_D3D11VA;

            var bufRef = FfmpegNative.av_hwdevice_ctx_alloc(type); // AVBufferRef*
            if (bufRef == IntPtr.Zero) return IntPtr.Zero;

            // AVBufferRef.data -> AVHWDeviceContext.hwctx -> AVD3D11VADeviceContext.device
            var hwDeviceCtx = Marshal.ReadIntPtr(bufRef, FfmpegOffsets.AvBufferRefDataOffset);
            var hwctx = Marshal.ReadIntPtr(hwDeviceCtx, FfmpegOffsets.AvHwDeviceCtxHwctxOffset);
            Marshal.WriteIntPtr(hwctx, FfmpegOffsets.AvD3D11VaDeviceOffset, d3d11Device);

            // CRITICAL: FFmpeg's AVD3D11VADeviceContext docs state:
            //   "Deallocating the AVHWDeviceContext will always release this
            //    interface, and it does not matter whether it was user-allocated."
            //
            // We must AddRef the device here to create a reference that FFmpeg
            // will Release on uninit (av_buffer_unref → d3d11va_device_uninit).
            // Without this, each init/uninit cycle drops the device's COM ref
            // count, and after enough cycles the device is destroyed. All
            // subsequent calls receive a dangling pointer → AccessViolation.
            Marshal.AddRef(d3d11Device);

            // init fills device_context / video_device / video_context / lock from device.
            var hr = FfmpegNative.av_hwdevice_ctx_init(bufRef);
            if (hr < 0)
            {
                // Init failed. In our bundled FFmpeg build, a failed
                // av_hwdevice_ctx_init does NOT run d3d11va_device_uninit
                // (verified empirically), so the ref we added above would
                // leak. Release it here to keep the count balanced.
                Marshal.Release(d3d11Device);
                Logger?.Warn($"av_hwdevice_ctx_init failed for shared device: 0x{hr:X8}");
                FfmpegNative.av_buffer_unref(ref bufRef);
                return IntPtr.Zero;
            }
            return bufRef;
        }
        catch (Exception ex)
        {
            Logger?.Warn($"CreateForDevice failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    // Returns a ref on a single shared device (caller releases via the codec
    // context). Kept for the shared-mode probe; production uses CreateNew.
    public static IntPtr Acquire() => _shared.Value == IntPtr.Zero ? IntPtr.Zero : FfmpegNative.av_buffer_ref(_shared.Value);

    // Releases the shared device (if it was created) on shutdown.
    public static void Shutdown()
    {
        if (!_shared.IsValueCreated) return;
        var ctx = _shared.Value;
        if (ctx != IntPtr.Zero)
            FfmpegNative.av_buffer_unref(ref ctx);
    }

    private static IntPtr CreateRaw()
    {
        try
        {
            var type = FfmpegNative.av_hwdevice_find_type_by_name("d3d11va");
            if (type < 0) type = FfmpegNative.AV_HWDEVICE_TYPE_D3D11VA;

            var ctx = IntPtr.Zero;
            var hr = FfmpegNative.av_hwdevice_ctx_create(ref ctx, type, IntPtr.Zero, IntPtr.Zero, 0);
            if (hr < 0 || ctx == IntPtr.Zero)
            {
                Logger?.Warn($"D3D11VA hw device unavailable (av_hwdevice_ctx_create=0x{hr:X8}); using software decode");
                return IntPtr.Zero;
            }

            return ctx;
        }
        catch (Exception ex)
        {
            Logger?.Warn($"D3D11VA init failed ({ex.Message}); using software decode");
            return IntPtr.Zero;
        }
    }
}
