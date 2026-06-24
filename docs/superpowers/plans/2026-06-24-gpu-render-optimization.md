# GPU Render Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add measurable GPU-saving controls for video wallpapers through playback diagnostics, frame-rate limiting, and Quality/Balanced/Saver performance profiles.

**Architecture:** Keep the existing FFmpeg D3D11VA + DXGI zero-copy renderer. Add a small playback policy model that maps persisted settings to a max-present-FPS limit, pass that policy through `PlaybackManager` into `PlaybackSession`, and gate `Present()` calls without changing decoder or renderer ownership. Add debug counters so GPU changes can be verified before considering deeper renderer rewrites.

**Tech Stack:** C#/.NET 8 WPF, xUnit, FFmpeg interop, D3D11/DXGI via Vortice, JSON settings via `System.Text.Json`.

---

## Source Spec

- `docs/superpowers/specs/2026-06-24-gpu-render-optimization-design.zh-CN.md`
- English reference: `docs/superpowers/specs/2026-06-24-gpu-render-optimization-design.md`

## File Structure

Create:

- `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`
  - Defines `PlaybackPerformancePolicy` and maps `WallpaperPerformanceProfile` to `MaxPresentFps`.

Modify:

- `src/WallpaperApp/Models/AppSettings.cs`
  - Add `WallpaperPerformanceProfile` enum and `PerformanceProfile` setting.
- `src/WallpaperApp/Services/Playback/PlaybackManager.cs`
  - Store current performance policy, pass it to new sessions, update active sessions.
- `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
  - Accept/update performance policy, gate presentation, log counters.
- `src/WallpaperApp/Services/Playback/IClock.cs`
  - Optional tiny test seam for deterministic frame pacing if render-loop tests are flaky.
- `src/WallpaperApp/App.xaml.cs`
  - Initialize `PlaybackManager` policy from loaded settings.
- `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`
  - Expose performance profile options and update settings/policy when changed.
- `src/WallpaperApp/UI/Views/SettingsView.xaml`
  - Add the profile selector row.
- `src/WallpaperApp/Localization/Strings.cs`
  - Add strongly typed string properties for the new labels.
- `src/WallpaperApp/Resources/Strings.resx`
  - Add English/default strings.
- `src/WallpaperApp/Resources/Strings.zh-CN.resx`
  - Add Chinese strings.
- `tests/WallpaperApp.Tests/Services/SettingsServiceTests.cs`
  - Cover settings persistence.
- `tests/WallpaperApp.Tests/Services/PlaybackManagerTests.cs`
  - Cover policy passing and policy updates.
- `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`
  - Cover frame-rate gating and existing pause behavior.

Do not modify:

- `FfmpegBackend.cs` unless tests reveal a frame lifetime bug.
- `DxgiRenderer.cs` beyond optional debug counters if the session-level counters are sufficient.
- Native FFmpeg offsets.

## Task 1: Add The Persisted Performance Setting

**Files:**

- Modify: `src/WallpaperApp/Models/AppSettings.cs`
- Test: `tests/WallpaperApp.Tests/Services/SettingsServiceTests.cs`

- [ ] **Step 1: Write a failing settings persistence test**

Add this test to `SettingsServiceTests`:

```csharp
[Fact]
public async Task SaveAndLoad_PersistsPerformanceProfile()
{
    var path = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
    try
    {
        var service = new SettingsService(path);
        var settings = new AppSettings
        {
            PerformanceProfile = WallpaperPerformanceProfile.Saver
        };

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal(WallpaperPerformanceProfile.Saver, loaded.PerformanceProfile);
    }
    finally
    {
        File.Delete(path);
    }
}
```

- [ ] **Step 2: Run the focused test and confirm it fails**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~SettingsServiceTests.SaveAndLoad_PersistsPerformanceProfile"
```

Expected: compile failure because `PerformanceProfile` and `WallpaperPerformanceProfile` do not exist.

- [ ] **Step 3: Add the enum and setting**

Modify `src/WallpaperApp/Models/AppSettings.cs`:

