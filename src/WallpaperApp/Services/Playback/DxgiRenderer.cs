using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WallpaperApp.Services.Logging;
using static Vortice.Direct3D11.D3D11;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiFormat = Vortice.DXGI.Format;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace WallpaperApp.Services.Playback;

// Renders decoded BGRA frames to a wallpaper window using a D3D11 device + DXGI
// flip-model swap chain. This is the production wallpaper-engine technique.
//
// Why not an ID2D1HwndRenderTarget: its present lands on a DWM redirection
// surface that is never recomposited for a WS_CHILD of the (cross-process)
// desktop WorkerW, so it shows nothing. A DXGI flip-model swap chain hands the
// back buffer straight to DWM for composition every frame, which displays
// reliably on the WorkerW child.
//
// Scaling: the swap chain is sized to the VIDEO frame and created with
// Scaling.Stretch, so DXGI stretches each frame to the window on present — no
// shader or Direct2D needed. Each frame is uploaded 1:1 to a frame-sized
// dynamic texture and copied into the current back buffer.
//
// Threading: the D3D11 device, swap chain, and textures are created lazily on
// the session's dedicated render thread (first Present) and used only there.
public sealed class DxgiRenderer : IFrameRenderer
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    private readonly FileLogger _logger;
    private readonly IntPtr _hwnd;

    private IDXGIFactory2? _dxgiFactory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain3? _swapChain;
    private ID3D11Texture2D? _stagingTexture;

    private int _frameWidth;
    private int _frameHeight;
    private int _presentCount;
    private bool _disposed;

    public DxgiRenderer(IntPtr hwnd, int width, int height, FileLogger logger)
    {
        _hwnd = hwnd;
        _logger = logger;
        // width/height here is the window size; the swap chain is built to the
        // video frame size and stretched to the window by DXGI.
    }

    public unsafe bool Present(FrameData frame)
    {
        if (_disposed) return false;

        try
        {
            if (_swapChain == null && !EnsureResources(frame.Width, frame.Height))
            {
                _logger.Warn("DXGI resources could not be created");
                return false;
            }
            if (_context == null || _swapChain == null || _stagingTexture == null)
                return false;

            // Recreate the staging texture + swap chain when the frame size changes.
            if (frame.Width != _frameWidth || frame.Height != _frameHeight)
            {
                if (!ResizeResources(frame.Width, frame.Height))
                    return false;
            }

            // Upload the frame into the staging texture (row by row, respecting
            // both the source stride and the GPU row pitch).
            var mapped = _context.Map(_stagingTexture, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                int rowBytes = frame.Width * 4;
                byte* src = (byte*)frame.Buffer;
                byte* dst = (byte*)mapped.DataPointer;
                int srcPitch = frame.Stride;
                int dstPitch = (int)mapped.RowPitch;
                for (int y = 0; y < frame.Height; y++)
                {
                    Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
                    src += srcPitch;
                    dst += dstPitch;
                }
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }

            // Copy the staging texture into the swap chain's current back buffer
            // (1:1, same size) and present. DXGI Stretch maps the frame-sized
            // swap chain onto the window-sized HWND.
            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(_swapChain.CurrentBackBufferIndex);
            _context.CopySubresourceRegion(backBuffer, 0, 0, 0, 0, _stagingTexture, 0, null);

            var presentHr = _swapChain.Present(1, PresentFlags.None);
            _presentCount++;
            if (_presentCount <= 3 || _presentCount % 300 == 0)
                _logger.Info($"DXGI Present #{_presentCount}: frame {frame.Width}x{frame.Height}, presentHr=0x{presentHr.Code:X8}");

            if (presentHr.Failure)
            {
                _logger.Warn($"Present failed: 0x{presentHr.Code:X8}; recreating device");
                ReleaseDevice();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"DXGI Present error: {ex.Message}");
            return false;
        }
    }

    private bool EnsureResources(int frameWidth, int frameHeight)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        if (D3D11CreateDevice(null, DriverType.Hardware, flags, FeatureLevels,
                out var dev, out _, out var ctx).Failure)
        {
            _logger.Warn("Hardware D3D11 device failed; trying WARP");
            if (D3D11CreateDevice(null, DriverType.Warp, flags, FeatureLevels,
                    out dev, out _, out ctx).Failure)
            {
                _logger.Error("D3D11CreateDevice failed (hardware and WARP)");
                return false;
            }
        }
        _device = dev;
        _context = ctx;

        var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
        _dxgiFactory = adapter.GetParent<IDXGIFactory2>();

        if (!CreateSwapChain(frameWidth, frameHeight))
        {
            dxgiDevice.Dispose();
            adapter.Dispose();
            return false;
        }

        _dxgiFactory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
        CreateStagingTexture(frameWidth, frameHeight);

        dxgiDevice.Dispose();
        adapter.Dispose();

        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        _logger.Info($"DXGI swap chain created on window {_hwnd} (frame {frameWidth}x{frameHeight})");
        return true;
    }

    private bool CreateSwapChain(int width, int height)
    {
        var desc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        };

        var swapChain1 = _dxgiFactory!.CreateSwapChainForHwnd(_device, _hwnd, desc, null, null);
        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        swapChain1.Dispose();
        return true;
    }

    private void CreateStagingTexture(int width, int height)
    {
        _stagingTexture?.Dispose();
        var texDesc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        _stagingTexture = _device!.CreateTexture2D(texDesc);
    }

    private bool ResizeResources(int frameWidth, int frameHeight)
    {
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _swapChain?.Dispose();
        _swapChain = null;

        if (!CreateSwapChain(frameWidth, frameHeight))
            return false;
        CreateStagingTexture(frameWidth, frameHeight);
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        return true;
    }

    public void Resize(int width, int height)
    {
        // The window size is handled by DXGI Scaling.Stretch; nothing to do here.
    }

    private void ReleaseDevice()
    {
        _stagingTexture?.Dispose(); _stagingTexture = null;
        _swapChain?.Dispose(); _swapChain = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _dxgiFactory?.Dispose(); _dxgiFactory = null;
        _frameWidth = 0;
        _frameHeight = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDevice();
    }
}
