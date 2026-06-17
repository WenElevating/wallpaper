using System.Windows.Threading;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playlists;

// Runtime for a single (monitor, playlist) pair: a DispatcherTimer that fires at
// the playlist's interval and switches to the next wallpaper via the injected
// switcher, advancing the index and persisting it.
//
// Dependencies (switcher / saveIndex) are injected as funcs so the runner can be
// unit-tested without PlaybackManager or a real database. The DispatcherTimer
// ensures ticks run on the UI thread (wallpaper switching goes through the
// ViewModel, which lives on the UI thread).
public sealed class PlaylistRunner
{
    private readonly FileLogger _logger;
    private readonly string _monitorKey;
    private readonly Playlist _playlist;
    private readonly Func<Guid, Task<bool>> _switchTo;   // wallpaperId -> success
    private readonly Func<int, Task> _saveIndex;          // persist index
    private readonly DispatcherTimer _timer;
    private int _currentIndex;
    private bool _started;

    public PlaylistRunner(
        FileLogger logger,
        string monitorKey,
        Playlist playlist,
        Func<Guid, Task<bool>> switchTo,
        Func<int, Task> saveIndex)
    {
        _logger = logger;
        _monitorKey = monitorKey;
        _playlist = playlist;
        _switchTo = switchTo;
        _saveIndex = saveIndex;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, playlist.IntervalMinutes))
        };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    // Start: switch to startIndex immediately, then start the timer.
    public async Task StartAsync(int startIndex)
    {
        if (_playlist.Members.Count == 0)
        {
            _logger.Warn($"Playlist '{_playlist.Name}' has no members; runner idle on {_monitorKey}");
            return;
        }
        _currentIndex = ClampIndex(startIndex);
        await SwitchCurrentAsync();
        _timer.Start();
        _started = true;
        _logger.Info($"Playlist runner started on {_monitorKey}: '{_playlist.Name}', interval {_playlist.IntervalMinutes}min, start index {_currentIndex}");
    }

    // Fire one switch (test-visible; the timer calls the same method).
    // NOTE: rotation continues while wallpapers are paused (User/Fullscreen/...).
    // A paused system that gets a fresh SetWallpaperAsync will resume playback.
    // This is a known gap — gating ticks on pause state requires injecting a
    // pause-signal into the runner; deferred to a follow-up. See plan F1 M-3.
    public async Task TickAsync()
    {
        if (!_started || _playlist.Members.Count == 0) return;
        _currentIndex = NextIndex();
        try { await SwitchCurrentAsync(); }
        catch (Exception ex)
        {
            // The switcher contract is Task<bool>, but assign/Guid.Parse can throw;
            // never let a tick exception kill this monitor's rotation (the timer
            // callback is async-void, so a throw would silently stop the timer).
            _logger.Warn($"Playlist tick failed on {_monitorKey}: {ex.Message}");
        }
        try { await _saveIndex(_currentIndex); }
        catch (Exception ex) { _logger.Warn($"Failed to save playlist index: {ex.Message}"); }
    }

    private async Task SwitchCurrentAsync()
    {
        var ordered = _playlist.Members.OrderBy(m => m.Order).ToList();
        var wallpaperId = ordered[_currentIndex].WallpaperId;
        var ok = await _switchTo(wallpaperId);
        if (!ok)
            _logger.Warn($"Playlist switch failed on {_monitorKey} -> wallpaper {wallpaperId}");
    }

    private int NextIndex()
    {
        var count = _playlist.Members.Count;
        if (count == 0) return 0;
        if (_playlist.Shuffle)
        {
            if (count == 1) return 0;
            int next;
            do { next = Random.Shared.Next(count); } while (next == _currentIndex);
            return next;
        }
        return (_currentIndex + 1) % count;
    }

    private int ClampIndex(int i) => i < 0 || i >= _playlist.Members.Count ? 0 : i;

    public void Stop()
    {
        _timer.Stop();
        _started = false;
        _logger.Info($"Playlist runner stopped on {_monitorKey}");
    }
}