```csharp
namespace WallpaperApp.Models;

public enum WallpaperPerformanceProfile
{
    Quality,
    Balanced,
    Saver
}

// Settings bag, persisted as JSON by SettingsService. A record so language (and
// future) updates can use an immutable `with` copy instead of in-place mutation.
public record AppSettings
{
    public bool LaunchAtStartup { get; init; }
    public bool StartMinimizedToTray { get; init; }
    public bool GlobalPauseOnFullscreen { get; init; } = true;
    /// <summary>Pause all wallpapers while running on battery power (laptops).</summary>
    public bool PauseOnBattery { get; init; } = true;
    /// <summary>Pause all wallpapers during an RDP or Miracast session (bandwidth saver).</summary>
    public bool PauseOnRemoteSession { get; init; } = true;
    public FitMode DefaultFitMode { get; init; } = FitMode.Fill;
    public bool HardwareAccelerationEnabled { get; init; } = true;
    public WallpaperPerformanceProfile PerformanceProfile { get; init; } = WallpaperPerformanceProfile.Balanced;
    public string LogVerbosity { get; init; } = "Info";
    public string Theme { get; init; } = "Dark";
    /// <summary>Global hotkey bindings. Empty slots are unbound.</summary>
    public HotkeyBindings Hotkeys { get; init; } = new();
    /// <summary>UI language code ("zh-CN", "en"); empty = follow the OS UI language.</summary>
    public string Language { get; init; } = "";
    /// <summary>Library storage root. Empty = default (LocalAppData/WallpaperApp).
    /// Videos go in <root>/library, posters in <root>/posters.</summary>
    public string LibraryRoot { get; init; } = "";
}
```

- [ ] **Step 4: Run the settings tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~SettingsServiceTests"
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WallpaperApp/Models/AppSettings.cs tests/WallpaperApp.Tests/Services/SettingsServiceTests.cs
git commit -m "Persist wallpaper performance profile" -m "Wallpaper playback needs a durable quality/performance choice before runtime policy can be wired through the player. The setting defaults to Balanced so existing installs get the GPU-saving profile without losing the ability to choose Quality later.`n`nConstraint: Settings are JSON record values with init-only properties`nConfidence: high`nScope-risk: narrow`nTested: dotnet test tests/WallpaperApp.Tests --filter `"FullyQualifiedName~SettingsServiceTests`"`nNot-tested: Runtime UI selection"
```

## Task 2: Add PlaybackPerformancePolicy

**Files:**

- Create: `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`
- Test: `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`

- [ ] **Step 1: Write failing policy tests**

Create `tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs`:

```csharp
using WallpaperApp.Models;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class PlaybackPerformancePolicyTests
{
    [Theory]
    [InlineData(WallpaperPerformanceProfile.Quality, null)]
    [InlineData(WallpaperPerformanceProfile.Balanced, 30)]
    [InlineData(WallpaperPerformanceProfile.Saver, 15)]
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
        var policy = new PlaybackPerformancePolicy(fps);

        Assert.Equal(expected, policy.MinFrameIntervalUs);
    }
}
```

- [ ] **Step 2: Run the focused test and confirm it fails**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"
```

Expected: compile failure because `PlaybackPerformancePolicy` does not exist.

- [ ] **Step 3: Implement the policy**

Create `src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs`:

```csharp
using WallpaperApp.Models;

namespace WallpaperApp.Services.Playback;

public readonly record struct PlaybackPerformancePolicy(int? MaxPresentFps)
{
    public long MinFrameIntervalUs =>
        MaxPresentFps is > 0 ? 1_000_000L / MaxPresentFps.Value : 0L;

    public static PlaybackPerformancePolicy FromProfile(WallpaperPerformanceProfile profile)
        => profile switch
        {
            WallpaperPerformanceProfile.Quality => new PlaybackPerformancePolicy(null),
            WallpaperPerformanceProfile.Saver => new PlaybackPerformancePolicy(15),
            _ => new PlaybackPerformancePolicy(30),
        };
}
```

