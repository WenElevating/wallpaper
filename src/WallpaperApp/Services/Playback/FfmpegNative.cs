using System.Runtime.InteropServices;

namespace WallpaperApp.Services.Playback;

internal static partial class FfmpegNative
{
    private const string AvFormat = "avformat-61";
    private const string AvCodec = "avcodec-61";
    private const string AvUtil = "avutil-59";
    private const string SwScale = "swscale-8";

    [LibraryImport(AvFormat)]
    internal static partial IntPtr avformat_alloc_context();

    [LibraryImport(AvFormat)]
    internal static partial void avformat_free_context(IntPtr ps);

    [LibraryImport(AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avformat_open_input(out IntPtr ps, string url, IntPtr fmt, IntPtr options);

    [LibraryImport(AvFormat)]
    internal static partial void avformat_close_input(out IntPtr ps);

    [LibraryImport(AvFormat)]
    internal static partial int avformat_find_stream_info(IntPtr ps, IntPtr options);

    [LibraryImport(AvFormat)]
    internal static partial long av_find_best_stream(IntPtr ps, int mediaType, int wantedStreamNb, int relatedStream, ref IntPtr decoderRet, int flags);

    [LibraryImport(AvFormat)]
    internal static partial long av_seek_frame(IntPtr s, int streamIndex, long timestamp, int flags);

    [LibraryImport(AvFormat)]
    internal static partial uint avformat_version();

    [LibraryImport(AvCodec)]
    internal static partial uint avcodec_version();

    [LibraryImport(AvUtil)]
    internal static partial uint avutil_version();

    [LibraryImport(AvFormat)]
    internal static partial int av_read_frame(IntPtr ps, IntPtr pkt);

    [LibraryImport(AvFormat)]
    internal static partial long av_get_stream_duration(IntPtr fmtCtx, int stream_index);

    [LibraryImport(AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr avcodec_find_decoder(int id);

    [LibraryImport(AvCodec)]
    internal static partial IntPtr avcodec_alloc_context3(IntPtr codec);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_free_context(IntPtr avctx);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_parameters_to_context(IntPtr avctx, IntPtr par);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_open2(IntPtr avctx, IntPtr codec, IntPtr options);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_send_packet(IntPtr avctx, IntPtr avpkt);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_receive_frame(IntPtr avctx, IntPtr frame);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_flush_buffers(IntPtr avctx);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_frame_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_free(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_width(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_height(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial long av_frame_get_best_effort_timestamp(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_frame_get_data(IntPtr frame, int plane);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_linesize(IntPtr frame, int plane);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_packet_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_packet_free(IntPtr pkt);

    [LibraryImport(AvUtil)]
    internal static partial void av_packet_unref(IntPtr pkt);

    [LibraryImport(AvUtil)]
    internal static partial void av_free(IntPtr ptr);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_malloc(ulong size);

    [LibraryImport(SwScale)]
    internal static partial IntPtr sws_getContext(
        int srcW, int srcH, int srcFormat,
        int dstW, int dstH, int dstFormat,
        int flags, IntPtr srcFilter, IntPtr dstFilter, IntPtr param);

    [LibraryImport(SwScale)]
    internal static partial void sws_freeContext(IntPtr swsContext);

    [LibraryImport(SwScale)]
    internal static partial int sws_scale(
        IntPtr context, IntPtr srcSlice, int[] srcStride,
        int srcSliceY, int srcSliceH,
        IntPtr dstSlice, int[] dstStride);

    internal const int AVMEDIA_TYPE_VIDEO = 0;
    internal const int AV_CODEC_ID_NONE = 0;
    internal const int AV_PIX_FMT_BGRA = 26;
    internal const int AV_PIX_FMT_YUV420P = 0;
    internal const int SWS_BILINEAR = 2;

    internal const int AVSEEK_FLAG_BACKWARD = 1;
    internal const int AVSEEK_FLAG_FRAME = 2;
    internal const int AVSEEK_FLAG_ANY = 4;
}
