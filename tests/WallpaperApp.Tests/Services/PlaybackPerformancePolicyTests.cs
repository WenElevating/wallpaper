using WallpaperApp.Models;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PlaybackPerformancePolicyTests
{
    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, null)]
    [InlineData(WallpaperPerformanceProfile.Balanced, null)]
    [InlineData(WallpaperPerformanceProfile.Saver, null)]
    public void FromProfile_MapsProfileToFrameRateCap(WallpaperPerformanceProfile profile, int? expected)
    {
        var policy = PlaybackPerformancePolicy.FromProfile(profile);

        Assert.Equal(expected, policy.MaxPresentFps);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1_000_000)]
    [InlineData(30, 33_333)]
    [InlineData(60, 16_666)]
    public void MinFrameIntervalUs_ReturnsExpectedInterval(int? fps, long expected)
    {
        var policy = new PlaybackPerformancePolicy(fps, DecoderFrameDiscard.Default);

        Assert.Equal(expected, policy.MinFrameIntervalUs);
    }

    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, DecoderFrameDiscard.Default)]
    [InlineData(WallpaperPerformanceProfile.Balanced, DecoderFrameDiscard.Default)]
    [InlineData(WallpaperPerformanceProfile.Saver, DecoderFrameDiscard.NonReference)]
    public void FromProfile_MapsProfileToDecoderDiscard(WallpaperPerformanceProfile profile, DecoderFrameDiscard expected)
    {
        var policy = PlaybackPerformancePolicy.FromProfile(profile);

        Assert.Equal(expected, policy.DecoderDiscard);
    }
}
