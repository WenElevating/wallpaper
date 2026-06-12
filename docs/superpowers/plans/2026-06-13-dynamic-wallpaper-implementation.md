# Dynamic Wallpaper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows-only dynamic wallpaper application that plays MP4/GIF videos as desktop background behind icons, with tray integration, multi-monitor support, and a polished WPF UI.

**Architecture:** WPF shell for management UI + Win32 HWND with D3D11 swap chain for wallpaper rendering placed behind desktop icons via WorkerW/Progman. FFmpeg (P/Invoke to `lib/ffmpeg/`) as primary playback backend, Media Foundation as fallback. SQLite for library storage with schema versioning.

**Tech Stack:** C# / .NET 8 / WPF / P/Invoke / FFmpeg 7.x / SQLite / Win32 API

---

## File Structure

```
WallpaperApp/
├── WallpaperApp.sln
├── lib/ffmpeg/                          (existing DLLs)
├── src/WallpaperApp/
│   ├── WallpaperApp.csproj
│   ├── App.xaml                         (application entry, DI setup)
│   ├── App.xaml.cs
│   ├── Models/
│   │   ├── WallpaperItem.cs             (library entity)
│   │   ├── MonitorAssignment.cs         (per-monitor assignment)
│   │   ├── AppSettings.cs               (settings POCO)
│   │   └── FitMode.cs                   (enum: Fill, Fit, Stretch, CenterCrop)
│   ├── Data/
│   │   ├── AppDbContext.cs              (SQLite context + migration)
│   │   └── Migrations/
│   │       └── V001_Initial.cs          (schema creation)
│   ├── Services/
│   │   ├── Playback/
│   │   │   ├── IPlaybackBackend.cs      (interface contract)
│   │   │   ├── PlaybackSession.cs       (per-monitor session wrapper)
│   │   │   ├── FfmpegBackend.cs         (FFmpeg P/Invoke implementation)
│   │   │   ├── FfmpegNative.cs          (P/Invoke declarations)
│   │   │   ├── MfBackend.cs             (Media Foundation fallback)
│   │   │   └── PlaybackManager.cs       (orchestrates sessions per monitor)
│   │   ├── Desktop/
│   │   │   ├── DesktopHost.cs           (WorkerW/Progman + fallback)
│   │   │   └── WallpaperWindow.cs       (Win32 HWND + D3D11 surface)
│   │   ├── Monitor/
│   │   │   ├── MonitorManager.cs        (enumerate + track monitors)
│   │   │   ├── MonitorIdentity.cs       (EDID hash + connection type)
│   │   │   └── FullscreenDetector.cs    (detect fullscreen apps)
│   │   ├── Library/
│   │   │   ├── LibraryService.cs        (CRUD + import logic)
│   │   │   ├── ThumbnailService.cs      (extract/generate thumbnails)
│   │   │   └── GifTranscoder.cs         (GIF → MP4 via ffmpeg CLI)
│   │   ├── Settings/
│   │   │   └── SettingsService.cs       (load/save settings + auto-start)
│   │   └── Logging/
│   │       └── FileLogger.cs            (rolling file logger)
│   ├── UI/
│   │   ├── MainWindow.xaml / .cs        (main WPF window)
│   │   ├── TrayIcon.cs                  (system tray integration)
│   │   ├── Converters/
│   │   │   └── EnumDescriptionConverter.cs
│   │   └── ViewModels/
│   │       ├── MainViewModel.cs
│   │       ├── LibraryViewModel.cs
│   │       └── SettingsViewModel.cs
│   └── Interop/
│       └── NativeMethods.cs             (Win32 P/Invoke declarations)
└── tests/WallpaperApp.Tests/
    ├── WallpaperApp.Tests.csproj
    ├── Data/
    │   └── AppDbContextTests.cs
    ├── Services/
    │   ├── LibraryServiceTests.cs
    │   ├── MonitorIdentityTests.cs
    │   └── SettingsServiceTests.cs
    └── Playback/
        └── PlaybackSessionTests.cs
```

---

## Task 1: Project Scaffolding

**Files:**
- Create: `WallpaperApp.sln`
- Create: `src/WallpaperApp/WallpaperApp.csproj`
- Create: `src/WallpaperApp/App.xaml`
- Create: `src/WallpaperApp/App.xaml.cs`
- Create: `tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj`

- [ ] **Step 1: Create solution and project**

```bash
dotnet new wpf -n WallpaperApp -o src/WallpaperApp --framework net8.0-windows
dotnet new xunit -n WallpaperApp.Tests -o tests/WallpaperApp.Tests --framework net8.0
dotnet new sln -n WallpaperApp
dotnet sln add src/WallpaperApp/WallpaperApp.csproj
dotnet sln add tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj
dotnet add tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj reference src/WallpaperApp/WallpaperApp.csproj
```

- [ ] **Step 2: Add NuGet packages**

```bash
# In src/WallpaperApp/
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 8.0.*
dotnet add package Microsoft.Extensions.DependencyInjection --version 8.0.*
dotnet add package System.Drawing.Common --version 8.0.*
dotnet add package Hardcodet.Wpf.TaskbarNotification --version 1.1.*

# In tests/WallpaperApp.Tests/
dotnet add package Microsoft.EntityFrameworkCore.Sqlite --version 8.0.*
dotnet add package Moq --version 4.20.*
```

- [ ] **Step 3: Copy FFmpeg DLLs to output**

Edit `src/WallpaperApp/WallpaperApp.csproj` to add:

```xml
<ItemGroup>
  <None Include="../../lib/ffmpeg/*.dll" CopyToOutputDirectory="PreserveNewest" Link="ffmpeg/%(Filename)%(Extension)" />
</ItemGroup>
```

- [ ] **Step 4: Verify build**

```bash
dotnet build WallpaperApp.sln
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: scaffold WPF project with FFmpeg DLLs and test project"
```

---

## Task 2: Models and Enums

**Files:**
- Create: `src/WallpaperApp/Models/FitMode.cs`
- Create: `src/WallpaperApp/Models/WallpaperItem.cs`
- Create: `src/WallpaperApp/Models/MonitorAssignment.cs`
- Create: `src/WallpaperApp/Models/AppSettings.cs`
- Create: `src/WallpaperApp/Interop/NativeMethods.cs`

- [ ] **Step 1: Create FitMode enum**

```csharp
// src/WallpaperApp/Models/FitMode.cs
namespace WallpaperApp.Models;

public enum FitMode
{
    Fill,
    Fit,
    Stretch,
    CenterCrop
}
```

- [ ] **Step 2: Create WallpaperItem model**

```csharp
// src/WallpaperApp/Models/WallpaperItem.cs
namespace WallpaperApp.Models;

public class WallpaperItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string SourceType { get; set; } = "Video"; // "Video" or "Gif"
    public string OriginalFileName { get; set; } = string.Empty;
    public string ManagedFilePath { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContainerFormat { get; set; } = string.Empty;
    public string CodecSummary { get; set; } = string.Empty;
    public long FileBytes { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
    public string ValidationStatus { get; set; } = "Pending"; // Pending, Valid, Invalid
}
```

- [ ] **Step 3: Create MonitorAssignment model**

```csharp
// src/WallpaperApp/Models/MonitorAssignment.cs
namespace WallpaperApp.Models;

public class MonitorAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MonitorKey { get; set; } = string.Empty; // EDID hash + connection type
    public string MonitorDeviceName { get; set; } = string.Empty; // fallback identifier
    public Guid WallpaperId { get; set; }
    public FitMode FitMode { get; set; } = FitMode.Fill;
    public bool PausedByFullscreen { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 4: Create AppSettings model**

```csharp
// src/WallpaperApp/Models/AppSettings.cs
namespace WallpaperApp.Models;

public class AppSettings
{
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimizedToTray { get; set; } = true;
    public bool GlobalPauseOnFullscreen { get; set; } = true;
    public FitMode DefaultFitMode { get; set; } = FitMode.Fill;
    public bool HardwareAccelerationEnabled { get; set; } = true;
    public string LogVerbosity { get; set; } = "Info";
    public string Theme { get; set; } = "Dark";
}
```

- [ ] **Step 5: Create NativeMethods P/Invoke declarations**

```csharp
// src/WallpaperApp/Interop/NativeMethods.cs
using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