- [ ] **Step 4: Run policy tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackPerformancePolicyTests"
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WallpaperApp/Services/Playback/PlaybackPerformancePolicy.cs tests/WallpaperApp.Tests/Services/PlaybackPerformancePolicyTests.cs
git commit -m "Map wallpaper performance profiles to playback policy" -m "A small policy object keeps settings translation out of the render loop and gives tests a deterministic place to verify profile semantics.`n`nConstraint: First optimization pass only needs max-present-FPS, not resolution or decoder policy`nConfidence: high`nScope-risk: narrow`nTested: dotnet test tests/WallpaperApp.Tests --filter `"FullyQualifiedName~PlaybackPerformancePolicyTests`"`nNot-tested: Integration with active sessions"
```

## Task 3: Wire Policy Through PlaybackManager

**Files:**

- Modify: `src/WallpaperApp/Services/Playback/PlaybackManager.cs`
- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
- Test: `tests/WallpaperApp.Tests/Services/PlaybackManagerTests.cs`

- [ ] **Step 1: Add a narrow test hook for active session policy**

To verify `PlaybackManager` without changing production behavior, add this internal method to `PlaybackManager`:

```csharp
internal PlaybackPerformancePolicy? GetPerformancePolicyForTests(Guid monitorId)
{
    lock (_lock)
        return _sessions.TryGetValue(monitorId, out var session)
            ? session.PerformancePolicyForTests
            : null;
}
```

Add this internal property to `PlaybackSession`:

```csharp
internal PlaybackPerformancePolicy PerformancePolicyForTests => _performancePolicy;
```

If the test project cannot see internals, add `src/WallpaperApp/Properties/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("WallpaperApp.Tests")]
```

- [ ] **Step 2: Write failing tests for manager policy passing and updates**

In `PlaybackManagerTests`, add these tests:

```csharp
[Fact]
public async Task SetWallpaperAsync_PassesCurrentPerformancePolicyToSession()
{
    var backend = new FakePlaybackBackend(CreateFrame());
    var monitorId = Guid.NewGuid();
    using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
    using var desktopHost = new DesktopHost(_logger);
    using var manager = new PlaybackManager(
        _logger,
        desktopHost,
        createSurface: (_, _, _, _) => surface,
        createRenderer: (_, _, _, _) => new FakeRenderer(true),
        createBackend: () => backend,
        createFallbackBackend: () => new FakePlaybackBackend());

    manager.UpdatePerformancePolicy(new PlaybackPerformancePolicy(15));

    var ok = await manager.SetWallpaperAsync(monitorId, Guid.NewGuid(), "sample.mp4", 0, 0, 1, 1);

    Assert.True(ok);
    Assert.Equal(15, manager.GetPerformancePolicyForTests(monitorId)?.MaxPresentFps);
}

[Fact]
public async Task UpdatePerformancePolicy_UpdatesActiveSessionsWithoutStoppingPlayback()
{
    var backend = new FakePlaybackBackend(CreateFrame(), CreateFrame());
    var monitorId = Guid.NewGuid();
    using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
    using var desktopHost = new DesktopHost(_logger);
    using var manager = new PlaybackManager(
        _logger,
        desktopHost,
        createSurface: (_, _, _, _) => surface,
        createRenderer: (_, _, _, _) => new FakeRenderer(true),
        createBackend: () => backend,
        createFallbackBackend: () => new FakePlaybackBackend());

    var ok = await manager.SetWallpaperAsync(monitorId, Guid.NewGuid(), "sample.mp4", 0, 0, 1, 1);
    Assert.True(ok);

    manager.UpdatePerformancePolicy(new PlaybackPerformancePolicy(15));

    Assert.True(backend.IsPlaying);
    Assert.Equal(15, manager.GetPerformancePolicyForTests(monitorId)?.MaxPresentFps);
}
```

- [ ] **Step 3: Run focused manager tests and confirm they fail**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackManagerTests"
```

Expected: compile failure because `UpdatePerformancePolicy` and session policy constructor support do not exist.

- [ ] **Step 4: Add policy fields and session update API**

Modify `PlaybackSession` constructor to accept an optional final parameter:

```csharp
PlaybackPerformancePolicy performancePolicy = default
```

Add fields:

```csharp
private PlaybackPerformancePolicy _performancePolicy;
```

