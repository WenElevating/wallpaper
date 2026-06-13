using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Renders decoded frames onto a wallpaper window using Direct2D.
//
// IMPORTANT — threading: the factory is SINGLE-THREADED, so every D2D object
// (factory, render target, bitmap) MUST be created AND used from the same
// thread. Resources are therefore created lazily on the first Present() call
// (which runs on the session's dedicated render thread), never in the
// constructor. The render target size tracks the window; the bitmap size
// tracks the video frame, so DrawBitmap scales the source to fit.
public sealed class D2dRenderer : IFrameRenderer
{
    private readonly FileLogger _logger;
    private readonly IntPtr _hwnd;

    private IntPtr _factory;
    private IntPtr _renderTarget;
    private IntPtr _bitmap;

    // Render target (window) dimensions — fixed to the window size.
    private readonly int _targetWidth;
    private readonly int _targetHeight;

    // Current bitmap (frame) dimensions — recreated when the frame size changes.
    private int _frameWidth;
    private int _frameHeight;
    private int _frameStride;
    private int _presentCount;
    private bool _disposed;

    public D2dRenderer(IntPtr hwnd, int width, int height, FileLogger logger)
    {
        _hwnd = hwnd;
        _targetWidth = width > 0 ? width : 1920;
        _targetHeight = height > 0 ? height : 1080;
        _logger = logger;
        // Device resources are created lazily in EnsureResources() on the
        // render thread to satisfy the single-threaded factory contract.
    }

    public bool Present(FrameData frame)
    {
        if (_disposed)
            return false;

        try
        {
            if (_factory == IntPtr.Zero && !EnsureResources())
            {
                _logger.Warn("D2D resources could not be created");
                return false;
            }

            // Recreate the bitmap when the source frame size changes.
            if (frame.Width != _frameWidth || frame.Height != _frameHeight)
            {
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _frameStride = frame.Stride;
                if (!RecreateBitmap())
                    return false;
            }

            var copyHr = D2D1.CopyFromMemory(_bitmap, IntPtr.Zero, frame.Buffer, frame.Stride);
            if (copyHr != D2D1.S_OK)
            {
                _logger.Warn($"CopyFromMemory failed: 0x{copyHr:X8}");
                return false;
            }

            D2D1.BeginDraw(_renderTarget);

            // Destination = full render target (window); source = full bitmap
            // (frame). D2D scales the frame to fit the window.
            var dstRect = D2D1.D2D1_RECT_F.Full(_targetWidth, _targetHeight);
            var srcRect = D2D1.D2D1_RECT_F.Full(_frameWidth, _frameHeight);
            D2D1.DrawBitmap(_renderTarget, _bitmap, ref dstRect, 1.0f, 1, ref srcRect);

            var hr = D2D1.EndDraw(_renderTarget);

            _presentCount++;
            if (_presentCount <= 3 || _presentCount % 300 == 0)
                _logger.Info($"Present #{_presentCount}: frame {frame.Width}x{frame.Height} stride {frame.Stride}, copyHr=0x{copyHr:X8}, endHr=0x{hr:X8}");

            if (hr == D2D1.D2DERR_RECREATE_TARGET)
            {
                ReleaseDevice();
                return true;
            }

            if (hr != D2D1.S_OK)
            {
                _logger.Warn($"EndDraw failed: 0x{hr:X8}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"D2D Present error: {ex.Message}");
            return false;
        }
    }

    // Creates the factory + HWND render target. The bitmap is created on demand
    // once the first frame reveals its size. Returns false on failure.
    private bool EnsureResources()
    {
        if (_factory != IntPtr.Zero)
            return true;

        var hr = D2D1.D2D1CreateFactory(
            D2D1.D2D1_FACTORY_TYPE_SINGLE_THREADED,
            D2D1.IID_ID2D1Factory,
            IntPtr.Zero,
            out _factory);

        if (hr != D2D1.S_OK || _factory == IntPtr.Zero)
        {
            _logger.Error($"D2D1CreateFactory failed: 0x{hr:X8}");
            _factory = IntPtr.Zero;
            return false;
        }

        var rtProps = D2D1.D2D1_RENDER_TARGET_PROPERTIES.Default();
        var hwndProps = D2D1.D2D1_HWND_RENDER_TARGET_PROPERTIES.ForHwnd(
            _hwnd, (uint)_targetWidth, (uint)_targetHeight);

        hr = D2D1.CreateHwndRenderTarget(_factory, ref rtProps, ref hwndProps, out _renderTarget);
        if (hr != D2D1.S_OK || _renderTarget == IntPtr.Zero)
        {
            _logger.Error($"CreateHwndRenderTarget failed: 0x{hr:X8}");
            ReleaseDevice();
            return false;
        }

        _logger.Info($"D2D render target created on window {_hwnd} ({_targetWidth}x{_targetHeight})");
        return true;
    }

    private bool RecreateBitmap()
    {
        ReleaseBitmap();
        if (_renderTarget == IntPtr.Zero)
            return false;

        var size = new D2D1.D2D1_SIZE_U { Width = (uint)_frameWidth, Height = (uint)_frameHeight };
        var bmpProps = D2D1.D2D1_BITMAP_PROPERTIES.Default();

        var hr = D2D1.CreateBitmap(_renderTarget, size, IntPtr.Zero, _frameStride, ref bmpProps, out _bitmap);
        if (hr != D2D1.S_OK || _bitmap == IntPtr.Zero)
        {
            _logger.Error($"CreateBitmap failed: 0x{hr:X8} ({_frameWidth}x{_frameHeight})");
            _bitmap = IntPtr.Zero;
            return false;
        }
        return true;
    }

    public void Resize(int width, int height)
    {
        // Window-driven resize is handled by recreating the window surface in
        // PlaybackManager; the render target is created to match the window.
        // No-op here to avoid touching device state from the wrong thread.
    }

    private void ReleaseBitmap()
    {
        if (_bitmap != IntPtr.Zero)
        {
            D2D1.Release(_bitmap);
            _bitmap = IntPtr.Zero;
        }
    }

    private void ReleaseRenderTarget()
    {
        if (_renderTarget != IntPtr.Zero)
        {
            D2D1.Release(_renderTarget);
            _renderTarget = IntPtr.Zero;
        }
    }

    private void ReleaseDevice()
    {
        ReleaseBitmap();
        ReleaseRenderTarget();
        if (_factory != IntPtr.Zero)
        {
            D2D1.Release(_factory);
            _factory = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDevice();
    }
}
