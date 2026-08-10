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
    bool PreferProxyVideo,
    int MaxPresentCostUs = 0,
    int SceneIntervalUs = 0)
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
            false,
            0,
            0)
    {
    }

    // The minimum gap between presents. A scene override (battery/lock screen)
    // wins over the profile's fixed FPS cap; 0 means uncapped.
    public long MinFrameIntervalUs
    {
        get
        {
            if (SceneIntervalUs > 0) return SceneIntervalUs;
            return TargetFps is > 0 ? 1_000_000L / TargetFps.Value : 0L;
        }
    }

    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Saver =>
                new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.NonReference, true, MaxPresentCostUs: 8_333),
            WallpaperPerformanceProfile.Balanced =>
                new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.Default, true, MaxPresentCostUs: 11_111),
            // Quality keeps the ORIGINAL file and full decode (no proxy, no
            // discard), but its present rate is capped at 30 FPS like the other
            // profiles: for a 60 FPS 4K source the ShouldPresentFrame gate drops
            // every other frame before the renderer, halving the per-second GPU
            // copy + draw work. Its present budget is the most generous of the
            // three — frames are only dropped when a single Present exceeds
            // ~16.7ms, i.e. only under real GPU load.
            _ =>
                new PlaybackPerformancePolicy(profile, 30, DecoderFrameDiscard.Default, false, MaxPresentCostUs: 16_666),
        };
}
