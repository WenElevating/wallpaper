# GPU Frame-Rate Throttling Implementation Plan (waitable-timer path)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce wallpaper GPU usage by re-enabling the Balanced/Saver performance profiles with high-precision frame-rate throttling, replacing the removed coarse `Thread.Sleep` gate with a waitable-timer that eliminates the ~15ms timer-granularity jitter that caused the earlier revert.

**Architecture:** The render loop (`PlaybackSession.RenderLoop`) currently paces with `Thread.Sleep(ms)` (PTS pacing) and has a dead present-side gate (`ShouldPresentFrame`, disabled because all profiles set `MaxPresentFps=null`). This plan re-enables `MaxPresentFps` (Balanced=30, Saver=15) and replaces BOTH sleep points with a single waitable timer (`CreateWaitableTimer`/`SetWaitableTimer`/`WaitForSingleObject`) for ~1ms precision. The present-side gate (`ShouldPresentFrame`) is kept and made functional again — it decides whether to present or skip, but the pacing wait that feeds it is now high-precision.

**Why this approach (probe result):** A prior probe (`tests/WallpaperApp.FilterProbe/`) confirmed that FFmpeg's libavfilter `fps` filter **rejects `AV_PIX_FMT_D3D11`** (error: "Setting BufferSourceContext.pix_fmt to a HW format requires hw_frames_ctx to be non-NULL"). This means the fps-filter approach can only throttle the software/CPU decode path — not the zero-copy D3D11 path that dominates GPU usage. The waitable-timer approach applies to ALL paths uniformly. The filter-probe code remains in the repo as a documented dead-end for future reference.

**Tech Stack:** C# / .NET 8 (net8.0-windows), raw `[LibraryImport]` P/Invoke (kernel32 waitable timers), xUnit.

## Global Constraints

