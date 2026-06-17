using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playlists;

// Manages the lifecycle of all active PlaylistRunners. When a monitor's playlist
// binding changes, stops the old runner and starts a new one.
//
// The switcher is injected as (monitorKey, wallpaperId) -> success so the
// coordinator stays decoupled from PlaybackManager / monitor geometry. MainViewModel
// supplies a switcher that resolves the monitor and calls AssignWallpaperAsync.
//
// The coordinator holds a single PlaylistService reference (Transient in DI);
// runner startup reads the playlist snapshot at bind time, so later playlist edits
// require a ReassignAsync to pick up the new members.
public sealed class PlaylistCoordinator
{
    private readonly FileLogger _logger;
    private readonly PlaylistService _service;
    private readonly Func<string, Guid, Task<bool>> _switchTo;
    private readonly Dictionary<string, PlaylistRunner> _runners = new();

    public PlaylistCoordinator(
        FileLogger logger,
        PlaylistService service,
        Func<string, Guid, Task<bool>> switchTo)
    {
        _logger = logger;
        _service = service;
        _switchTo = switchTo;
    }

    // On startup: start a runner for every monitor that has a playlist bound.
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        var assignments = await _service.GetAllAssignmentsAsync(ct);
        foreach (var (monitorKey, playlistId) in assignments)
        {
            await StartRunnerAsync(monitorKey, playlistId, ct);
        }
    }

    // Binding change: stop the old runner for this monitor, start a new one (if
    // playlistId is non-null).
    public async Task ReassignAsync(string monitorKey, Guid? playlistId, CancellationToken ct = default)
    {
        if (_runners.TryGetValue(monitorKey, out var old))
        {
            old.Stop();
            _runners.Remove(monitorKey);
        }
        if (playlistId != null)
            await StartRunnerAsync(monitorKey, playlistId.Value, ct);
    }

    // For F3's "SkipNext" hotkey: advance the runner on a specific monitor.
    public async Task SkipNextAsync(string monitorKey)
    {
        if (_runners.TryGetValue(monitorKey, out var runner))
            await runner.TickAsync();
    }

    // Whether any runner is active (drives whether skip-next hotkey is relevant).
    public bool HasActiveRunner => _runners.Count > 0;

    private async Task StartRunnerAsync(string monitorKey, Guid playlistId, CancellationToken ct)
    {
        var playlist = await _service.GetByIdAsync(playlistId, ct);
        if (playlist == null || playlist.Members.Count == 0)
        {
            _logger.Warn($"Cannot start runner on {monitorKey}: playlist {playlistId} empty or missing");
            return;
        }
        var runner = new PlaylistRunner(
            _logger, monitorKey, playlist,
            wallpaperId => _switchTo(monitorKey, wallpaperId),
            index => _service.SaveLastIndexAsync(playlistId, index, ct));
        await runner.StartAsync(playlist.LastPlayedIndex);
        _runners[monitorKey] = runner;
    }

    public void StopAll()
    {
        foreach (var r in _runners.Values) r.Stop();
        _runners.Clear();
    }
}
