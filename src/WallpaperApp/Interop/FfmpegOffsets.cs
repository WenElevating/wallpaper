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
}
