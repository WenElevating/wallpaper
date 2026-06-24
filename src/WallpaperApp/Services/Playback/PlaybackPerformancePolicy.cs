using WallpaperApp.Models;

namespace WallpaperApp.Services.Playback;

public readonly record struct PlaybackPerformancePolicy(int? MaxPresentFps)
{
    public long MinFrameIntervalUs =>
        MaxPresentFps is > 0 ? 1_000_000L / MaxPresentFps.Value : 0L;

    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Quality => new PlaybackPerformancePolicy(null),
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15),
            _ => new PlaybackPerformancePolicy(30),
        };
}
