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

Use a native Windows architecture built with WPF for the application shell and Windows desktop integration APIs for wallpaper placement. Use Media Foundation as the primary playback backend for video rendering, with a fallback abstraction point so an alternative decoder can be added later if required.

### Why this approach

- WPF provides a mature Windows desktop UI stack with good tray and settings support.
- Native Windows APIs make it practical to place wallpaper content behind desktop icons.
- Media Foundation offers hardware-accelerated playback for common Windows video scenarios.
- This keeps the application Windows-focused, efficient, and aligned with the product goals.

### Alternatives considered

#### 1. WPF + FFmpeg/libmpv

Pros:
- Strong codec compatibility.
- Good future path for more advanced media features.

Cons:
- More packaging complexity.
- More native dependency management.
- Higher implementation and maintenance cost for a v1 product.

Decision:
- Not chosen for v1, but the media backend will be abstracted so this can be introduced later.

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
- Loop playback.
- Apply mute-by-design behavior.
- Support fit modes such as fill, fit, stretch, and center crop.

Technology choice:
- Primary backend: Media Foundation for video playback.
- GIF support: dedicated image animation path or conversion into a frame-timed playback path managed by the engine abstraction.

Architecture rule:
- Playback is exposed behind an interface so a future backend such as libmpv can replace or augment Media Foundation without a UI rewrite.

### 5.4 Desktop Host

Responsibilities:
- Create the native wallpaper host window.
- Attach rendering surfaces behind desktop icons.
- Recreate the host when Explorer restarts or desktop topology changes.

Technical direction:
- Use the common WorkerW / Progman wallpaper-hosting technique on Windows.
- The application creates a child or hosted window positioned in the desktop wallpaper layer, not above normal application windows.

Risk note:
- Explorer shell behavior is implementation-sensitive across Windows versions, so this subsystem must be isolated and resilient.

### 5.5 Monitor & Session Manager

Responsibilities:
- Detect monitor topology changes.
- Maintain wallpaper assignment per monitor.
- Manage pause/resume behavior for fullscreen applications, session lock, unlock, and display changes.

V1 behavior:
- Each monitor can be assigned its own wallpaper.
- Playback instances are independent per monitor.
- If the same asset is used on multiple monitors, each monitor still gets its own render instance to keep implementation simple and reliable.

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
2. App validates extension and playback compatibility.
3. App copies the asset into the managed library.
4. App extracts thumbnail and media metadata.
5. App inserts a library record.
6. App reports any failures per file.

### 6.3 Assign wallpaper to monitors

1. User selects a wallpaper.
2. User sees connected monitors and current assignments.
3. User assigns one wallpaper to selected monitors.
4. App starts or updates playback instances for those monitors.

### 6.4 Fullscreen pause behavior

1. App detects a fullscreen foreground application on a monitor.
2. Wallpaper playback pauses on the affected monitor.
3. When fullscreen ends, playback resumes from the loop state.

V1 decision:
- Pause behavior is global by default for simplicity and predictable UX. If any fullscreen application is detected, all wallpaper playback pauses.

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
- Pause all wallpaper playback while fullscreen content is active.
- Resume automatically when fullscreen content exits.

## 8. Non-Functional Requirements

### 8.1 Performance

- Target 60fps playback for supported assets on hardware that can sustain it.
- Prefer GPU-accelerated decode and presentation when available.
- Avoid unnecessary CPU polling in monitor and fullscreen detection.
- Startup to active wallpaper restore should feel immediate after login.

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
- Settings and library data should be easy to migrate in future versions.

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
- `ImportedAtUtc`
- `LastUsedAtUtc`
- `ValidationStatus`

### 9.2 Monitor assignment entity

Suggested fields:
- `Id`
- `MonitorKey`
- `WallpaperId`
- `FitMode`
- `PausedByFullscreen`
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

Mitigation:
- Isolate the desktop host subsystem.
- Build explicit recovery logic for Explorer restarts.
- Test on supported Windows 10 and Windows 11 versions early.

### 13.2 Codec compatibility variance

Reason:
- Media Foundation support depends on installed codecs and OS capabilities.

Mitigation:
- Validate files during import.
- Message support clearly.
- Keep playback backend replaceable.

### 13.3 WPF and high-performance video composition boundaries

Reason:
- WPF itself is not the playback engine and should not become the frame rendering bottleneck.

Mitigation:
- Keep video rendering in a native-capable playback layer.
- Avoid designing around a pure WPF media stack if profiling shows instability.

## 14. Delivery Phasing

### Phase 1

- App shell
- Tray integration
- Local library import
- Single-monitor MP4 playback
- Desktop host placement

### Phase 2

- GIF support
- Multi-monitor assignment
- Startup restore
- Fullscreen pause/resume

### Phase 3

- Polished UI
- Diagnostics and recovery hardening
- Expanded compatibility validation

## 15. Open Decisions Resolved In This Spec

The following decisions are intentionally fixed to remove planning ambiguity:
- Windows only.
- C# WPF application shell.
- Media Foundation as the primary video backend.
- Managed local library copy on import by default.
- Silent playback only.
- Global pause when any fullscreen app is active.
- Independent playback instance per monitor.
- No playlists, online sources, or web wallpapers in v1.

## 16. Implementation Planning Readiness

This spec is intended to be ready for implementation planning. The expected planning outcome should break work into modules aligned with:
- UI shell and tray
- Library storage and import pipeline
- Playback abstraction and Media Foundation backend
- Desktop host / Explorer integration
- Monitor management and fullscreen detection
- Settings, startup, logging, and recovery
