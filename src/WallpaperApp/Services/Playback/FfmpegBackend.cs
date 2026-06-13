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

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Duration => TimeSpan.FromTicks(_durationUs * 10);
    public TimeSpan Position { get; private set; }

    public event EventHandler? EndOfStream;

    public FfmpegBackend(FileLogger logger)
    {
        _logger = logger;
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

                ret = FfmpegNative.avcodec_open2(_codecCtx, decoder, IntPtr.Zero);
                if (ret < 0)
                    return Fail($"avcodec_open2 failed");

                // NOTE: the swscale context is created lazily in NextFrameAsync
                // using the frame's ACTUAL pixel format, instead of a hardcoded
                // AV_PIX_FMT_YUV420P. The decoded format can be YUV420P, a
                // 10-bit variant (YUV420P10LE), full-range YUVJ420P, NV12, etc.;
                // assuming YUV420P produces wrong colors and slow conversions.

                _avFrame = FfmpegNative.av_frame_alloc();
                _avPacket = FfmpegNative.av_packet_alloc();

                _bufferA = FfmpegNative.av_malloc((ulong)_frameSize);
                _bufferB = FfmpegNative.av_malloc((ulong)_frameSize);
                if (_bufferA == IntPtr.Zero || _bufferB == IntPtr.Zero)
                    return Fail("av_malloc failed for double buffers");

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

                    // (Re)create the swscale context for the frame's actual
                    // decoded pixel format (handles 10-bit, full-range, NV12,
                    // etc., instead of assuming 8-bit YUV420P).
                    var frameFormat = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameFormat);
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

                    var activeBuffer = _useBufferA ? _bufferA : _bufferB;
                    _useBufferA = !_useBufferA;

                    unsafe
                    {
                        var srcData = stackalloc IntPtr[8];
                        var srcStride = stackalloc int[8];
                        srcData[0] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData0);
                        srcData[1] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData1);
                        srcData[2] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData2);
                        srcStride[0] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize0);
                        srcStride[1] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize1);
                        srcStride[2] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize2);

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

    private void Close()
    {
        if (_swsCtx != IntPtr.Zero) { FfmpegNative.sws_freeContext(_swsCtx); _swsCtx = IntPtr.Zero; }
        _swsSrcFormat = -1;
        _swsSrcRange = -1;
        _swsColorspace = -1;
        if (_avFrame != IntPtr.Zero) { FfmpegNative.av_frame_free(ref _avFrame); }
        if (_avPacket != IntPtr.Zero) { FfmpegNative.av_packet_free(ref _avPacket); }
        if (_codecCtx != IntPtr.Zero) { FfmpegNative.avcodec_free_context(ref _codecCtx); }
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
