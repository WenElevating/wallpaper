using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using WallpaperApp.Localization;
using WallpaperApp.Models;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI;
using WallpaperApp.UI.Views;

namespace WallpaperApp.UI.ViewModels;

public enum ShellView { Library, Detail, Settings }
public enum LibrarySort { Recent, Name, Size }

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

    // Filtered + sorted view the grid binds to (search/sort drive this).
    public ICollectionView WallpapersView { get; }

    public ICommand OpenDetailCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand SetAsWallpaperCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand PauseToggleCommand { get; }

    private ShellView _currentView = ShellView.Library;
    public ShellView CurrentView
    {
        get => _currentView;
        set
        {
            if (_currentView != value)
            {
                _currentView = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsLibrary));
                OnPropertyChanged(nameof(IsDetail));
                OnPropertyChanged(nameof(IsSettings));
                OnPropertyChanged(nameof(HeaderTitle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsLibrary => CurrentView == ShellView.Library;
    public bool IsDetail => CurrentView == ShellView.Detail;
    public bool IsSettings => CurrentView == ShellView.Settings;

    public string HeaderTitle => CurrentView switch
    {
        ShellView.Detail => ActiveWallpaper?.DisplayName ?? "",
        ShellView.Settings => Strings.SettingsLabel,
        _ => Strings.LibraryLabel,
    };

    private WallpaperItem? _activeWallpaper;
    public WallpaperItem? ActiveWallpaper
    {
        get => _activeWallpaper;
        set
        {
            if (_activeWallpaper != value)
            {
                _activeWallpaper = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HeaderTitle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (_searchText != value) { _searchText = value; OnPropertyChanged(); WallpapersView.Refresh(); } }
    }

    private LibrarySort _sort = LibrarySort.Recent;
    public LibrarySort Sort
    {
        get => _sort;
        set { if (_sort != value) { _sort = value; OnPropertyChanged(); ApplySort(); } }
    }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { if (_isPaused != value) { _isPaused = value; OnPropertyChanged(); } }
    }

    // Mirrors whether any wallpaper is currently playing; the pause button is
    // disabled when nothing is set.
    private bool _hasActivePlayback;
    public bool HasActivePlayback => _hasActivePlayback;

    // Transient in-app toast (replaces the blocking system MessageBox).
    private string? _toastMessage;
    public string? ToastMessage
    {
        get => _toastMessage;
        set { if (_toastMessage != value) { _toastMessage = value; OnPropertyChanged(); } }
    }

    private bool _isToastVisible;
    public bool IsToastVisible
    {
        get => _isToastVisible;
        set { if (_isToastVisible != value) { _isToastVisible = value; OnPropertyChanged(); } }
    }

    private bool _isToastError;
    public bool IsToastError
    {
        get => _isToastError;
        set { if (_isToastError != value) { _isToastError = value; OnPropertyChanged(); } }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

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

        WallpapersView = CollectionViewSource.GetDefaultView(Wallpapers);
        WallpapersView.Filter = FilterWallpaper;
        ApplySort();

        _playback.SessionsChanged += OnSessionsChanged;

        OpenDetailCommand = new RelayCommand(p =>
        {
            if (p is not WallpaperItem item) return;
            ActiveWallpaper = item;
            CurrentView = ShellView.Detail;
            if (item.Width == 0) _ = EnrichMetadataAsync(item);
        });
        GoBackCommand = new RelayCommand(_ => { ActiveWallpaper = null; CurrentView = ShellView.Library; });
        GoSettingsCommand = new RelayCommand(_ => CurrentView = ShellView.Settings);
        ImportCommand = new RelayCommand(_ => _ = ImportAsync());
        PauseToggleCommand = new RelayCommand(_ => _ = TogglePauseAsync());
        SetAsWallpaperCommand = new RelayCommand(_ => _ = SetAsWallpaperAsync(), _ => ActiveWallpaper != null);
    }

    private bool FilterWallpaper(object obj)
    {
        if (obj is not WallpaperItem w) return false;
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        return w.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort()
    {
        WallpapersView.SortDescriptions.Clear();
        var sd = _sort switch
        {
            LibrarySort.Name => new SortDescription(nameof(WallpaperItem.DisplayName), ListSortDirection.Ascending),
            LibrarySort.Size => new SortDescription(nameof(WallpaperItem.FileBytes), ListSortDirection.Descending),
            _ => new SortDescription(nameof(WallpaperItem.ImportedAtUtc), ListSortDirection.Descending),
        };
        WallpapersView.SortDescriptions.Add(sd);
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

    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"{Strings.DlgImportFilterMedia}|*.mp4;*.webm;*.avi;*.mov;*.gif;*.mkv|{Strings.DlgImportFilterAll}|*.*",
            Multiselect = true,
            Title = Strings.DlgImportTitle
        };
        if (dialog.ShowDialog() == true)
        {
            await ImportFilesAsync(dialog.FileNames);
            CurrentView = ShellView.Library;
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

    private async Task EnrichMetadataAsync(WallpaperItem item)
    {
        try
        {
            var (w, h, ms) = await MediaProbe.ProbeAsync(item.ManagedFilePath, _logger);
            if (w <= 0) return;
            item.Width = w;
            item.Height = h;
            item.DurationMs = ms;
            await _library.UpdateMetadataAsync(item.Id, w, h, ms);
            OnPropertyChanged(nameof(ActiveWallpaper));
        }
        catch (Exception ex)
        {
            _logger.Warn($"Metadata probe failed for {item.ManagedFilePath}: {ex.Message}");
        }
    }

    private async Task SetAsWallpaperAsync()
    {
        var wallpaper = ActiveWallpaper;
        if (wallpaper == null) return;

        _monitors.Refresh();
        var monitors = _monitors.Monitors.Values.ToList();
        if (monitors.Count == 0) return;

        if (monitors.Count == 1)
        {
            await AssignAndReportAsync(monitors[0], wallpaper);
            return;
        }

        var picker = new MonitorPickerWindow { Owner = Application.Current.MainWindow };
        picker.Monitors = monitors;
        if (picker.ShowDialog() != true) return;

        if (picker.AllMonitors)
        {
            bool any = false;
            foreach (var m in monitors)
                any |= await AssignWallpaperAsync(m, wallpaper);
            ShowToast(any ? Strings.MsgSetSuccessAll : Strings.MsgSetFailedShort, error: !any);
        }
        else if (picker.Selected != null)
        {
            await AssignAndReportAsync(picker.Selected, wallpaper);
        }
    }

    private async Task AssignAndReportAsync(MonitorInfo monitor, WallpaperItem wallpaper)
    {
        var ok = await AssignWallpaperAsync(monitor, wallpaper);
        ShowToast(ok ? Strings.MsgSetSuccessDesktop : Strings.MsgSetFailedShort, error: !ok);
    }

    private async Task TogglePauseAsync()
    {
        if (IsPaused) { await ResumeAllAsync(); IsPaused = false; }
        else { await PauseAllAsync(); IsPaused = true; }
    }

    // Keep HasActivePlayback in sync with the playback engine. The event usually
    // fires on the UI thread, but marshal to the dispatcher just in case.
    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        var has = _playback.HasActiveSessions;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) SetHasPlayback(has);
        else dispatcher.BeginInvoke(new Action(() => SetHasPlayback(has)));
    }

    private void SetHasPlayback(bool value)
    {
        if (_hasActivePlayback != value)
        {
            _hasActivePlayback = value;
            OnPropertyChanged(nameof(HasActivePlayback));
        }
    }

    private CancellationTokenSource? _toastCts;

    // Show a transient toast; a newer toast cancels any still-visible older one.
    private void ShowToast(string message, bool error = false)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        ToastMessage = message;
        IsToastError = error;
        IsToastVisible = true;
        _ = HideToastAsync(_toastCts.Token);
    }

    private async Task HideToastAsync(CancellationToken ct)
    {
        try { await Task.Delay(2800, ct); }
        catch (TaskCanceledException) { return; }
        if (!ct.IsCancellationRequested) IsToastVisible = false;
    }

    public async Task PauseAllAsync(CancellationToken ct = default) => await _playback.PauseAllAsync(ct);
    public async Task ResumeAllAsync(CancellationToken ct = default) => await _playback.ResumeAllAsync(ct);

    public async Task SetLanguageAsync(string code)
    {
        Settings = Settings with { Language = code };
        await _settings.SaveAsync(Settings);
        LocalizationService.ApplyCulture(code);
        OnPropertyChanged(nameof(HeaderTitle)); // localized labels may change
        _logger.Info($"Language set to {code}");
    }
}
