using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using WallpaperApp.Interop;
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

    // DXGI occlusion: RegisterOcclusionStatusEvent signals this event when the
    // swap chain's window becomes occluded (fully covered by other windows).
    // We check it per-frame; when occluded we skip rendering entirely. The
    // _occlusionCookie is from RegisterOcclusionStatusEvent; the manual-reset
    // event is owned here and released with the device.
    private IntPtr _occlusionEvent = IntPtr.Zero;
    private int _occlusionCookie;
    private volatile bool _occluded;

    // DXGI_STATUS_OCCLUDED: a SUCCESS HRESULT the swap chain's Present returns
    // when its window is occluded. Vortice's Result.Failure is false for it, so
    // we compare the raw code explicitly.
    private const int DXGI_STATUS_OCCLUDED = unchecked((int)0x087A0001);

    private int _frameWidth;
    private int _frameHeight;
    private int _presentCount;
    private bool _disposed;

    // True when the static GPU pipeline state (viewport, topology, shaders,
    // sampler, SRVs) needs re-binding. These never change frame-to-frame, so we
    // only set them on init / device loss / resolution change — not every frame.
    private bool _pipelineDirty = true;

    // GPU zero-copy resources.
    private bool _zcInited;
    private int _nv12W, _nv12H;
    // Cached compiled bytecode so zero-copy resources can be recreated cheaply
    // after a device loss (sleep/resume, monitor change) without recompiling.
    private byte[]? _vsBc;
    private byte[]? _psBc;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11SamplerState? _sampler;

    // SRV cache for the D3D11VA decoded texture array. Instead of copying each
    // decoded frame into an intermediate NV12 texture (a full-frame GPU copy
    // every frame), we create SRVs that view a SINGLE slice of the decoded
    // texture array directly and let the shader sample it.
    //
    // Keyed by the decoded texture's native pointer (FFmpeg's D3D11VA pool
    // reuses one texture-array object, so this is stable per pool; a new pool
    // after seek/resize yields a different pointer and the cache rebuilds).
    // Value: per-slice SRV pairs (luma, chroma), indexed by slice number.
    private readonly Dictionary<IntPtr, (ID3D11ShaderResourceView luma, ID3D11ShaderResourceView chroma)[]> _decodedSrvCache = new();
    private IntPtr _lastDecodedTexture;

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

    // (Re)creates the zero-copy pipeline resources on the given device. Cheap
    // after a device loss because the compiled shader bytecode is cached.
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
        // Shader/sampler (re)created -> the pipeline state must be re-bound.
        _pipelineDirty = true;
    }

    // Returns the luma/chroma SRVs for a specific slice of the decoded D3D11VA
    // texture array, building+caching them on first sight of the texture. The
    // decoded texture is an NV12 texture array (ArraySize > 1); we view a single
    // slice with a Texture2DArray SRV (FirstArraySlice=slice, ArraySize=1),
    // which is how the shader samples the decoded frame with NO per-frame copy.
    private (ID3D11ShaderResourceView luma, ID3D11ShaderResourceView chroma) GetDecodedSliceSrv(ID3D11Texture2D decodedTex, int slice)
    {
        var ptr = Marshal.GetIUnknownForObject(decodedTex);
        try
        {
            // New texture pool (seek/resize/file change) -> drop old cache.
            if (ptr != _lastDecodedTexture)
            {
                ClearDecodedSrvCache();
                _lastDecodedTexture = ptr;
            }

            if (!_decodedSrvCache.TryGetValue(ptr, out var srvs))
            {
                srvs = BuildDecodedSrvCache(_device!, decodedTex);
                _decodedSrvCache[ptr] = srvs;
            }
            return (srvs[slice].luma, srvs[slice].chroma);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    // Pre-builds a per-slice SRV pair array over the whole decoded texture
    // array (one luma R8_UNorm + one chroma R8G8_UNorm per slice). NV12 is
    // planar: the luma SRV views plane 0 as R8, the chroma SRV views plane 1
    // (half-height) as R8G8 — identical to how the old intermediate _nv12Tex
    // was viewed, but applied directly to each array slice.
    private static (ID3D11ShaderResourceView luma, ID3D11ShaderResourceView chroma)[] BuildDecodedSrvCache(ID3D11Device dev, ID3D11Texture2D decodedTex)
    {
        var desc = decodedTex.Description;
        var srvs = new (ID3D11ShaderResourceView, ID3D11ShaderResourceView)[desc.ArraySize];
        for (var s = 0; s < desc.ArraySize; s++)
        {
            var luma = dev.CreateShaderResourceView(decodedTex, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    FirstArraySlice = s,
                    ArraySize = 1,
                },
            });
            var chroma = dev.CreateShaderResourceView(decodedTex, new ShaderResourceViewDescription
            {
                Format = DxgiFormat.R8G8_UNorm,
                ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayShaderResourceView
                {
                    MostDetailedMip = 0,
                    MipLevels = 1,
                    FirstArraySlice = s,
                    ArraySize = 1,
                },
            });
            srvs[s] = (luma, chroma);
        }
        return srvs;
    }

    private void ClearDecodedSrvCache()
    {
        foreach (var srvs in _decodedSrvCache.Values)
            foreach (var (luma, chroma) in srvs) { luma.Dispose(); chroma.Dispose(); }
        _decodedSrvCache.Clear();
    }

    public bool Present(FrameData frame)
    {
        if (_disposed) return false;

        // DXGI occlusion handling. RegisterOcclusionStatusEvent signals a
        // manual-reset event when the swap chain's occlusion status CHANGES.
        // We don't trust it for the new state — the source of truth is the
        // Present() return code (DXGI_STATUS_OCCLUDED, a success code Vortice's
        // .Failure won't catch, compared explicitly below).
        //
        // Strategy: while we believe the window is occluded AND no change event
        // has fired, skip rendering entirely (returns success so the session
        // doesn't treat it as device loss). The moment an event fires, we let a
        // real Present through; UpdateOcclusionState flips _occluded based on
        // the HRESULT, which either re-arms the skip (still occluded) or resumes
        // normal rendering (visible again).
        if (_occlusionEvent != IntPtr.Zero && _occluded)
        {
            var waited = NativeMethods.WaitForSingleObject(_occlusionEvent, 0);
            if (waited != NativeMethods.WAIT_OBJECT_0)
                return true; // still occluded, no change signal — skip this frame
            NativeMethods.ResetEvent(_occlusionEvent);
            // change signaled — fall through to a real Present to re-evaluate
        }

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
        if (dbg) _logger.Info($"zc enter: swapchain={_swapChain is not null} dev={_device is not null} zcInited={_zcInited} vs={_vs is not null} nv12dims={_nv12W}x{_nv12H}/{frame.Width}x{frame.Height}");

        if (_swapChain is null && !EnsureResources(frame.Width, frame.Height))
            return false;
        if (frame.Width != _frameWidth || frame.Height != _frameHeight)
        {
            if (!ResizeResources(frame.Width, frame.Height)) return false;
        }

        void Step(string name, Action a) { if (dbg) _logger.Info($"zc step: {name}"); a(); }

        // AddRef the decoded texture so the slice stays alive through Draw; the
        // finally below releases this ref. (FFmpeg's _heldHwFrame also pins the
        // slice until the next NextFrameAsync, but this is the renderer's own
        // guarantee independent of that timing.)
        ID3D11Texture2D decodedTex;
        try
        {
            Step("ensure-rtv", () => EnsureRtv());
            Marshal.AddRef(frame.Texture);
            decodedTex = MarshallingHelpers.FromPointer<ID3D11Texture2D>(frame.Texture);
        }
        catch (Exception ex)
        {
            _logger.Warn($"zc pre-step threw: {ex.Message}");
            throw;
        }

        if (_device is null || _context is null || _swapChain is null) return false;

        // Re-create the zero-copy pipeline (shaders/sampler) if released after a
        // device loss. Bytecode is cached, so this is cheap.
        if (_vs is null && _zcInited)
            EnsureZeroCopyResources(_device, frame.Width, frame.Height);

        _nv12W = frame.Width;
        _nv12H = frame.Height;

        Result presentHr = default;
        try
        {
            // Get the SRVs that view THIS frame's slice of the decoded texture
            // array directly — no intermediate copy. (SRVs are cached per texture
            // + slice, so this is a dictionary lookup, not a per-frame create.)
            var (srvLuma, srvChroma) = GetDecodedSliceSrv(decodedTex, frame.TextureIndex);

            var rtv = _rtvs[(int)_swapChain.CurrentBackBufferIndex];
            if (rtv is null) return false;
            // The RTV must be re-bound every frame: flip-model swap chains flip
            // the back-buffer index, so the active target changes each frame.
            Step("om-rtv", () => _context.OMSetRenderTargets(rtv, null));

            // The static pipeline state (viewport, topology, shaders, sampler) is
            // invariant across frames for a given resolution, so re-bind only on
            // init / device loss / resolution change. SRVs are NOT cached here —
            // the decoded slice changes every frame, so they're bound below.
            if (_pipelineDirty)
            {
                Step("viewport", () => _context.RSSetViewports(new[] { new Viewport(0, 0, frame.Width, frame.Height) }));
                Step("topology", () => _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList));
                Step("vs", () => _context.VSSetShader(_vs));
                Step("ps", () => _context.PSSetShader(_ps));
                Step("samplers", () => _context.PSSetSamplers(0, 1, new[] { _sampler! }));
                _pipelineDirty = false;
            }
            // SRVs point at the decoded frame's slice; re-bind every frame.
            Step("srvs", () => _context.PSSetShaderResources(0, 2, new[] { srvLuma, srvChroma }));
            Step("draw", () => _context.Draw(3, 0));

            // Flip-model swap chains require the back buffer to be UNBOUND from
            // the output-merger before Present (else DXGI_ERROR_INVALID_CALL).
            Step("unbind", () => _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null));

            if (dbg) _logger.Info("zc step: present");
            // Sync interval 0: don't block on VSync. The wallpaper isn't an
            // interactive scene, and its window goes through DWM composition,
            // which performs the final frame sync — so tearing can't reach the
            // desktop. VSync (interval 1) forced the GPU to stall every frame
            // waiting for the next 60Hz tick, which is wasted work for a 35-40fps
            // video and a real chunk of the steady-state GPU usage.
            presentHr = _swapChain.Present(0, PresentFlags.None);
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
        UpdateOcclusionState(presentHr.Code);
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

        // Sync interval 0 (no VSync stall) — see PresentGpu for rationale.
        var presentHr = _swapChain.Present(0, PresentFlags.None);
        _presentCount++;
        if (_presentCount <= 3 || _presentCount % 300 == 0)
            _logger.Info($"DXGI Present #{_presentCount}: frame {frame.Width}x{frame.Height}, presentHr=0x{presentHr.Code:X8}");

        if (presentHr.Failure)
        {
            _logger.Warn($"Present failed: 0x{presentHr.Code:X8}; recreating device");
            ReleaseDevice();
        }
        UpdateOcclusionState(presentHr.Code);
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

            RegisterOcclusion();

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
        // Resolution changed -> viewport and back-buffer RTVs changed.
        _pipelineDirty = true;
        return true;
    }

    public void Resize(int width, int height)
    {
        // Window size is handled by DXGI Scaling.Stretch; nothing to do here.
    }

    private void ReleaseZeroCopyTextures()
    {
        // The decoded-slice SRV cache is the only per-decode texture state now
        // (the old intermediate _nv12Tex is gone — slices are sampled directly).
        ClearDecodedSrvCache();
        _lastDecodedTexture = IntPtr.Zero;
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
        UnregisterOcclusion();
        _dxgiFactory?.Dispose(); _dxgiFactory = null;
        _frameWidth = 0;
        _frameHeight = 0;
        // Everything is torn down; the next Present must re-bind the whole
        // pipeline from scratch once resources are recreated.
        _pipelineDirty = true;
    }

    // Registers a manual-reset event with DXGI; the OS signals it whenever the
    // swap chain's occlusion status changes. We poll it per-frame in Present().
    // Non-fatal: if registration fails we simply have no occlusion awareness
    // and keep rendering unconditionally (the Z-order detector is the backup).
    private void RegisterOcclusion()
    {
        if (_dxgiFactory is null) return;
        try
        {
            UnregisterOcclusion();
            _occlusionEvent = NativeMethods.CreateEventW(IntPtr.Zero, true, false, null);
            if (_occlusionEvent == IntPtr.Zero) return;
            // Vortice surfaces RegisterOcclusionStatusEvent as returning the
            // registration cookie (an int; 0 means registration failed). The
            // native HRESULT is dropped, but a zero cookie reliably indicates
            // failure per the DXGI docs.
            _occlusionCookie = _dxgiFactory.RegisterOcclusionStatusEvent(_occlusionEvent);
            if (_occlusionCookie == 0)
            {
                _logger.Warn("RegisterOcclusionStatusEvent returned 0; no DXGI occlusion awareness");
                NativeMethods.CloseHandle(_occlusionEvent);
                _occlusionEvent = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"RegisterOcclusion failed: {ex.Message}");
        }
    }

    private void UnregisterOcclusion()
    {
        if (_occlusionEvent != IntPtr.Zero && _dxgiFactory is not null)
        {
            try { _dxgiFactory.UnregisterOcclusionStatus(_occlusionCookie); } catch { }
        }
        if (_occlusionEvent != IntPtr.Zero)
        {
            try { NativeMethods.CloseHandle(_occlusionEvent); } catch { }
        }
        _occlusionEvent = IntPtr.Zero;
        _occlusionCookie = 0;
        _occluded = false;
    }

    // True when DXGI reports the swap chain's window is occluded (fully covered
    // by other windows). Read by PlaybackSession to skip decoding+rendering.
    public bool IsOccluded => _occluded;

    // Interprets the Present() HRESULT to update the occlusion flag. Called after
    // every real Present. DXGI_STATUS_OCCLUDED (a success code) means the window
    // is covered; anything else means visible.
    private void UpdateOcclusionState(int presentCode)
    {
        var wasOccluded = _occluded;
        _occluded = presentCode == DXGI_STATUS_OCCLUDED;
        if (_occluded != wasOccluded)
        {
            _logger.Info(_occluded
                ? "DXGI occlusion: swap chain window occluded, will skip render until visible"
                : "DXGI occlusion: window visible again, resuming render");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseDevice();
    }
}
