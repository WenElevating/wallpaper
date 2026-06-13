using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using WallpaperApp.Models;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;

namespace WallpaperApp.UI.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _library;
    private readonly PlaybackManager _playback;
    private readonly MonitorManager _monitors;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WallpaperItem> Wallpapers { get; } = new();
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    private WallpaperItem? _selectedWallpaper;
    public WallpaperItem? SelectedWallpaper
    {
        get => _selectedWallpaper;
        set
        {
            if (_selectedWallpaper != value)
            {
                _selectedWallpaper = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public AppSettings Settings { get; private set; } = new();

    public MainViewModel(
        LibraryService library,
        PlaybackManager playback,
        MonitorManager monitors,
        SettingsService settings,
        FileLogger logger)
    {
        _library = library;
        _playback = playback;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        Settings = await _settings.LoadAsync();
        _monitors.Refresh();
        Monitors.Clear();
        foreach (var m in _monitors.Monitors.Values)
            Monitors.Add(m);

        var items = await _library.GetAllAsync(ct);
        Wallpapers.Clear();
        foreach (var item in items)
            Wallpapers.Add(item);

        _logger.Debug($"Loaded {Wallpapers.Count} wallpapers, {Monitors.Count} monitors");
    }

    public async Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        foreach (var path in filePaths)
        {
            var item = await _library.ImportAsync(path, ct);
            if (item != null)
                Wallpapers.Insert(0, item);
        }
    }

    public async Task<bool> AssignWallpaperAsync(MonitorInfo monitor, WallpaperItem wallpaper, CancellationToken ct = default)
    {
        var assigned = await _playback.SetWallpaperAsync(
            Guid.Parse(monitor.MonitorKey),
            wallpaper.Id,
            wallpaper.ManagedFilePath,
            monitor.X,
            monitor.Y,
            monitor.Width,
            monitor.Height,
            ct);
        if (!assigned)
            return false;

        wallpaper.LastUsedAtUtc = DateTime.UtcNow;
        _logger.Info($"Assigned '{wallpaper.DisplayName}' to {monitor.DeviceName}");
        return true;
    }

    public async Task PauseAllAsync(CancellationToken ct = default)
    {
        await _playback.PauseAllAsync(ct);
    }

    public async Task ResumeAllAsync(CancellationToken ct = default)
    {
        await _playback.ResumeAllAsync(ct);
    }
}
