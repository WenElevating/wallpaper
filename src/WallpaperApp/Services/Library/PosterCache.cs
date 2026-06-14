using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Services.Library;

// Extracts a single downscaled poster frame from a managed video and caches it
// on disk keyed by the video's content hash (the managed filename is "<hash>.<ext>").
//
// Why this exists: the app ships FFmpeg as DLLs (used by FfmpegBackend), not the
// ffmpeg.exe CLI, so ThumbnailService can't shell out. Instead we reuse the proven
// FfmpegBackend decode path (open -> decode one frame -> sws -> BGRA) and save the
// frame straight to JPEG via System.Drawing (BGRA maps 1:1 onto Format32bppArgb's
// memory layout). Posters are generated one at a time (semaphore) and cached, so
// library cards can show a static poster and only spin up live playback on hover.
public static class PosterCache
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperApp", "posters");

    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Must be set at startup (App) so the decoder can log. Null => skip extraction.
    public static FileLogger? Logger { get; set; }

    public static async Task<string?> GetOrCreateAsync(string managedFilePath)
    {
        try
        {
            if (!File.Exists(managedFilePath)) return null;
            var logger = Logger;
            if (logger == null) return null;

            Directory.CreateDirectory(CacheDir);
            var hash = Path.GetFileNameWithoutExtension(managedFilePath);
            var posterPath = Path.Combine(CacheDir, hash + ".jpg");
            if (File.Exists(posterPath)) return posterPath;

            await Gate.WaitAsync();
            try
            {
                if (File.Exists(posterPath)) return posterPath; // another caller won the race

                var backend = new FfmpegBackend(logger);
                try
                {
                    if (!await backend.OpenAsync(managedFilePath)) return null;
                    await backend.PlayAsync();
                    var frame = await backend.NextFrameAsync();
                    if (frame == null) return null;

                    // frame.Buffer is BGRA; Format32bppArgb is laid out B,G,R,A in memory.
                    using var bmp = new Bitmap(frame.Width, frame.Height, frame.Stride, PixelFormat.Format32bppArgb, frame.Buffer);
                    bmp.Save(posterPath, ImageFormat.Jpeg);
                    return File.Exists(posterPath) ? posterPath : null;
                }
                finally
                {
                    backend.Dispose();
                }
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            Logger?.Warn($"Poster extraction failed for {managedFilePath}: {ex.Message}");
            return null;
        }
    }
}
