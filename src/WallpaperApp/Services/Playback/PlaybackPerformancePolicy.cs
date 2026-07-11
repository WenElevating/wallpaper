using WallpaperApp.Models;

namespace WallpaperApp.Services.Playback;

public enum DecoderFrameDiscard
{
    Default,
    NonReference,
}

public readonly record struct PlaybackPerformancePolicy(
    int? MaxPresentFps,
    DecoderFrameDiscard DecoderDiscard = DecoderFrameDiscard.Default)
{
    public long MinFrameIntervalUs =>
        MaxPresentFps is > 0 ? 1_000_000L / MaxPresentFps.Value : 0L;

    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15, DecoderFrameDiscard.NonReference),
            WallpaperPerformanceProfile.Balanced => new PlaybackPerformancePolicy(30, DecoderFrameDiscard.Default),
            _ => new PlaybackPerformancePolicy(null, DecoderFrameDiscard.Default),  // Quality: passthrough (no cap)
        };
}
