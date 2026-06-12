# Dynamic Wallpaper Design

## 1. Overview

This document defines the design for a Windows-only dynamic wallpaper application that allows users to import local video files such as MP4 and display them directly on the desktop as live wallpapers. The product target is a polished end-user application rather than a prototype. The first version focuses on stable local playback, desktop integration, and a good management experience.

Primary goals:
- Import local video and GIF wallpaper assets.
- Render wallpapers behind desktop icons, not as a normal floating window.
- Support multiple monitors.
- Provide a polished desktop UI and tray experience.
- Maintain smooth playback with a 60fps target when hardware permits.
- Prioritize stability, graceful recovery, and low user friction.

Non-goals for v1:
- Online wallpaper marketplace.
- Community sharing or cloud sync.
- Interactive web wallpapers.
- Audio playback.
- Cross-platform support.

## 2. User Requirements

The approved product requirements are:
- Platform: Windows only.
- Tech stack preference: C# with WPF.
- Product intent: build a real distributable product.
- Core wallpaper sources: local MP4 and GIF files.
- Additional format target: MP4, WebM, AVI, MKV, MOV.
- Multi-monitor support.
- System tray integration.
- Wallpaper library management.
- Auto-start with Windows.
- Pause wallpaper playback when a fullscreen app is active.
- UI direction: beautiful and polished.
- Performance target: smooth 60fps playback when possible.
- Audio: disabled in the product.
- Stability: very important, with recovery paths and clear error handling.

## 3. Recommended Approach

### Chosen approach

Use a native Windows architecture built with WPF for the application shell and Windows desktop integration APIs for wallpaper placement. The wallpaper rendering surface is a Win32 window using DirectComposition or Direct3D swap chain, not WPF rendering pipeline. FFmpeg (via P/Invoke to existing DLLs in `lib/ffmpeg/`) is the primary playback backend, with Media Foundation as a fallback path for systems where FFmpeg has issues.

### Why this approach

- WPF provides a mature Windows desktop UI stack with good tray and settings support.
- Native Windows APIs make it practical to place wallpaper content behind desktop icons.
- FFmpeg provides broad codec coverage (MP4, WebM, MKV, MOV, AVI) and mature frame-level control, avoiding the codec compatibility gaps of Media Foundation.
- Separating WPF (management UI) from wallpaper rendering (native Win32 surface) avoids WPF airspace and performance bottlenecks.
- The existing FFmpeg 7.x DLLs in `lib/ffmpeg/` are ready for P/Invoke integration.

### Alternatives considered

#### 1. Media Foundation fallback

Pros:
- System-built, no additional DLL dependencies.
- Hardware acceleration available on most Windows installs.

Cons:
- Codec compatibility depends on OS version and installed codecs.
- Frame-level loop control is weaker than FFmpeg.
- Error diagnostics are difficult.

Decision:
- Retained as a fallback backend. If FFmpeg has issues on a specific system, the app can degrade to MF for supported formats (MP4 with H.264/H.265). The playback interface abstraction supports this without UI changes.

#### 2. WPF + WebView2 / HTML5 video

Pros:
- Faster to prototype.
- Easier if web wallpapers become a future requirement.

Cons:
- Weaker fit for a high-performance native wallpaper renderer.
- Harder to guarantee stable multi-monitor video playback behavior.
- More browser-process overhead.

Decision:
- Rejected for v1 because performance and desktop integration are more important than prototyping speed.

## 4. Product Scope For V1

### Included

- Import local wallpaper files.
- Display a selected wallpaper behind desktop icons.
- Loop playback continuously.
- GIF playback support.
- Multi-monitor support with one wallpaper assignment per monitor.
- Wallpaper library with thumbnail previews and metadata.
- Tray icon with quick controls.
- Auto-start on Windows login.
- Pause and resume playback based on fullscreen app detection.
- Basic playback fit modes.
- Error reporting and recovery.

### Explicitly excluded

- Wallpaper audio controls.
- Online downloads.
- URL-based wallpapers.
- Scriptable or interactive wallpaper types.
- Advanced visual effects editor.
- Playlist scheduling in v1.

## 5. High-Level Architecture

The system will be split into six application areas:

1. `Shell/UI`
2. `Wallpaper Library`
3. `Playback Engine`
4. `Desktop Host`
5. `Monitor & Session Manager`
6. `Settings, Startup, and Diagnostics`

### 5.1 Shell/UI

Responsibilities:
- Main application window.
- Wallpaper browsing, preview, import, and settings screens.
- Tray menu and application commands.
- User-facing status and error messages.

