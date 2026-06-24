# GPU Policy Decode Throttle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix performance profiles so Balanced stops causing post-decode jitter and Saver pushes reduction into FFmpeg decode policy.

**Architecture:** Extend `PlaybackPerformancePolicy` with a decoder discard mode, propagate policy changes from `PlaybackSession` to backends that support it, and map Saver to FFmpeg `skip_frame=AVDISCARD_NONREF` via the option API. Keep render-thread ownership and zero-copy rendering unchanged.

**Tech Stack:** C#/.NET 8 WPF, xUnit, FFmpeg P/Invoke, D3D11VA/DXGI via existing Vortice pipeline.

---

## Task 1: Policy Model

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`
- Test: `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`

- [ ] Add `DecoderFrameDiscard` enum with `Default` and `NonReference`.
- [ ] Change `PlaybackPerformancePolicy` to store `MaxPresentFps` and `DecoderDiscard`.
- [ ] Map `Quality` and `Balanced` to native FPS + default discard.
- [ ] Map `Saver` to native FPS + `NonReference` discard.
- [ ] Update tests so Balanced expects no present cap and Saver expects decoder discard.

## Task 2: Backend Policy Hook

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/IPlaybackBackend.cs`
- Modify: `src/WallpaperApp/Services/Playback/FfmpegBackend.cs`
- Test: `tests/WallpaperApp.Tests/Services/FfmpegBackendTests.cs`

- [ ] Add `UpdatePerformancePolicy(PlaybackPerformancePolicy policy)` to `IPlaybackBackend`.
- [ ] Implement no-op in `MfBackend` and test fakes.
- [ ] Add `FfmpegBackend.CurrentPerformancePolicyForTests`.
- [ ] Store the latest policy in `FfmpegBackend`.
- [ ] When codec context exists, set FFmpeg `skip_frame` option based on policy.

## Task 3: Render Loop Behavior

**Files:**
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
- Test: `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`

- [ ] Send the current performance policy to the backend after open and whenever the loop observes a changed policy.
- [ ] Replace the capped-mode test with a Balanced test that presents every decoded frame.
- [ ] Add a Saver test proving the policy is pushed to the backend.
- [ ] Keep `ShouldPresentFrame` available for future caps, but current profile mapping should not use it for Balanced/Saver.

## Task 4: Verification

**Files:**
- No source changes.

- [ ] Run focused tests:
  `dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests|FullyQualifiedName~PlaybackSessionTests|FullyQualifiedName~FfmpegBackendTests"`
- [ ] Run full tests:
  `dotnet test tests/WallpaperApp.Tests --no-restore`
- [ ] Run build:
  `dotnet build WallpaperApp.sln`
- [ ] Commit with Lore trailers.