Initialize in constructor:

```csharp
_performancePolicy = performancePolicy;
```

Add public method:

```csharp
public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
{
    _performancePolicy = policy;
}
```

Modify `PlaybackManager`:

```csharp
private PlaybackPerformancePolicy _performancePolicy =
    PlaybackPerformancePolicy.FromProfile(WallpaperPerformanceProfile.Balanced);
```

Add:

```csharp
public void UpdatePerformancePolicy(PlaybackPerformancePolicy policy)
{
    PlaybackSession[] sessions;
    lock (_lock)
    {
        _performancePolicy = policy;
        sessions = _sessions.Values.ToArray();
    }

    foreach (var session in sessions)
        session.UpdatePerformancePolicy(policy);
}
```

Pass policy into new sessions in `SetWallpaperAsync`:

```csharp
PlaybackPerformancePolicy performancePolicy;
lock (_lock)
{
    _sessions.TryGetValue(monitorId, out oldSession);
    performancePolicy = _performancePolicy;
}
```

Then include it in the `new PlaybackSession(...)` call:

```csharp
_logger,
performancePolicy);
```

- [ ] **Step 5: Run manager and session tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackManagerTests|FullyQualifiedName~PlaybackSessionTests"
```

Expected: pass after any necessary test adjustment.

- [ ] **Step 6: Commit**

```powershell
git add src/WallpaperApp/Services/Playback/PlaybackManager.cs src/WallpaperApp/Services/Playback/PlaybackSession.cs src/WallpaperApp/Properties/AssemblyInfo.cs tests/WallpaperApp.Tests/Services/PlaybackManagerTests.cs
git commit -m "Thread performance policy through playback sessions" -m "Active wallpaper sessions need a narrow runtime policy channel so settings changes can alter frame pacing without restarting the app or touching pause reason accounting.`n`nConstraint: Policy updates must not resume or stop sessions`nConfidence: medium`nScope-risk: moderate`nTested: dotnet test tests/WallpaperApp.Tests --filter `"FullyQualifiedName~PlaybackManagerTests|FullyQualifiedName~PlaybackSessionTests`"`nNot-tested: Real wallpaper runtime update"
```

## Task 4: Implement Frame-Rate Gate And Counters

**Files:**

- Modify: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
- Create if needed: `src/WallpaperApp/Services/Playback/IClock.cs`
- Test: `tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs`

- [ ] **Step 1: Add deterministic clock seam only if render-loop tests need it**

Prefer testing through `PlaybackSession` with quick fake frames first. If the test is flaky because wall-clock timing is too fast or too slow, create `src/WallpaperApp/Services/Playback/IClock.cs`:

```csharp
namespace WallpaperApp.Services.Playback;

internal interface IClock
{
    long NowUs { get; }
}

internal sealed class StopwatchClock : IClock
{
    public long NowUs => Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency;
}
```

Add optional constructor parameter to `PlaybackSession`:

```csharp
IClock? clock = null
```

Store:

```csharp
private readonly IClock _clock;
```

Initialize:

```csharp
_clock = clock ?? new StopwatchClock();
```

Use `_clock.NowUs` for the frame gate. Test fake:

```csharp
private sealed class FakeClock : IClock
{
    private readonly Queue<long> _values;

    public FakeClock(params long[] values)
    {
        _values = new Queue<long>(values);
    }