Key constraints:
- The UI must stay responsive while wallpapers are playing.
- Media and desktop operations must run outside the UI thread when possible.

### 5.2 Wallpaper Library

Responsibilities:
- Track imported assets.
- Store metadata and thumbnails.
- Expose wallpaper list, monitor assignment, and deletion actions.

Storage model:
- Use a local application data directory under the user's profile.
- Store imported metadata in SQLite.
- Store thumbnails and derived assets on disk.
- Preserve references to source files only when the user chooses not to copy assets into the managed library.

V1 decision:
- By default, imported files are copied into the managed library directory so wallpapers remain available even if the original file is moved.

### 5.3 Playback Engine

Responsibilities:
- Open, decode, and render video or GIF content.
- Loop playback with frame-accurate seek for seamless looping.
- Apply mute-by-design behavior.
- Support fit modes such as fill, fit, stretch, and center crop.
- Expose decode and render as separable concerns to allow future shared-decode multi-monitor output.

Technology choice:
- Primary backend: FFmpeg via P/Invoke to DLLs in `lib/ffmpeg/` (avcodec-61, avformat-61, avutil-59, etc.).
- Fallback backend: Media Foundation for systems where FFmpeg DLLs fail to load.
- GIF support: imported GIFs are pre-transcoded to MP4 (H.264 silent) during import. This avoids per-frame bitmap memory overhead and variable frame-delay complexity at runtime. The import pipeline runs a background transcode with progress reporting.

GIF transcode timeout behavior:
- Timeout after 5 minutes for the transcode process.
- On timeout: cancel the ffmpeg process, delete any partial output file, remove the library record if it was inserted, and report a per-file error to the user ("Transcode timed out — file may be too large or complex").
- The user can retry with a smaller GIF or trim the file.

Architecture rule:
- Playback is exposed behind `IWallpaperPlaybackBackend` interface.
- FFmpeg backend implements this interface using direct P/Invoke calls to `lib/ffmpeg/*.dll`.
- The interface contract must be validated in Phase 1 with both backends (FFmpeg primary, MF fallback) to ensure runtime behavior — not just compile-time correctness — is sound.

### 5.4 Desktop Host

Responsibilities:
- Create the native wallpaper host window.
- Attach rendering surfaces behind desktop icons.
- Recreate the host when Explorer restarts or desktop topology changes.

Technical direction:
- Use the WorkerW / Progman wallpaper-hosting technique on Windows.
- The application sends `0x052C` to Progman to spawn a WorkerW, finds it via `FindWindowEx`, then parents the wallpaper rendering HWND into it.
- The wallpaper rendering HWND uses DirectComposition or Direct3D11 swap chain for GPU-accelerated frame presentation. It does not use WPF rendering.

Fallback strategy:
- If WorkerW discovery or attachment fails (e.g. `SendMessage(Progman, 0x052C, ...)` returns failure), create a `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` window with `HWND_BOTTOM` Z-order as a degraded overlay.
- Log the failure and show a persistent tray notification indicating degraded mode.
- Retry WorkerW attachment on a timer (every 60 seconds) until Explorer restarts or the shell stabilizes.

Minimum supported version:
- Windows 10 version 1903 (build 18362) and later. Earlier versions are not supported due to shell behavior differences.

Risk note:
- Explorer shell behavior is implementation-sensitive across Windows versions. Windows 11 24H2+ has had shell changes that may affect WorkerW discovery. This subsystem must be isolated and resilient.

### 5.5 Monitor & Session Manager

Responsibilities:
- Detect monitor topology changes.
- Maintain wallpaper assignment per monitor.
- Manage pause/resume behavior for fullscreen applications, session lock, unlock, and display changes.

V1 behavior:
- Each monitor can be assigned its own wallpaper.
- Each monitor gets its own render instance (swap chain + presentation).
- If the same asset is used on multiple monitors, each monitor gets its own decode instance for simplicity. However, the architecture decouples `IDecoder` from `IRenderer` so a future shared-decode path (one decode, N swap chains) can be introduced without changing the monitor manager.

Monitor identity strategy:
- Primary key: EDID hash + connection type (HDMI/DP/etc.) combination, which survives port swaps better than DeviceName alone.
- Fallback: DeviceName as secondary match.
- If no match is found after hot-plug, surface a "reassign wallpapers" UI prompt rather than silently losing assignments.

Fullscreen pause strategy:
- The `PausedByFullscreen` field in the monitor assignment entity is per-monitor, not global.
- V1 default behavior: global pause (any fullscreen on any monitor pauses all). This is a runtime policy, not a data model constraint.
- The data model supports per-monitor pause so the policy can be relaxed in a future version without schema changes.

