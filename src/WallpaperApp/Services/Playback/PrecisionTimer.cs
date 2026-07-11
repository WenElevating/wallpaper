using System.Runtime.InteropServices;
using WallpaperApp.Interop;

namespace WallpaperApp.Services.Playback;

/// <summary>
/// High-precision wait using a waitable timer (~1ms resolution), replacing
/// Thread.Sleep which has ~15.6ms granularity on Windows by default. Used by
/// the render loop to pace frames without the jitter that caused the earlier
/// present-side throttle (commit af68e99) to be reverted.
/// </summary>
/// <remarks>
/// Not thread-safe; intended for use from a single dedicated thread (the
/// per-monitor render thread owns one instance via a <c>using</c> local).
/// </remarks>
public sealed class PrecisionTimer : IDisposable
{
    private const uint Infinite = 0xFFFFFFFF;

    private IntPtr _handle;
    private bool _disposed;

    public PrecisionTimer()
    {
        _handle = NativeMethods.CreateWaitableTimerW(IntPtr.Zero, bManualReset: true, lpTimerName: null);
        // If creation failed (resource exhaustion — extremely unlikely), _handle
        // stays Zero and Wait() falls back to a coarse Thread.Sleep. Pacing will
        // be less precise but still functional.
    }

    /// <summary>
    /// Block for approximately <paramref name="microseconds"/> microseconds.
    /// Values &lt;= 0 return immediately (no wait), matching Thread.Sleep(0) semantics.
    /// </summary>
    public void Wait(long microseconds)
    {
        if (_disposed) return;
        if (microseconds <= 0) return;
        if (_handle == IntPtr.Zero)
        {
            // Fallback if timer creation failed: coarse sleep, truncated to ms.
            System.Threading.Thread.Sleep((int)System.Math.Min(microseconds / 1000, int.MaxValue));
            return;
        }

        // SetWaitableTimer dueTime is in 100ns units; negative = relative interval.
        var dueTime100ns = -microseconds * 10;
        if (!NativeMethods.SetWaitableTimer(_handle, ref dueTime100ns, lPeriod: 0,
                pfnCompletionRoutine: IntPtr.Zero, lpArgToCompletionRoutine: IntPtr.Zero, fResume: false))
        {
            // Timer set failed — fall back to coarse sleep.
            System.Threading.Thread.Sleep((int)System.Math.Min(microseconds / 1000, int.MaxValue));
            return;
        }

        // Block until the timer signals. INFINITE wait is safe because the timer
        // WILL fire (period=0 means one-shot, no indefinite repeat).
        NativeMethods.WaitForSingleObject(_handle, dwMilliseconds: Infinite);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
