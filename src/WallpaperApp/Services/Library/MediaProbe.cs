using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Services.Library;

// Lightweight metadata probe: opens a file through FfmpegBackend just far enough
// to read its width/height/duration, without running the playback pipeline. Lets
// us populate wallpaper metadata that the import step doesn't compute.
public static class MediaProbe
{
    public static async Task<(int Width, int Height, long DurationMs)> ProbeAsync(
        string path, FileLogger logger)
    {
        try
        {
            using var backend = new FfmpegBackend(logger);
            if (!await backend.OpenAsync(path)) return (0, 0, 0);
            return (backend.VideoWidth, backend.VideoHeight, (long)backend.Duration.TotalMilliseconds);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
