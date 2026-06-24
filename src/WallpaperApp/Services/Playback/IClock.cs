using System.Diagnostics;

namespace WallpaperApp.Services.Playback;

internal interface IClock
{
    long NowUs { get; }
}

internal sealed class StopwatchClock : IClock
{
    public long NowUs => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
}