- **Platform:** Windows-only (net8.0-windows).
- **No new dependencies.** Waitable timers are kernel32 — already P/Invoked via `NativeMethods.cs`.
- **Render-thread ownership:** D3D/D2D/window objects for a session are created AND used on one dedicated `WallpaperRender-{monitorId}` thread. Do not break this.
- **Pause-reason accounting:** Pause remains controlled by `PauseReason` + `ApplyPauseAsync`/`ClearPauseAsync`. Performance policy is a SEPARATE axis.
- **No-blank wallpaper switching:** Preserve the existing "build new session → swap → dispose old" pattern.
- **Commit directive from `af68e99`:** "Do not reintroduce post-decode frame skipping as the default Balanced/Saver behavior without user-visible jitter validation." This plan addresses the jitter root cause (coarse `Thread.Sleep` ~15ms granularity) by using a high-precision waitable timer (~1ms). The validation task (Task 4) uses PresentMon guidance to confirm smoothness before declaring done.
- **P/Invoke style:** `[LibraryImport]` source-generator pattern matching `NativeMethods.cs`. `kernel32.dll` sync primitives already use `SetLastError=true` + `MarshalAs(Bool)` for returns — match that.
- **Build/test:** `dotnet build WallpaperApp.sln` and `dotnet test tests/WallpaperApp.Tests`. xUnit.

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/WallpaperApp/Interop/NativeMethods.cs` | Win32 P/Invoke | **Modify** — add `CreateWaitableTimerW`, `SetWaitableTimer` (CloseHandle + WaitForSingleObject already exist) |
| `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs` | Profile→policy mapping | **Modify** — re-enable `MaxPresentFps`: Balanced=30, Saver=15, Quality=null |
| `src/WallpaperApp/Services/Playback/PrecisionTimer.cs` | NEW — thin wrapper over the waitable-timer API for sub-frame waits | **Create** |
| `src/WallpaperApp/Services/Playback/PlaybackSession.cs` | Render loop | **Modify** — replace `Thread.Sleep` pacing (line 354) with `PrecisionTimer.Wait`, wire `ShouldPresentFrame` gate to the new policy values |
| `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs` | Policy tests | **Modify** — update for new FromProfile mapping |
| `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs` | Session tests | **Modify** — update ShouldPresentFrame expectations |

---

## Task 1: Add waitable-timer P/Invokes to NativeMethods.cs

**Files:**
- Modify: `src/WallpaperApp/Interop/NativeMethods.cs` (add near the existing kernel32 sync primitives, around line 413)

**Interfaces:**
- Consumes: nothing
- Produces: `NativeMethods.CreateWaitableTimerW`, `NativeMethods.SetWaitableTimer` for `PrecisionTimer` (Task 2). (`CloseHandle` at line 402 and `WaitForSingleObject` at line 409 already exist — reuse them.)

- [ ] **Step 1: Add the two missing P/Invoke declarations**

In `src/WallpaperApp/Interop/NativeMethods.cs`, after `ResetEvent` (line 413) and before the "Wallpaper visibility" section comment (line 415), add:

```csharp
    // ---------- High-precision pacing (waitable timer) ----------
    // Used by PlaybackSession's render loop for frame pacing that doesn't
    // suffer from Thread.Sleep's ~15.6ms default timer-resolution granularity.
    // SetWaitableTimer with a negative dueTime (in 100ns units) gives ~1ms waits.

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWaitableTimerW(IntPtr lpTimerAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, string? lpTimerName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(IntPtr hTimer, ref long lpDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, [MarshalAs(UnmanagedType.Bool)] bool fResume);
```

Note: `CreateWaitableTimerW` (the Unicode variant) is used rather than the auto-routed `CreateWaitableTimer` to be explicit, matching the existing `CreateEventW` style at line 397. The `lpName` is `null` (unnamed timer). `bManualReset=true` so the timer stays signaled until we re-arm it (manual reset).

- [ ] **Step 2: Build to verify the source generator emits the P/Invokes**

Run: `dotnet build src/WallpaperApp/WallpaperApp.csproj`
Expected: build succeeds. Nothing calls them yet.

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Interop/NativeMethods.cs
git commit -m "Add CreateWaitableTimerW + SetWaitableTimer P/Invokes for high-precision pacing"
```

---

## Task 2: Create PrecisionTimer — waitable-timer wrapper

**Files:**
- Create: `src/WallpaperApp/Services/Playback/PrecisionTimer.cs`
- Test: `tests/WallpaperApp.Tests/Services/PrecisionTimerTests.cs`

**Interfaces:**
- Consumes: `NativeMethods.CreateWaitableTimerW`, `NativeMethods.SetWaitableTimer`, `NativeMethods.WaitForSingleObject`, `NativeMethods.CloseHandle`
- Produces:

```csharp
public sealed class PrecisionTimer : IDisposable
{
    public PrecisionTimer();                                   // creates the waitable timer handle
    public void Wait(long microseconds);                       // blocks ~microseconds (uses SetWaitableTimer + WaitForSingleObject)
    public void Dispose();                                     // CloseHandle
}
```

Used by `PlaybackSession` (Task 3) to replace `Thread.Sleep` for frame pacing.

- [ ] **Step 1: Write the failing test**

Create `tests/WallpaperApp.Tests/Services/PrecisionTimerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PrecisionTimerTests"`
Expected: FAIL — "type or namespace 'PrecisionTimer' could not be found".

- [ ] **Step 3: Implement PrecisionTimer**

Create `src/WallpaperApp/Services/Playback/PrecisionTimer.cs`:

```csharp
using System.Runtime.InteropServices;

namespace WallpaperApp.Services.Playback;

/// <summary>
/// High-precision wait using a waitable timer (~1ms resolution), replacing
/// Thread.Sleep which has ~15.6ms granularity on Windows by default. Used by
/// the render loop to pace frames without the jitter that caused the earlier
/// present-side throttle (commit af68e99) to be reverted.
/// </summary>
public sealed class PrecisionTimer : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public PrecisionTimer()
    {
        _handle = NativeMethods.CreateWaitableTimerW(IntPtr.Zero, bManualReset: true, lpTimerName: null);
        if (_handle == IntPtr.Zero)
        {
            // Extremely unlikely (only under resource exhaustion). Fall back to
            // a sentinel that makes Wait a no-op — the caller's PTS pacing will
            // still work via VSync back-pressure, just less precisely.
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Block for approximately <paramref name="microseconds"/> microseconds.
    /// Values <= 0 return immediately (no wait), matching Thread.Sleep(0) semantics.
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
        NativeMethods.WaitForSingleObject(_handle, dwMilliseconds: 0xFFFFFFFF);
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PrecisionTimerTests"`
Expected: 3 PASS. (The 5ms test should report actual elapsed in range.)

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PrecisionTimer.cs tests/WallpaperApp.Tests/Services/PrecisionTimerTests.cs
git commit -m "Add PrecisionTimer — waitable-timer wrapper for high-precision waits"
```

---

## Task 3: Re-enable MaxPresentFps in FromProfile

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs` (lines 18-23)
- Modify: `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `FromProfile` returns non-null `MaxPresentFps` for Balanced (30) and Saver (15); Quality stays null. `MinFrameIntervalUs` becomes non-zero for those, reactivating `ShouldPresentFrame`.

- [ ] **Step 1: Update the failing test first**

In `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`, update/add tests for the new FromProfile mapping. Read the existing file first to match its style, then ensure these cases are covered:

```csharp
[Theory]
[InlineData(WallpaperPerformanceProfile.Quality, null)]
[InlineData(WallpaperPerformanceProfile.Balanced, 30)]
[InlineData(WallpaperPerformanceProfile.Saver, 15)]
public void FromProfile_SetsMaxPresentFps(WallpaperPerformanceProfile profile, int? expectedFps)
{
    var policy = PlaybackPerformancePolicy.FromProfile(profile);
    Assert.Equal(expectedFps, policy.MaxPresentFps);
}

[Fact]
public void FromProfile_SaverKeepsNonReferenceDiscard()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Saver);
    Assert.Equal(DecoderFrameDiscard.NonReference, policy.DecoderDiscard);
}

