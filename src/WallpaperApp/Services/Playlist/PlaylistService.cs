using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

// Namespace is plural (Playlists) to avoid clashing with the Playlist model type.
namespace WallpaperApp.Services.Playlists;

// Playlist CRUD + index calculation. Registered as a SINGLETON in DI and holds
// one AppDbContext for its whole life (the rotation engine is single-instance
// and long-lived). This is a deliberate captive-dependency trade-off: it keeps
// the tracked-entity graph consistent for the runner's index round-trip
// (GetByIdAsync + SaveLastIndexAsync share one context), at the cost of
// accumulated tracking entries over a very long session and staleness if a
// structural edit is ever made through a DIFFERENT context. Structural edits
// (add/remove member, reassign) currently all flow through this same instance,
// so staleness is not reachable today. If a future UI edits playlists through a
// separate context, switch this to the per-method IServiceProvider pattern used
// by LibraryService, or add AsNoTracking to reads + ExecuteUpdateAsync writes.
public sealed class PlaylistService
{
    private readonly FileLogger _logger;
    private readonly AppDbContext _db;

    public PlaylistService(FileLogger logger, AppDbContext db)
    {
        _logger = logger;
        _db = db;
    }

    public async Task<Guid> CreateAsync(string name, CancellationToken ct = default)
    {
        var pl = new Playlist { Name = name };
        _db.Playlists.Add(pl);
        await _db.SaveChangesAsync(ct);
        _logger.Info($"Created playlist '{name}' ({pl.Id})");
        return pl.Id;
    }

    public async Task<List<Playlist>> GetAllAsync(CancellationToken ct = default)
        => await _db.Playlists.Include(p => p.Members).OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Playlist?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Sort members in memory (EF Include+OrderBy + tracking is fragile).
        var pl = await _db.Playlists.Include(p => p.Members)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl != null)
            pl.Members = pl.Members.OrderBy(m => m.Order).ToList();
        return pl;
    }

    public async Task AddMemberAsync(Guid playlistId, Guid wallpaperId, CancellationToken ct = default)
    {
        var pl = await _db.Playlists.Include(p => p.Members).FirstAsync(p => p.Id == playlistId, ct);
        var nextOrder = pl.Members.Count == 0 ? 0 : pl.Members.Max(m => m.Order) + 1;
        pl.Members.Add(new PlaylistMember { PlaylistId = playlistId, WallpaperId = wallpaperId, Order = nextOrder });
        pl.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid playlistId, Guid wallpaperId, CancellationToken ct = default)
    {
        var pl = await _db.Playlists.Include(p => p.Members).FirstAsync(p => p.Id == playlistId, ct);
        var member = pl.Members.FirstOrDefault(m => m.WallpaperId == wallpaperId);
        if (member == null) return;
        pl.Members.Remove(member);
        var i = 0;
        foreach (var m in pl.Members.OrderBy(m => m.Order)) m.Order = i++;
        pl.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid playlistId, CancellationToken ct = default)
    {
        var pl = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (pl == null) return;
        _db.Playlists.Remove(pl);
        await _db.SaveChangesAsync(ct);
    }

    // Pure: computes the next index without persisting. Sequential wraps around;
    // shuffle avoids immediate repeat.
    public int ComputeNextIndex(Guid playlistId, int currentIndex, bool shuffle, int count)
    {
        if (count <= 0) return 0;
        if (shuffle)
        {
            if (count == 1) return 0;
            int next;
            do { next = Random.Shared.Next(count); } while (next == currentIndex);
            return next;
        }
        return (currentIndex + 1) % count;
    }

    public async Task SaveLastIndexAsync(Guid playlistId, int index, CancellationToken ct = default)
    {
        var pl = await _db.Playlists.FirstAsync(p => p.Id == playlistId, ct);
        pl.LastPlayedIndex = index;
        await _db.SaveChangesAsync(ct);
    }

    public async Task AssignMonitorAsync(string monitorKey, Guid? playlistId, CancellationToken ct = default)
    {
        var existing = await _db.MonitorPlaylistAssignments
            .FirstOrDefaultAsync(a => a.MonitorKey == monitorKey, ct);
        if (existing == null)
        {
            _db.MonitorPlaylistAssignments.Add(new MonitorPlaylistAssignment
            { MonitorKey = monitorKey, PlaylistId = playlistId });
        }
        else
        {
            existing.PlaylistId = playlistId;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<Playlist?> GetPlaylistForMonitorAsync(string monitorKey, CancellationToken ct = default)
    {
        var a = await _db.MonitorPlaylistAssignments
            .FirstOrDefaultAsync(x => x.MonitorKey == monitorKey, ct);
        if (a?.PlaylistId == null) return null;
        var pl = await _db.Playlists.Include(p => p.Members)
            .FirstOrDefaultAsync(p => p.Id == a.PlaylistId.Value, ct);
        if (pl != null)
            pl.Members = pl.Members.OrderBy(m => m.Order).ToList();
        return pl;
    }

    public async Task<List<(string monitorKey, Guid playlistId)>> GetAllAssignmentsAsync(CancellationToken ct = default)
    {
        return await _db.MonitorPlaylistAssignments
            .Where(a => a.PlaylistId != null)
            .Select(a => new ValueTuple<string, Guid>(a.MonitorKey, a.PlaylistId!.Value))
            .ToListAsync(ct);
    }
}
