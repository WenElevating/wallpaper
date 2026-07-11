using System.Diagnostics;
using WallpaperApp.Services.Playback;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class PrecisionTimerTests
{
    [Fact]
    public void Wait_For5ms_SleepsAtLeast5ms()
    {
        using var timer = new PrecisionTimer();
        var sw = Stopwatch.StartNew();
        timer.Wait(5_000);  // 5ms = 5000us
        sw.Stop();
        // Waitable timer has ~1ms resolution; the wait should be >= 5ms and
        // not wildly over (allow generous headroom for scheduler latency).
        Assert.True(sw.ElapsedMilliseconds >= 5, $"Expected >= 5ms, got {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected < 500ms, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Wait_WithZeroOrNegativeMicroseconds_DoesNotThrow()
    {
        using var timer = new PrecisionTimer();
        var ex0 = Record.Exception(() => timer.Wait(0));
        var exNeg = Record.Exception(() => timer.Wait(-100));
        Assert.Null(ex0);
        Assert.Null(exNeg);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var timer = new PrecisionTimer();
        timer.Dispose();
        var ex = Record.Exception(() => timer.Dispose());
        Assert.Null(ex);
    }
}
