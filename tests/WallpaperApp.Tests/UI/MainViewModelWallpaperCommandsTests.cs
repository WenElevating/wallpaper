using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Input;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Playlists;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.Tests.UI;

public sealed class MainViewModelWallpaperCommandsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly FileLogger _logger;
    private readonly string _tempDir;

    public MainViewModelWallpaperCommandsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WallpaperCmdTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        // Transient factory (not Singleton): LibraryService disposes each context
        // it creates via 'await using', which would invalidate a shared singleton
        // for the next method. Each resolution gets a fresh context over the
        // shared in-memory SQLite connection.
        services.AddTransient(_ => NewContext());
        _provider = services.BuildServiceProvider();
        _logger = new FileLogger(_tempDir);
    }

    private AppDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        var ctx = new AppDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public void Commands_ArePopulatedAfterConstruction()
    {
        var vm = CreateViewModel();
        Assert.NotNull(vm.Commands);
        Assert.NotNull(vm.Commands.SetAsWallpaper);
        Assert.NotNull(vm.Commands.OpenDetail);
        Assert.NotNull(vm.Commands.OpenFileLocation);
        Assert.NotNull(vm.Commands.Rename);
        Assert.NotNull(vm.Commands.AddToPlaylist);
        Assert.NotNull(vm.Commands.CopyToFolder);
        Assert.NotNull(vm.Commands.Delete);
    }

    [Fact]
    public async Task ApplyRenameAsync_UpdatesMemoryObjectAndPersists()
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        var vm = CreateViewModel(library);
        var item = await SeedWallpaperAsync(library);
        vm.Wallpapers.Add(item);

        await vm.ApplyRenameAsync(item, "New Name");

        Assert.Equal("New Name", item.DisplayName);
        var persisted = await library.GetByIdAsync(item.Id);
        Assert.Equal("New Name", persisted!.DisplayName);
    }

    [Fact]
    public async Task DeleteWallpaperCoreAsync_StopsPlayback_RemovesFromListAndPlaylists()
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        var playlistService = new PlaylistService(_logger, _db);
        var vm = CreateViewModel(library, playlistService);
        var item = await SeedWallpaperAsync(library);
        vm.Wallpapers.Add(item);

        // Put it in a playlist so we can verify cascade cleanup.
        var playlistId = await playlistService.CreateAsync("P1");
        await playlistService.AddMemberAsync(playlistId, item.Id);
        await vm.LoadPlaylistsAsync();

        // Pretend it's playing on a monitor so we verify playback release.
        var monitorId = Guid.NewGuid();
        vm.Monitors.Add(new MonitorInfo
        {
            DeviceName = "D1",
            MonitorKey = monitorId.ToString(),
            Width = 1920,
            Height = 1080
        });
        var playback = (TestablePlaybackManager)vm.PlaybackForTests;
        playback.SetActive(monitorId, item.Id);

        // Tally impact the same way the UI flow does, then run the headless core
        // (the confirm dialog itself needs an STA thread and is exercised manually).
        var (playingMonitors, referencedPlaylists) = vm.TallyDeleteImpact(item);
        await vm.DeleteWallpaperCoreAsync(item, playingMonitors, referencedPlaylists);

        Assert.Empty(vm.Wallpapers);
        Assert.True(playback.WasRemoved(monitorId));
        var persisted = await library.GetByIdAsync(item.Id);
        Assert.Null(persisted);
        var pl = await playlistService.GetByIdAsync(playlistId);
        Assert.Empty(pl!.Members);
    }

    // Verifies SetAsWallpaperCommand's canExecute accepts a parameter wallpaper
    // (the refactor that made it param-driven). The full assign path depends on
    // MonitorManager.Refresh() scanning real hardware, which isn't reachable in a
    // unit test, so execution-level coverage lives in the manual smoke test (Task 9).
    [Fact]
    public void SetAsWallpaperCommand_CanExecuteWithParameterWallpaper()
    {
        var vm = CreateViewModel();
        var wallpaper = new WallpaperItem { DisplayName = "x", ManagedFilePath = "x" };

        // With a parameter, CanExecute is true even though ActiveWallpaper is null.
        vm.ActiveWallpaper = null;
        Assert.True(vm.SetAsWallpaperCommand.CanExecute(wallpaper));

        // With neither parameter nor ActiveWallpaper, CanExecute is false.
        Assert.False(vm.SetAsWallpaperCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectedPerformanceProfile_LoadsPersistsAndUpdatesPlaybackPolicy()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        await settings.SaveAsync(new AppSettings
        {
            PerformanceProfile = WallpaperPerformanceProfile.Saver
        });

        var vm = CreateViewModel(settings: settings);
        await vm.LoadAsync();

        Assert.Equal(WallpaperPerformanceProfile.Saver, vm.SelectedPerformanceProfile);
        Assert.Null(CurrentPolicy(vm).MaxPresentFps);
        Assert.Equal(DecoderFrameDiscard.NonReference, CurrentPolicy(vm).DecoderDiscard);

        vm.SelectedPerformanceProfile = WallpaperPerformanceProfile.Quality;
        await WaitForAsync(async () => (await settings.LoadAsync()).PerformanceProfile == WallpaperPerformanceProfile.Quality);

        Assert.Equal(WallpaperPerformanceProfile.Quality, vm.SelectedPerformanceProfile);
        Assert.Null(CurrentPolicy(vm).MaxPresentFps);
        Assert.Equal(DecoderFrameDiscard.Default, CurrentPolicy(vm).DecoderDiscard);
    }

    private static PlaybackPerformancePolicy CurrentPolicy(MainViewModel vm)
    {
        var field = typeof(PlaybackManager).GetField("_performancePolicy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<PlaybackPerformancePolicy>(field.GetValue(vm.PlaybackForTests));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!await condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    private async Task<WallpaperItem> SeedWallpaperAsync(LibraryService library, string name = "clip")
    {
        var path = Path.Combine(_tempDir, name + ".mp4");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00 });
        return (await library.ImportAsync(path))!;
    }

    private MainViewModel CreateViewModel(
        LibraryService? library = null,
        PlaylistService? playlistService = null,
        SettingsService? settings = null)
    {
        library ??= new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        playlistService ??= new PlaylistService(_logger, _db);
        var desktopHost = new DesktopHost(_logger);
        var playback = new TestablePlaybackManager(_logger, desktopHost);
        var monitors = new MonitorManager(_logger);
        settings ??= new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var hotkeys = new GlobalHotkeyService(_logger);
        var shuffler = new RandomWallpaperSwitcher(_logger);
        return new MainViewModel(library, playback, monitors, settings, _logger, hotkeys, playlistService, shuffler);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

// Minimal PlaybackManager subclass that records calls instead of rendering, so
// tests can assert stop/assign without a real D2D surface. Overrides the virtual
// members made virtual for testability.
internal sealed class TestablePlaybackManager : PlaybackManager
{
    private readonly Dictionary<Guid, Guid> _active = new();
    private readonly HashSet<Guid> _removed = new();
    private readonly Dictionary<Guid, Guid> _assigned = new();

    public TestablePlaybackManager(FileLogger logger, DesktopHost host) : base(logger, host) { }

    public void SetActive(Guid monitorId, Guid wallpaperId) => _active[monitorId] = wallpaperId;
    public bool WasRemoved(Guid monitorId) => _removed.Contains(monitorId);
    public Guid? AssignedId(Guid monitorId) => _assigned.TryGetValue(monitorId, out var id) ? id : null;

    public override Guid? GetActiveWallpaperId(Guid monitorId)
        => _active.TryGetValue(monitorId, out var id) ? id : null;

    public override Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        _active.Remove(monitorId);
        _removed.Add(monitorId);
        return Task.CompletedTask;
    }

    public override Task<bool> SetWallpaperAsync(Guid monitorId, Guid wallpaperId, string filePath,
        int x, int y, int w, int h, CancellationToken ct = default)
    {
        _active[monitorId] = wallpaperId;
        _assigned[monitorId] = wallpaperId;
        return Task.FromResult(true);
    }
}
