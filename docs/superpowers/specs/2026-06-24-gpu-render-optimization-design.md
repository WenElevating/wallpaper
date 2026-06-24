# GPU Render Optimization Design

Date: 2026-06-24

## Problem

The wallpaper app has already reduced CPU usage by moving the main playback path to FFmpeg D3D11VA hardware decode and a DXGI renderer. Users still see high GPU usage, sometimes around 30-40%, because visible video wallpapers still require continuous per-frame GPU work.

The current zero-copy path is structurally sound:

- `App.xaml.cs` creates a shared `GpuDevice` and assigns it to `PlaybackManager`.
- `PlaybackManager` creates `FfmpegBackend` with a D3D11VA hardware-device provider.
- `PlaybackSession` initializes `DxgiRenderer.TryInitZeroCopy()` and sets `FfmpegBackend.PreferZeroCopy`.
- `FfmpegBackend` returns D3D11 texture frames for hardware-decoded video.
- `DxgiRenderer` copies the decoded NV12 texture, runs a fullscreen NV12-to-RGB shader, and presents through a flip-discard swap chain.

This avoids the old CPU-heavy color conversion path, but the GPU still performs hardware video decode, texture copy, shader conversion, and presentation work for every source frame on every active monitor.

## Goal

Reduce GPU usage while preserving:

- low CPU usage,
- stable wallpaper playback,
- existing pause behavior,
- render-thread ownership rules,
- zero-copy hardware decode when available,
- no-blank wallpaper switching.

The first implementation should target the highest-probability, lowest-risk wins: measurement, frame pacing, and user-selectable performance profiles.

## Non-Goals

- Do not replace FFmpeg.
- Do not remove the existing DXGI/NV12 shader path.
- Do not introduce new dependencies.
- Do not rewrite the desktop WorkerW integration.
- Do not make D3D11 video processor the default in the first pass.
- Do not change library import/transcoding behavior in this spec.

## Design Principles

1. Measure before claiming performance wins.
2. Reduce submitted frames before rewriting rendering internals.
3. Preserve the current working zero-copy path as the default rendering foundation.
4. Make performance tradeoffs explicit to the user.
5. Keep pause reasons and throttle policies separate.

## Proposed Approach

Implement GPU optimization in a staged design:

1. Add playback/render counters and benchmark guidance.
2. Add global wallpaper frame-rate limiting.
3. Add performance profiles that map to frame-rate behavior.
4. Leave resolution budgeting and D3D11 video processor as follow-up specs unless phase-one measurements show they are needed immediately.

This intentionally favors a narrow first version. The biggest controllable cost today is how often the app runs the copy/shader/present path. A frame-rate cap can reduce that work without destabilizing decoder setup, swap-chain creation, or desktop attachment.

## User-Facing Behavior

Settings gets a new performance section.

Profile choices:

- Quality: native/source frame rate.
- Balanced: 30 FPS cap.
- Saver: 15 FPS cap.

Default: Balanced.

Rationale: a video wallpaper is background ambience, not foreground media playback. Balanced should reduce GPU load for 60 FPS wallpapers while remaining smooth enough for normal desktop use. Quality remains available for users who prefer maximum smoothness.

Existing settings remain:

- pause on fullscreen,
- pause on battery,
- pause on remote session.

Battery behavior:

- If `PauseOnBattery` is enabled, current pause behavior remains unchanged.
- If `PauseOnBattery` is disabled, the selected profile still applies.

Changing profile should affect active sessions without requiring app restart. If live retiming is simpler and safe, update active sessions in place. If restart is needed, reuse the current no-blank replacement pattern rather than tearing down the visible wallpaper first.

## Architecture

### Settings Model

Extend `AppSettings` with a render performance setting.

Recommended shape:

```csharp
public enum WallpaperPerformanceProfile
{
    Quality,
    Balanced,
    Saver
}
```

`AppSettings` adds:

```csharp
public WallpaperPerformanceProfile PerformanceProfile { get; init; } = WallpaperPerformanceProfile.Balanced;
```

The runtime maps profiles to FPS caps:

- Quality: no cap beyond source timing.
- Balanced: 30 FPS.
- Saver: 15 FPS.

Keep this as an enum rather than a raw numeric field for the first version. A profile is easier to localize, explain, test, and evolve. Raw numeric custom FPS can be added later if users need it.

### Playback Policy

Introduce a small value object for effective playback limits.

Suggested concept:

```csharp
public readonly record struct PlaybackPerformancePolicy(int? MaxPresentFps);
```

Responsibilities:

- convert settings/profile to runtime limits,
- give `PlaybackManager` a single object to pass into new sessions,
- allow active sessions to update their policy.

This policy should not contain pause reasons. Pause remains controlled by `PauseReason` and `PlaybackSession.ApplyPauseAsync` / `ClearPauseAsync`.

### Playback Manager

`PlaybackManager` should own the current performance policy and pass it into `PlaybackSession`.

Needed behavior:

- newly created sessions receive the current policy,
- active sessions can be updated when settings change,
- policy updates do not resume a manually paused session,
- policy updates do not stop playback.

The simplest public surface is:

```csharp
public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy);
```

### Playback Session

`PlaybackSession.RenderLoop` currently decodes the next frame, sleeps based on source PTS, and presents every frame. Add an additional presentation gate after timing has produced a frame but before `Present`.

Required counters:

- decoded frames,
- presented frames,
- skipped frames,
- last summary timestamp.

Frame cap behavior:

- Quality/native: present as today.
- Balanced/Saver: present only when enough wall-clock time has elapsed since the last presented frame.
- Skipped frames must still be disposed.
- End-of-stream looping remains unchanged.
- Pause still sleeps and does not decode.

Important GPU-frame lifetime rule:

`FfmpegBackend` keeps a hardware frame alive until the next `NextFrameAsync` call. Skipping a GPU `FrameData` must not keep that frame pinned forever. The existing lifecycle releases the previous held frame at the start of the next decode call, so a skipped frame should be disposed normally and the next decode should release it. Tests should exercise this path with fake frames even though the true COM lifetime is native.

### Diagnostics

Add periodic Debug logging from playback sessions:

```text
Playback perf monitor=<id> path=zero-copy decoded=60/s presented=30/s skipped=30/s fpsCap=30
```

Log interval: 30 seconds.

This gives a lightweight built-in performance signal without adding dependencies or requiring external tooling for every run.

### Settings UI

Add one compact setting row to `SettingsView`:

- label: Wallpaper performance
- control: combo box or segmented equivalent using existing WPF style
- choices: Quality, Balanced, Saver

Localized strings should be added to `Strings.cs` resources using the existing localization pattern.

The existing settings page already has performance-related toggles. Place the new row near fullscreen/battery/remote-session pause settings.

## Data Flow

1. App startup loads `AppSettings`.
2. `MainViewModel.Settings` exposes `PerformanceProfile`.
3. `App.xaml.cs` or `MainViewModel` maps the profile to `PlaybackPerformancePolicy`.
4. `PlaybackManager` stores the current policy.
5. `PlaybackManager.SetWallpaperAsync` passes the policy into each new `PlaybackSession`.
6. `PlaybackSession.RenderLoop` uses the policy to decide whether to present or skip decoded frames.
7. Settings changes save JSON and call `PlaybackManager.UpdatePerformancePolicy`.
8. Active sessions use the new policy on the next render-loop iteration.

## Error Handling

- Invalid/missing settings JSON should continue to fall back to `new AppSettings()`.
- Unknown enum values should fall back to Balanced if practical.
- If logging fails, playback must continue.
- If policy update races with session stop, ignore the stopped session.
- If a frame is skipped, dispose it in the same loop iteration.

## Testing Strategy

Unit tests:

- `SettingsService` persists and reloads `PerformanceProfile`.
- `PlaybackManager` passes the current policy to new sessions.
- `PlaybackManager.UpdatePerformancePolicy` updates active fake sessions.
- `PlaybackSession` with a 30 FPS policy presents fewer frames than it decodes when fed faster fake PTS/wall-clock data.
- Native/Quality mode preserves current present-every-frame behavior.
- Pause reasons still prevent decode/present and still resume only after the last reason clears.
- End-of-stream looping still works when FPS cap is active.

Manual performance verification:

- Test 1080p60 and 4K60 wallpapers.
- Record Task Manager GPU usage before/after.
- Record GPU engine split when possible.
- Use PresentMon for process-level present timing where available.
- Compare Quality, Balanced, and Saver.
- Verify covered/fullscreen/battery/remote-session pauses still work.

Acceptance targets:

- Balanced with a 60 FPS source presents about 30 FPS.
- Saver with a 60 FPS source presents about 15 FPS.
- GPU usage measurably decreases in Balanced/Saver versus Quality in a visible desktop scenario.
- CPU usage does not materially regress.
- No render failure, stuck pause, blank wallpaper gap, or unbounded logging.

## Risks

### Decode Work May Continue At Source FPS

The first version skips presentation, not decode. Hardware video decode may still run at source FPS, so total GPU reduction depends on whether the user's GPU load is dominated by video decode or by copy/shader/present/composition.

Mitigation: add counters and use PresentMon/GPU engine split. If Video Decode remains the dominant cost, create a follow-up spec for decode-level frame dropping or import-time lower-FPS variants.

### Motion May Look Less Smooth

15 FPS and 30 FPS are tradeoffs.

Mitigation: expose Quality mode and make the choice reversible.

### Timing Tests Can Be Flaky

Render-loop timing currently uses `Stopwatch` and `Thread.Sleep`.

Mitigation: design the frame cap as a small testable policy helper where possible, so unit tests can use deterministic timestamps rather than real sleeps.

### Settings Changes May Need Session Plumbing

Existing sessions do not appear to receive many live setting updates except via pause controllers.

Mitigation: add a narrow `UpdatePerformancePolicy` path rather than broad settings injection.

## Follow-Up Specs

These are intentionally outside the first implementation:

1. Resolution budget profile: render 4K wallpapers at 1440p/1080p internal budget.
2. D3D11 VideoProcessor renderer: prototype `ID3D11VideoProcessorBlt` as an alternative to the NV12 pixel shader.
3. Per-monitor visibility throttling: throttle barely visible monitors without globally pausing all sessions.
4. Import-time optimized variants: optionally generate lower-FPS/lower-resolution wallpaper copies for saver mode.

## Open Decisions

The first implementation should answer these through measurement:

- Does Balanced reduce the user's observed GPU usage enough?
- Is the dominant GPU engine Video Decode, 3D, Copy, or DWM composition?
- Is 30 FPS acceptable as the default background-wallpaper experience?

Until those are measured, avoid deeper renderer rewrites.
