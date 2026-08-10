using WallpaperApp.Models;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PlaybackPerformancePolicyTests
{
    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, 30)]
    [InlineData(WallpaperPerformanceProfile.Balanced, 30)]
    [InlineData(WallpaperPerformanceProfile.Saver, 30)]
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

    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, false)]
    [InlineData(WallpaperPerformanceProfile.Balanced, true)]
    [InlineData(WallpaperPerformanceProfile.Saver, true)]
    public void FromProfile_SelectsProxyOnlyForNonQualityProfiles(WallpaperPerformanceProfile profile, bool expected)
    {
        Assert.Equal(expected, PlaybackPerformancePolicy.FromProfile(profile).PreferProxyVideo);
    }

    [Fact]
    public void FromProfile_IdentifiesTheRequestedProfile()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Saver);

        Assert.Equal(WallpaperPerformanceProfile.Saver, policy.Profile);
        Assert.Equal(30, policy.TargetFps);
    }

    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, 16_666)]
    [InlineData(WallpaperPerformanceProfile.Balanced, 11_111)]
    [InlineData(WallpaperPerformanceProfile.Saver, 8_333)]
    public void FromProfile_AssignsAdaptivePresentBudget(WallpaperPerformanceProfile profile, int expected)
    {
        var policy = PlaybackPerformancePolicy.FromProfile(profile);

        Assert.Equal(expected, policy.MaxPresentCostUs);
    }

    [Fact]
    public void MinFrameIntervalUs_UsesSceneInterval_WhenSet()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced)
            with { SceneIntervalUs = 200_000 };

        Assert.Equal(200_000, policy.MinFrameIntervalUs);
    }

    [Fact]
    public void MinFrameIntervalUs_IgnoresSceneInterval_WhenZero()
    {
        var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);

        Assert.Equal(33_333, policy.MinFrameIntervalUs);
    }

    [Theory]
    [InlineData(ScenePerformanceState.Battery, 66_666)]
    [InlineData(ScenePerformanceState.Locked, 200_000)]
    public void SceneInterval_ReturnsPerStateInterval(ScenePerformanceState state, int expected)
    {
        Assert.Equal(expected, ScenePerformance.SceneIntervalUs(state));
    }

    [Fact]
    public void StrongestIntervalUs_PicksMostRestrictiveActiveState()
    {
        Assert.Equal(200_000, ScenePerformance.StrongestIntervalUs(
            new[] { ScenePerformanceState.Battery, ScenePerformanceState.Locked }));
        Assert.Equal(66_666, ScenePerformance.StrongestIntervalUs(
            new[] { ScenePerformanceState.Battery }));
        Assert.Equal(0, ScenePerformance.StrongestIntervalUs(Array.Empty<ScenePerformanceState>()));
    }
}