[Fact]
public void MinFrameIntervalUs_ForBalanced_IsOneOver30()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
    Assert.Equal(1_000_000L / 30, policy.MinFrameIntervalUs);
}
```

If existing tests assert `MaxPresentFps == null` for Balanced/Saver, update them to the new values (those assertions are now wrong by design).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"`
Expected: FAIL — current `FromProfile` returns `null` for Balanced, but test expects 30.

- [ ] **Step 3: Update FromProfile**

In `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`, replace the `FromProfile` body (lines 18-23):

```csharp
    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15, DecoderFrameDiscard.NonReference),
            WallpaperPerformanceProfile.Balanced => new PlaybackPerformancePolicy(30, DecoderFrameDiscard.Default),
            _ => new PlaybackPerformancePolicy(null, DecoderFrameDiscard.Default),  // Quality: passthrough (no cap)
        };
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs
git commit -m "Re-enable MaxPresentFps: Balanced=30, Saver=15 (waitable-timer path)"
```

---

## Task 4: Wire PrecisionTimer into PlaybackSession render loop, reactivate the present gate

This is the central integration. It replaces the coarse `Thread.Sleep` pacing with `PrecisionTimer.Wait` and makes the `ShouldPresentFrame` gate functional again (it was dormant because `MaxPresentFps` was null; Task 3 re-enabled the values). This applies uniformly to zero-copy AND software paths.

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
- Modify: `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`

