using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
using WallpaperApp.Services.Input;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Playlists;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI;
using WallpaperApp.UI.Controls;
using WallpaperApp.UI.Views;

namespace WallpaperApp.UI.ViewModels;

public enum ShellView { Library, Detail, Settings, Playlist }
public enum LibrarySort { Recent, Name, Size }

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly LibraryService _library;
    private readonly PlaybackManager _playback;
    private readonly MonitorManager _monitors;
    private readonly SettingsService _settings;
    private readonly FileLogger _logger;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly PlaylistService _playlistService;
    private readonly RandomWallpaperSwitcher _shuffler;
    // Coordinator is attached after construction (its switcher needs this ViewModel
    // instance, creating a cycle; App.xaml.cs calls AttachPlaylistCoordinator).
    private PlaylistCoordinator? _playlists;

    // Guards Wallpapers for cross-thread mutation (see EnableCollectionSynchronization).
    private readonly object _wallpapersLock = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WallpaperItem> Wallpapers { get; } = new();
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();
    public ObservableCollection<Playlist> Playlists { get; } = new();
    public ObservableCollection<PlaylistMemberRow> PlaylistMembers { get; } = new();
    public ObservableCollection<WallpaperItem> AddableWallpapers { get; } = new();

    // Filtered + sorted view the grid binds to (search/sort drive this).
    public ICollectionView WallpapersView { get; }

    public ICommand OpenDetailCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand GoPlaylistCommand { get; }
    public ICommand SetAsWallpaperCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand PauseToggleCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand RemoveFromPlaylistCommand { get; }
    public ICommand DeletePlaylistCommand { get; }
    public ICommand SavePlaylistSettingsCommand { get; }
    public ICommand MovePlaylistMemberUpCommand { get; }
    public ICommand MovePlaylistMemberDownCommand { get; }
    public ICommand AssignPlaylistMonitorCommand { get; }

    // Aggregate of the 7 wallpaper-card context-menu commands, bound down to each
    // WallpaperCard via its Commands dependency property.
    public WallpaperCommands Commands { get; }

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
                OnPropertyChanged(nameof(IsPlaylist));
                OnPropertyChanged(nameof(HeaderTitle));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool IsLibrary => CurrentView == ShellView.Library;
    public bool IsDetail => CurrentView == ShellView.Detail;
    public bool IsSettings => CurrentView == ShellView.Settings;
    public bool IsPlaylist => CurrentView == ShellView.Playlist;

    private Playlist? _selectedPlaylist;
    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (_selectedPlaylist == value) return;
            _selectedPlaylist = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
            _ = RefreshSelectedPlaylistAsync();
        }
    }

    private string _playlistName = "";
    public string PlaylistName
    {
        get => _playlistName;
        set
        {
            if (_playlistName == value) return;
            _playlistName = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private int _playlistIntervalMinutes = 10;
    public int PlaylistIntervalMinutes
    {
        get => _playlistIntervalMinutes;
        set
        {
            var normalized = Math.Max(1, value);
            if (_playlistIntervalMinutes == normalized) return;
            _playlistIntervalMinutes = normalized;
            OnPropertyChanged();
        }
    }

    private bool _playlistShuffle;
    public bool PlaylistShuffle
    {
        get => _playlistShuffle;
        set
        {
            if (_playlistShuffle == value) return;
            _playlistShuffle = value;
            OnPropertyChanged();
        }
    }

    private string? _selectedPlaylistMonitorKey;
    public string? SelectedPlaylistMonitorKey
    {
        get => _selectedPlaylistMonitorKey;
        set
        {
            if (_selectedPlaylistMonitorKey == value) return;
            _selectedPlaylistMonitorKey = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string HeaderTitle => CurrentView switch
    {
        ShellView.Detail => ActiveWallpaper?.DisplayName ?? "",
        ShellView.Settings => Strings.SettingsLabel,
        ShellView.Playlist => Strings.PlaylistLabel,
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
        FileLogger logger,
        GlobalHotkeyService hotkeys,
        PlaylistService playlistService,
        RandomWallpaperSwitcher shuffler)
    {
        _library = library;
        _playback = playback;
        _monitors = monitors;
        _settings = settings;
        _logger = logger;
        _hotkeys = hotkeys;
        _playlistService = playlistService;
        _shuffler = shuffler;

        WallpapersView = CollectionViewSource.GetDefaultView(Wallpapers);
        // Allow Wallpapers to be mutated from any thread (e.g. import runs on a
        // background thread, unit tests run without a Dispatcher). The view's
        // CollectionView would otherwise throw on cross-thread SourceCollection
        // changes; this callback marshals enumeration under the lock.
        BindingOperations.EnableCollectionSynchronization(Wallpapers, _wallpapersLock);
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
        GoPlaylistCommand = new RelayCommand(_ => CurrentView = ShellView.Playlist);
        ImportCommand = new RelayCommand(_ => _ = ImportAsync());
        PauseToggleCommand = new RelayCommand(_ => _ = TogglePauseAsync());
        SetAsWallpaperCommand = new RelayCommand(p => _ = SetAsWallpaperAsync(p as WallpaperItem), p => (p as WallpaperItem) != null || ActiveWallpaper != null);
        CreatePlaylistCommand = new RelayCommand(_ => _ = CreatePlaylistAsync());
        AddToPlaylistCommand = new RelayCommand(async p => await AddToPlaylistAsync(p), _ => _selectedPlaylist != null);
        RemoveFromPlaylistCommand = new RelayCommand(async p => await RemoveFromPlaylistAsync(p), _ => _selectedPlaylist != null);
        DeletePlaylistCommand = new RelayCommand(async _ => await DeleteSelectedPlaylistAsync(), _ => _selectedPlaylist != null);
        SavePlaylistSettingsCommand = new RelayCommand(async _ => await SavePlaylistSettingsAsync(), _ => _selectedPlaylist != null && !string.IsNullOrWhiteSpace(PlaylistName));
        MovePlaylistMemberUpCommand = new RelayCommand(async p => await MovePlaylistMemberAsync(p, -1), p => CanMovePlaylistMember(p, -1));
        MovePlaylistMemberDownCommand = new RelayCommand(async p => await MovePlaylistMemberAsync(p, 1), p => CanMovePlaylistMember(p, 1));
        AssignPlaylistMonitorCommand = new RelayCommand(async _ => await AssignPlaylistMonitorAsync(), _ => _selectedPlaylist != null);

        Commands = new WallpaperCommands
        {
            SetAsWallpaper = SetAsWallpaperCommand,
            OpenDetail = OpenDetailCommand,
            OpenFileLocation = new RelayCommand(p => OpenFileLocation(p as WallpaperItem)),
            Rename = new RelayCommand(p => _ = RenameWallpaperAsync(p as WallpaperItem)),
            AddToPlaylist = new RelayCommand(p => _ = AddToPlaylistFromCardAsync(p as WallpaperItem), _ => Playlists.Count > 0),
            CopyToFolder = new RelayCommand(p => _ = CopyToFolderAsync(p as WallpaperItem)),
            Delete = new RelayCommand(p => _ = DeleteWallpaperAsync(p as WallpaperItem)),
        };
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
        await LoadPlaylistsAsync(ct);

        // F3: 注入热键回调并应用当前绑定。TogglePause 复用现有的暂停切换逻辑;
        // SkipNext/SkipPrevious/ToggleMute 槽位本期默认 None(未绑定),F1(播放列表)
        // /F4(声音)实现后在此追加对应 handler。
        _hotkeys.SetHandler("TogglePause", async () =>
        {
            if (IsPaused) { await ResumeAllAsync(); IsPaused = false; }
            else { await PauseAllAsync(); IsPaused = true; }
        });
        _hotkeys.Apply(Settings.Hotkeys);

        _logger.Debug($"Loaded {Wallpapers.Count} wallpapers, {Monitors.Count} monitors");
    }

    public async Task LoadPlaylistsAsync(CancellationToken ct = default)
    {
        var playlists = await _playlistService.GetAllAsync(ct);
        Playlists.Clear();
        foreach (var playlist in playlists)
            Playlists.Add(playlist);
        var selectedId = _selectedPlaylist?.Id;
        _selectedPlaylist = selectedId == null
            ? Playlists.FirstOrDefault()
            : Playlists.FirstOrDefault(p => p.Id == selectedId) ?? Playlists.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedPlaylist));
        CommandManager.InvalidateRequerySuggested();
        await RefreshSelectedPlaylistAsync(ct);
    }

    private async Task RefreshSelectedPlaylistAsync(CancellationToken ct = default)
    {
        PlaylistMembers.Clear();
        AddableWallpapers.Clear();
        if (_selectedPlaylist == null)
        {
            SetPlaylistEditorFields(null, null);
            return;
        }

        var playlist = await _playlistService.GetByIdAsync(_selectedPlaylist.Id, ct);
        if (playlist == null)
        {
            _selectedPlaylist = null;
            OnPropertyChanged(nameof(SelectedPlaylist));
            SetPlaylistEditorFields(null, null);
            return;
        }

        _selectedPlaylist = playlist;
        OnPropertyChanged(nameof(SelectedPlaylist));
        var monitorKey = await _playlistService.GetMonitorKeyForPlaylistAsync(playlist.Id, ct);
        SetPlaylistEditorFields(playlist, monitorKey);

        var memberIds = playlist.Members.Select(m => m.WallpaperId).ToHashSet();
        foreach (var member in playlist.Members)
        {
            var wallpaper = Wallpapers.FirstOrDefault(w => w.Id == member.WallpaperId);
            PlaylistMembers.Add(new PlaylistMemberRow(
                member,
                wallpaper?.DisplayName ?? member.WallpaperId.ToString("N")[..8]));
        }
        foreach (var item in Wallpapers.Where(w => !memberIds.Contains(w.Id)))
            AddableWallpapers.Add(item);
    }

    private void SetPlaylistEditorFields(Playlist? playlist, string? monitorKey)
    {
        PlaylistName = playlist?.Name ?? "";
        PlaylistIntervalMinutes = playlist?.IntervalMinutes ?? 10;
        PlaylistShuffle = playlist?.Shuffle ?? false;
        SelectedPlaylistMonitorKey = monitorKey;
    }

    private async Task ReloadPlaylistsKeepingSelectionAsync(Guid? preferredPlaylistId = null, CancellationToken ct = default)
    {
        var selectedId = preferredPlaylistId ?? _selectedPlaylist?.Id;
        var playlists = await _playlistService.GetAllAsync(ct);
        Playlists.Clear();
        foreach (var playlist in playlists)
            Playlists.Add(playlist);

        _selectedPlaylist = selectedId == null ? null : Playlists.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(SelectedPlaylist));
        CommandManager.InvalidateRequerySuggested();
        await RefreshSelectedPlaylistAsync(ct);
    }

    private async Task AddToPlaylistAsync(object? parameter)
    {
        if (_selectedPlaylist == null || parameter is not WallpaperItem wallpaper) return;
        await _playlistService.AddMemberAsync(_selectedPlaylist.Id, wallpaper.Id);
        await ReloadPlaylistsKeepingSelectionAsync();
    }

    private async Task RemoveFromPlaylistAsync(object? parameter)
    {
        if (_selectedPlaylist == null) return;
        var wallpaperId = parameter switch
        {
            PlaylistMemberRow row => row.WallpaperId,
            PlaylistMember member => member.WallpaperId,
            _ => (Guid?)null
        };
        if (wallpaperId == null) return;
        await _playlistService.RemoveMemberAsync(_selectedPlaylist.Id, wallpaperId.Value);
        await ReloadPlaylistsKeepingSelectionAsync();
    }

    private async Task DeleteSelectedPlaylistAsync()
    {
        if (_selectedPlaylist == null) return;
        var id = _selectedPlaylist.Id;
        await _playlistService.DeleteAsync(id);
        SelectedPlaylist = null;
        await ReloadPlaylistsKeepingSelectionAsync();
    }

    private async Task SavePlaylistSettingsAsync()
    {
        if (_selectedPlaylist == null) return;
        await _playlistService.UpdateAsync(
            _selectedPlaylist.Id,
            PlaylistName,
            PlaylistIntervalMinutes,
            PlaylistShuffle);
        await AssignPlaylistMonitorAsync();
        await ReloadPlaylistsKeepingSelectionAsync();
    }

    private bool CanMovePlaylistMember(object? parameter, int offset)
    {
        if (parameter is not PlaylistMemberRow row) return false;
        var index = PlaylistMembers.IndexOf(row);
        var target = index + offset;
        return _selectedPlaylist != null && index >= 0 && target >= 0 && target < PlaylistMembers.Count;
    }

    private async Task MovePlaylistMemberAsync(object? parameter, int offset)
    {
        if (parameter is not PlaylistMemberRow row) return;
        var index = PlaylistMembers.IndexOf(row);
        if (index < 0) return;
        var target = index + offset;
        if (target < 0 || target >= PlaylistMembers.Count) return;
        await ReorderPlaylistMembersAsync(index, target);
    }

    public async Task MovePlaylistMemberAsync(PlaylistMemberRow source, PlaylistMemberRow target)
    {
        var sourceIndex = PlaylistMembers.IndexOf(source);
        var targetIndex = PlaylistMembers.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;
        await ReorderPlaylistMembersAsync(sourceIndex, targetIndex);
    }

    private async Task ReorderPlaylistMembersAsync(int sourceIndex, int targetIndex)
    {
        if (_selectedPlaylist == null) return;
        var ordered = PlaylistMembers.Select(m => m.WallpaperId).ToList();
        var moved = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        ordered.Insert(targetIndex, moved);
        await _playlistService.ReorderMembersAsync(_selectedPlaylist.Id, ordered);
        await ReloadPlaylistsKeepingSelectionAsync();
    }

    private async Task AssignPlaylistMonitorAsync()
    {
        if (_selectedPlaylist == null) return;
        var currentMonitorKey = await _playlistService.GetMonitorKeyForPlaylistAsync(_selectedPlaylist.Id);
        if (string.IsNullOrWhiteSpace(SelectedPlaylistMonitorKey))
        {
            if (!string.IsNullOrWhiteSpace(currentMonitorKey))
                await _playlistService.AssignMonitorAsync(currentMonitorKey, null);
            return;
        }

        await _playlistService.AssignMonitorAsync(SelectedPlaylistMonitorKey, _selectedPlaylist.Id);
        if (!string.IsNullOrWhiteSpace(currentMonitorKey) && currentMonitorKey != SelectedPlaylistMonitorKey)
            await _playlistService.AssignMonitorAsync(currentMonitorKey, null);
        SelectedPlaylistMonitorKey = await _playlistService.GetMonitorKeyForPlaylistAsync(_selectedPlaylist.Id);
    }

    // 设置页热键重置按钮调用:应用新绑定(默认构造 = 默认热键)并持久化。
    public async void ApplyHotkeys(HotkeyBindings bindings)
    {
        Settings = Settings with { Hotkeys = bindings };
        _hotkeys.Apply(bindings);
        try { await _settings.SaveAsync(Settings); }
        catch (Exception ex) { _logger.Warn($"Failed to save hotkey settings: {ex.Message}"); }
    }

    // F1: Coordinator's switcher needs this ViewModel instance (to resolve monitor +
    // wallpaper), creating a construction cycle. App.xaml.cs builds the coordinator
    // after the ViewModel and calls this to close the loop.
    public void AttachPlaylistCoordinator(PlaylistCoordinator coordinator)
    {
        _playlists = coordinator;
    }

    // Switch wallpaper via playlist rotation. Resolves the monitor by key and the
    // wallpaper by id, then reuses the existing AssignWallpaperAsync path so the
    // switch goes through the normal PlaybackManager.SetWallpaperAsync flow.
    public async Task<bool> SwitchViaPlaylistAsync(string monitorKey, Guid wallpaperId)
    {
        var monitor = Monitors.FirstOrDefault(m => m.MonitorKey == monitorKey);
        if (monitor == null) return false;
        var wallpaper = Wallpapers.FirstOrDefault(w => w.Id == wallpaperId);
        if (wallpaper == null) return false;
        return await AssignWallpaperAsync(monitor, wallpaper);
    }

    // Tray "Shuffle wallpaper" command. For each monitor:
    //   - if a playlist is bound, advance THAT playlist (don't bypass F1's
    //     rotation contract — the user explicitly set up an order/shuffle for
    //     it; we just trigger an early "next");
    //   - otherwise pick a random wallpaper that isn't the current one and
    //     isn't in the recent-history window for this monitor.
    // Surfaces a single toast summarising success across all monitors so a
    // multi-monitor shuffle doesn't spam.
    public async Task ShuffleAllMonitorsAsync(CancellationToken ct = default)
    {
        if (Wallpapers.Count == 0)
        {
            ShowToast(Strings.MsgShuffleNoLibrary, error: true);
            return;
        }

        _monitors.Refresh();
        var monitors = _monitors.Monitors.Values.ToList();
        if (monitors.Count == 0) return;

        var libraryIds = Wallpapers.Select(w => w.Id).ToList();
        int success = 0;
        foreach (var m in monitors)
        {
            // F1 coexistence: if this monitor is bound to a playlist, treat
            // "shuffle" as "skip to next in that playlist" instead of yanking
            // the wallpaper out from under the runner.
            if (_playlists != null)
            {
                var bound = await _playlistService.GetPlaylistForMonitorAsync(m.MonitorKey, ct);
                if (bound != null && bound.Members.Count > 0)
                {
                    await _playlists.SkipNextAsync(m.MonitorKey);
                    success++;
                    continue;
                }
            }

            Guid? current = null;
            try { current = _playback.GetActiveWallpaperId(Guid.Parse(m.MonitorKey)); }
            catch (FormatException) { /* MonitorKey isn't a Guid on this monitor; treat as "no current" */ }

            var pickId = _shuffler.PickNext(m.MonitorKey, current, libraryIds);
            if (pickId is not Guid id) continue;
            var item = Wallpapers.FirstOrDefault(w => w.Id == id);
            if (item == null) continue;
            if (await AssignWallpaperAsync(m, item, ct)) success++;
        }

        ShowToast(success > 0 ? Strings.MsgShuffleDone : Strings.MsgSetFailedShort, error: success == 0);
    }

    private async Task CreatePlaylistAsync()
    {
        var name = $"Playlist {DateTime.Now:HHmmss}";
        var id = await _playlistService.CreateAsync(name);
        await ReloadPlaylistsKeepingSelectionAsync(id);
        _logger.Info($"Created playlist {name} ({id})");
        ShowToast(Strings.MsgPlaylistCreated, error: false);
    }

    public async Task ImportFilesAsync(IEnumerable<string> filePaths, CancellationToken ct = default)
    {
        foreach (var path in filePaths)
        {
            var item = await _library.ImportAsync(path, ct);
            if (item != null)
                Wallpapers.Insert(0, item);
        }
        if (_selectedPlaylist != null)
            await RefreshSelectedPlaylistAsync(ct);
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

    private Task SetAsWallpaperAsync()
        => SetAsWallpaperAsync(ActiveWallpaper);

    // Test hook: set a specific wallpaper (the card-menu "Set as wallpaper" path)
    // without depending on ActiveWallpaper. Returns the assignment result.
    public Task<bool> AssignWallpaperFromCommandAsync(WallpaperItem? wallpaper)
        => SetAsWallpaperAsync(wallpaper);

    private async Task<bool> SetAsWallpaperAsync(WallpaperItem? wallpaper)
    {
        wallpaper ??= ActiveWallpaper;
        if (wallpaper == null) return false;

        _monitors.Refresh();
        var monitors = _monitors.Monitors.Values.ToList();
        if (monitors.Count == 0) return false;

        if (monitors.Count == 1)
        {
            await AssignAndReportAsync(monitors[0], wallpaper);
            return true;
        }

        var picker = new MonitorPickerWindow { Owner = Application.Current.MainWindow };
        picker.Monitors = monitors;
        if (picker.ShowDialog() != true) return false;

        if (picker.AllMonitors)
        {
            bool any = false;
            foreach (var m in monitors)
                any |= await AssignWallpaperAsync(m, wallpaper);
            ShowToast(any ? Strings.MsgSetSuccessAll : Strings.MsgSetFailedShort, error: !any);
            return any;
        }
        else if (picker.Selected != null)
        {
            await AssignAndReportAsync(picker.Selected, wallpaper);
            return true;
        }
        return false;
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

    // Test seam: exposes the playback manager so tests can substitute a fake
    // (TestablePlaybackManager) to assert stop/assign without a real D2D surface.
    internal PlaybackManager PlaybackForTests => _playback;

    private void OpenFileLocation(WallpaperItem? wallpaper)
    {
        if (wallpaper == null || string.IsNullOrEmpty(wallpaper.ManagedFilePath)) return;
        try
        {
            // Explorer reuses a single process, so the wrapper is usually
            // already-exited — but it still implements IDisposable, so dispose it.
            using var proc = Process.Start("explorer.exe", $"/select,\"{wallpaper.ManagedFilePath}\"");
        }
        catch (Exception ex) { _logger.Warn($"Open file location failed: {ex.Message}"); }
    }

    public async Task RenameWallpaperAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;
        var dialog = new RenameWindow(wallpaper.DisplayName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await ApplyRenameAsync(wallpaper, dialog.NewName);
    }

    // Core rename logic, separated so tests can exercise it headless (no Window).
    internal async Task ApplyRenameAsync(WallpaperItem wallpaper, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) { ShowToast(Strings.MsgNameEmpty, error: true); return; }

        if (await _library.RenameAsync(wallpaper.Id, newName))
        {
            wallpaper.DisplayName = newName.Trim();
            RefreshWallpapersView();
            ShowToast(Strings.MsgRenamed);
        }
    }

    // Refresh the wallpapers view (re-applies sort/filter). Guarded for the no-UI
    // test context: WPF's CollectionView refuses Refresh() off its Dispatcher.
    // In production we marshal to the Dispatcher; in unit tests (no Dispatcher
    // loop) BeginInvoke posts the work but it never runs (no message pump), which
    // is fine — tests assert against the in-memory model, not the view.
    private void RefreshWallpapersView()
    {
        if (WallpapersView is not System.Windows.Threading.DispatcherObject d) return;
        if (d.Dispatcher.CheckAccess()) WallpapersView.Refresh();
        else d.Dispatcher.BeginInvoke(new Action(WallpapersView.Refresh));
    }

    public async Task DeleteWallpaperAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;

        // Tally impact: which monitors show this wallpaper, which playlists reference it.
        var (playingMonitors, referencedPlaylists) = TallyDeleteImpact(wallpaper);

        // Confirm via modal (shows live-impact rows so the user knows the blast radius).
        var dialog = new ConfirmDeleteWindow(
            wallpaper.DisplayName,
            playingMonitors.Count > 0,
            referencedPlaylists.Count)
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        await DeleteWallpaperCoreAsync(wallpaper, playingMonitors, referencedPlaylists);
    }

    internal (List<Guid> playingMonitors, List<Playlist> referencedPlaylists) TallyDeleteImpact(WallpaperItem wallpaper)
    {
        var playingMonitors = new List<Guid>();
        foreach (var m in Monitors)
        {
            if (Guid.TryParse(m.MonitorKey, out var mid) &&
                _playback.GetActiveWallpaperId(mid) == wallpaper.Id)
                playingMonitors.Add(mid);
        }
        var referencedPlaylists = Playlists
            .Where(p => p.Members.Any(mem => mem.WallpaperId == wallpaper.Id))
            .ToList();
        return (playingMonitors, referencedPlaylists);
    }

    // Core delete execution, separated from the confirm dialog so it's testable
    // headless. Caller has already gathered impact and obtained confirmation.
    // Order matters: stop playback (release file handle) -> clean playlist refs
    // -> delete file+record -> sync memory. Reversing playback/delete risks the
    // decoder still holding the file open on Windows.
    internal async Task DeleteWallpaperCoreAsync(
        WallpaperItem wallpaper, IReadOnlyList<Guid> playingMonitors, IReadOnlyList<Playlist> referencedPlaylists)
    {
        foreach (var mid in playingMonitors)
            await _playback.RemoveWallpaperAsync(mid);

        foreach (var pl in referencedPlaylists)
            await _playlistService.RemoveMemberAsync(pl.Id, wallpaper.Id);

        await _library.DeleteAsync(wallpaper.Id);

        Wallpapers.Remove(wallpaper);

        ShowToast(string.Format(Strings.MsgDeleted, wallpaper.DisplayName));
    }

    private async Task AddToPlaylistFromCardAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;
        if (Playlists.Count == 0) { ShowToast(Strings.DlgPlaylistEmpty, error: true); return; }

        var dialog = new PlaylistPickerWindow(Playlists.ToList())
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Selected == null) return;

        await _playlistService.AddMemberAsync(dialog.Selected.Id, wallpaper.Id);
        await ReloadPlaylistsKeepingSelectionAsync();
        ShowToast(string.Format(Strings.MsgAddedToPlaylist, dialog.Selected.Name));
    }

    private async Task CopyToFolderAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null || string.IsNullOrEmpty(wallpaper.ManagedFilePath)) return;
        var dialog = new SaveFileDialog
        {
            FileName = string.IsNullOrEmpty(wallpaper.OriginalFileName)
                ? wallpaper.DisplayName
                : wallpaper.OriginalFileName,
            Filter = $"{Strings.DlgImportFilterAll}|*.*",
            Title = Strings.DlgCopyTitle
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await Task.Run(() => File.Copy(wallpaper.ManagedFilePath, dialog.FileName, overwrite: true));
            ShowToast(Strings.MsgCopySuccess);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Copy failed: {ex.Message}");
            ShowToast(Strings.MsgCopyFailed, error: true);
        }
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

    // Settings-page toggles. The underlying AppSettings is a record with init-only
    // setters, so these façade properties hold a mutable projection: the setter
    // produces an immutable `with` copy, persists it, and raises change so the
    // checkbox and the live consumers (fullscreen/power controllers read
    // Settings directly) stay in sync.
    public bool IsGlobalPauseOnFullscreen
    {
        get => Settings.GlobalPauseOnFullscreen;
        set => UpdatePerfSetting(s => s with { GlobalPauseOnFullscreen = value });
    }

    public bool IsPauseOnBattery
    {
        get => Settings.PauseOnBattery;
        set => UpdatePerfSetting(s => s with { PauseOnBattery = value });
    }

    public bool IsPauseOnRemoteSession
    {
        get => Settings.PauseOnRemoteSession;
        set => UpdatePerfSetting(s => s with { PauseOnRemoteSession = value });
    }

    private async void UpdatePerfSetting(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        OnPropertyChanged(nameof(IsGlobalPauseOnFullscreen));
        OnPropertyChanged(nameof(IsPauseOnBattery));
        OnPropertyChanged(nameof(IsPauseOnRemoteSession));
        try { await _settings.SaveAsync(Settings); }
        catch (Exception ex) { _logger.Warn($"Failed to save settings: {ex.Message}"); }
    }
}

public sealed class PlaylistMemberRow
{
    public PlaylistMemberRow(PlaylistMember member, string wallpaperName)
    {
        Member = member;
        WallpaperName = wallpaperName;
    }

    public PlaylistMember Member { get; }
    public Guid WallpaperId => Member.WallpaperId;
    public int Order => Member.Order;
    public string WallpaperName { get; }
}
