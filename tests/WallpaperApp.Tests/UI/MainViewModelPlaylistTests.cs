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

public sealed class MainViewModelPlaylistTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly FileLogger _logger;
    private readonly string _tempDir;

    public MainViewModelPlaylistTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PlaylistVmTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _provider = services.BuildServiceProvider();
        _logger = new FileLogger(_tempDir);
    }

    [Fact]
    public async Task LoadPlaylistsAsync_PopulatesExistingPlaylists()
    {
        var playlistService = new PlaylistService(_logger, _db);
        await playlistService.CreateAsync("Morning");
        var vm = CreateViewModel(playlistService);

        await vm.LoadPlaylistsAsync();

        var playlist = Assert.Single(vm.Playlists);
        Assert.Equal("Morning", playlist.Name);
    }

    [Fact]
    public async Task CreatePlaylistCommand_AddsCreatedPlaylistToVisibleCollection()
    {
        var vm = CreateViewModel(new PlaylistService(_logger, _db));

        vm.CreatePlaylistCommand.Execute(null);
        await WaitForAsync(() => vm.Playlists.Count == 1);

        Assert.StartsWith("Playlist ", vm.Playlists[0].Name);
    }

    [Fact]
    public void PlaylistView_MemberCountBinding_IsExplicitlyOneWay()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "WallpaperApp", "UI", "Views", "PlaylistView.xaml"));

        Assert.Contains("Text=\"{Binding Members.Count, Mode=OneWay", xaml);
    }

    [Fact]
    public void PlaylistView_WiresEditorCommandsAndReadableSelectionStyle()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "WallpaperApp", "UI", "Views", "PlaylistView.xaml"));

        Assert.Contains("SelectedItem=\"{Binding SelectedPlaylist, Mode=TwoWay}\"", xaml);
        Assert.Contains("Property=\"IsSelected\"", xaml);
        Assert.Contains("Property=\"Foreground\" Value=\"{StaticResource TextBrush}\"", xaml);
        Assert.Contains("AddToPlaylistCommand", xaml);
        Assert.Contains("RemoveFromPlaylistCommand", xaml);
        Assert.Contains("DeletePlaylistCommand", xaml);
    }

    [Fact]
    public async Task SelectingPlaylist_PopulatesMembersAndAddableWallpapers()
    {
        var playlistService = new PlaylistService(_logger, _db);
        var wallpaperA = await SeedWallpaperAsync("A");
        var wallpaperB = await SeedWallpaperAsync("B");
        var playlistId = await playlistService.CreateAsync("Morning");
        await playlistService.AddMemberAsync(playlistId, wallpaperA.Id);

        var vm = CreateViewModel(playlistService);
        vm.Wallpapers.Add(wallpaperA);
        vm.Wallpapers.Add(wallpaperB);
        await vm.LoadPlaylistsAsync();

        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == playlistId);
        await WaitForAsync(() => vm.PlaylistMembers.Count == 1 && vm.AddableWallpapers.Count == 1);

        Assert.Equal(wallpaperA.Id, vm.PlaylistMembers[0].WallpaperId);
        Assert.Single(vm.AddableWallpapers);
        Assert.Equal(wallpaperB.Id, vm.AddableWallpapers[0].Id);
    }

    [Fact]
    public async Task AddRemoveDeletePlaylistCommands_UpdateSelectionState()
    {
        var playlistService = new PlaylistService(_logger, _db);
        var wallpaperA = await SeedWallpaperAsync("A");
        var wallpaperB = await SeedWallpaperAsync("B");
        var playlistId = await playlistService.CreateAsync("Morning");

        var vm = CreateViewModel(playlistService);
        vm.Wallpapers.Add(wallpaperA);
        vm.Wallpapers.Add(wallpaperB);
        await vm.LoadPlaylistsAsync();
        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == playlistId);
        await WaitForAsync(() => vm.AddableWallpapers.Count == 2);

        vm.AddToPlaylistCommand.Execute(wallpaperA);
        await WaitForAsync(() => vm.PlaylistMembers.Count == 1 && vm.Playlists[0].Members.Count == 1);
        Assert.Single(vm.PlaylistMembers);
        Assert.Single(vm.AddableWallpapers);
        Assert.Single(vm.Playlists[0].Members);

        vm.RemoveFromPlaylistCommand.Execute(vm.PlaylistMembers[0]);
        await WaitForAsync(() => vm.PlaylistMembers.Count == 0 && vm.Playlists[0].Members.Count == 0);
        Assert.Empty(vm.PlaylistMembers);
        Assert.Equal(2, vm.AddableWallpapers.Count);
        Assert.Empty(vm.Playlists[0].Members);

        vm.DeletePlaylistCommand.Execute(null);
        await WaitForAsync(() => vm.Playlists.Count == 0);
        Assert.Empty(vm.Playlists);
        Assert.Null(vm.SelectedPlaylist);
    }

    [Fact]
    public async Task SavePlaylistSettingsCommand_PersistsEditableFields()
    {
        var playlistService = new PlaylistService(_logger, _db);
        var playlistId = await playlistService.CreateAsync("Morning");

        var vm = CreateViewModel(playlistService);
        await vm.LoadPlaylistsAsync();
        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == playlistId);
        await WaitForAsync(() => vm.PlaylistName == "Morning");

        vm.PlaylistName = "Evening";
        vm.PlaylistIntervalMinutes = 17;
        vm.PlaylistShuffle = true;
        vm.SavePlaylistSettingsCommand.Execute(null);
        await WaitForAsync(() => vm.SelectedPlaylist?.Name == "Evening");

        var persisted = await playlistService.GetByIdAsync(playlistId);
        Assert.Equal("Evening", persisted!.Name);
        Assert.Equal(17, persisted.IntervalMinutes);
        Assert.True(persisted.Shuffle);
        Assert.Equal("Evening", vm.Playlists.Single().Name);
    }

    [Fact]
    public async Task MovePlaylistMemberCommands_ReorderMembers()
    {
        var playlistService = new PlaylistService(_logger, _db);
        var wallpaperA = await SeedWallpaperAsync("A");
        var wallpaperB = await SeedWallpaperAsync("B");
        var wallpaperC = await SeedWallpaperAsync("C");
        var playlistId = await playlistService.CreateAsync("Morning");
        await playlistService.AddMemberAsync(playlistId, wallpaperA.Id);
        await playlistService.AddMemberAsync(playlistId, wallpaperB.Id);
        await playlistService.AddMemberAsync(playlistId, wallpaperC.Id);

        var vm = CreateViewModel(playlistService);
        vm.Wallpapers.Add(wallpaperA);
        vm.Wallpapers.Add(wallpaperB);
        vm.Wallpapers.Add(wallpaperC);
        await vm.LoadPlaylistsAsync();
        await WaitForAsync(() => vm.PlaylistMembers.Count == 3);

        vm.MovePlaylistMemberUpCommand.Execute(vm.PlaylistMembers[2]);
        await WaitForAsync(() => vm.PlaylistMembers[1].WallpaperId == wallpaperC.Id);
        vm.MovePlaylistMemberDownCommand.Execute(vm.PlaylistMembers[0]);
        await WaitForAsync(() => vm.PlaylistMembers[1].WallpaperId == wallpaperA.Id);

        var persisted = await playlistService.GetByIdAsync(playlistId);
        Assert.Equal(new[] { wallpaperC.Id, wallpaperA.Id, wallpaperB.Id }, persisted!.Members.Select(m => m.WallpaperId));
    }

    [Fact]
    public async Task AssignPlaylistMonitorCommand_PersistsSelectedMonitor()
    {
        var playlistService = new PlaylistService(_logger, _db);
        var playlistId = await playlistService.CreateAsync("Morning");

        var vm = CreateViewModel(playlistService);
        vm.Monitors.Add(new MonitorInfo
        {
            DeviceName = "Display 1",
            MonitorKey = "MON-1",
            Width = 1920,
            Height = 1080
        });
        await vm.LoadPlaylistsAsync();
        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == playlistId);
        await WaitForAsync(() => vm.PlaylistName == "Morning");

        vm.SelectedPlaylistMonitorKey = "MON-1";
        vm.AssignPlaylistMonitorCommand.Execute(null);
        await WaitForAsync(() => vm.SelectedPlaylistMonitorKey == "MON-1");

        Assert.Equal("MON-1", await playlistService.GetMonitorKeyForPlaylistAsync(playlistId));
    }

    [Fact]
    public void PlaylistView_WiresSettingsSortAndMonitorBindings()
    {
        var repoRoot = FindRepoRoot();
        var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "WallpaperApp", "UI", "Views", "PlaylistView.xaml"));

        Assert.Contains("PlaylistName", xaml);
        Assert.Contains("PlaylistIntervalMinutes", xaml);
        Assert.Contains("PlaylistShuffle", xaml);
        Assert.Contains("SelectedPlaylistMonitorKey", xaml);
        Assert.Contains("SavePlaylistSettingsCommand", xaml);
        Assert.Contains("AssignPlaylistMonitorCommand", xaml);
        Assert.Contains("MovePlaylistMemberUpCommand", xaml);
        Assert.Contains("MovePlaylistMemberDownCommand", xaml);
        Assert.Contains("AllowDrop=\"True\"", xaml);
        Assert.DoesNotContain("<GroupBox", xaml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WallpaperApp.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private MainViewModel CreateViewModel(PlaylistService playlistService)
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "library"));
        var desktopHost = new DesktopHost(_logger);
        var playback = new PlaybackManager(_logger, desktopHost);
        var monitors = new MonitorManager(_logger);
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        var hotkeys = new GlobalHotkeyService(_logger);
        var shuffler = new RandomWallpaperSwitcher(_logger);

        return new MainViewModel(library, playback, monitors, settings, _logger, hotkeys, playlistService, shuffler);
    }

    private async Task<WallpaperItem> SeedWallpaperAsync(string name)
    {
        var item = new WallpaperItem
        {
            DisplayName = name,
            OriginalFileName = name + ".mp4",
            ManagedFilePath = Path.Combine(_tempDir, name + ".mp4"),
            ContainerFormat = "mp4",
            ValidationStatus = "Valid",
            ImportedAtUtc = DateTime.UtcNow
        };
        _db.WallpaperItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
        {
            await Task.Delay(25, cts.Token);
        }
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }
}