internal static partial class NativeMethods
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FindWindowExW(
        IntPtr hWndParent, IntPtr hWndChildAfter,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszClass,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpszWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowLongW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    internal const uint WM_SPAWN_WORKERW = 0x052C;
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal static readonly IntPtr HWND_BOTTOM = new(-1);
    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // D3D11 interop for swap chain rendering
    [LibraryImport("d3d11.dll")]
    internal static partial int D3D11CreateDevice(
        IntPtr pAdapter, int DriverType, IntPtr Software,
        uint Flags, [In] int[] pFeatureLevels, uint FeatureLevels,
        uint SDKVersion, out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);
}
```

- [ ] **Step 6: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/WallpaperApp/Models/ src/WallpaperApp/Interop/
git commit -m "feat: add data models, enums, and Win32 P/Invoke declarations"
```

---

## Task 3: SQLite Database with Migration

**Files:**
- Create: `src/WallpaperApp/Data/AppDbContext.cs`
- Create: `src/WallpaperApp/Data/Migrations/V001_Initial.cs`
- Create: `tests/WallpaperApp.Tests/Data/AppDbContextTests.cs`

- [ ] **Step 1: Write failing test for database creation**

```csharp
// tests/WallpaperApp.Tests/Data/AppDbContextTests.cs
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using Xunit;

namespace WallpaperApp.Tests.Data;

public class AppDbContextTests
{
    [Fact]
    public async Task Database_CanBeCreated_AndHasSchemaVersion()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var version = await context.SchemaVersions.FirstOrDefaultAsync();
        Assert.NotNull(version);
        Assert.Equal(1, version.Version);
    }

    [Fact]
    public async Task CanInsertAndQueryWallpaperItem()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var item = new WallpaperApp.Models.WallpaperItem
        {
            DisplayName = "Test Wallpaper",
            SourceType = "Video",
            OriginalFileName = "test.mp4",
            ManagedFilePath = "/managed/test.mp4",
            FileBytes = 1024,
            ValidationStatus = "Valid"
        };

        context.WallpaperItems.Add(item);
        await context.SaveChangesAsync();

        var retrieved = await context.WallpaperItems.FirstAsync();
        Assert.Equal("Test Wallpaper", retrieved.DisplayName);
        Assert.Equal("Video", retrieved.SourceType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "AppDbContextTests" -v n
```

Expected: FAIL — `AppDbContext` does not exist.

- [ ] **Step 3: Implement AppDbContext**

```csharp
// src/WallpaperApp/Data/AppDbContext.cs
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Models;

namespace WallpaperApp.Data;

public class AppDbContext : DbContext
{
    public DbSet<WallpaperItem> WallpaperItems => Set<WallpaperItem>();
    public DbSet<MonitorAssignment> MonitorAssignments => Set<MonitorAssignment>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    private readonly string _dbPath;

    public AppDbContext(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "wallpaper.db");
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dir = Path.GetDirectoryName(_dbPath)!;
            Directory.CreateDirectory(dir);
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallpaperItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.SourceType).IsRequired().HasMaxLength(10);
            e.Property(x => x.OriginalFileName).HasMaxLength(500);
            e.Property(x => x.ManagedFilePath).IsRequired().HasMaxLength(1000);
            e.Property(x => x.ThumbnailPath).HasMaxLength(1000);
            e.Property(x => x.ContainerFormat).HasMaxLength(20);
            e.Property(x => x.CodecSummary).HasMaxLength(100);
            e.Property(x => x.ValidationStatus).HasMaxLength(20);
        });

        modelBuilder.Entity<MonitorAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MonitorKey).IsRequired().HasMaxLength(200);
            e.Property(x => x.MonitorDeviceName).HasMaxLength(200);
        });

        modelBuilder.Entity<SchemaVersion>(e =>
        {
            e.HasKey(x => x.Id);
        });
    }

    public async Task MigrateAsync()
    {
        await Database.EnsureCreatedAsync();

        var current = await SchemaVersions.FirstOrDefaultAsync();
        var targetVersion = 1;

        if (current == null)
        {
            SchemaVersions.Add(new SchemaVersion { Version = targetVersion, AppliedAtUtc = DateTime.UtcNow });
            await SaveChangesAsync();
        }
        else if (current.Version < targetVersion)
        {
            // Future migrations go here
            current.Version = targetVersion;
            current.AppliedAtUtc = DateTime.UtcNow;
            await SaveChangesAsync();
        }
    }
}

public class SchemaVersion
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DateTime AppliedAtUtc { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "AppDbContextTests" -v n
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Data/ tests/WallpaperApp.Tests/Data/
git commit -m "feat: add SQLite AppDbContext with schema versioning and migration"
```

---

## Task 4: Logging Framework

**Files:**
- Create: `src/WallpaperApp/Services/Logging/FileLogger.cs`

- [ ] **Step 1: Implement FileLogger**

```csharp
// src/WallpaperApp/Services/Logging/FileLogger.cs
namespace WallpaperApp.Services.Logging;

public sealed class FileLogger : IDisposable
{
    private readonly string _logDir;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private DateTime _currentDate;

    public FileLogger(string? logDir = null)
    {
        _logDir = logDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "logs");
        Directory.CreateDirectory(_logDir);
        RotateIfNeeded();
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex.Message}\n{ex.StackTrace}" : message;
        Write("ERROR", msg);
    }
    public void Debug(string message) => Write("DEBUG", message);

    private void Write(string level, string message)
    {
        lock (_lock)
        {
            RotateIfNeeded();
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _writer?.WriteLine($"[{timestamp}] [{level}] {message}");
            _writer?.Flush();
        }
    }

    private void RotateIfNeeded()
    {
        var today = DateTime.UtcNow.Date;
        if (_currentDate != today || _writer == null)
        {
            _writer?.Dispose();
            _currentDate = today;
            var path = Path.Combine(_logDir, $"wallpaper-{today:yyyy-MM-dd}.log");
            _writer = new StreamWriter(path, append: true) { AutoFlush = false };
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Services/Logging/
git commit -m "feat: add rolling file logger"
```

---

## Task 5: FFmpeg P/Invoke Backend

**Files:**
- Create: `src/WallpaperApp/Services/Playback/IPlaybackBackend.cs`
- Create: `src/WallpaperApp/Services/Playback/FfmpegNative.cs`
- Create: `src/WallpaperApp/Services/Playback/FfmpegBackend.cs`

- [ ] **Step 1: Define IPlaybackBackend interface**

```csharp
// src/WallpaperApp/Services/Playback/IPlaybackBackend.cs
namespace WallpaperApp.Services.Playback;

public interface IPlaybackBackend : IDisposable
{
    bool IsPlaying { get; }
    bool IsPaused { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }

    Task<bool> OpenAsync(string filePath, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task SeekAsync(TimeSpan position, CancellationToken ct = default);

    /// <summary>
    /// Returns the next decoded frame as BGRA pixel data.
    /// Blocks until a frame is available or timeout.
    /// Returns null on end-of-stream or error.
    /// </summary>
    Task<FrameData?> NextFrameAsync(CancellationToken ct = default);

    event EventHandler? EndOfStream;
}

public record FrameData(
    IntPtr Buffer,
    int Width,
    int Height,
    int Stride,
    long PresentationTimestampUs
);
```

- [ ] **Step 2: Write FFmpeg P/Invoke declarations**