### 5.6 Settings, Startup, and Diagnostics

Responsibilities:
- Persist user settings.
- Register and unregister app auto-start.
- Provide logs and recovery diagnostics.

Persistence:
- Store user settings in a local configuration file.
- Store library data in SQLite.
- Store rolling logs in the application data directory.

## 6. Core User Flows

### 6.1 First-time setup

1. User launches the app.
2. App shows an empty-state library view.
3. User imports one or more wallpaper files.
4. App validates the file, generates a thumbnail, and adds it to the library.
5. User selects a wallpaper and assigns it to one or more monitors.
6. App creates wallpaper host windows and starts playback.
7. User can close the main window while wallpapers continue through the tray process.

### 6.2 Import wallpaper

1. User chooses files through a picker or drag-and-drop.
2. App validates extension against supported list.
3. App checks available disk space against file size. If free space < 2x file size, warn the user before proceeding.
4. App validates playback compatibility by attempting a short decode test.
5. For GIF files, app pre-transcodes to MP4 (H.264 silent) via ffmpeg with progress reporting. Timeout after 5 minutes. On timeout: cancel process, delete partial output, report error to user.
6. App copies the asset (or transcoded result) into the managed library.
7. App extracts thumbnail and media metadata.
8. App inserts a library record.
9. App reports any failures per file.
10. Files exceeding 2GB display a soft warning suggesting the user may want to trim or compress the video, but still allow import.

### 6.3 Assign wallpaper to monitors

1. User selects a wallpaper.
2. User sees connected monitors and current assignments.
3. User assigns one wallpaper to selected monitors.
4. App starts or updates playback instances for those monitors.

### 6.4 Fullscreen pause behavior

1. App detects a fullscreen foreground application on any monitor.
2. V1: all wallpaper playback pauses globally.
3. The `PausedByFullscreen` flag on each monitor assignment is set individually, so the data model is ready for per-monitor pause in a future version.
4. When fullscreen ends, playback resumes from the loop state.

### 6.5 Tray control flow

Tray menu actions:
- Open main window.
- Pause wallpapers.
- Resume wallpapers.
- Next wallpaper is out of scope for v1.
- Exit application.

## 7. Functional Requirements

### 7.1 Media support

- Must support local MP4 files in v1.
- Should support common Windows-playable WebM, AVI, MKV, and MOV files where codec support is available.
- Must support GIF wallpapers.
- Unsupported codecs must fail with a clear user message.

Clarification:
- Container support does not guarantee every codec inside that container. The product will advertise support as best-effort for non-MP4 containers, with definitive validation at import time.

### 7.2 Playback behavior

- Loop playback indefinitely.
- Start wallpaper playback automatically after assignment.
- Render without audio output.
- Default fit mode: fill while preserving aspect ratio, cropping if necessary.
- Provide additional fit modes in settings or assignment UI.

### 7.3 Library behavior

- Show imported wallpaper list with thumbnails.
- Allow reassigning wallpapers without reimporting.
- Allow deletion from library.
- On deletion, allow user choice to remove only the managed library copy, not arbitrary original files outside the library.

### 7.4 Multi-monitor behavior

- Detect monitor add/remove/reorder events.
- Persist assignment by monitor identity as reliably as Windows allows.
- When monitor identity changes unexpectedly, surface a repairable state instead of silently losing all assignments.

### 7.5 Tray behavior

- App can remain active after main window closes.
- Tray icon reflects running state.
- Exit from tray must fully stop wallpaper processes and release host windows.

### 7.6 Startup behavior

- User can enable or disable launch at login.
- On startup, the app restores the last valid wallpaper assignments and settings.

### 7.7 Fullscreen detection

- Detect exclusive fullscreen and borderless fullscreen foreground windows as best as practical.
- V1 behavior: pause all wallpaper playback while any fullscreen content is active.
- Resume automatically when fullscreen content exits.
- The per-monitor `PausedByFullscreen` flag is maintained in the data model so the global-vs-per-monitor policy can be switched via settings in a future version.

## 8. Non-Functional Requirements

### 8.1 Performance

- Target 60fps playback for supported assets on hardware that can sustain it.
- Prefer GPU-accelerated decode and presentation when available.
- Avoid unnecessary CPU polling in monitor and fullscreen detection.
- Startup to active wallpaper restore should feel immediate after login.

Quantified targets (1080p MP4 on mid-range hardware):
- CPU usage during idle desktop playback: ≤ 3%.
- GPU decode engine usage: ≤ 15%.
- Memory working set per monitor: ≤ 200MB.
- Import of 1GB file to managed library: ≤ 30 seconds on SSD.
- Time from login to wallpaper visible: ≤ 5 seconds.

