using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class D2dRenderer : IFrameRenderer
{
    private readonly FileLogger _logger;
    private readonly IntPtr _hwnd;

    private IntPtr _factory;
    private IntPtr _renderTarget;
    private IntPtr _bitmap;
    private int _width;
    private int _height;
    private int _stride;
    private bool _disposed;

    public D2dRenderer(IntPtr hwnd, int width, int height, FileLogger logger)
    {
        _hwnd = hwnd;
        _width = width;
        _height = height;
        _stride = width * 4;
        _logger = logger;

        var hr = D2D1.D2D1CreateFactory(
            D2D1.D2D1_FACTORY_TYPE_SINGLE_THREADED,
            D2D1.IID_ID2D1Factory,
            out _factory);

        if (hr != D2D1.S_OK)
        {
            _logger.Error($"D2D1CreateFactory failed: 0x{hr:X8}");
            return;
        }

        CreateRenderTarget(hwnd);
    }

    public bool Present(FrameData frame)
    {
        if (_disposed || _renderTarget == IntPtr.Zero) return false;

        try
        {
            if (frame.Width != _width || frame.Height != _height)
                Resize(frame.Width, frame.Height);

            var hr = D2D1.CopyFromMemory(_bitmap, IntPtr.Zero, frame.Buffer, _stride);
            if (hr != D2D1.S_OK)
            {
                _logger.Warn($"CopyFromMemory failed: 0x{hr:X8}");
                return false;
            }

            hr = D2D1.BeginDraw(_renderTarget);
            if (hr != D2D1.S_OK) return false;

            var dstRect = D2D1.D2D1_RECT_F.Full(_width, _height);
            var srcRect = D2D1.D2D1_RECT_F.Full(_width, _height);
            hr = D2D1.DrawBitmap(_renderTarget, _bitmap, ref dstRect, 1.0f, 0, ref srcRect);
            if (hr != D2D1.S_OK) return false;

            hr = D2D1.EndDraw(_renderTarget);
            if (hr == D2D1.D2DERR_RECREATE_TARGET)
            {
                RecreateRenderTarget();
                return true;
            }

            return hr == D2D1.S_OK;
        }
        catch (Exception ex)
        {
            _logger.Warn($"D2D Present error: {ex.Message}");
            return false;
        }
    }

    public void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
        RecreateRenderTarget();
    }

    private void CreateRenderTarget(IntPtr hwnd)
    {
        ReleaseBitmap();
        ReleaseRenderTarget();

        if (_factory == IntPtr.Zero) return;

        var rtProps = D2D1.D2D1_RENDER_TARGET_PROPERTIES.Default();
        var hwndProps = D2D1.D2D1_HWND_RENDER_TARGET_PROPERTIES.ForHwnd(
            hwnd, (uint)_width, (uint)_height);

        var hr = D2D1.CreateHwndRenderTarget(
            _factory, ref rtProps, ref hwndProps, out _renderTarget);

        if (hr != D2D1.S_OK || _renderTarget == IntPtr.Zero)
        {
            _logger.Error($"CreateHwndRenderTarget failed: 0x{hr:X8}");
            return;
        }

        var size = new D2D1.D2D1_SIZE_U { Width = (uint)_width, Height = (uint)_height };
        var bmpProps = D2D1.D2D1_BITMAP_PROPERTIES.Default();

        hr = D2D1.CreateBitmap(_renderTarget, size, IntPtr.Zero, _stride, ref bmpProps, out _bitmap);
        if (hr != D2D1.S_OK || _bitmap == IntPtr.Zero)
            _logger.Error($"CreateBitmap failed: 0x{hr:X8}");
    }

    private void RecreateRenderTarget()
    {
        CreateRenderTarget(_hwnd);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseBitmap();
        ReleaseRenderTarget();
        if (_factory != IntPtr.Zero)
        {
            D2D1.Release(_factory);
            _factory = IntPtr.Zero;
        }
    }
}