```csharp
// src/WallpaperApp/Services/Playback/FfmpegNative.cs
using System.Runtime.InteropServices;

namespace WallpaperApp.Services.Playback;

internal static partial class FfmpegNative
{
    private const string AvFormat = "avformat-61";
    private const string AvCodec = "avcodec-61";
    private const string AvUtil = "avutil-59";
    private const string SwScale = "swscale-8";

    // avformat
    [LibraryImport(AvFormat)]
    internal static partial IntPtr avformat_alloc_context();

    [LibraryImport(AvFormat)]
    internal static partial void avformat_free_context(IntPtr ps);

    [LibraryImport(AvFormat, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int avformat_open_input(out IntPtr ps, string? url, IntPtr fmt, IntPtr[]? options);

    [LibraryImport(AvFormat)]
    internal static partial void avformat_close_input(out IntPtr ps);

    [LibraryImport(AvFormat)]
    internal static partial int avformat_find_stream_info(IntPtr ps, IntPtr[]? options);

    [LibraryImport(AvFormat)]
    internal static partial int av_read_frame(IntPtr ps, IntPtr pkt);

    [LibraryImport(AvFormat)]
    internal static partial long av_find_best_stream(IntPtr ps, int type, int wanted_stream_nb, int related_stream, IntPtr[]? decoder_ret, int flags);

    // avcodec
    [LibraryImport(AvCodec, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr avcodec_find_decoder(int id);

    [LibraryImport(AvCodec)]
    internal static partial IntPtr avcodec_alloc_context3(IntPtr codec);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_free_context(IntPtr[] avctx);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_parameters_to_context(IntPtr codec, IntPtr par);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_open2(IntPtr ctx, IntPtr codec, IntPtr[]? options);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_send_packet(IntPtr ctx, IntPtr avpkt);

    [LibraryImport(AvCodec)]
    internal static partial int avcodec_receive_frame(IntPtr ctx, IntPtr frame);

    [LibraryImport(AvCodec)]
    internal static partial void avcodec_flush_buffers(IntPtr ctx);

    // avutil - AVFrame
    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_frame_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_frame_free(IntPtr[] frame);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_width(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_height(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial long av_frame_get_best_effort_timestamp(IntPtr frame);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_frame_get_data(IntPtr frame, int plane);

    [LibraryImport(AvUtil)]
    internal static partial int av_frame_get_linesize(IntPtr frame, int plane);

    // avutil - AVPacket
    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_packet_alloc();

    [LibraryImport(AvUtil)]
    internal static partial void av_packet_free(IntPtr[] pkt);

    [LibraryImport(AvUtil)]
    internal static partial void av_packet_unref(IntPtr pkt);

    // avutil - misc
    [LibraryImport(AvUtil)]
    internal static partial void av_free(IntPtr ptr);

    [LibraryImport(AvUtil)]
    internal static partial IntPtr av_malloc(ulong size);

    // swscale
    [LibraryImport(SwScale)]
    internal static partial IntPtr sws_getContext(
        int srcW, int srcH, int srcFormat,
        int dstW, int dstH, int dstFormat,
        int flags, IntPtr srcFilter, IntPtr dstFilter, IntPtr[]? param);

    [LibraryImport(SwScale)]
    internal static partial void sws_freeContext(IntPtr swsContext);

    [LibraryImport(SwScale)]
    internal static partial int sws_scale(
        IntPtr context,
        IntPtr[] srcSlice, int[] srcStride, int srcSliceY, int srcH,
        IntPtr[] dstSlice, int[] dstStride);

    // Constants
    internal const int AVMEDIA_TYPE_VIDEO = 0;
    internal const int AV_CODEC_ID_NONE = 0;
    internal const int AV_PIX_FMT_BGRA = 26; // BGRA format for D3D11
    internal const int AV_PIX_FMT_YUV420P = 0;
    internal const int SWS_BILINEAR = 2;
    internal const int AVSEEK_FLAG_BACKWARD = 1;
    internal const int AVFMT_FLAG_NOBUFFER = 0x0040;
}
```

- [ ] **Step 3: Implement FfmpegBackend**

```csharp
// src/WallpaperApp/Services/Playback/FfmpegBackend.cs
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class FfmpegBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;
    private IntPtr _formatContext;
    private IntPtr _codecContext;
    private IntPtr _swsContext;
    private IntPtr _frame;
    private IntPtr _packet;
    private int _videoStreamIndex = -1;
    private AVRational _timeBase;
    private long _durationUs;
    private bool _isOpen;

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public TimeSpan Duration => TimeSpan.FromTicks(_durationUs * 10);
    public TimeSpan Position { get; private set; }

    public event EventHandler? EndOfStream;

    public FfmpegBackend(FileLogger logger)
    {
        _logger = logger;
        _frame = FfmpegNative.av_frame_alloc();
        _packet = FfmpegNative.av_packet_alloc();
    }

    public async Task<bool> OpenAsync(string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                Close();

                _formatContext = FfmpegNative.avformat_alloc_context();

                // Disable buffering for lower latency
                var options = FfmpegNative.av_malloc(0);
                FfmpegNative.avformat_open_input(out _formatContext, filePath, IntPtr.Zero, null);

                if (FfmpegNative.avformat_find_stream_info(_formatContext, null) < 0)
                {
                    _logger.Error($"Failed to find stream info: {filePath}");
                    return false;
                }

                _videoStreamIndex = (int)FfmpegNative.av_find_best_stream(
                    _formatContext, FfmpegNative.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);

                if (_videoStreamIndex < 0)
                {
                    _logger.Error($"No video stream found: {filePath}");
                    return false;
                }

                // Get stream info
                var streamPtr = Marshal.ReadIntPtr(
                    _formatContext + Marshal.OffsetOf<AVFormatContext>("streams"));
                var stream = Marshal.ReadIntPtr(streamPtr + _videoStreamIndex * IntPtr.Size);

                _timeBase = Marshal.PtrToStructure<AVRational>(
                    stream + Marshal.OffsetOf<AVStream>("time_base"));

                var durationPtr = stream + Marshal.OffsetOf<AVStream>("duration");
                _durationUs = Marshal.ReadInt64(durationPtr);

                // Get codec parameters
                var codecparPtr = stream + Marshal.OffsetOf<AVStream>("codecpar");
                var codecId = Marshal.ReadInt32(codecparPtr + Marshal.OffsetOf<AVCodecParameters>("codec_id"));

                var codec = FfmpegNative.avcodec_find_decoder(codecId);
                if (codec == IntPtr.Zero)
                {
                    _logger.Error($"Decoder not found for codec ID: {codecId}");
                    return false;
                }

                _codecContext = FfmpegNative.avcodec_alloc_context3(codec);
                FfmpegNative.avcodec_parameters_to_context(_codecContext, codecparPtr);

                if (FfmpegNative.avcodec_open2(_codecContext, codec, null) < 0)
                {
                    _logger.Error($"Failed to open codec: {codecId}");
                    return false;
                }

                var width = FfmpegNative.av_frame_get_width(
                    Marshal.ReadIntPtr(_codecContext + 64)); // frame pointer offset
                var height = FfmpegNative.av_frame_get_height(
                    Marshal.ReadIntPtr(_codecContext + 64));

                // Create SWS context for format conversion to BGRA
                var srcWidth = Marshal.ReadInt32(_codecContext + Marshal.OffsetOf<AVCodecContext>("width"));
                var srcHeight = Marshal.ReadInt32(_codecContext + Marshal.OffsetOf<AVCodecContext>("height"));
                var srcPixFmt = Marshal.ReadInt32(_codecContext + Marshal.OffsetOf<AVCodecContext>("pix_fmt"));

                _swsContext = FfmpegNative.sws_getContext(
                    srcWidth, srcHeight, srcPixFmt,
                    srcWidth, srcHeight, FfmpegNative.AV_PIX_FMT_BGRA,
                    FfmpegNative.SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, null);

                _isOpen = true;
                _logger.Info($"Opened: {filePath} ({srcWidth}x{srcHeight})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open: {filePath}", ex);
                return false;
            }
        }, ct);
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        IsPlaying = true;
        IsPaused = false;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        IsPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        IsPaused = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        IsPlaying = false;
        IsPaused = false;
        Position = TimeSpan.Zero;
        if (_isOpen)
        {
            FfmpegNative.avcodec_flush_buffers(_codecContext);
        }
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    {
        if (!_isOpen) return Task.CompletedTask;

        var ts = (long)(position.TotalSeconds * _timeBase.den / _timeBase.num);
        // Seek to keyframe before target
        var streamPtr = Marshal.ReadIntPtr(
            _formatContext + Marshal.OffsetOf<AVFormatContext>("streams"));
        var stream = Marshal.ReadIntPtr(streamPtr + _videoStreamIndex * IntPtr.Size);
        var streamIndex = Marshal.ReadInt32(stream + Marshal.OffsetOf<AVStream>("index"));

        // Use avformat_seek_file
        Position = position;
        return Task.CompletedTask;
    }

    public async Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
    {
        if (!_isOpen || !IsPlaying || IsPaused)
            return null;

        return await Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    var ret = FfmpegNative.av_read_frame(_formatContext, _packet);
                    if (ret < 0)
                    {
                        // End of file — loop back
                        SeekAsync(TimeSpan.Zero).Wait();
                        EndOfStream?.Invoke(this, EventArgs.Empty);
                        return null;
                    }

                    var streamIndex = Marshal.ReadInt32(
                        _packet + Marshal.OffsetOf<AVPacket>("stream_index"));

                    if (streamIndex != _videoStreamIndex)
                    {
                        FfmpegNative.av_packet_unref(_packet);
                        continue;
                    }

                    // Decode
                    var sendRet = FfmpegNative.avcodec_send_packet(_codecContext, _packet);
                    FfmpegNative.av_packet_unref(_packet);

                    if (sendRet < 0) continue;

                    var receiveRet = FfmpegNative.avcodec_receive_frame(_codecContext, _frame);
                    if (receiveRet < 0) continue;

                    var pts = FfmpegNative.av_frame_get_best_effort_timestamp(_frame);
                    var ts = pts * _timeBase.num / _timeBase.den;
                    Position = TimeSpan.FromTicks(ts * TimeSpan.TicksPerSecond);

                    // Convert to BGRA
                    var width = FfmpegNative.av_frame_get_width(_frame);
                    var height = FfmpegNative.av_frame_get_height(_frame);
                    var bufferSize = width * height * 4;
                    var buffer = FfmpegNative.av_malloc((ulong)bufferSize);

                    var srcData = new IntPtr[8];
                    var srcLinesize = new int[8];
                    srcData[0] = FfmpegNative.av_frame_get_data(_frame, 0);
                    srcLinesize[0] = FfmpegNative.av_frame_get_linesize(_frame, 0);

                    var dstData = new IntPtr[] { buffer };
                    var dstLinesize = new int[] { width * 4 };

                    FfmpegNative.sws_scale(
                        _swsContext, srcData, srcLinesize, 0, height,
                        dstData, dstLinesize);

                    return new FrameData(buffer, width, height, width * 4, pts);
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.Error("Frame decode error", ex);
                return null;
            }
        }, ct);
    }

    private void Close()
    {
        if (_swsContext != IntPtr.Zero)
        {
            FfmpegNative.sws_freeContext(_swsContext);
            _swsContext = IntPtr.Zero;
        }
        if (_codecContext != IntPtr.Zero)
        {
            FfmpegNative.avcodec_free_context(new[] { _codecContext });
            _codecContext = IntPtr.Zero;
        }
        if (_formatContext != IntPtr.Zero)
        {
            FfmpegNative.avformat_close_input(out _formatContext);
        }
        _isOpen = false;
    }

    public void Dispose()
    {
        Close();
        if (_frame != IntPtr.Zero)
            FfmpegNative.av_frame_free(new[] { _frame });
        if (_packet != IntPtr.Zero)
            FfmpegNative.av_packet_free(new[] { _packet });
    }

    // Helper structs for offset calculation
    private struct AVFormatContext { }
    private struct AVStream { }
    private struct AVCodecContext { }
    private struct AVCodecParameters { }
    private struct AVPacket { }
    private struct AVRational { public int num; public int den; }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/
git commit -m "feat: add IPlaybackBackend interface and FFmpeg P/Invoke backend"
```

