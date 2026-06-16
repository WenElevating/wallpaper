using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperApp.Services.Logging;
using static Vortice.Direct3D11.D3D11;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using DxgiFormat = Vortice.DXGI.Format;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace WallpaperApp.Services.Playback;

// Renders decoded frames onto a wallpaper window using a D3D11 device + DXGI
// flip-model swap chain. Two present paths:
//
//  - GPU ZERO-COPY (shared GpuDevice + TryInitZeroCopy succeeded + GPU frames):
//    the decoded NV12 texture is CopySubresourceRegion'd onto a SHADER_RESOURCE
//    texture and blitted to the back buffer by a NV12->RGB pixel shader. One
//    GPU copy, ZERO CPU/system-RAM round-trip — this is the high-performance
//    path (the whole point of zero-copy: no av_hwframe_transfer_data, no
//    sws_scale, no Map/memcpy).
//
//  - CPU UPLOAD (fallback / software): the BGRA buffer is mapped into a staging
//    texture and copied to the back buffer. Used whenever zero-copy isn't set
//    up (no shared device, shader compile failed, or software decode).
//
// Threading: device/swap chain/textures are created lazily on the session's
// dedicated render thread (first Present) and used only there. With a shared
// GpuDevice the immediate context is multithread-protected, so the decode
// threads and this render thread can both drive it.
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
    private readonly GpuDevice? _gpu;

    private IDXGIFactory2? _dxgiFactory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private bool _ownsDevice;
    private IDXGISwapChain3? _swapChain;
    private ID3D11Texture2D? _stagingTexture; // CPU upload path
    private readonly ID3D11RenderTargetView?[] _rtvs = new ID3D11RenderTargetView?[2];

    private int _frameWidth;
    private int _frameHeight;
    private int _presentCount;
    private bool _disposed;

    // GPU zero-copy resources.
    private bool _zcInited;
    private int _nv12W, _nv12H;
    // Cached compiled bytecode so zero-copy resources can be recreated cheaply
    // after a device loss (sleep/resume, monitor change) without recompiling.
    private byte[]? _vsBc;
    private byte[]? _psBc;
    private ID3D11Texture2D? _nv12Tex;
    private ID3D11ShaderResourceView? _srvLuma;
    private ID3D11ShaderResourceView? _srvChroma;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;

    public DxgiRenderer(IntPtr hwnd, int width, int height, FileLogger logger, GpuDevice? gpu = null)
    {
        _hwnd = hwnd;
        _logger = logger;
        _gpu = gpu;
        // width/height here is the window size; the swap chain is built to the
        // video frame size and stretched to the window by DXGI.
    }

    // Pre-creates the zero-copy GPU resources (shaders, sampler, NV12 texture +
    // SRVs) for the given video size. Returns false if anything fails — the
    // caller then decodes to CPU BGRA and the renderer uses the upload path.
    public bool TryInitZeroCopy(int videoWidth, int videoHeight)
    {
        if (_disposed || _gpu is null || !_gpu.IsAvailable || videoWidth <= 0 || videoHeight <= 0) return false;
        try
        {
            _vsBc ??= Nv12Shader.CompileVertexShader();
            _psBc ??= Nv12Shader.CompilePixelShader();
            if (_vsBc is null || _psBc is null)
            {
                _logger.Warn("NV12 shader compile failed; using CPU upload");
                return false;
            }
            EnsureZeroCopyResources(_gpu.Device, videoWidth, videoHeight);
            _zcInited = true;
            _logger.Info($"DxgiRenderer zero-copy (NV12 shader) initialized for {videoWidth}x{videoHeight}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"TryInitZeroCopy failed: {ex.Message}; using CPU upload");
            ReleaseZeroCopy();
            return false;
        }
    }

    // (Re)creates the zero-copy resources on the given device. Cheap after a
    // device loss because the compiled shader bytecode is cached. The shaders /
    // sampler are device-independent; the NV12 texture tracks the frame size.
    private void EnsureZeroCopyResources(ID3D11Device dev, int w, int h)
    {
        if (_vs is null) _vs = dev.CreateVertexShader(_vsBc!);
        if (_ps is null) _ps = dev.CreatePixelShader(_psBc!);
        if (_sampler is null)
        {
            _sampler = dev.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });
        }
        EnsureNv12(dev, w, h);
    }

    private void EnsureNv12(ID3D11Device dev, int w, int h)
    {
        if (_nv12Tex is not null && _nv12W == w && _nv12H == h) return;
        ReleaseZeroCopyTextures();
        var desc = new Texture2DDescription
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormat.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        };
        _nv12Tex = dev.CreateTexture2D(desc);
        _srvLuma = dev.CreateShaderResourceView(_nv12Tex, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        });
        _srvChroma = dev.CreateShaderResourceView(_nv12Tex, new ShaderResourceViewDescription
        {
            Format = DxgiFormat.R8G8_UNorm,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
        });
        _nv12W = w;
        _nv12H = h;
    }

    public bool Present(FrameData frame)
    {
        if (_disposed) return false;
        try
        {
            return frame.IsGpu && _zcInited ? PresentGpu(frame) : PresentCpu(frame);
        }
        catch (Exception ex)
        {
            _logger.Warn($"DXGI Present error: {ex.Message}");
            return false;
        }
    }

    // GPU zero-copy path: blit the decoded NV12 texture to the back buffer via
    // the NV12->RGB shader. No CPU copy.
    private bool PresentGpu(FrameData frame)
    {
        var dbg = _presentCount < 3;
        if (dbg) _logger.Info($"zc enter: swapchain={_swapChain is not null} dev={_device is not null} zcInited={_zcInited} vs={_vs is not null} nv12={_nv12Tex is not null} nv12dims={_nv12W}x{_nv12H}/{frame.Width}x{frame.Height}");

        if (_swapChain is null && !EnsureResources(frame.Width, frame.Height))
            return false;
        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            if (!ResizeResources(frame.Width, frame.Height)) return false;
        }

        void Step(string name, Action a) { if (dbg) _logger.Info($"zc step: {name}"); a(); }

        // The pre-"copy" calls (render targets + borrowing the decoded texture)
        // are the ones we couldn't see before. Wrap them so the next run shows
        // exactly which throws.
        ID3D11Texture2D decodedTex;
        try
        {
            Step("ensure-rtv", () => EnsureRtv());
            // AddRef the decoded texture so it stays alive during Present.
            // FromPointer wraps the pointer for Dispose(); the finally block's
            // decodedTex.Dispose() releases the ref, balancing our AddRef.
            Marshal.AddRef(frame.Texture);
            decodedTex = MarshallingHelpers.FromPointer<ID3D11Texture2D>(frame.Texture);
        }
        catch (Exception ex)
        {
            _logger.Warn($"zc pre-copy threw: {ex.Message}");
            throw;
        }

        if (_device is null || _context is null || _swapChain is null) return false;

        // Re-create the zero-copy resources if they were released after a device
        // loss (shaders/sampler/NV12). Bytecode is cached, so this is cheap.
        if (_vs is null && _zcInited)
            EnsureZeroCopyResources(_device, frame.Width, frame.Height);

        if (_nv12W != frame.Width || _nv12H != frame.Height)
            EnsureNv12(_device, frame.Width, frame.Height);

        Result presentHr = default;
        try
        {
            // CopySubresourceRegion on video textures indexes the array slice and
            // copies the entire planar surface (both planes) in one call.
            Step("copy", () => _context.CopySubresourceRegion(_nv12Tex!, 0, 0, 0, 0, decodedTex, frame.TextureIndex, null));

            var rtv = _rtvs[(int)_swapChain.CurrentBackBufferIndex];
            if (rtv is null) return false;
            Step("om-rtv", () => _context.OMSetRenderTargets(rtv, null));
            Step("viewport", () => _context.RSSetViewports(new[] { new Viewport(0, 0, frame.Width, frame.Height) }));
            Step("topology", () => _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList));
            Step("vs", () => _context.VSSetShader(_vs));
            Step("ps", () => _context.PSSetShader(_ps));
            Step("srvs", () => _context.PSSetShaderResources(0, 2, new[] { _srvLuma!, _srvChroma! }));
            Step("samplers", () => _context.PSSetSamplers(0, 1, new[] { _sampler! }));
            Step("draw", () => _context.Draw(3, 0));

            // Flip-model swap chains require the back buffer to be UNBOUND from
            // the output-merger before Present (else DXGI_ERROR_INVALID_CALL).
            Step("unbind", () => _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null));

            if (dbg) _logger.Info("zc step: present");
            presentHr = _swapChain.Present(1, PresentFlags.None);
        }
        finally { decodedTex.Dispose(); }

        _presentCount++;
        if (_presentCount <= 3 || _presentCount % 300 == 0)
            _logger.Info($"DXGI GPU Present #{_presentCount}: frame {frame.Width}x{frame.Height}, presentHr=0x{presentHr.Code:X8}");

        if (presentHr.Failure)
        {
            _logger.Warn($"GPU Present failed: 0x{presentHr.Code:X8}; recreating device");
            ReleaseDevice();
        }
        return true;
    }

    // CPU upload path (fallback / software): copy BGRA into a staging texture,
    // then onto the back buffer.
    private unsafe bool PresentCpu(FrameData frame)
    {
        if (_swapChain is null && !EnsureResources(frame.Width, frame.Height))
        {
            _logger.Warn("DXGI resources could not be created");
            return false;
        }
        if (_context is null || _swapChain is null || _stagingTexture is null)
            return false;

        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            if (!ResizeResources(frame.Width, frame.Height)) return false;
        }

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

    // Lazily creates the RTV for the CURRENT back buffer. Flip-model swap chains
    // only expose buffer index 1 after the first Present, so we must NOT pre-
    // create both RTVs up front (GetBuffer(1) throws DXGI_ERROR_INVALID_CALL
    // before any Present). Creating per-index on demand matches the copy path's
    // access pattern and stays valid as the index flips.
    private void EnsureRtv()
    {
        if (_swapChain is null || _device is null) return;
        var idx = (int)_swapChain.CurrentBackBufferIndex;
        if (idx < 0 || idx >= _rtvs.Length || _rtvs[idx] is not null) return;
        using var buf = _swapChain.GetBuffer<ID3D11Texture2D>(idx);
        _rtvs[idx] = _device.CreateRenderTargetView(buf);
    }

    private bool EnsureResources(int frameWidth, int frameHeight)
    {
        if (_device is null)
        {
            if (_gpu is not null && _gpu.IsAvailable)
            {
                // Share the GPU device (same device as the decoder -> zero-copy).
                _device = _gpu.Device;
                _context = _gpu.Context;
                _ownsDevice = false;
            }
            else
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
                _ownsDevice = true;
            }

            var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            var adapter = dxgiDevice.GetParent<IDXGIAdapter>();
            _dxgiFactory = adapter.GetParent<IDXGIFactory2>();
            dxgiDevice.Dispose();
            adapter.Dispose();

            if (!CreateSwapChain(frameWidth, frameHeight))
                return false;

            _dxgiFactory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
            CreateStagingTexture(frameWidth, frameHeight);
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _logger.Info($"DXGI swap chain created on window {_hwnd} (frame {frameWidth}x{frameHeight})");
        }
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
        _rtvs[0]?.Dispose(); _rtvs[0] = null;
        _rtvs[1]?.Dispose(); _rtvs[1] = null;
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
        // Window size is handled by DXGI Scaling.Stretch; nothing to do here.
    }

    private void ReleaseZeroCopyTextures()
    {
        _srvLuma?.Dispose(); _srvLuma = null;
        _srvChroma?.Dispose(); _srvChroma = null;
        _nv12Tex?.Dispose(); _nv12Tex = null;
    }

    private void ReleaseZeroCopy()
    {
        // Releases device-bound resources. _zcInited (the mode) and the cached
        // bytecode (_vsBc/_psBc) survive so PresentGpu can re-init after a
        // device loss without recompiling.
        ReleaseZeroCopyTextures();
        _sampler?.Dispose(); _sampler = null;
        _vs?.Dispose(); _vs = null;
        _ps?.Dispose(); _ps = null;
    }

    private void ReleaseDevice()
    {
        foreach (var rtv in _rtvs) { rtv?.Dispose(); }
        _rtvs[0] = null; _rtvs[1] = null;
        ReleaseZeroCopy();
        _stagingTexture?.Dispose(); _stagingTexture = null;
        _swapChain?.Dispose(); _swapChain = null;
        if (_ownsDevice)
        {
            _context?.Dispose(); _context = null;
            _device?.Dispose();
        }
        _device = null;
        _context = null;
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