These are targets, not hard guarantees — they will be validated during Phase 1 and used as optimization baselines.

### 8.2 Stability

- Explorer restarts must not require a full app restart in the normal case.
- A playback failure on one monitor must not crash the entire application.
- Invalid media files must fail cleanly.
- Crashes in auxiliary playback components must be logged and recovered where possible.

### 8.3 UX quality

- UI should look polished and modern, aligned with contemporary Windows desktop design.
- Common actions should take minimal clicks.
- Failures must be explained in user language, not raw exception text.

### 8.4 Maintainability

- Desktop hosting, playback, and library management must remain separate modules.
- The playback backend must be abstracted.
- SQLite schema must include a version table and a migration mechanism from v1.

## 9. Data Design

### 9.1 Library entity

Suggested fields:
- `Id`
- `DisplayName`
- `SourceType` with values `Video` or `Gif`
- `OriginalFileName`
- `ManagedFilePath`
- `ThumbnailPath`
- `DurationMs`
- `Width`
- `Height`
- `ContainerFormat`
- `CodecSummary`
- `FileBytes`
- `ImportedAtUtc`
- `LastUsedAtUtc`
- `ValidationStatus`

### 9.2 Monitor assignment entity

Suggested fields:
- `Id`
- `MonitorKey` (EDID hash + connection type)
- `MonitorDeviceName` (secondary fallback identifier)
- `WallpaperId`
- `FitMode`
- `PausedByFullscreen` (per-monitor flag; V1 uses global pause policy at runtime)
- `UpdatedAtUtc`

### 9.3 App settings

Suggested settings:
- Launch at startup
- Start minimized to tray
- Global pause on fullscreen
- Default fit mode
- Hardware acceleration enabled
- Log verbosity
- Theme preference

### 9.4 Schema migration

- A `SchemaVersion` table stores the current schema version number.
- On startup, the app compares the stored version against the code's expected version.
- If the version is behind, a sequence of ordered SQL migration scripts is executed.
- Each migration script is idempotent and wrapped in a transaction.
- Before running migrations, the app backs up the existing database file.

## 10. Error Handling And Recovery

### Failure classes

1. Import failure
2. Unsupported media
3. Playback initialization failure
4. Desktop host attachment failure
5. Explorer restart or shell reset
6. Monitor topology change inconsistency

### Recovery rules

- Import failure: keep other files importing and report per-file results.
- Unsupported media: reject the asset and explain the reason.
- Playback initialization failure: mark wallpaper inactive for that monitor and keep the rest of the app alive.
- Desktop host failure: retry host discovery and reattachment with bounded retries.
- Explorer restart: rebuild desktop host windows and restore assignments automatically.
- Database or config corruption: back up the broken file, create a new clean store, and notify the user.

### Logging

Must log:
- Import validation failures.
- Playback backend errors.
- Desktop host attach and reattach attempts.
- Fullscreen detection state changes.
- App startup restore results.

## 11. Security And Safety

- The app only imports local user-selected files in v1.
- The app must not execute scripts or arbitrary web content.
- The app should avoid deleting user-owned files outside the managed library.
- Crashes or invalid media must not leave orphaned always-on-top or visible host windows.

## 12. Testing Strategy

### Unit-level targets

- Library metadata validation.
- Settings persistence.
- Monitor assignment mapping.
- Fullscreen detection heuristics behind abstractions.

### Integration targets

- Importing a valid MP4 and assigning it to a monitor.
- Restoring wallpaper state after app restart.
- Rebuilding wallpaper hosts after Explorer restart.
- Pausing and resuming on fullscreen application transitions.
- Monitor hot-plug behavior.

### Manual validation targets

- Single-monitor smooth playback.
- Multi-monitor independent assignment.
- Tray behavior after main window close.
- Auto-start behavior after Windows login.
- Invalid media import UX.
- High DPI and resolution change behavior.

## 13. Main Technical Risks

### 13.1 Windows desktop host compatibility

Reason:
- The WorkerW / Progman hosting pattern is common but implementation-sensitive.
- Windows 11 24H2+ has had shell changes that may affect WorkerW discovery.

Mitigation:
- Isolate the desktop host subsystem.
- Build explicit recovery logic for Explorer restarts.
- Implement fallback to `WS_EX_TOOLWINDOW` bottom-Z overlay when WorkerW fails.
- Test on Windows 10 1903+ and Windows 11 early.

### 13.2 Codec compatibility variance

Reason:
- Even with FFmpeg, some exotic codec profiles or broken containers may fail.
- Media Foundation fallback has its own codec gaps.