---

## Task 6: Playback Session and Manager

**Files:**
- Create: `src/WallpaperApp/Services/Playback/PlaybackSession.cs`
- Create: `src/WallpaperApp/Services/Playback/PlaybackManager.cs`

- [ ] **Step 1: Implement PlaybackSession**

```csharp
// src/WallpaperApp/Services/Playback/PlaybackSession.cs
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackSession : IDisposable
{
    private readonly IPlaybackBackend _backend;
    private readonly FileLogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _playbackTask;

    public Guid MonitorId { get; }
    public Guid WallpaperId { get; set; }
    public bool IsPlaying => _backend.IsPlaying;
    public bool IsPaused => _backend.IsPaused;

    public PlaybackSession(Guid monitorId, IPlaybackBackend backend, FileLogger logger)
    {
        MonitorId = monitorId;
        _backend = backend;
        _logger = logger;
    }

    public async Task<bool> LoadAsync(string filePath, CancellationToken ct = default)
    {
        return await _backend.OpenAsync(filePath, ct);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _backend.PlayAsync(_cts.Token);
        _playbackTask = Task.Run(() => RenderLoop(_cts.Token), _cts.Token);
        _logger.Info($"Session started for monitor {MonitorId}");
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_playbackTask != null)
        {
            try { await _playbackTask; }
            catch (OperationCanceledException) { }
        }
        await _backend.StopAsync();
        _logger.Info($"Session stopped for monitor {MonitorId}");
    }

    public async Task PauseAsync()
    {
        await _backend.PauseAsync();
    }

    public async Task ResumeAsync()
    {
        await _backend.ResumeAsync();
    }

    private async Task RenderLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var frame = await _backend.NextFrameAsync(ct);
                if (frame == null)
                {
                    // End of stream or error — loop
                    await _backend.SeekAsync(TimeSpan.Zero, ct);
                    continue;
                }

                // TODO: Submit frame to D3D11 swap chain for presentation
                // This will be implemented in the DesktopHost task
                FfmpegNative.av_free(frame.Buffer);

                // Target ~60fps
                await Task.Delay(16, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"Render loop error for monitor {MonitorId}", ex);
                await Task.Delay(1000, ct);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _backend.Dispose();
    }
}
```

- [ ] **Step 2: Implement PlaybackManager**

```csharp
// src/WallpaperApp/Services/Playback/PlaybackManager.cs
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class PlaybackManager : IDisposable
{
    private readonly FileLogger _logger;
    private readonly Dictionary<Guid, PlaybackSession> _sessions = new();
    private readonly Dictionary<Guid, string> _monitorFiles = new();

    public IReadOnlyDictionary<Guid, PlaybackSession> Sessions => _sessions;

    public PlaybackManager(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task SetWallpaperAsync(Guid monitorId, Guid wallpaperId, string filePath, FitMode fitMode)
    {
        // Stop existing session for this monitor
        if (_sessions.TryGetValue(monitorId, out var existing))
        {
            await existing.StopAsync();
            existing.Dispose();
            _sessions.Remove(monitorId);
        }

        // Create new backend and session
        var backend = CreateBackend();
        var session = new PlaybackSession(monitorId, backend, _logger);

        if (await session.LoadAsync(filePath))
        {
            _sessions[monitorId] = session;
            _monitorFiles[monitorId] = filePath;
            session.Start();
            _logger.Info($"Wallpaper {wallpaperId} assigned to monitor {monitorId}");
        }
        else
        {
            session.Dispose();
            _logger.Warn($"Failed to load wallpaper for monitor {monitorId}: {filePath}");
        }
    }

    public async Task RemoveWallpaperAsync(Guid monitorId)
    {
        if (_sessions.TryGetValue(monitorId, out var session))
        {
            await session.StopAsync();
            session.Dispose();
            _sessions.Remove(monitorId);
            _monitorFiles.Remove(monitorId);
        }
    }

    public async Task PauseAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.PauseAsync();
        }
    }

    public async Task ResumeAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.ResumeAsync();
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.StopAsync();
            session.Dispose();
        }
        _sessions.Clear();
        _monitorFiles.Clear();
    }

    private IPlaybackBackend CreateBackend()
    {
        try
        {
            // Try FFmpeg first
            return new FfmpegBackend(_logger);
        }
        catch
        {
            // Fallback to Media Foundation
            _logger.Warn("FFmpeg unavailable, falling back to Media Foundation");
            return new MfBackend(_logger);
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
```

- [ ] **Step 3: Create MfBackend stub**

```csharp
// src/WallpaperApp/Services/Playback/MfBackend.cs
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class MfBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;

    public bool IsPlaying => false;
    public bool IsPaused => false;
    public TimeSpan Duration => TimeSpan.Zero;
    public TimeSpan Position => TimeSpan.Zero;

    public event EventHandler? EndOfStream;

    public MfBackend(FileLogger logger)
    {
        _logger = logger;
        _logger.Info("Media Foundation backend initialized (stub)");
    }

    public Task<bool> OpenAsync(string filePath, CancellationToken ct = default)
    {
        _logger.Warn($"MF backend stub: cannot open {filePath}");
        return Task.FromResult(false);
    }

    public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position, CancellationToken ct = default) => Task.CompletedTask;

    public Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
        => Task.FromResult<FrameData?>(null);

    public void Dispose() { }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Playback/
git commit -m "feat: add PlaybackSession, PlaybackManager, and MF fallback stub"
```

