using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Picks the "next" wallpaper for an on-demand shuffle (e.g. tray-menu "Shuffle
// wallpaper") so the user doesn't see the same image twice in a short span.
//
// Per-monitor recent-history buffer:
//   - Each monitor keeps its own FIFO of the last N wallpaper ids it showed.
//   - PickNext excludes both the wallpaper currently on screen AND everything
//     in that monitor's recent history, then samples uniformly from the rest.
//   - Window size adapts to the library: window = min(DefaultRecentWindow, count - 1)
//     so there is always at least one valid candidate. Without the clamp a small
//     library (2-3 items) would block-list the entire pool.
//
// Process-local, NOT persisted: shuffle history is a "right now" affordance;
// across restarts the user expects to start fresh, and persisting it would
// cost a settings/migration round-trip for negligible value.
//
// Singleton in DI: the in-memory state is the whole point — every call goes
// through the same instance so the recent-history actually accumulates.
public sealed class RandomWallpaperSwitcher
{
    private const int DefaultRecentWindow = 5;

    private readonly FileLogger _logger;
    private readonly Dictionary<string, Queue<Guid>> _recentByMonitor = new();
    private readonly object _lock = new();

    public RandomWallpaperSwitcher(FileLogger logger)
    {
        _logger = logger;
    }

    // Returns a wallpaper id that:
    //   - is not currentWallpaperId (the one already on this monitor), and
    //   - is not in the recent-history queue for monitorKey,
    // sampled uniformly from the surviving candidates. Falls back to "anything
    // != current" if the history is too aggressive vs. library size; returns
    // the only id (or null) when the library has 0/1 items.
    //
    // Side effect: the chosen id is appended to monitorKey's history (caller
    // does not have to call back to "remember"). If null is returned (empty
    // library) the history is unchanged.
    public Guid? PickNext(string monitorKey, Guid? currentWallpaperId, IReadOnlyList<Guid> libraryIds)
    {
        if (libraryIds.Count == 0) return null;
        if (libraryIds.Count == 1) return libraryIds[0];

        lock (_lock)
        {
            // Window scales with library size: a 3-item library can never have
            // 5 distinct "recent" ids without exhausting the pool.
            var window = Math.Clamp(DefaultRecentWindow, 1, libraryIds.Count - 1);

            if (!_recentByMonitor.TryGetValue(monitorKey, out var recent))
            {
                recent = new Queue<Guid>();
                _recentByMonitor[monitorKey] = recent;
            }

            // Shrink the live history if the library shrank since last call.
            while (recent.Count > window) recent.Dequeue();

            var blocked = new HashSet<Guid>(recent);
            if (currentWallpaperId is Guid cur) blocked.Add(cur);

            var candidates = new List<Guid>(libraryIds.Count);
            foreach (var id in libraryIds)
                if (!blocked.Contains(id)) candidates.Add(id);

            // Degenerate case: the recent-history blocked the entire library
            // (only reachable if libraryIds shrank under us). Fall back to
            // "anything != current"; we can prove this list is non-empty
            // because count >= 2 and at most one item equals current.
            if (candidates.Count == 0)
            {
                foreach (var id in libraryIds)
                    if (id != currentWallpaperId) candidates.Add(id);
            }

            var pick = candidates[Random.Shared.Next(candidates.Count)];
            recent.Enqueue(pick);
            while (recent.Count > window) recent.Dequeue();
            return pick;
        }
    }

    // Test/debug hook: forget all per-monitor history. Not used by app code.
    internal void ResetForTests()
    {
        lock (_lock) _recentByMonitor.Clear();
    }
}
