# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build & Test

```bash
# Build entire solution
dotnet build WallpaperApp.sln

# Run all unit tests
dotnet test tests/WallpaperApp.Tests

# Run a single test class
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PlaybackSessionTests"

# Run a specific test method
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~PauseReason_AutoResume_DoesNotClobberManualPause"

# Run smoke tests (requires ffmpeg.exe on PATH for cross-validation)
dotnet test tests/WallpaperApp.Tests --filter "FullyQualifiedName~Smoke"

# Rebuild + run tests
dotnet build WallpaperApp.sln && dotnet test tests/WallpaperApp.Tests
```

Tests use xUnit. Smoke tests spawn `ffmpeg` as a subprocess — `ffmpeg.exe` must be on PATH.

## Architecture

Windows-only WPF app that renders video wallpapers behind desktop icons using a Win32 WorkerW child window + D3D11/DXGI flip-model swap chain.

### Key layers

- **Desktop integration** (`Services/Desktop/`): `DesktopHost` manages attachment to the desktop's WorkerW. `WallpaperWindow` creates a Win32 child window beneath `Progman`/`WorkerW` (the pre-Windows-11-24H2 and post-24H2 code paths differ). `IWallpaperSurface` abstracts the HWND for the renderer.

- **Playback pipeline** (`Services/Playback/`):
  - `PlaybackManager` orchestrates per-monitor sessions and routes pause/resume by reason (User / Fullscreen / Power).
  - `PlaybackSession` owns one dedicated render thread per monitor: a `WallpaperWindow` + `IFrameRenderer` + `IPlaybackBackend`. The thread owns the window + D2D/DXGI render target (Direct2D requires single-threaded factory).
  - `FfmpegBackend` is the primary decoder — P/Invokes native `lib/ffmpeg/avcodec-61.dll`, `avformat-61.dll`, `swscale-8.dll`, etc. Opens files synchronously via `Task.Run`, then runs decode in a thread-pool loop. D3D11VA hardware decode is optional; `GpuDevice` is shared between decoder and renderer for zero-copy.
  - `DxgiRenderer` is the preferred renderer (D3D11 + DXGI flip-model). Has two paths: **GPU zero-copy** (shared device → NV12 pixel shader → back buffer, no CPU round-trip) and **CPU upload** (BGRA buffer mapped to staging texture → back buffer).
  - `D2dRenderer` is the legacy fallback (Direct2D HWND render target + `CopyFromMemory`). Only used when DXGI swap chain creation fails.
  - `MfBackend` is a stub (Media Foundation fallback not implemented).
  - `Nv12Shader` compiles a fullscreen-triangle vertex shader + NV12→RGB pixel shader at runtime via `d3dcompiler_47.dll`.

- **Monitor services** (`Services/Monitor/`):
  - `MonitorManager` enumerates monitors via `EnumDisplayMonitors` + EDID hashing for stable keys.
  - `FullscreenDetector` polls `GetForegroundWindow` every 500ms to detect borderless-fullscreen apps.
  - `PowerAwareController` pauses wallpapers on battery (polls + `SystemEvents.PowerModeChanged`).
  - `ExplorerWatcher` is defined but **not wired up** in `App.xaml.cs`.

- **UI layer** (`UI/`): Standard WPF MVVM (`MainViewModel`), tray icon via raw `Shell_NotifyIconW` (bypasses Hardcodet.NotifyIcon due to DPI/menu-dispatch bugs), toast notification system. The `MainWindow` minimizes-to-tray by cancelling `Closing` and calling `Hide()`.

- **Data** (`Data/AppDbContext.cs` + `Models/`): EF Core SQLite. Schema versioning is fragile (single-row version check; real migrations would go in `MigrateAsync`). Files are deduplicated by SHA-256 hash in the library directory.

- **Localization** (`Localization/`): `LocalizationService` sets `Thread.CurrentUICulture` + `CultureInfo.DefaultThreadCurrentUICulture` on startup and on language switch. Resource strings in `Strings.cs` are accessed via `{loc:Loc}` XAML markup.

### FFmpeg interop

The `lib/ffmpeg/` DLLs are native FFmpeg 7.x binaries copied to the output directory. All P/Invoke declarations live in `Interop/FfmpegNative.cs` (the FFmpeg API wrappers) and `Interop/FfmpegOffsets.cs` (hardcoded struct field offsets for `AVFormatContext`, `AVCodecContext`, `AVFrame`, etc.). These offsets are FFmpeg-version-specific and **must** stay in sync with the `lib/ffmpeg/` DLLs.

### Test structure

- `WallpaperApp.Tests/` — xUnit unit tests. `FakePlaybackBackend`/`FakeRenderer`/`FakeWallpaperSurface` are used for PlaybackManager and PlaybackSession tests.
- `WallpaperApp.FfmpegProbe/` — standalone console exe that opens a video via FfmpegBackend and dumps metadata + first pixel. Used by `FfmpegBackendSmokeTests` as a cross-process validation against reference `ffmpeg.exe` output.
- `WallpaperApp.HwDecodeProbe/` — standalone probe for D3D11VA hardware decode.
- `WallpaperApp.RenderProbe/` — standalone probe for DXGI/D2D rendering.

## Key patterns to preserve

- **Pause reason accounting**: `PlaybackSession` tracks multiple `PauseReason` sources in a `HashSet`. Pause is applied on empty→non-empty transition; resume on last→empty. This prevents auto-pause (fullscreen, battery) from clobbering manual pause.
- **Render thread ownership**: `PlaybackSession.Run()` creates the Win32 window AND the D2D/DXGI render target on a single dedicated thread. This is required by Direct2D's single-threaded factory — breaking this makes `EndDraw()` return `S_OK` with no visible output.
- **Zero-copy device sharing**: `GpuDevice` is a singleton D3D11 device created with `VideoSupport` + `BgraSupport` + `MultithreadProtected`. Both the decoder (D3D11VA via `HwDecodeDevice`) and the renderer (DXGI swap chain + NV12 shader) use the same device so decoded textures can be blitted without CPU copy.
- **Device resources recreated on demand**: DxgiRenderer lazily creates device/swap chain/textures and re-releases them when `Present` fails (handles GPU resets from sleep/resume or driver update).
- **Monochrome tray icon**: drawn programmatically with `System.Drawing` (no external icon file).
