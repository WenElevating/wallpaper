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

                _fmtCtx = FfmpegNative.avformat_alloc_context();
                var ret = FfmpegNative.avformat_open_input(out _fmtCtx, filePath, IntPtr.Zero, IntPtr.Zero);
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

                _swsCtx = FfmpegNative.sws_getContext(
                    _width, _height, FfmpegNative.AV_PIX_FMT_YUV420P,
                    _width, _height, FfmpegNative.AV_PIX_FMT_BGRA,
                    FfmpegNative.SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (_swsCtx == IntPtr.Zero)
                    return Fail("sws_getContext failed");

                _avFrame = FfmpegNative.av_frame_alloc();
                _avPacket = FfmpegNative.av_packet_alloc();

                _bufferA = FfmpegNative.av_malloc((ulong)_frameSize);
                _bufferB = FfmpegNative.av_malloc((ulong)_frameSize);
                if (_bufferA == IntPtr.Zero || _bufferB == IntPtr.Zero)
                    return Fail("av_malloc failed for double buffers");

                _timeBase = ReadTimeBase(streamPtr);
                _durationUs = Marshal.ReadInt64(streamPtr, 0x88);

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

                    var pktStreamIdx = Marshal.ReadInt32(_avPacket, 0x08);
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

                    var pts = Marshal.ReadInt64(_avFrame, FfmpegOffsets.FrameBestEffortPts);

                    var activeBuffer = _useBufferA ? _bufferA : _bufferB;
                    _useBufferA = !_useBufferA;

                    var srcData = new IntPtr[4];
                    var srcStride = new int[4];
                    srcData[0] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData0);
                    srcData[1] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData1);
                    srcData[2] = Marshal.ReadIntPtr(_avFrame, FfmpegOffsets.FrameData2);
                    srcStride[0] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize0);
                    srcStride[1] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize1);
                    srcStride[2] = Marshal.ReadInt32(_avFrame, FfmpegOffsets.FrameLinesize2);

                    var dstData = new IntPtr[] { activeBuffer };
                    var dstStride = new int[] { _stride };

                    FfmpegNative.sws_scale(_swsCtx, srcData, srcStride, 0, _height, dstData, dstStride);

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
        if (_codecCtx != IntPtr.Zero) { FfmpegNative.avcodec_free_context(_codecCtx); _codecCtx = IntPtr.Zero; }
        if (_avFrame != IntPtr.Zero) { FfmpegNative.av_frame_free(_avFrame); _avFrame = IntPtr.Zero; }
        if (_avPacket != IntPtr.Zero) { FfmpegNative.av_packet_free(_avPacket); _avPacket = IntPtr.Zero; }
        if (_fmtCtx != IntPtr.Zero) { FfmpegNative.avformat_close_input(out _fmtCtx); }
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
        // AVStream.time_base at offset 0x50 (avformat-61)
        var num = Marshal.ReadInt32(streamPtr, 0x50);
        var den = Marshal.ReadInt32(streamPtr, 0x54);
        return new AVRational(num, den);
    }

    private readonly record struct AVRational(int Num, int Den);
}
