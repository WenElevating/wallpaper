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
        // Waitable timers are subject to platform clock granularity and the
        // elapsed-millisecond property truncates fractional milliseconds.
        Assert.True(sw.Elapsed.TotalMilliseconds >= 4.0,
            $"Expected approximately 5ms, got {sw.Elapsed.TotalMilliseconds:F3}ms");
        Assert.True(sw.Elapsed.TotalMilliseconds < 500,
            $"Expected < 500ms, got {sw.Elapsed.TotalMilliseconds:F3}ms");
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