    public long NowUs => _values.Count > 0 ? _values.Dequeue() : long.MaxValue;
}
```

- [ ] **Step 2: Refactor the gate into a deterministic helper**

Before changing the render loop, create a private static helper in `PlaybackSession`:

```csharp
private static bool ShouldPresentFrame(long nowUs, long lastPresentedUs, PlaybackPerformancePolicy policy)
{
    var minIntervalUs = policy.MinFrameIntervalUs;
    return minIntervalUs <= 0 || lastPresentedUs < 0 || nowUs - lastPresentedUs >= minIntervalUs;
}
```

Make it `internal static` if direct unit tests are cleaner; `AssemblyInfo.cs` from Task 3 already exposes internals to the test assembly.

- [ ] **Step 3: Write failing render-loop behavior tests**

Add to `PlaybackSessionTests` a fake backend that returns multiple frames quickly and a renderer that records present calls. Use a capped policy:

```csharp
[Fact]
public async Task PerformancePolicy_CappedMode_PresentsFewerFramesThanDecoded()
{
    using var backend = new FakePlaybackBackend(
        CreateFrame(0),
        CreateFrame(1_000),
        CreateFrame(2_000),
        CreateFrame(40_000));
    using var renderer = new FakeRenderer(true);
    using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
    using var session = new PlaybackSession(
        Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
        (_, _, _, _) => surface,
        (_, _, _, _) => renderer,
        () => backend,
        () => throw new NotImplementedException(),
        _logger,
        new PlaybackPerformancePolicy(30),
        new FakeClock(0, 1_000, 2_000, 40_000));

    var started = await session.StartAsync();
    await session.StopAsync();

    Assert.True(started);
    Assert.True(backend.NextFrameCalls >= 2);
    Assert.True(renderer.PresentCalls < backend.NextFrameCalls);
}

[Fact]
public async Task PerformancePolicy_QualityMode_PresentsEveryDecodedFrame()
{
    using var backend = new FakePlaybackBackend(
        CreateFrame(0),
        CreateFrame(1_000),
        CreateFrame(2_000));
    using var renderer = new FakeRenderer(true);
    using var surface = new FakeWallpaperSurface(new IntPtr(1), 1, 1);
    using var session = new PlaybackSession(
        Guid.NewGuid(), Guid.NewGuid(), "fake.mp4", 0, 0, 1, 1,
        (_, _, _, _) => surface,
        (_, _, _, _) => renderer,
        () => backend,
        () => throw new NotImplementedException(),
        _logger,
        new PlaybackPerformancePolicy(null),
        new FakeClock(0, 1_000, 2_000));

    var started = await session.StartAsync();
    await session.StopAsync();

    Assert.True(started);
    Assert.Equal(backend.NextFrameCalls, renderer.PresentCalls);
}
```

Add overload:

```csharp
private static FrameData CreateFrame(long ptsUs)
{
    var size = 4 * 4;
    var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
    return new FrameData(buffer, 1, 1, 4, ptsUs);
}
```

Track decode calls in fake backend:

```csharp
public int NextFrameCalls { get; private set; }
```

Increment only when returning a non-null frame.

- [ ] **Step 4: Run the new session tests and confirm failure**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PerformancePolicy"
```

Expected: capped test fails because every frame is currently presented.

- [ ] **Step 5: Implement counters and gate**

In `RenderLoop`, add counters before loop:

```csharp
var lastPresentedUs = -1L;
var decodedFrames = 0;
var presentedFrames = 0;
var skippedFrames = 0;
var lastPerfLog = Stopwatch.StartNew();
```

After a non-null frame is received:

```csharp
decodedFrames++;
```

Before `Present(frame)`:

```csharp
var nowUs = _clock.NowUs;
if (!ShouldPresentFrame(nowUs, lastPresentedUs, _performancePolicy))
{
    skippedFrames++;
    frame.Dispose();
    LogPerformanceSummaryIfDue();
    continue;
}
```

After successful/attempted present:

```csharp
lastPresentedUs = nowUs;
presentedFrames++;
```

Implement the local summary function inside `RenderLoop` or a private method:

```csharp
void LogPerformanceSummaryIfDue()
{
    if (lastPerfLog.Elapsed < TimeSpan.FromSeconds(30)) return;
    var fpsCap = _performancePolicy.MaxPresentFps?.ToString() ?? "native";
    _logger.Debug($"Playback perf monitor={_monitorId} decoded={decodedFrames}/30s presented={presentedFrames}/30s skipped={skippedFrames}/30s fpsCap={fpsCap}");
    decodedFrames = 0;
    presentedFrames = 0;
    skippedFrames = 0;
    lastPerfLog.Restart();
}
```

Call `LogPerformanceSummaryIfDue()` after present, after skip, and while paused only if that does not make logs noisy. Do not log every frame.

- [ ] **Step 6: Protect first-frame readiness**

