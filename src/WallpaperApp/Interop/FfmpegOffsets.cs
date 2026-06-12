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
    internal const int NbStreamsOffset   = 0x68;  // int
    internal const int StreamsOffset     = 0x70;  // AVStream** (IntPtr)

    // AVStream (avformat-61)
    internal const int CodecparOffset    = 0x60;  // AVCodecParameters* (IntPtr)

    // AVCodecParameters (avcodec-61)
    internal const int CodecTypeOffset   = 0x04;  // int (AVMediaType)
    internal const int CodecIdOffset     = 0x08;  // int (AVCodecID)
    internal const int WidthOffset       = 0x2C;  // int
    internal const int HeightOffset      = 0x30;  // int

    // AVStream (avformat-61) — additional
    internal const int StreamDurationOffset = 0x88;  // int64_t duration
    internal const int StreamTimeBaseNum    = 0x50;  // int (AVRational.num)
    internal const int StreamTimeBaseDen    = 0x54;  // int (AVRational.den)

    // AVPacket (avcodec-61)
    internal const int PacketStreamIndex    = 0x08;  // int stream_index

    // AVFrame (avutil-59)
    internal const int FrameData0          = 0x00;  // uint8_t* data[0]
    internal const int FrameData1          = 0x08;  // uint8_t* data[1]
    internal const int FrameData2          = 0x10;  // uint8_t* data[2]
    internal const int FrameLinesize0      = 0x28;  // int linesize[0]
    internal const int FrameLinesize1      = 0x2C;  // int linesize[1]
    internal const int FrameLinesize2      = 0x30;  // int linesize[2]
    internal const int FramePts            = 0x38;  // int64_t pts
    internal const int FrameBestEffortPts  = 0xA8;  // int64_t best_effort_timestamp
    internal const int FrameWidth          = 0x68;  // int width
    internal const int FrameHeight         = 0x6C;  // int height
}
