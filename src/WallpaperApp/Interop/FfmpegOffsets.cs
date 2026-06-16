using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

/// <summary>
/// FFmpeg 7.1 (avformat-61 / avcodec-61 / avutil-59) struct field offsets.
/// These are ABI-specific to the bundled DLL build.
/// Verify on FFmpeg version upgrade by checking struct layouts with a C probe.
/// </summary>
internal static class FfmpegOffsets
{
    // AVFormatContext (avformat-61)
    internal const int NbStreamsOffset   = 0x2C;  // unsigned int
    internal const int StreamsOffset     = 0x30;  // AVStream** (IntPtr)

    // AVStream (avformat-61)
    internal const int CodecparOffset    = 0x10;  // AVCodecParameters* (IntPtr)

    // AVCodecParameters (avcodec-61)
    internal const int CodecTypeOffset   = 0x00;  // int (AVMediaType)
    internal const int CodecIdOffset     = 0x04;  // int (AVCodecID)
    internal const int WidthOffset       = 0x48;  // int
    internal const int HeightOffset      = 0x4C;  // int

    // AVStream (avformat-61) — additional
    internal const int StreamDurationOffset = 0x30;  // int64_t duration
    internal const int StreamTimeBaseNum    = 0x20;  // int (AVRational.num)
    internal const int StreamTimeBaseDen    = 0x24;  // int (AVRational.den)

    // AVPacket (avcodec-61)
    internal const int PacketStreamIndex    = 0x24;  // int stream_index

    // AVCodecContext (avcodec-61)
    // AVBufferRef* hw_device_ctx — set before avcodec_open2 to enable hardware
    // decode. avcodec_free_context() releases the ref written here, so once it
    // is assigned the codec context owns it. Verified by offsetof() probe for
    // FFmpeg 7.1 (avcodec-61); re-verify on upgrade.
    internal const int HwDeviceCtxOffset   = 0x230;

    // AVBufferRef (avutil-59) — uint8_t* data points at the owned context struct.
    // Used to reach the AVHWDeviceContext from an AVBufferRef*.
    // Verified by offsetof() probe for FFmpeg 7.1; re-verify on upgrade.
    internal const int AvBufferRefDataOffset   = 0x08;

    // AVHWDeviceContext (avutil-59) — void* hwctx (the format-specific context,
    // e.g. AVD3D11VADeviceContext). Verified by offsetof() probe for FFmpeg 7.1.
    internal const int AvHwDeviceCtxHwctxOffset = 0x10;

    // AVD3D11VADeviceContext (avutil-59) — ID3D11Device* device (first field; the
    // only mandatory one — FFmpeg fills device_context/video_* from it on init).
    internal const int AvD3D11VaDeviceOffset   = 0x00;

    // AVFrame (avutil-59)
    internal const int FrameData0          = 0x00;  // uint8_t* data[0]
    internal const int FrameData1          = 0x08;  // uint8_t* data[1]
    internal const int FrameData2          = 0x10;  // uint8_t* data[2]
    internal const int FrameLinesize0      = 0x40;  // int linesize[0]
    internal const int FrameLinesize1      = 0x44;  // int linesize[1]
    internal const int FrameLinesize2      = 0x48;  // int linesize[2]
    internal const int FramePts            = 0x88;  // int64_t pts
    internal const int FrameColorRange     = 0x128; // enum AVColorRange
    internal const int FrameColorspace     = 0x134; // enum AVColorSpace
    internal const int FrameBestEffortPts  = 0x140; // int64_t best_effort_timestamp
    internal const int FrameWidth          = 0x68;  // int width
    internal const int FrameHeight         = 0x6C;  // int height
    internal const int FrameFormat         = 0x74;  // int format (AVPixelFormat)
}