Ensure the first decoded frame is always presented even under a cap by relying on `lastPresentedUs < 0`. This preserves `StartAsync()` behavior: it still resolves only after a successful first render.

- [ ] **Step 7: Run session tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackSessionTests"
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add src/WallpaperApp/Services/Playback/PlaybackSession.cs src/WallpaperApp/Services/Playback/IClock.cs tests/WallpaperApp.Tests/Services/PlaybackSessionTests.cs
git commit -m "Limit wallpaper present rate by performance policy" -m "The first GPU-saving lever is reducing copy, shader, and present frequency while keeping decode and renderer ownership unchanged. The render loop now skips presentation when the active policy cap has not elapsed and logs coarse counters for verification.`n`nConstraint: First frame must still render so StartAsync can report readiness`nRejected: Decoder-level frame dropping | higher risk before measuring present-path savings`nConfidence: medium`nScope-risk: moderate`nDirective: Do not merge pause reasons with performance throttling; they are separate controls`nTested: dotnet test tests/WallpaperApp.Tests --filter `"FullyQualifiedName~PlaybackSessionTests`"`nNot-tested: Real GPU usage on user hardware"
```

## Task 5: Initialize And Update Policy From App Settings

**Files:**

- Modify: `src/WallpaperApp/App.xaml.cs`
- Modify: `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`

- [ ] **Step 1: Add PlaybackManager dependency to MainViewModel if absent**

Inspect the current constructor. If `PlaybackManager` is already injected, reuse it. If not, add a private field:

```csharp
private readonly PlaybackManager _playback;
```

Add constructor parameter:

```csharp
PlaybackManager playback,
```

Assign:

```csharp
_playback = playback;
```

Because DI already registers `PlaybackManager` as singleton, no service registration change should be needed.

- [ ] **Step 2: Initialize policy after settings load**

In `MainViewModel.LoadAsync()` after `Settings = await _settings.LoadAsync();`, add:

```csharp
_playback.UpdatePerformancePolicy(PlaybackPerformancePolicy.FromProfile(Settings.PerformanceProfile));
```

If the implementation instead initializes in `App.xaml.cs`, place it after `var appSettings = await settings.LoadAsync();`:

```csharp
playback.UpdatePerformancePolicy(PlaybackPerformancePolicy.FromProfile(appSettings.PerformanceProfile));
```

Prefer doing it in `MainViewModel` if settings changes also live there, so policy ownership stays near settings mutation.

- [ ] **Step 3: Add ViewModel property for selected profile**

In `MainViewModel`, add:

```csharp
public WallpaperPerformanceProfile SelectedPerformanceProfile
{
    get => Settings.PerformanceProfile;
    set => UpdatePerfSetting(s => s with { PerformanceProfile = value });
}
```

Update `UpdatePerfSetting` to notify and apply policy:

```csharp
OnPropertyChanged(nameof(SelectedPerformanceProfile));
_playback.UpdatePerformancePolicy(PlaybackPerformancePolicy.FromProfile(Settings.PerformanceProfile));
```

Keep the existing notifications for fullscreen/battery/remote-session.

- [ ] **Step 4: Add profile options for ComboBox binding**

In `MainViewModel`, expose:

```csharp
public IReadOnlyList<WallpaperPerformanceProfile> PerformanceProfiles { get; } =
    Enum.GetValues<WallpaperPerformanceProfile>();
```

If localized display names are needed in this phase, use ComboBoxItem values in XAML instead. Keep this simple unless binding display text becomes clumsy.

- [ ] **Step 5: Build**

Run:

```powershell
dotnet build WallpaperApp.sln
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

```powershell
git add src/WallpaperApp/App.xaml.cs src/WallpaperApp/UI/ViewModels/MainViewModel.cs
git commit -m "Apply performance policy from settings" -m "The persisted performance profile now drives playback policy at startup and when the user changes settings, so active wallpaper sessions can pick up frame pacing without restarting the app.`n`nConstraint: Settings mutations are centralized in MainViewModel`nConfidence: medium`nScope-risk: moderate`nTested: dotnet build WallpaperApp.sln`nNot-tested: Interactive settings change in the running WPF app"
```