Mitigation:
- FFmpeg handles the vast majority of formats natively (MP4, WebM, MKV, MOV, AVI).
- Validate files during import with a short decode test.
- Clearly communicate which formats are "guaranteed" vs "best-effort".
- Fall back to Media Foundation if FFmpeg DLLs fail to load on a specific system.

### 13.3 Media Foundation limitations (fallback path)

Reason:
- MF has weaker frame-level loop control compared to FFmpeg.
- MF error codes and state machine are difficult to diagnose.
- Some container/codec combinations fail silently or produce visual artifacts.

Mitigation:
- FFmpeg is the primary backend, so MF limitations only affect fallback scenarios.
- Abstract the playback backend behind `IWallpaperPlaybackBackend` so the switch is transparent.
- Wrap MF errors into user-friendly messages at the UI boundary.

### 13.4 WPF and high-performance video composition boundaries

Reason:
- WPF rendering pipeline is not suitable for 4K 60fps wallpaper rendering.
- WPF airspace issues and thread model add complexity if video is embedded in WPF windows.

Mitigation:
- Wallpaper rendering uses a standalone Win32 HWND with DirectComposition or D3D11 swap chain.
- WPF is used only for management UI (main window, settings, tray menu).
- The two surfaces are completely decoupled — WPF never renders into the wallpaper window.

## 14. Delivery Phasing

### Phase 1

- App shell (WPF main window, tray integration)
- Logging framework and basic recovery (Explorer restart detection + host rebuild)
- Local library import with disk space check and validation
- Single-monitor MP4 playback via FFmpeg
- Desktop host placement (WorkerW + fallback)
- FFmpeg P/Invoke integration with `lib/ffmpeg/` DLLs
- MF fallback backend for systems where FFmpeg fails

### Phase 2

- GIF support (pre-transcode on import)
- Multi-monitor assignment
- Startup restore
- Fullscreen pause/resume (global policy, per-monitor data model)

### Phase 3

- Polished UI (themes, animations)
- Diagnostics and recovery hardening
- Expanded compatibility validation (WebM, MKV, MOV edge cases)
- Performance profiling and optimization against quantified targets

## 14.1 Distribution and Packaging

- Package format: MSIX for Windows Store or sideload, with Inno Setup as fallback for non-Store distribution.
- .NET runtime: target .NET 8 or later. The installer must check for the runtime and prompt installation if missing.
- Auto-start under MSIX: must use the `windows.startupTask` extension in the AppxManifest.xml, declaring a unique `StartupTaskId`. The app calls `StartupTask.GetAsync()` to check state and `StartupTask.RequestEnableAsync()` to toggle. This differs from traditional Win32 registry-based auto-start — the two approaches are not interchangeable, and the code must handle both paths depending on installation method.
- Auto-start under Inno Setup: uses the standard `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key.
- Update mechanism: v1 includes a manual "check for updates" action in settings. Auto-update is not in v1 scope.
- Portable mode: not supported in v1. The app requires installation to register auto-start and tray integration properly.

## 15. Open Decisions Resolved In This Spec

The following decisions are intentionally fixed to remove planning ambiguity:
- Windows only, minimum version Windows 10 1903+.
- C# WPF application shell; wallpaper rendering via Win32 HWND + DirectComposition/D3D11, not WPF rendering pipeline.
- FFmpeg as the primary playback backend (DLLs in `lib/ffmpeg/`), Media Foundation as fallback.
- GIF pre-transcoded to MP4 on import; timeout cancels and cleans up on failure.
- Managed local library copy on import by default.
- Silent playback only.
- Global pause when any fullscreen app is active (V1 runtime policy); data model supports per-monitor pause.
- Independent render instance per monitor; decode and render decoupled for future shared-decode support.
- Monitor identity uses EDID hash + connection type as primary key.
- SQLite schema versioning with migration scripts from v1.
- MSIX uses `windows.startupTask` for auto-start; Inno Setup uses registry key. Code handles both.
- No playlists, online sources, or web wallpapers in v1.

## 16. Implementation Planning Readiness

This spec is intended to be ready for implementation planning. The expected planning outcome should break work into modules aligned with:
- UI shell and tray
- Library storage, import pipeline, and schema migration
- Playback abstraction, FFmpeg backend (P/Invoke to `lib/ffmpeg/`), and MF fallback
- Desktop host / Explorer integration and fallback
- Monitor management, fullscreen detection, and per-monitor pause data model
- Settings, startup (MSIX `windows.startupTask` + registry), logging, recovery, and diagnostics
- Packaging and distribution (MSIX/Inno Setup, .NET 8+ runtime check)