---

## Task 7: Desktop Host (WorkerW/Progman)

**Files:**
- Create: `src/WallpaperApp/Services/Desktop/DesktopHost.cs`
- Create: `src/WallpaperApp/Services/Desktop/WallpaperWindow.cs`

- [ ] **Step 1: Implement WallpaperWindow**

```csharp
// src/WallpaperApp/Services/Desktop/WallpaperWindow.cs
using System.Runtime.InteropServices;
using System.Windows.Interop;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class WallpaperWindow : IDisposable
{
    private readonly FileLogger _logger;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _isFallback;

    public IntPtr Handle => _hwnd;
    public bool IsFallback => _isFallback;

    public WallpaperWindow(FileLogger logger)
    {
        _logger = logger;
    }

    public bool TryAttachToDesktop()
    {
        try
        {
            // Find Progman
            var progman = NativeMethods.FindWindowExW(IntPtr.Zero, IntPtr.Zero, "Progman", null);
            if (progman == IntPtr.Zero)
            {
                _logger.Warn("Progman window not found");
                return false;
            }

            // Send 0x052C to spawn WorkerW
            NativeMethods.SendMessageW(progman, NativeMethods.WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero);

            // Find the WorkerW spawned by Progman
            var workerW = FindWorkerWBehindProgman(progman);

            if (workerW == IntPtr.Zero)
            {
                _logger.Warn("WorkerW not found after 0x052C, trying alternative discovery");
                workerW = FindWorkerWAlternative();
            }

            if (workerW == IntPtr.Zero)
            {
                _logger.Warn("All WorkerW discovery methods failed, using fallback");
                return CreateFallbackWindow();
            }

            // Create our wallpaper window
            CreateWallpaperHwnd(workerW);
            _isFallback = false;
            _logger.Info("Attached to desktop via WorkerW");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Desktop attachment failed", ex);
            return CreateFallbackWindow();
        }
    }

    private IntPtr FindWorkerWBehindProgman(IntPtr progman)
    {
        // Progman -> SHELLDLL_DefView -> WorkerW
        var defView = NativeMethods.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) return IntPtr.Zero;

        // Find the WorkerW after SHELLDLL_DefView
        var workerW = NativeMethods.FindWindowExW(IntPtr.Zero, defView, "WorkerW", null);
        return workerW;
    }

    private IntPtr FindWorkerWAlternative()
    {
        // Enumerate all top-level windows looking for WorkerW with SHELLDLL_DefView child
        IntPtr result = IntPtr.Zero;
        var callback = new NativeMethods.EnumWindowsDelegate((hwnd, _) =>
        {
            var className = GetClassName(hwnd);
            if (className == "WorkerW")
            {
                var child = NativeMethods.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (child != IntPtr.Zero)
                {
                    result = hwnd;
                    return false;
                }
            }
            return true;
        });

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        return result;
    }

    private bool CreateFallbackWindow()
    {
        try
        {
            var parameters = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                hInstance = NativeMethods.GetModuleHandleW(null),
                lpszClassName = "WallpaperApp_Fallback",
                lpfnWndProc = DefWindowProc
            };

            NativeMethods.RegisterClassExW(ref parameters);

            _hwnd = NativeMethods.CreateWindowExW(
                NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE,
                "WallpaperApp_Fallback", null,
                NativeMethods.WS_CHILD,
                0, 0,
                NativeMethods.GetSystemMetrics(SM_CXSCREEN),
                NativeMethods.GetSystemMetrics(SM_CYSCREEN),
                IntPtr.Zero, IntPtr.Zero, parameters.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                _logger.Error("Failed to create fallback window");
                return false;
            }

            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_BOTTOM, 0, 0, 0, 0,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);

            _isFallback = true;
            _logger.Info("Created fallback WS_EX_TOOLWINDOW overlay");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("Fallback window creation failed", ex);
            return false;
        }
    }

    private void CreateWallpaperHwnd(IntPtr parent)
    {
        var parameters = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = "WallpaperApp_Wallpaper",
            lpfnWndProc = DefWindowProc
        };

        NativeMethods.RegisterClassExW(ref parameters);

        _hwnd = NativeMethods.CreateWindowExW(
            0, "WallpaperApp_Wallpaper", null,
            NativeMethods.WS_CHILD,
            0, 0,
            NativeMethods.GetSystemMetrics(SM_CXSCREEN),
            NativeMethods.GetSystemMetrics(SM_CYSCREEN),
            parent, IntPtr.Zero, parameters.hInstance, IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        }
    }

    public void Resize(int width, int height)
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = NativeMethods.GetClassNameW(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    private static IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    // Additional P/Invoke declarations needed
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsDelegate lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetModuleHandleW(string? lpModuleName);

    internal delegate bool EnumWindowsDelegate(IntPtr hWnd, IntPtr lParam);

    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const uint WS_POPUP = 0x80000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }
}
```

- [ ] **Step 2: Implement DesktopHost**

```csharp
// src/WallpaperApp/Services/Desktop/DesktopHost.cs
using WallpaperApp.Services.Logging;
using WallpaperApp.Interop;

namespace WallpaperApp.Services.Desktop;

public sealed class DesktopHost : IDisposable
{
    private readonly FileLogger _logger;
    private readonly List<WallpaperWindow> _windows = new();
    private System.Threading.Timer? _retryTimer;
    private bool _attached;

    public bool IsAttached => _attached;
    public bool IsFallback => _windows.Any(w => w.IsFallback);

    public DesktopHost(FileLogger logger)
    {
        _logger = logger;
    }

    public bool Attach()
    {
        var window = new WallpaperWindow(_logger);
        if (window.TryAttachToDesktop())
        {
            _windows.Add(window);
            _attached = true;
            return true;
        }

        window.Dispose();
        _attached = false;
        StartRetryTimer();
        return false;
    }

    public void CreateForMonitor(Guid monitorId, int width, int height)
    {
        // For multi-monitor, create additional wallpaper windows
        // Each gets its own HWND parented to the same WorkerW
        if (_windows.Count == 0)
        {
            Attach();
        }

        if (_windows.Count > 0)
        {
            _windows[0].Resize(width, height);
        }
    }

    public void ResizeMainWindow(int width, int height)
    {
        if (_windows.Count > 0)
        {
            _windows[0].Resize(width, height);
        }
    }

    private void StartRetryTimer()
    {
        _retryTimer?.Dispose();
        _retryTimer = new System.Threading.Timer(_ =>
        {
            if (!_attached)
            {
                _logger.Debug("Retrying WorkerW attachment...");
                Attach();
            }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void StopRetryTimer()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
    }

    public void Dispose()
    {
        StopRetryTimer();
        foreach (var window in _windows)
        {
            window.Dispose();
        }
        _windows.Clear();
        _attached = false;
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/WallpaperApp/Services/Desktop/
git commit -m "feat: add DesktopHost with WorkerW/Progman and fallback window"
```

---

## Task 8: Monitor Manager and Fullscreen Detection

**Files:**
- Create: `src/WallpaperApp/Services/Monitor/MonitorIdentity.cs`
- Create: `src/WallpaperApp/Services/Monitor/MonitorManager.cs`
- Create: `src/WallpaperApp/Services/Monitor/FullscreenDetector.cs`
- Create: `tests/WallpaperApp.Tests/Services/MonitorIdentityTests.cs`

- [ ] **Step 1: Write failing test for MonitorIdentity**

```csharp
// tests/WallpaperApp.Tests/Services/MonitorIdentityTests.cs
using WallpaperApp.Services.Monitor;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class MonitorIdentityTests
{
    [Fact]
    public void GenerateKey_CombinesHashAndConnectionType()
    {
        var key = MonitorIdentity.GenerateKey("ABC123", "HDMI");
        Assert.Contains("ABC123", key);
        Assert.Contains("HDMI", key);
    }

    [Fact]
    public void GenerateKey_DifferentInputsProduceDifferentKeys()
    {
        var key1 = MonitorIdentity.GenerateKey("ABC123", "HDMI");
        var key2 = MonitorIdentity.GenerateKey("ABC123", "DP");
        var key3 = MonitorIdentity.GenerateKey("XYZ789", "HDMI");
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "MonitorIdentityTests" -v n
```

