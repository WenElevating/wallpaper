using WallpaperApp.Models;

namespace WallpaperApp.Services.Playback;

public enum DecoderFrameDiscard
{
    Default,
    NonReference,
}

public readonly record struct PlaybackPerformancePolicy(
    WallpaperPerformanceProfile Profile,
    int? TargetFps,
    DecoderFrameDiscard DecoderDiscard,
    bool PreferProxyVideo)
{
    // Compatibility alias for callers that reason about the old presentation cap.
    public int? MaxPresentFps => TargetFps;

    // Legacy constructor retained for tests and integrations that construct a
    // policy directly. New code should use FromProfile so proxy preference and
    // profile identity travel with the policy.
    public PlaybackPerformancePolicy(int? targetFps, DecoderFrameDiscard decoderDiscard = DecoderFrameDiscard.Default)
        : this(
            targetFps is null ? WallpaperPerformanceProfile.Quality :
                targetFps <= 24 ? WallpaperPerformanceProfile.Saver : WallpaperPerformanceProfile.Balanced,
            targetFps,
            decoderDiscard,
            false)
    {
    }

    public long MinFrameIntervalUs =>
        TargetFps is > 0 ? 1_000_000L / TargetFps.Value : 0L;

    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.NonReference, true),
            WallpaperPerformanceProfile.Balanced => new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.Default, true),
            // Quality keeps the ORIGINAL file and full decode (no proxy, no
            // discard), but its present rate is capped at 30 FPS like the other
            // profiles: for a 60 FPS 4K source the ShouldPresentFrame gate drops
            // every other frame before the renderer, halving the per-second GPU
            // copy + draw work with no perceptible change for wallpaper content.
            _ => new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.Default, false),
        };
}
