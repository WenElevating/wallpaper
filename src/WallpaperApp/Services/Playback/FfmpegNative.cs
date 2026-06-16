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
    internal static partial int avformat_open_input(ref IntPtr ps, string url, IntPtr fmt, IntPtr options);

    [LibraryImport(AvFormat)]
    internal static partial void avformat_close_input(ref IntPtr ps);

    [LibraryImport(AvFormat)]
    internal static partial int avformat_find_stream_info(IntPtr ps, IntPtr options);

    [LibraryImport(AvFormat)]
    internal static partial int av_find_best_stream(IntPtr ps, int mediaType, int wantedStreamNb, int relatedStream, ref IntPtr decoderRet, int flags);

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

    [LibraryImport(AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr avcodec_find_decoder(int id);

    [LibraryImport(AvCodec)]
    internal static partial IntPtr avcodec_alloc_context3(IntPtr codec);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_free_context(ref IntPtr avctx);

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
    internal static partial void av_frame_free(ref IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_unref(IntPtr frame);

    [LibraryImport(AvCodec)]
    internal static partial IntPtr av_packet_alloc();

    [LibraryImport(AvCodec)]
    internal static partial void av_packet_free(ref IntPtr pkt);

    [LibraryImport(AvCodec)]
    internal static partial void av_packet_unref(IntPtr pkt);

    [LibraryImport(AvUtil)]
    internal static partial void av_free(IntPtr ptr);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_malloc(ulong size);

    // --- Hardware acceleration (libavutil/hwcontext.h) ---
    // Resolve a hw device type by name ("d3d11va", "dxva2", ...) -> AVHWDeviceType.
    [LibraryImport(AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int av_hwdevice_find_type_by_name(string name);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_hwdevice_ctx_alloc(int type);

    // Creates a hw device context (alloc + init). On success *ctx points to an
    // AVBufferRef* the caller owns (av_buffer_unref to release).
    [LibraryImport(AvUtil)]
    internal static partial int av_hwdevice_ctx_create(ref IntPtr ctx, int type, IntPtr device, IntPtr opts, int flags);

    [LibraryImport(AvUtil)]
    internal static partial int av_hwdevice_ctx_init(IntPtr ctx);

    // Returns a NEW AVBufferRef* (caller owns; av_buffer_unref to release).
    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_buffer_ref(IntPtr buf);

    [LibraryImport(AvUtil)]
    internal static partial void av_buffer_unref(ref IntPtr buf);

    // Copies a hardware frame to a software frame (GPU texture -> CPU pixels).
    // dst must be an av_frame_alloc()'d frame; its format is filled in.
    [LibraryImport(AvUtil)]
    internal static partial int av_hwframe_transfer_data(IntPtr dst, IntPtr src, int field);

    [LibraryImport(SwScale)]
    internal static partial IntPtr sws_getContext(
        int srcW, int srcH, int srcFormat,
        int dstW, int dstH, int dstFormat,
        int flags, IntPtr srcFilter, IntPtr dstFilter, IntPtr param);

    [LibraryImport(SwScale)]
    internal static partial IntPtr sws_getCachedContext(
        IntPtr context,
        int srcW, int srcH, int srcFormat,
        int dstW, int dstH, int dstFormat,
        int flags, IntPtr srcFilter, IntPtr dstFilter, IntPtr param);

    [LibraryImport(SwScale)]
    internal static partial IntPtr sws_getCoefficients(int colorspace);

    [LibraryImport(SwScale)]
    internal static partial int sws_setColorspaceDetails(
        IntPtr context,
        IntPtr invTable,
        int srcRange,
        IntPtr table,
        int dstRange,
        int brightness,
        int contrast,
        int saturation);

    [LibraryImport(SwScale)]
    internal static partial void sws_freeContext(IntPtr swsContext);

    [LibraryImport(SwScale)]
    internal static unsafe partial int sws_scale(
        IntPtr context,
        IntPtr* srcSlice,
        int* srcStride,
        int srcSliceY,
        int srcSliceH,
        IntPtr* dstSlice,
        int* dstStride);

    internal const int AVMEDIA_TYPE_VIDEO = 0;
    internal const int AV_CODEC_ID_NONE = 0;
    internal const int AV_PIX_FMT_BGRA = 28;
    internal const int AV_PIX_FMT_YUV420P = 0;
    // Hardware pixel formats / device types (verified by offsetof probe for FFmpeg 7.1).
    internal const int AV_PIX_FMT_D3D11 = 171;       // AVPixelFormat: a D3D11 GPU texture (VLD) frame
    internal const int AV_HWDEVICE_TYPE_D3D11VA = 7; // AVHWDeviceType
    internal const int SWS_BILINEAR = 2;

    internal const int AVCOL_RANGE_UNSPECIFIED = 0;
    internal const int AVCOL_RANGE_MPEG = 1;
    internal const int AVCOL_RANGE_JPEG = 2;

    internal const int AVCOL_SPC_RGB = 0;
    internal const int AVCOL_SPC_BT709 = 1;
    internal const int AVCOL_SPC_UNSPECIFIED = 2;
    internal const int AVCOL_SPC_FCC = 4;
    internal const int AVCOL_SPC_BT470BG = 5;
    internal const int AVCOL_SPC_SMPTE170M = 6;
    internal const int AVCOL_SPC_SMPTE240M = 7;
    internal const int AVCOL_SPC_BT2020_NCL = 9;
    internal const int AVCOL_SPC_BT2020_CL = 10;

    internal const int SWS_CS_ITU709 = 1;
    internal const int SWS_CS_FCC = 4;
    internal const int SWS_CS_ITU601 = 5;
    internal const int SWS_CS_SMPTE240M = 7;
    internal const int SWS_CS_BT2020 = 9;

    internal const int AVSEEK_FLAG_BACKWARD = 1;
    internal const int AVSEEK_FLAG_FRAME = 2;
    internal const int AVSEEK_FLAG_ANY = 4;
}