## Task 6: Add Settings UI And Localization

**Files:**

- Modify: `src/WallpaperApp/UI/Views/SettingsView.xaml`
- Modify: `src/WallpaperApp/Localization/Strings.cs`
- Modify: `src/WallpaperApp/Resources/Strings.resx`
- Modify: `src/WallpaperApp/Resources/Strings.zh-CN.resx`

- [ ] **Step 1: Add resource keys**

Add these keys to both resx files:

English/default:

```text
WallpaperPerformanceLabel = Wallpaper performance
PerformanceProfileQuality = Quality
PerformanceProfileBalanced = Balanced
PerformanceProfileSaver = Saver
```

Chinese:

```text
WallpaperPerformanceLabel = 壁纸性能
PerformanceProfileQuality = 画质优先
PerformanceProfileBalanced = 平衡
PerformanceProfileSaver = 省电
```

- [ ] **Step 2: Add strongly typed string accessors**

Modify `Strings.cs`:

```csharp
public static string WallpaperPerformanceLabel => Get(nameof(WallpaperPerformanceLabel));
public static string PerformanceProfileQuality => Get(nameof(PerformanceProfileQuality));
public static string PerformanceProfileBalanced => Get(nameof(PerformanceProfileBalanced));
public static string PerformanceProfileSaver => Get(nameof(PerformanceProfileSaver));
```

Place them near existing settings labels.

- [ ] **Step 3: Add the ComboBox row to SettingsView**

Insert this border near the existing pause/performance settings:

```xml
<Border Background="{StaticResource CardSurfaceBrush}"
        BorderBrush="{StaticResource CardBorderBrush}" BorderThickness="1"
        CornerRadius="10" Padding="16,13" Margin="0,12,0,0">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>
        <TextBlock Grid.Column="0" Text="{loc:Loc WallpaperPerformanceLabel}" VerticalAlignment="Center" FontSize="14"/>
        <ComboBox Grid.Column="1" Width="150"
                  SelectedValue="{Binding SelectedPerformanceProfile, Mode=TwoWay}"
                  SelectedValuePath="Tag">
            <ComboBoxItem Content="{loc:Loc PerformanceProfileQuality}" Tag="{x:Static models:WallpaperPerformanceProfile.Quality}"/>
            <ComboBoxItem Content="{loc:Loc PerformanceProfileBalanced}" Tag="{x:Static models:WallpaperPerformanceProfile.Balanced}"/>
            <ComboBoxItem Content="{loc:Loc PerformanceProfileSaver}" Tag="{x:Static models:WallpaperPerformanceProfile.Saver}"/>
        </ComboBox>
    </Grid>
</Border>
```

Add the namespace to the root `UserControl`:

```xml
xmlns:models="clr-namespace:WallpaperApp.Models"
```

- [ ] **Step 4: Build**

Run:

```powershell
dotnet build WallpaperApp.sln
```

Expected: build succeeds. If XAML cannot bind enum values with `x:Static`, switch to binding `ItemsSource="{Binding PerformanceProfiles}"` and accept enum names for the first build, then add a small converter only if necessary.

- [ ] **Step 5: Commit**

```powershell
git add src/WallpaperApp/UI/Views/SettingsView.xaml src/WallpaperApp/Localization/Strings.cs src/WallpaperApp/Resources/Strings.resx src/WallpaperApp/Resources/Strings.zh-CN.resx
git commit -m "Expose wallpaper performance profiles in settings" -m "Users need an explicit quality/performance choice for the new frame pacing behavior. The settings page now offers localized Quality, Balanced, and Saver options near the existing resource-saving toggles.`n`nConstraint: Follow existing WPF localization pattern`nConfidence: medium`nScope-risk: narrow`nTested: dotnet build WallpaperApp.sln`nNot-tested: Manual UI selection"
```

## Task 7: Full Verification And Manual Performance Checklist

**Files:**

- Modify if needed: `docs/superpowers/specs/2026-06-24-gpu-render-optimization-design.zh-CN.md`
- No code changes expected unless verification finds bugs.