Expected: FAIL

- [ ] **Step 3: Implement MonitorIdentity**

```csharp
// src/WallpaperApp/Services/Monitor/MonitorIdentity.cs
using System.Security.Cryptography;
using System.Text;

namespace WallpaperApp.Services.Monitor;

public static class MonitorIdentity
{
    public static string GenerateKey(string edidHash, string connectionType)
    {
        var input = $"{edidHash}|{connectionType}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16];
    }

    public static string HashEdid(byte[] edidData)
    {
        var hash = SHA256.HashData(edidData);
        return Convert.ToHexString(hash)[..16];
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "MonitorIdentityTests" -v n
```

Expected: PASS

- [ ] **Step 5: Implement MonitorManager**

```csharp
// src/WallpaperApp/Services/Monitor/MonitorManager.cs
using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

public sealed class MonitorInfo
{
    public string MonitorKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class MonitorManager
{
    private readonly FileLogger _logger;
    private readonly Dictionary<string, MonitorInfo> _monitors = new();

    public IReadOnlyDictionary<string, MonitorInfo> Monitors => _monitors;

    public event EventHandler? MonitorsChanged;

    public MonitorManager(FileLogger logger)
    {
        _logger = logger;
    }

    public void Refresh()
    {
        _monitors.Clear();
        var callback = new NativeMethods.MonitorEnumDelegate((hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                var monitorInfo = new MonitorInfo
                {
                    MonitorKey = hMonitor.ToString("X"),
                    DeviceName = $"Monitor_{hMonitor:X}",
                    X = info.rcMonitor.Left,
                    Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    IsPrimary = (info.dwFlags & 1) != 0 // MONITORINFOF_PRIMARY
                };

                _monitors[monitorInfo.MonitorKey] = monitorInfo;
            }
            return true;
        });

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        _logger.Info($"Found {_monitors.Count} monitors");
    }

    public MonitorInfo? GetMonitorAtPoint(int x, int y)
    {
        return _monitors.Values.FirstOrDefault(m =>
            x >= m.X && x < m.X + m.Width &&
            y >= m.Y && y < m.Y + m.Height);
    }
}
```

- [ ] **Step 6: Implement FullscreenDetector**

```csharp
// src/WallpaperApp/Services/Monitor/FullscreenDetector.cs
using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

public sealed class FullscreenDetector
{
    private readonly FileLogger _logger;
    private IntPtr _lastForegroundWindow;

    public bool IsAnyFullscreen { get; private set; }

    public event EventHandler<bool>? FullscreenStateChanged;

    public FullscreenDetector(FileLogger logger)
    {
        _logger = logger;
    }

    public void Poll()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == _lastForegroundWindow) return;
        _lastForegroundWindow = hwnd;

        var wasFullscreen = IsAnyFullscreen;
        IsAnyFullscreen = CheckIsFullscreen(hwnd);

        if (wasFullscreen != IsAnyFullscreen)
        {
            _logger.Debug($"Fullscreen state changed: {IsAnyFullscreen}");
            FullscreenStateChanged?.Invoke(this, IsAnyFullscreen);
        }
    }

    private bool CheckIsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // Check window style for borderless fullscreen
        var style = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_STYLE);
        var exStyle = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);

        // WS_POPUP without WS_CAPTION = borderless fullscreen
        bool isPopup = (style & 0x80000000) != 0; // WS_POPUP
        bool hasCaption = (style & 0x00C00000) != 0; // WS_CAPTION | WS_THICKFRAME
        bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

        if (isPopup && !hasCaption && !isToolWindow)
        {
            // Check if it covers the full screen
            var rect = new NativeMethods.RECT();
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out rect, Marshal.SizeOf<NativeMethods.RECT>()) == 0)
            {
                var screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                var screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

                if (rect.Left <= 0 && rect.Top <= 0 &&
                    rect.Right >= screenWidth && rect.Bottom >= screenHeight)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
```

