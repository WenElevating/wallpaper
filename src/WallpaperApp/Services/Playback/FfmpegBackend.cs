using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class FfmpegBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;

    // FFmpeg contexts (native pointers)
    private IntPtr _fmtCtx;
    private IntPtr _codecCtx;
    private IntPtr _swsCtx;
    private int _swsSrcFormat = -1;
    private int _swsSrcRange = -1;
    private int _swsColorspace = -1;
    private IntPtr _avFrame;
    private IntPtr _avPacket;

    // Stream info
    private int _videoStreamIndex = -1;
    private int _width;
    private int _height;
    private int _stride;
    private int _frameSize;

    // Double buffers for sws_scale output
    private IntPtr _bufferA;
    private IntPtr _bufferB;
    private bool _useBufferA;

    // FPS / timing
    private AVRational _timeBase;
    private long _durationUs;

    // State
    private bool _isOpen;
    private bool _disposed;

    // Hardware decode (optional). When provided, OpenAsync assigns a D3D11VA
    // device context to the codec so decoded reference frames live in GPU
    // memory instead of system RAM. The provider returns a fresh AVBufferRef*
    // each call (or IntPtr.Zero to signal "use software").
    private readonly Func<IntPtr>? _hwDeviceProvider;
    private IntPtr _hwDeviceRef;   // ref assigned to the codec ctx; released by avcodec_free_context
    private IntPtr _swFrame;       // lazily-allocated destination for av_hwframe_transfer_data
    private bool _useHardware;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    // True only if at least one frame actually came back from the hardware
    // decoder (AV_PIX_FMT_D3D11). _useHardware is "hw was attempted"; this is
    // "hw is really in use" — the decoder can silently fall back to software.
    private bool _decodedHw;
    public bool IsHardwareDecoding => _decodedHw;
    private bool _warnedSwFallback;

    // When true and decoding on the shared GPU device, hw frames are handed to
    // the renderer as D3D11 NV12 textures (zero-copy) instead of being
    // transferred to CPU + sws_scale'd to BGRA. Set by PlaybackSession after the
    // renderer confirms it can do the NV12 shader path; cleared to fall back to
    // the CPU color pipeline.
    public bool PreferZeroCopy { get; set; }
    // True while a GPU frame is held out for the renderer (av_frame_unref'd on
    // the next NextFrameAsync call, since Present is synchronous).
    private bool _heldHwFrame;
    public TimeSpan Duration => TimeSpan.FromTicks(_durationUs * 10);
    public int VideoWidth => _width;
    public int VideoHeight => _height;
    public TimeSpan Position { get; private set; }

    public event EventHandler? EndOfStream;

    public FfmpegBackend(FileLogger logger, Func<IntPtr>? hwDeviceProvider = null)
    {
        _logger = logger;
        _hwDeviceProvider = hwDeviceProvider;
    }

    public Task<bool> OpenAsync(string filePath, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Close();

                _fmtCtx = IntPtr.Zero;
                var ret = FfmpegNative.avformat_open_input(ref _fmtCtx, filePath, IntPtr.Zero, IntPtr.Zero);
                if (ret < 0)
                    return Fail($"avformat_open_input failed: {filePath}");

                ret = FfmpegNative.avformat_find_stream_info(_fmtCtx, IntPtr.Zero);
                if (ret < 0)
                    return Fail($"avformat_find_stream_info failed: {filePath}");

                IntPtr decoder = IntPtr.Zero;
                var streamIdx = FfmpegNative.av_find_best_stream(
                    _fmtCtx, FfmpegNative.AVMEDIA_TYPE_VIDEO, -1, -1, ref decoder, 0);

                if (streamIdx < 0 || decoder == IntPtr.Zero)
                    return Fail($"No video stream found: {filePath}");

                _videoStreamIndex = streamIdx;

                // Read AVFormatContext->streams[_videoStreamIndex]->codecpar via offsets
                var streamsPtr = Marshal.ReadIntPtr(_fmtCtx, FfmpegOffsets.StreamsOffset);
                var streamPtr = Marshal.ReadIntPtr(streamsPtr + _videoStreamIndex * IntPtr.Size);
                var codecPar = Marshal.ReadIntPtr(streamPtr, FfmpegOffsets.CodecparOffset);

                _width = Marshal.ReadInt32(codecPar, FfmpegOffsets.WidthOffset);
                _height = Marshal.ReadInt32(codecPar, FfmpegOffsets.HeightOffset);
                if (_width <= 0 || _height <= 0)
                    return Fail($"Invalid dimensions: {_width}x{_height}");

                _stride = _width * 4;
                _frameSize = _height * _stride;

                _codecCtx = FfmpegNative.avcodec_alloc_context3(decoder);
                FfmpegNative.avcodec_parameters_to_context(_codecCtx, codecPar);

                if (!OpenCodecWithFallback(decoder, codecPar))
                    return Fail($"avcodec_open2 failed");

                // NOTE: the swscale context is created lazily in NextFrameAsync
                // using the frame's ACTUAL pixel format, instead of a hardcoded
                // AV_PIX_FMT_YUV420P. The decoded format can be YUV420P, a
                // 10-bit variant (YUV420P10LE), full-range YUVJ420P, NV12, etc.;
                // assuming YUV420P produces wrong colors and slow conversions.

                _avFrame = FfmpegNative.av_frame_alloc();
                _avPacket = FfmpegNative.av_packet_alloc();

                // NOTE: the CPU double-buffers (_bufferA/__bufferB) are allocated
                // LAZILY by EnsureCpuBuffers() on first use of the sws_scale path,
                // NOT here. In zero-copy mode (the common case) the sws path is
                // never taken, so pre-allocating two frame-sized BGRA buffers here
                // would waste ~16MB at 1080p / ~66MB at 4K for nothing.

                _timeBase = ReadTimeBase(streamPtr);
                var streamDuration = Marshal.ReadInt64(streamPtr, FfmpegOffsets.StreamDurationOffset);
                _durationUs = streamDuration * _timeBase.Num * 1_000_000 / _timeBase.Den;

                _isOpen = true;
                _logger.Info($"Opened: {filePath} ({_width}x{_height})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Open failed: {filePath}", ex);
                return false;
            }

            bool Fail(string msg) { _logger.Error(msg); return false; }
        }, ct);
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        IsPlaying = true; IsPaused = false; return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        IsPaused = true; return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        IsPaused = false; return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsPlaying = false; IsPaused = false; Position = TimeSpan.Zero; _useBufferA = false;
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        Position = position;
        if (_isOpen)
        {
            var targetPts = (long)(position.TotalSeconds * _timeBase.Den / _timeBase.Num);
            FfmpegNative.avcodec_flush_buffers(_codecCtx);
            FfmpegNative.av_seek_frame(_fmtCtx, _videoStreamIndex, targetPts, FfmpegNative.AVSEEK_FLAG_BACKWARD);
        }
        return Task.CompletedTask;
    }

    public async Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
    {
        if (!_isOpen || !IsPlaying) return null;

        // Release a previously-held GPU frame. Zero-copy keeps _avFrame alive
        // across the renderer's synchronous Present(); by the time the next
        // frame is requested, the texture has been copied and can be recycled.
        if (_heldHwFrame)
        {
            FfmpegNative.av_frame_unref(_avFrame);
            _heldHwFrame = false;
        }

        return await Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var ret = FfmpegNative.av_read_frame(_fmtCtx, _avPacket);
                    if (ret < 0)
                    {
                        EndOfStream?.Invoke(this, EventArgs.Empty);
                        return null;
                    }

                    var pktStreamIdx = Marshal.ReadInt32(_avPacket, FfmpegOffsets.PacketStreamIndex);
                    if (pktStreamIdx != _videoStreamIndex)
                    {
                        FfmpegNative.av_packet_unref(_avPacket);
                        continue;
                    }

                    var sendRet = FfmpegNative.avcodec_send_packet(_codecCtx, _avPacket);
                    FfmpegNative.av_packet_unref(_avPacket);
                    if (sendRet < 0) continue;

                    var recvRet = FfmpegNative.avcodec_receive_frame(_codecCtx, _avFrame);
                    if (recvRet < 0) continue;

                    // Hardware-decoded frames arrive as AV_PIX_FMT_D3D11 (a GPU
                    // texture; reference frames live in VRAM). Transfer to a
                    // software frame so sws_scale can read CPU pixels. Software
                    // decoders produce ordinary CPU frames and skip this.
                    var srcFrame = _avFrame;
                    var frameFormat = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameFormat);
                    if (frameFormat == FfmpegNative.AV_PIX_FMT_D3D11)
                    {
                        _decodedHw = true; // decoder is really using D3D11VA

                        // Zero-copy: hand the NV12 texture straight to the renderer.
                        // data[0] = ID3D11Texture2D*, data[1] = array slice index
                        // (stored as an intptr_t in a pointer slot).
                        if (PreferZeroCopy)
                        {
                            var texture = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData0);
                            var index = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData1).ToInt32();
                            var zPts = Marshal.ReadInt64(_avFrame, FfmpegOffsets.FrameBestEffortPts);
                            var zPtsUs = zPts * _timeBase.Num * 1_000_000 / _timeBase.Den;
                            Position = TimeSpan.FromMicroseconds(zPtsUs);
                            _heldHwFrame = true; // keep _avFrame alive until next call
                            return FrameData.Gpu(texture, index, _width, _height, zPtsUs);
                        }

                        if (_swFrame == IntPtr.Zero)
                            _swFrame = FfmpegNative.av_frame_alloc();
                        FfmpegNative.av_frame_unref(_swFrame);
                        var tret = FfmpegNative.av_hwframe_transfer_data(_swFrame, _avFrame, 0);
                        if (tret < 0)
                        {
                            _logger.Warn($"av_hwframe_transfer_data failed: 0x{tret:X8}");
                            FfmpegNative.av_frame_unref(_avFrame);
                            continue;
                        }
                        srcFrame = _swFrame;
                        frameFormat = Marshal.ReadInt32(_swFrame, FfmpegOffsets.FrameFormat);
                    }
                    else if (_useHardware && !_warnedSwFallback)
                    {
                        // hw_device_ctx was set and open succeeded, but the decoder
                        // handed back a software pixel format — FFmpeg's get_format
                        // silently fell back to software. This puts reference frames
                        // back in system RAM (hundreds of MB) and burns a CPU core,
                        // so flag it loudly.
                        _warnedSwFallback = true;
                        _logger.Warn($"Hardware decode requested but decoder returned software pixel format {frameFormat} (silent fallback to SOFTWARE). Reference frames will live in system RAM.");
                    }

                    // (Re)create the swscale context for the frame's actual
                    // decoded pixel format (handles 10-bit, full-range, NV12,
                    // etc., instead of assuming 8-bit YUV420P).
                    var frameColorRange = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameColorRange);
                    var frameColorspace = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameColorspace);
                    var swsColorspace = MapSwsColorspace(frameColorspace, _width, _height);
                    var srcRange = frameColorRange == FfmpegNative.AVCOL_RANGE_JPEG ? 1 : 0;

                    if (frameFormat != _swsSrcFormat ||
                        srcRange != _swsSrcRange ||
                        swsColorspace != _swsColorspace ||
                        _swsCtx == IntPtr.Zero)
                    {
                        _swsCtx = FfmpegNative.sws_getCachedContext(
                            _swsCtx,
                            _width, _height, frameFormat,
                            _width, _height, FfmpegNative.AV_PIX_FMT_BGRA,
                            FfmpegNative.SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        _swsSrcFormat = frameFormat;
                        if (_swsCtx == IntPtr.Zero)
                        {
                            _logger.Warn("sws_getCachedContext failed");
                            return null;
                        }

                        var coefficients = FfmpegNative.sws_getCoefficients(swsColorspace);
                        var colorHr = FfmpegNative.sws_setColorspaceDetails(
                            _swsCtx,
                            coefficients,
                            srcRange,
                            coefficients,
                            1,
                            0,
                            1 << 16,
                            1 << 16);

                        _swsSrcRange = srcRange;
                        _swsColorspace = swsColorspace;
                        _logger.Info($"swscale for decoded pixel format {frameFormat} -> BGRA, colorspace={frameColorspace}/{swsColorspace}, range={frameColorRange}/{srcRange}, colorHr={colorHr}");
                        if (colorHr < 0)
                            _logger.Warn($"sws_setColorspaceDetails failed: {colorHr}");
                    }

                    var pts = Marshal.ReadInt64(_avFrame, FfmpegOffsets.FrameBestEffortPts);

                    if (!EnsureCpuBuffers())
                        return null;

                    var activeBuffer = _useBufferA ? _bufferA : _bufferB;
                    _useBufferA = !_useBufferA;

                    unsafe
                    {
                        var srcData = stackalloc IntPtr[8];
                        var srcStride = stackalloc int[8];
                        srcData[0] = Marshal.ReadIntPtr(srcFrame, FfmpegOffsets.FrameData0);
                        srcData[1] = Marshal.ReadIntPtr(srcFrame, FfmpegOffsets.FrameData1);
                        srcData[2] = Marshal.ReadIntPtr(srcFrame, FfmpegOffsets.FrameData2);
                        srcStride[0] = Marshal.ReadInt32(srcFrame, FfmpegOffsets.FrameLinesize0);
                        srcStride[1] = Marshal.ReadInt32(srcFrame, FfmpegOffsets.FrameLinesize1);
                        srcStride[2] = Marshal.ReadInt32(srcFrame, FfmpegOffsets.FrameLinesize2);

                        var dstData = stackalloc IntPtr[8];
                        var dstStride = stackalloc int[8];
                        dstData[0] = activeBuffer;
                        dstStride[0] = _stride;

                        FfmpegNative.sws_scale(_swsCtx, srcData, srcStride, 0, _height, dstData, dstStride);
                    }

                    FfmpegNative.av_frame_unref(_avFrame);

                    var ptsUs = pts * _timeBase.Num * 1_000_000 / _timeBase.Den;
                    Position = TimeSpan.FromMicroseconds(ptsUs);

                    return new FrameData(activeBuffer, _width, _height, _stride, ptsUs);
                }
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                _logger.Warn($"Frame decode error: {ex.Message}");
                return null;
            }
        }, ct);
    }

    // Opens the codec, preferring D3D11VA hardware decode and transparently
    // retrying software if the hardware open fails. Sets _useHardware to reflect
    // which path is active. Returns false only if BOTH paths fail to open.
    private bool OpenCodecWithFallback(IntPtr decoder, IntPtr codecPar)
    {
        var dev = (_hwDeviceProvider?.Invoke()) ?? IntPtr.Zero;
        if (dev != IntPtr.Zero)
        {
            // Assign the hw device context: the codec context takes ownership
            // of this ref (released by avcodec_free_context in Close/Reset).
            _hwDeviceRef = dev;
            Marshal.WriteIntPtr(_codecCtx, FfmpegOffsets.HwDeviceCtxOffset, dev);

            if (FfmpegNative.avcodec_open2(_codecCtx, decoder, IntPtr.Zero) == 0)
            {
                _useHardware = true;
                _logger.Info("Hardware decode enabled (D3D11VA)");
                return true;
            }

            // Hardware open failed: release the codec ctx (which frees the hw
            // ref it was handed) and reallocate a clean ctx for software retry.
            _logger.Warn("Hardware avcodec_open2 failed; retrying software");
            ResetCodecCtx();
            _codecCtx = FfmpegNative.avcodec_alloc_context3(decoder);
            FfmpegNative.avcodec_parameters_to_context(_codecCtx, codecPar);
        }

        _useHardware = false;
        return FfmpegNative.avcodec_open2(_codecCtx, decoder, IntPtr.Zero) == 0;
    }

    // Frees the codec context and the transfer destination frame WITHOUT
    // touching the demuxer/format context (used for the hw->sw retry).
    private void ResetCodecCtx()
    {
        if (_swFrame != IntPtr.Zero)
            FfmpegNative.av_frame_free(ref _swFrame);
        if (_codecCtx != IntPtr.Zero)
            FfmpegNative.avcodec_free_context(ref _codecCtx);
        _hwDeviceRef = IntPtr.Zero;
    }

    // Lazily allocates the two CPU BGRA buffers the sws_scale path writes into.
    // Skipped entirely in zero-copy mode (the common case), so those buffers —
    // ~16MB at 1080p, ~66MB at 4K — are only paid for when the CPU color path is
    // actually in use. Returns false only if av_malloc fails.
    private bool EnsureCpuBuffers()
    {
        if (_bufferA != IntPtr.Zero && _bufferB != IntPtr.Zero)
            return true;
        if (_bufferA == IntPtr.Zero)
            _bufferA = FfmpegNative.av_malloc((ulong)_frameSize);
        if (_bufferB == IntPtr.Zero)
            _bufferB = FfmpegNative.av_malloc((ulong)_frameSize);
        if (_bufferA == IntPtr.Zero || _bufferB == IntPtr.Zero)
        {
            _logger.Warn("av_malloc failed for CPU double buffers");
            return false;
        }
        return true;
    }

    private void Close()
    {
        if (_swsCtx != IntPtr.Zero) { FfmpegNative.sws_freeContext(_swsCtx); _swsCtx = IntPtr.Zero; }
        _swsSrcFormat = -1;
        _swsSrcRange = -1;
        _swsColorspace = -1;
        if (_avFrame != IntPtr.Zero) { FfmpegNative.av_frame_free(ref _avFrame); }
        if (_avPacket != IntPtr.Zero) { FfmpegNative.av_packet_free(ref _avPacket); }
        if (_swFrame != IntPtr.Zero) { FfmpegNative.av_frame_free(ref _swFrame); }
        // avcodec_free_context releases hw_device_ctx (the ref we assigned), if any.
        if (_codecCtx != IntPtr.Zero) { FfmpegNative.avcodec_free_context(ref _codecCtx); }
        _hwDeviceRef = IntPtr.Zero;
        _useHardware = false;
        _decodedHw = false;
        _warnedSwFallback = false;
        _heldHwFrame = false;
        if (_fmtCtx != IntPtr.Zero) { FfmpegNative.avformat_close_input(ref _fmtCtx); }
        if (_bufferA != IntPtr.Zero) { FfmpegNative.av_free(_bufferA); _bufferA = IntPtr.Zero; }
        if (_bufferB != IntPtr.Zero) { FfmpegNative.av_free(_bufferB); _bufferB = IntPtr.Zero; }
        _isOpen = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private static AVRational ReadTimeBase(IntPtr streamPtr)
    {
        var num = Marshal.ReadInt32(streamPtr, FfmpegOffsets.StreamTimeBaseNum);
        var den = Marshal.ReadInt32(streamPtr, FfmpegOffsets.StreamTimeBaseDen);
        return new AVRational(num, den);
    }

    private static int MapSwsColorspace(int frameColorspace, int width, int height)
    {
        return frameColorspace switch
        {
            FfmpegNative.AVCOL_SPC_BT709 => FfmpegNative.SWS_CS_ITU709,
            FfmpegNative.AVCOL_SPC_FCC => FfmpegNative.SWS_CS_FCC,
            FfmpegNative.AVCOL_SPC_BT470BG or FfmpegNative.AVCOL_SPC_SMPTE170M => FfmpegNative.SWS_CS_ITU601,
            FfmpegNative.AVCOL_SPC_SMPTE240M => FfmpegNative.SWS_CS_SMPTE240M,
            FfmpegNative.AVCOL_SPC_BT2020_NCL or FfmpegNative.AVCOL_SPC_BT2020_CL => FfmpegNative.SWS_CS_BT2020,
            _ => width >= 1280 || height > 576 ? FfmpegNative.SWS_CS_ITU709 : FfmpegNative.SWS_CS_ITU601,
        };
    }

    private readonly record struct AVRational(int Num, int Den);
}