**Interfaces:**
- Consumes: `PrecisionTimer` (Task 2), `PlaybackPerformancePolicy.MaxPresentFps`/`MinFrameIntervalUs` (Task 3), existing `ShouldPresentFrame` (line 138-142 — keep it, it's correct)
- Produces: a render loop that throttles to MaxPresentFps via high-precision wait + present gate.

- [ ] **Step 1: Read the full RenderLoop and update tests first**

Read `src/WallpaperApp/Services/Playback/PlaybackSession.cs` lines 130-145 (`ShouldPresentFrame`) and 298-403 (`RenderLoop`) completely before editing.

In `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`, find tests that reference `ShouldPresentFrame` or frame-skipping behavior. The existing `ShouldPresentFrame` is a pure static function — tests likely feed it timestamps. Those tests should STILL pass (the function isn't changing). But if any test asserts "Balanced policy presents every frame" (relying on the dormant gate), update it: Balanced now has MaxPresentFps=30, so frames arriving faster than 33.3ms apart will be skipped by the gate.

Add/verify a test confirming the gate logic with the new policy values:

```csharp
[Fact]
public void ShouldPresentFrame_BalancedPolicy_SkipsFramesCloserThanInterval()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
    var intervalUs = policy.MinFrameIntervalUs;  // ~33333us for 30fps

    // First frame always presents (lastPresentedUs < 0).
    Assert.True(PlaybackSession.ShouldPresentFrame(0, -1, policy));
    // Frame 10ms later: skipped (under 33.3ms interval).
    Assert.False(PlaybackSession.ShouldPresentFrame(10_000, 0, policy));
    // Frame 34ms later: presented.
    Assert.True(PlaybackSession.ShouldPresentFrame(34_000, 0, policy));
}

[Fact]
public void ShouldPresentFrame_QualityPolicy_PresentsEverything()
{
    var policy = PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Quality);
    Assert.True(PlaybackSession.ShouldPresentFrame(0, -1, policy));
    Assert.True(PlaybackSession.ShouldPresentFrame(100, 0, policy));   // no cap
}
```

(Adjust namespace/visibility: `ShouldPresentFrame` is `internal static` — tests access it via the test project's InternalsVisibleTo.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackSessionTests"`
Expected: some FAIL (the Balanced skip test fails because current FromProfile returns null cap → ShouldPresentFrame returns true for all).

- [ ] **Step 3: Add a PrecisionTimer field to PlaybackSession and dispose it**

In `src/WallpaperApp/Services/Playback/PlaybackSession.cs`, find where session fields are declared and the existing `Dispose`/`Stop` path. Add a field:

```csharp
private PrecisionTimer? _pacingTimer;
```

Initialize it at the start of `RenderLoop` (or in the `Run` method just before the loop) and dispose it when the loop exits. The cleanest approach: create it at the top of `RenderLoop` and dispose in a `try/finally` around the `while` loop:

```csharp
    private void RenderLoop(CancellationToken ct)
    {
        _pacingTimer = new PrecisionTimer();
        try
        {
            RenderLoopCore(ct);
        }
        finally
        {
            _pacingTimer.Dispose();
            _pacingTimer = null;
        }
    }
```

Then rename the current `RenderLoop` body to `RenderLoopCore`. (Or simpler: add `using var pacingTimer = new PrecisionTimer();` as the first line of the existing RenderLoop and reference it locally — avoid the field entirely. PREFER THIS simpler approach since the timer is only used inside the loop.)

Use the local approach:
- Line 299 (first line inside `RenderLoop`): add `using var pacingTimer = new PrecisionTimer();`
- Line 354: replace `Thread.Sleep((int)Math.Min(waitUs / 1000, int.MaxValue));` with `pacingTimer.Wait(waitUs);`

- [ ] **Step 4: Replace the PTS-pacing Thread.Sleep with PrecisionTimer.Wait**

Change line 353-354 from:

```csharp
                if (waitUs > 0)
                    Thread.Sleep((int)Math.Min(waitUs / 1000, int.MaxValue));
```

to:

```csharp
                if (waitUs > 0)
                    pacingTimer.Wait(waitUs);
```

- [ ] **Step 5: Verify the present gate is now functional (no code change needed, but confirm)**

The gate at lines 367-374 (`ShouldPresentFrame(nowUs, lastPresentedUs, policy)`) is already wired. With Task 3's FromProfile change, `policy.MinFrameIntervalUs` is now non-zero for Balanced/Saver, so `ShouldPresentFrame` returns false for frames arriving too fast → they're skipped (`skippedFrames++`, disposed, continue). No code change needed here — Task 3 reactivated it. Just verify by reading that the gate path is intact.

- [ ] **Step 6: Run all tests**

Run: `dotnet test tests/WallpaperApp.Tests`
Expected: ALL PASS. The gate tests from Step 1 now pass because FromProfile returns the cap. Existing pause-reason / EOF-loop tests should be unaffected.

- [ ] **Step 7: Build the whole solution to catch any breakage**

Run: `dotnet build WallpaperApp.sln`
Expected: succeeds, no new warnings beyond pre-existing ones.

- [ ] **Step 8: Commit**

```bash
git add src/WallpaperApp/Services/Playback/PlaybackSession.cs tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs
git commit -m "Use PrecisionTimer for render-loop pacing; reactivate present gate for Balanced/Saver

Replaces the coarse Thread.Sleep (~15.6ms granularity) frame pacing with a
high-precision waitable timer (~1ms). Combined with re-enabled MaxPresentFps
(Balanced=30, Saver=15), the ShouldPresentFrame gate is functional again.
This addresses the jitter root cause that caused af68e99 to revert the cap."
```

---

## Task 5: Full build, test sweep, and validation guidance

**Files:** none (verification + docs only)

- [ ] **Step 1: Clean rebuild + full unit test suite**

Run: `dotnet build WallpaperApp.sln && dotnet test tests/WallpaperApp.Tests`
Expected: build OK, all unit tests PASS.

- [ ] **Step 2: Run smoke tests (requires ffmpeg.exe on PATH)**

Run: `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~Smoke"`
Expected: PASS (confirms real decode still works end-to-end through the modified render loop).

- [ ] **Step 3: Manual validation — compare GPU usage across profiles**

Launch the app with a 1080p60 (and 4K60 if available) wallpaper. For each, cycle Quality → Balanced → Saver and observe Task Manager GPU% (and the 3D/Video Decode engine split if visible). Record before/after numbers.

Expected:
- Quality ≈ source FPS, GPU% unchanged from baseline (no cap).
- Balanced ≈ 30 FPS, GPU% measurably lower than Quality.
- Saver ≈ 15 FPS, GPU% lowest of the three.
- **No visible stutter/jitter distinct from the old present-side skip behavior** — the waitable timer's ~1ms precision should make pacing smooth. If jitter is still visible, the `af68e99` directive is NOT satisfied and this must be reported.
- No blank flash on wallpaper switch, no crash on profile hot-switch.

- [ ] **Step 4: Manual validation — PresentMon (recommended)**

Run PresentMon against the app process. Confirm:
- Present count/s ≈ target FPS for Balanced (30) and Saver (15).
- Present timing is evenly spaced (not bursty) — the waitable timer should produce a steady cadence.

- [ ] **Step 5: Commit validation notes if written**

If a notes file was created, commit it; otherwise this step is a no-op.

---

## Self-Review Notes

**Spec coverage check** (corrected approach vs original spec's phase 1):
- "Re-enable Balanced/Saver profiles with smoother mechanism than old present-side skip" → Tasks 3+4 (PrecisionTimer replaces Thread.Sleep, gate reactivated). Covered.
- "Probe validates D3D11 compatibility" → DONE in prior session (result B/C, committed as FilterProbe). The probe is retained as a documented dead-end.
- "Delete dead present gate" → NOT applicable: the gate is now functional again (reactivated by Task 3). The spec's "delete dead code" instruction was predicated on the filter path working; since the filter path is dead, the gate is the mechanism. Adjusted.
- "filterDropped counter" → NOT applicable (no filter). The existing `skippedFrames` counter now measures present-side skips, which is the correct metric for this path. Adjusted.

**Type consistency:**
- `PrecisionTimer.Wait(long microseconds)` — called in Task 4 step 4 as `pacingTimer.Wait(waitUs)` where `waitUs` is `long`. ✓
- `FromProfile` returns `PlaybackPerformancePolicy(int?, DecoderFrameDiscard)` — Task 3 passes `15`/`30`/`null` as first arg. ✓
- `ShouldPresentFrame(long, long, PlaybackPerformancePolicy)` — unchanged signature, tests call it directly. ✓

**Risks flagged for implementer:**
- Task 4 step 3: the local `using var pacingTimer` approach is preferred over a field. If the render loop is structured such that a `using` local can't span the method (e.g. early returns), fall back to a try/finally. Flagged.
- The `0xFFFFFFFF` INFINITE constant in `WaitForSingleObject` — verify this is acceptable or use a named constant. Flagged in Task 2.
- Validation (Task 5 step 3-4) requires real hardware + PresentMon. If jitter is observed, do NOT claim success — report it.