- [ ] **Step 7: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add src/WallpaperApp/Services/Monitor/ tests/WallpaperApp.Tests/Services/
git commit -m "feat: add MonitorManager, FullscreenDetector, and MonitorIdentity"
```

---

## Task 9: Library Service and Import

**Files:**
- Create: `src/WallpaperApp/Services/Library/LibraryService.cs`
- Create: `src/WallpaperApp/Services/Library/ThumbnailService.cs`
- Create: `src/WallpaperApp/Services/Library/GifTranscoder.cs`
- Create: `tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs`

- [ ] **Step 1: Write failing test for LibraryService**

```csharp
// tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class LibraryServiceTests
{
    [Fact]
    public async Task ImportAsync_AddsItemToDatabase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();

        var logger = new FileLogger(Path.GetTempPath());
        var service = new LibraryService(context, logger);

        // Create a temp file to import
        var tempFile = Path.GetTempFileName() + ".mp4";
        await File.WriteAllBytesAsync(tempFile, new byte[100]);

        try
        {
            var item = await service.ImportAsync(tempFile, CancellationToken.None);
            Assert.NotNull(item);
            Assert.Equal("Valid", item.ValidationStatus);

            var count = await context.WallpaperItems.CountAsync();
            Assert.Equal(1, count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "LibraryServiceTests" -v n
```

Expected: FAIL

- [ ] **Step 3: Implement LibraryService**

```csharp
// src/WallpaperApp/Services/Library/LibraryService.cs
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class LibraryService
{
    private readonly AppDbContext _db;
    private readonly FileLogger _logger;
    private readonly string _libraryDir;

    public LibraryService(AppDbContext db, FileLogger logger)
    {
        _db = db;
        _logger = logger;
        _libraryDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "library");
        Directory.CreateDirectory(_libraryDir);
    }

    public async Task<WallpaperItem> ImportAsync(string sourcePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(sourcePath);
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        var sourceType = ext == ".gif" ? "Gif" : "Video";

        var item = new WallpaperItem
        {
            OriginalFileName = fileName,
            SourceType = sourceType,
            ValidationStatus = "Pending"
        };

        try
        {
            // Disk space check
            var fileInfo = new FileInfo(sourcePath);
            var drive = new DriveInfo(Path.GetPathRoot(_libraryDir)!);
            if (drive.AvailableFreeSpace < fileInfo.Length * 2)
            {
                _logger.Warn($"Low disk space for import: {fileName}");
            }

            // Large file warning
            if (fileInfo.Length > 2L * 1024 * 1024 * 1024)
            {
                _logger.Warn($"Large file import (>2GB): {fileName}");
            }

            // Copy to managed library
            var managedPath = Path.Combine(_libraryDir, $"{item.Id}{ext}");
            await CopyFileAsync(sourcePath, managedPath, ct);

            item.ManagedFilePath = managedPath;
            item.FileBytes = fileInfo.Length;
            item.DisplayName = Path.GetFileNameWithoutExtension(fileName);
            item.ContainerFormat = ext.TrimStart('.');

            // Validate by attempting short decode
            item.ValidationStatus = await ValidateFileAsync(managedPath) ? "Valid" : "Invalid";

            // Generate thumbnail
            var thumbPath = Path.Combine(_libraryDir, $"{item.Id}_thumb.jpg");
            item.ThumbnailPath = thumbPath;

            // Insert into database
            _db.WallpaperItems.Add(item);
            await _db.SaveChangesAsync(ct);

            _logger.Info($"Imported: {fileName} ({item.Id})");
            return item;
        }
        catch (Exception ex)
        {
            _logger.Error($"Import failed: {fileName}", ex);
            item.ValidationStatus = "Invalid";
            throw;
        }
    }

    public async Task<List<WallpaperItem>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.WallpaperItems
            .OrderByDescending(x => x.ImportedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<WallpaperItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.WallpaperItems.FindAsync(new object[] { id }, ct);
    }

    public async Task DeleteAsync(Guid id, bool deleteFile = true, CancellationToken ct = default)
    {
        var item = await _db.WallpaperItems.FindAsync(new object[] { id }, ct);
        if (item == null) return;

        if (deleteFile && File.Exists(item.ManagedFilePath))
        {
            File.Delete(item.ManagedFilePath);
        }

        if (File.Exists(item.ThumbnailPath))
        {
            File.Delete(item.ThumbnailPath);
        }

        _db.WallpaperItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        _logger.Info($"Deleted wallpaper: {id}");
    }

    private async Task<bool> ValidateFileAsync(string path)
    {
        // Basic validation — check file is readable and has content
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[1024];
            await fs.ReadAsync(buffer);
            return fs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var sourceStream = File.OpenRead(source);
        await using var destStream = File.Create(destination);
        await sourceStream.CopyToAsync(destStream, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "LibraryServiceTests" -v n
```

Expected: PASS

- [ ] **Step 5: Implement ThumbnailService stub**

```csharp
// src/WallpaperApp/Services/Library/ThumbnailService.cs
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class ThumbnailService
{
    private readonly FileLogger _logger;

    public ThumbnailService(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> GenerateAsync(string videoPath, string outputPath, CancellationToken ct = default)
    {
        // Extract first frame as thumbnail using ffmpeg
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{videoPath}\" -vframes 1 -q:v 2 \"{outputPath}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync(ct);
                return process.ExitCode == 0;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"Thumbnail generation failed: {videoPath}", ex);
            return false;
        }
    }
}
```

- [ ] **Step 6: Implement GifTranscoder**

```csharp
// src/WallpaperApp/Services/Library/GifTranscoder.cs
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class GifTranscoder
{
    private readonly FileLogger _logger;

    public GifTranscoder(FileLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> TranscodeAsync(string gifPath, string outputPath, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMinutes(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{gifPath}\" -movflags +faststart -an -c:v libx264 -pix_fmt yuv420p \"{outputPath}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync(cts.Token);

            if (cts.IsCancellationRequested)
            {
                // Timeout — kill process and clean up
                try { process.Kill(); } catch { }
                if (File.Exists(outputPath)) File.Delete(outputPath);
                _logger.Warn($"GIF transcode timed out: {gifPath}");
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout
            if (File.Exists(outputPath)) File.Delete(outputPath);
            _logger.Warn($"GIF transcode timed out: {gifPath}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error($"GIF transcode failed: {gifPath}", ex);
            if (File.Exists(outputPath)) File.Delete(outputPath);
            return false;
        }
    }
}
```

- [ ] **Step 7: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add src/WallpaperApp/Services/Library/ tests/WallpaperApp.Tests/Services/
git commit -m "feat: add LibraryService, ThumbnailService, and GifTranscoder"
```

---

## Task 10: Settings Service

**Files:**
- Create: `src/WallpaperApp/Services/Settings/SettingsService.cs`
- Create: `tests/WallpaperApp.Tests/Services/SettingsServiceTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/WallpaperApp.Tests/Services/SettingsServiceTests.cs
using WallpaperApp.Models;
using WallpaperApp.Services.Settings;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class SettingsServiceTests
{
    [Fact]
    public async Task SaveAndLoad_PersistsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new AppSettings
            {
                LaunchAtStartup = true,
                Theme = "Light",
                DefaultFitMode = FitMode.Stretch
            };

            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();

            Assert.Equal(true, loaded.LaunchAtStartup);
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal(FitMode.Stretch, loaded.DefaultFitMode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "SettingsServiceTests" -v n
```

Expected: FAIL

- [ ] **Step 3: Implement SettingsService**

```csharp
// src/WallpaperApp/Services/Settings/SettingsService.cs
using System.Text.Json;
using Microsoft.Win32;
using WallpaperApp.Models;

namespace WallpaperApp.Services.Settings;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private const string AutoStartRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WallpaperApp";

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json);

        // Sync auto-start registry
        UpdateAutoStartRegistry(settings.LaunchAtStartup);
    }

    private void UpdateAutoStartRegistry(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);
            if (key == null) return;

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? "";
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/WallpaperApp.Tests/ --filter "SettingsServiceTests" -v n
```

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Settings/ tests/WallpaperApp.Tests/Services/
git commit -m "feat: add SettingsService with JSON persistence and auto-start registry"
```

---

## Task 11: WPF UI - MainWindow and TrayIcon

**Files:**
- Create: `src/WallpaperApp/UI/MainWindow.xaml`
- Create: `src/WallpaperApp/UI/MainWindow.xaml.cs`
- Create: `src/WallpaperApp/UI/TrayIcon.cs`
- Create: `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`
- Create: `src/WallpaperApp/App.xaml`
- Create: `src/WallpaperApp/App.xaml.cs`

- [ ] **Step 1: Implement MainViewModel**

```csharp
// src/WallpaperApp/UI/ViewModels/MainViewModel.cs
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WallpaperApp.Models;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Settings;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.UI.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _library;
    private readonly PlaybackManager _playback;
    private readonly MonitorManager _monitors;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public ObservableCollection<WallpaperItem> Wallpapers { get; } = new();
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    private WallpaperItem? _selectedWallpaper;
    public WallpaperItem? SelectedWallpaper
    {
        get => _selectedWallpaper;
        set { _selectedWallpaper = value; OnPropertyChanged(); }
    }

    private AppSettings _settingsModel = new();
    public AppSettings SettingsModel
    {
        get => _settingsModel;
        set { _settingsModel = value; OnPropertyChanged(); }
    }

    public MainViewModel(LibraryService library, PlaybackManager playback,
        MonitorManager monitors, SettingsService settings, FileLogger logger)
    {
        _library = library;
        _playback = playback;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        var items = await _library.GetAllAsync();
        Wallpapers.Clear();
        foreach (var item in items)
        {
            Wallpapers.Add(item);
        }

        _monitors.Refresh();
        Monitors.Clear();
        foreach (var m in _monitors.Monitors.Values)
        {
            Monitors.Add(m);
        }

        SettingsModel = await _settings.LoadAsync();
    }

    public async Task ImportFilesAsync(IEnumerable<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            try
            {
                var item = await _library.ImportAsync(path);
                Wallpapers.Insert(0, item);
            }
            catch (Exception ex)
            {
                _logger.Error($"Import failed: {path}", ex);
            }
        }
    }

    public async Task AssignWallpaperAsync(string monitorKey)
    {
        if (SelectedWallpaper == null) return;

        var monitor = _monitors.Monitors.Values.FirstOrDefault(m => m.MonitorKey == monitorKey);
        if (monitor == null) return;

        await _playback.SetWallpaperAsync(
            Guid.Parse(monitorKey),
            SelectedWallpaper.Id,
            SelectedWallpaper.ManagedFilePath,
            SettingsModel.DefaultFitMode);
    }

    public async Task PauseAllAsync() => await _playback.PauseAllAsync();
    public async Task ResumeAllAsync() => await _playback.ResumeAllAsync();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
```

- [ ] **Step 2: Create MainWindow.xaml**

```xml
<!-- src/WallpaperApp/UI/MainWindow.xaml -->
<Window x:Class="WallpaperApp.UI.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:WallpaperApp.UI.ViewModels"
        Title="Wallpaper App" Height="600" Width="900"
        Background="#1E1E2E" WindowStartupLocation="CenterScreen">

    <Window.Resources>
        <Style TargetType="Button">
            <Setter Property="Background" Value="#3B3B5C"/>
            <Setter Property="Foreground" Value="White"/>
            <Setter Property="Padding" Value="16,8"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Cursor" Value="Hand"/>
        </Style>
    </Window.Resources>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="300"/>
        </Grid.ColumnDefinitions>

        <!-- Wallpaper Library -->
        <DockPanel Grid.Column="0" Margin="16">
            <TextBlock DockPanel.Dock="Top" Text="Wallpaper Library"
                       FontSize="24" FontWeight="Bold" Foreground="White" Margin="0,0,0,16"/>

            <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,16,0,0">
                <Button Content="Import Files" Click="OnImportClick" Margin="0,0,8,0"/>
                <Button Content="Pause All" Click="OnPauseClick" Margin="0,0,8,0"/>
                <Button Content="Resume All" Click="OnResumeClick"/>
            </StackPanel>

            <ListBox ItemsSource="{Binding Wallpapers}"
                     SelectedItem="{Binding SelectedWallpaper}"
                     Background="Transparent" BorderThickness="0">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Orientation="Horizontal" Margin="0,4">
                            <TextBlock Text="{Binding DisplayName}" Foreground="White" VerticalAlignment="Center"/>
                            <TextBlock Text="{Binding SourceType}" Foreground="#888" Margin="8,0,0,0" VerticalAlignment="Center"/>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>

        <!-- Monitor Panel -->
        <DockPanel Grid.Column="1" Margin="16" Background="#2A2A3C" CornerRadius="8">
            <TextBlock DockPanel.Dock="Top" Text="Monitors"
                       FontSize="18" FontWeight="Bold" Foreground="White" Margin="12,12,12,16"/>

            <ItemsControl ItemsSource="{Binding Monitors}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Background="#3B3B5C" CornerRadius="6" Padding="12" Margin="0,0,0,8">
                            <StackPanel>
                                <TextBlock Text="{Binding DeviceName}" Foreground="White" FontWeight="SemiBold"/>
                                <TextBlock Text="{Binding Width}x{Binding Height}" Foreground="#AAA" FontSize="12"/>
                                <Button Content="Set Wallpaper" Click="OnAssignClick"
                                        Tag="{Binding MonitorKey}" Margin="0,8,0,0"
                                        HorizontalAlignment="Stretch"/>
                            </StackPanel>
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </DockPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: Implement MainWindow code-behind**

```csharp
// src/WallpaperApp/UI/MainWindow.xaml.cs
using System.Windows;
using Microsoft.Win32;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Media Files|*.mp4;*.webm;*.avi;*.mkv;*.mov;*.gif|All Files|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.ImportFilesAsync(dialog.FileNames);
        }
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.PauseAllAsync();
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResumeAllAsync();
    }

    private async void OnAssignClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string monitorKey)
        {
            await _viewModel.AssignWallpaperAsync(monitorKey);
        }
    }
}
```

- [ ] **Step 4: Implement TrayIcon**

```csharp
// src/WallpaperApp/UI/TrayIcon.cs
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;

namespace WallpaperApp.UI;

public sealed class TrayIcon : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private readonly Action _onShow;
    private readonly Action _onPause;
    private readonly Action _onResume;
    private readonly Action _onExit;

    public TrayIcon(Action onShow, Action onPause, Action onResume, Action onExit)
    {
        _onShow = onShow;
        _onPause = onPause;
        _onResume = onResume;
        _onExit = onExit;
    }

    public void Show()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Wallpaper App",
            Visibility = Visibility.Visible
        };

        var menu = new System.Windows.Controls.ContextMenu();

        var showItem = new System.Windows.Controls.MenuItem { Header = "Open" };
        showItem.Click += (_, _) => _onShow();
        menu.Items.Add(showItem);

        var pauseItem = new System.Windows.Controls.MenuItem { Header = "Pause All" };
        pauseItem.Click += (_, _) => _onPause();
        menu.Items.Add(pauseItem);

        var resumeItem = new System.Windows.Controls.MenuItem { Header = "Resume All" };
        resumeItem.Click += (_, _) => _onResume();
        menu.Items.Add(resumeItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => _onExit();
        menu.Items.Add(exitItem);

        _trayIcon.ContextMenu = menu;
        _trayIcon.DoubleClickCommand = new RelayCommand(_ => _onShow());
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
    }
}

public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    public event EventHandler? CanExecuteChanged;

    public RelayCommand(Action<object?> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
```

- [ ] **Step 5: Update App.xaml for DI and startup**

```xml
<!-- src/WallpaperApp/App.xaml -->
<Application x:Class="WallpaperApp.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <SolidColorBrush x:Key="BackgroundBrush" Color="#1E1E2E"/>
            <SolidColorBrush x:Key="SurfaceBrush" Color="#2A2A3C"/>
            <SolidColorBrush x:Key="PrimaryBrush" Color="#3B3B5C"/>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 6: Implement App.xaml.cs with DI**

```csharp
// src/WallpaperApp/App.xaml.cs
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<FileLogger>();
        logger.Info("Application starting");

        // Initialize database
        var db = _serviceProvider.GetRequiredService<AppDbContext>();
        db.MigrateAsync().GetAwaiter().GetResult();

        // Setup tray icon
        _trayIcon = new TrayIcon(
            onShow: ShowMainWindow,
            onPause: () => _serviceProvider.GetRequiredService<PlaybackManager>().PauseAllAsync(),
            onResume: () => _serviceProvider.GetRequiredService<PlaybackManager>().ResumeAllAsync(),
            onExit: ShutdownApp
        );
        _trayIcon.Show();

        // Show main window unless minimized
        var settings = _serviceProvider.GetRequiredService<SettingsService>().LoadAsync().GetAwaiter().GetResult();
        if (!settings.StartMinimizedToTray)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null || !_mainWindow.IsLoaded)
        {
            var viewModel = _serviceProvider!.GetRequiredService<MainViewModel>();
            _mainWindow = new MainWindow(viewModel);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            _mainWindow.Show();
        }
        else
        {
            _mainWindow.Activate();
        }
    }

    private void ShutdownApp()
    {
        _serviceProvider?.GetRequiredService<PlaybackManager>().StopAllAsync().GetAwaiter().GetResult();
        _serviceProvider?.GetRequiredService<DesktopHost>().Dispose();
        _trayIcon?.Dispose();
        _serviceProvider?.Dispose();
        Shutdown();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<FileLogger>();
        services.AddSingleton<AppDbContext>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<PlaybackManager>();
        services.AddSingleton<DesktopHost>();
        services.AddSingleton<MonitorManager>();
        services.AddSingleton<FullscreenDetector>();
        services.AddSingleton<MainViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 7: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add src/WallpaperApp/UI/ src/WallpaperApp/App.xaml src/WallpaperApp/App.xaml.cs
git commit -m "feat: add WPF MainWindow, TrayIcon, and DI setup"
```

---

## Task 12: Integration and Startup Restore

**Files:**
- Modify: `src/WallpaperApp/App.xaml.cs`
- Create: `src/WallpaperApp/Services/Desktop/ExplorerWatcher.cs`

- [ ] **Step 1: Implement ExplorerWatcher**

```csharp
// src/WallpaperApp/Services/Desktop/ExplorerWatcher.cs
using System.Diagnostics;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class ExplorerWatcher : IDisposable
{
    private readonly DesktopHost _desktopHost;
    private readonly FileLogger _logger;
    private System.Threading.Timer? _timer;
    private int _lastExplorerPid;

    public event EventHandler? ExplorerRestarted;

    public ExplorerWatcher(DesktopHost desktopHost, FileLogger logger)
    {
        _desktopHost = desktopHost;
        _logger = logger;
        _lastExplorerPid = GetExplorerPid();
    }

    public void Start()
    {
        _timer = new System.Threading.Timer(CheckExplorer, null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void CheckExplorer(object? state)
    {
        var currentPid = GetExplorerPid();
        if (currentPid != _lastExplorerPid && _lastExplorerPid != 0)
        {
            _logger.Warn($"Explorer restart detected (PID {_lastExplorerPid} -> {currentPid})");
            _lastExplorerPid = currentPid;

            // Wait for Explorer to stabilize then reattach
            Task.Delay(2000).ContinueWith(_ =>
            {
                _desktopHost.Attach();
                ExplorerRestarted?.Invoke(this, EventArgs.Empty);
            });
        }
        _lastExplorerPid = currentPid;
    }

    private static int GetExplorerPid()
    {
        var procs = Process.GetProcessesByName("explorer");
        return procs.Length > 0 ? procs[0].Id : 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build src/WallpaperApp/WallpaperApp.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/Services/Desktop/ExplorerWatcher.cs
git commit -m "feat: add ExplorerWatcher for auto-reattach on Explorer restart"
```

---

## Task 13: Run All Tests

- [ ] **Step 1: Run full test suite**

```bash
dotnet test tests/WallpaperApp.Tests/ -v n
```

Expected: All tests pass.

- [ ] **Step 2: Final build verification**

```bash
dotnet build WallpaperApp.sln -c Release
```

Expected: Build succeeded.

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "chore: final integration and test verification"
```
