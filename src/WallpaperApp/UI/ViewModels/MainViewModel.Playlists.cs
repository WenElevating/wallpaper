using System.Windows.Input;
using WallpaperApp.Models;

namespace WallpaperApp.UI.ViewModels;

// Playlist persistence and editor synchronization are isolated from the
// library/import and playback orchestration in the main shell view model.
public sealed partial class MainViewModel
{
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
}