- [ ] **Step 1: Run focused tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~PlaybackPerformancePolicyTests|FullyQualifiedName~PlaybackManagerTests|FullyQualifiedName~PlaybackSessionTests"
```

Expected: pass.

- [ ] **Step 2: Run full unit tests**

Run:

```powershell
dotnet test tests/WallpaperApp.Tests
```

Expected: pass. If smoke tests require `ffmpeg.exe` and fail due to missing PATH, record that specifically and run non-smoke tests separately.

- [ ] **Step 3: Build solution**

Run:

```powershell
dotnet build WallpaperApp.sln
```

Expected: pass.

- [ ] **Step 4: Manual runtime verification**

Start the app and verify:

1. Settings page shows `壁纸性能` / `Wallpaper performance`.
2. Default profile is Balanced for fresh settings.
3. Selecting Quality/Balanced/Saver persists after app restart.
4. Active wallpaper keeps playing after profile switch.
5. Manual pause/resume still works.
6. Fullscreen pause still works.
7. Battery/remote-session behavior is unchanged where testable.
8. Logs include a periodic line like:

```text
Playback perf monitor=<id> decoded=<n>/30s presented=<n>/30s skipped=<n>/30s fpsCap=<native|30|15>
```

- [ ] **Step 5: Manual GPU measurement**

Use the same wallpaper and monitor setup for all modes.

Record:

```text
Video: <file name>, <resolution>, <source FPS>
Monitor setup: <count/resolution/refresh rate>
Quality GPU: <Task Manager overall and engine if available>
Balanced GPU: <Task Manager overall and engine if available>
Saver GPU: <Task Manager overall and engine if available>
CPU delta: <rough value>
Notes: <visible smoothness, artifacts, logs>
```

Expected:

- Balanced presents around 30 FPS for 60 FPS input.
- Saver presents around 15 FPS for 60 FPS input.
- GPU usage in Balanced/Saver is lower than Quality when the wallpaper is visible.

- [ ] **Step 6: Final commit for verification notes if docs changed**

Only commit if verification results are added to docs.

```powershell
git add docs/superpowers/specs/2026-06-24-gpu-render-optimization-design.zh-CN.md
git commit -m "Record GPU optimization verification notes" -m "Manual measurements document whether frame pacing reduces GPU usage enough before deeper renderer work is considered.`n`nConstraint: Measurement determines whether VideoProcessor or resolution-budget follow-up is warranted`nConfidence: medium`nScope-risk: narrow`nTested: dotnet test tests/WallpaperApp.Tests; dotnet build WallpaperApp.sln`nNot-tested: Hardware configurations not available on this machine"
```

## Self-Review

Spec coverage:

- Measurement and counters: Task 4 and Task 7.
- FPS limiting: Task 2 and Task 4.
- Quality/Balanced/Saver profiles: Task 1, Task 2, Task 5, Task 6.
- Runtime policy updates: Task 3 and Task 5.
- Settings UI and localization: Task 6.
- Existing pause behavior preserved: Task 3, Task 4, Task 7.
- Deeper renderer rewrites deferred: plan explicitly avoids `FfmpegBackend`/`DxgiRenderer` rewrites.

Quality scan:

- No unresolved placeholder markers or undefined future-only implementation steps.
- Each code-changing task includes concrete files, code snippets, commands, and expected results.

Type consistency:

- `WallpaperPerformanceProfile` is defined in `WallpaperApp.Models`.
- `PlaybackPerformancePolicy` is defined in `WallpaperApp.Services.Playback`.
- `PlaybackManager.UpdatePerformancePolicy(PlaybackPerformancePolicy policy)` is used consistently.
- `PlaybackSession.UpdatePerformancePolicy(PlaybackPerformancePolicy policy)` is used consistently.

## Execution Options

Plan complete and saved to `docs/superpowers/plans/2026-06-24-gpu-render-optimization.md`. Two execution options:

1. **Subagent-Driven (recommended)** - dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** - execute tasks in this session using executing-plans, batch execution with checkpoints.

Recommended choice: Subagent-Driven for Tasks 1-6 because model/settings/UI/playback changes are separable and benefit from review checkpoints.
