namespace WallpaperApp.Services.Playback;

// Transient scene states that tighten the present gate WITHOUT changing the
// wallpaper's source file or decode policy. The present floor interval is
// overridden on the session's policy, so wallpapers keep rendering (the lock
// screen still shows the wallpaper) at a drastically lower present rate.
public enum ScenePerformanceState
{
    Battery, // on battery with PauseOnBattery disabled
    Locked,  // desktop locked or a secure desktop (UAC) is showing
}

public static class ScenePerformance
{
    // The strongest (largest) interval among the active states wins.
    public static int SceneIntervalUs(ScenePerformanceState state) => state switch
    {
        ScenePerformanceState.Locked => 200_000, // 5 FPS: keeps the lock-screen background alive
        _ => 66_666,                             // 15 FPS on battery
    };

    public static int StrongestIntervalUs(IEnumerable<ScenePerformanceState> states)
    {
        var best = 0;
        foreach (var state in states)
            best = Math.Max(best, SceneIntervalUs(state));
        return best;
    }
}
