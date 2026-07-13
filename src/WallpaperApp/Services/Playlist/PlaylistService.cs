using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

// Namespace is plural (Playlists) to avoid clashing with the Playlist model type.
namespace WallpaperApp.Services.Playlists;

// Playlist CRUD + index calculation. Each operation uses a short-lived context
// so UI commands and timer callbacks do not share EF tracking or DbContext state.
public sealed class PlaylistService
{
    private readonly FileLogger _logger;
    private readonly Func<AppDbContext> _createDb;

    public PlaylistService(FileLogger logger, Func<AppDbContext> createDb)
    {
        _logger = logger;
        _createDb = createDb;
    }

    public async Task<Guid> CreateAsync(string name, CancellationToken ct = default)
    {
        var pl = new Playlist { Name = name };
        await using var db = _createDb();
        db.Playlists.Add(pl);
        await db.SaveChangesAsync(ct);
        _logger.Info($"Created playlist '{name}' ({pl.Id})");
        return pl.Id;
    }

    public async Task<List<Playlist>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = _createDb();
        return await db.Playlists.Include(p => p.Members).OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task UpdateAsync(Guid playlistId, string name, int intervalMinutes, bool shuffle, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var pl = await db.Playlists.FirstAsync(p => p.Id == playlistId, ct);
        var trimmedName = name.Trim();
        pl.Name = string.IsNullOrWhiteSpace(trimmedName) ? pl.Name : trimmedName;
        pl.IntervalMinutes = Math.Max(1, intervalMinutes);
        pl.Shuffle = shuffle;
        pl.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Playlist?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        // Sort members in memory (EF Include+OrderBy + tracking is fragile).
        await using var db = _createDb();
        var pl = await db.Playlists.Include(p => p.Members)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pl != null)
            pl.Members = pl.Members.OrderBy(m => m.Order).ToList();
        return pl;
    }

    public async Task AddMemberAsync(Guid playlistId, Guid wallpaperId, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var pl = await db.Playlists.Include(p => p.Members).FirstAsync(p => p.Id == playlistId, ct);
        var nextOrder = pl.Members.Count == 0 ? 0 : pl.Members.Max(m => m.Order) + 1;
        pl.Members.Add(new PlaylistMember { PlaylistId = playlistId, WallpaperId = wallpaperId, Order = nextOrder });
        pl.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderMembersAsync(Guid playlistId, IReadOnlyList<Guid> wallpaperIdsInOrder, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var pl = await db.Playlists.Include(p => p.Members).FirstAsync(p => p.Id == playlistId, ct);
        var membersByWallpaperId = pl.Members.ToDictionary(m => m.WallpaperId);
        var orderedMembers = new List<PlaylistMember>();
        foreach (var wallpaperId in wallpaperIdsInOrder)
        {
            if (membersByWallpaperId.Remove(wallpaperId, out var member))
                orderedMembers.Add(member);
        }
        orderedMembers.AddRange(membersByWallpaperId.Values.OrderBy(m => m.Order));

        for (var i = 0; i < orderedMembers.Count; i++)
            orderedMembers[i].Order = -orderedMembers.Count - i - 1;
        await db.SaveChangesAsync(ct);

        for (var i = 0; i < orderedMembers.Count; i++)
            orderedMembers[i].Order = i;

        pl.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveMemberAsync(Guid playlistId, Guid wallpaperId, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var pl = await db.Playlists.Include(p => p.Members).FirstAsync(p => p.Id == playlistId, ct);
        var member = pl.Members.FirstOrDefault(m => m.WallpaperId == wallpaperId);
        if (member == null) return;
        pl.Members.Remove(member);
        var i = 0;
        foreach (var m in pl.Members.OrderBy(m => m.Order)) m.Order = i++;
        pl.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid playlistId, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var pl = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (pl == null) return;
        db.Playlists.Remove(pl);
        await db.SaveChangesAsync(ct);
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
        await using var db = _createDb();
        var pl = await db.Playlists.FirstAsync(p => p.Id == playlistId, ct);
        pl.LastPlayedIndex = index;
        await db.SaveChangesAsync(ct);
    }

    public async Task AssignMonitorAsync(string monitorKey, Guid? playlistId, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var existing = await db.MonitorPlaylistAssignments
            .FirstOrDefaultAsync(a => a.MonitorKey == monitorKey, ct);
        if (existing == null)
        {
            db.MonitorPlaylistAssignments.Add(new MonitorPlaylistAssignment
            { MonitorKey = monitorKey, PlaylistId = playlistId });
        }
        else
        {
            existing.PlaylistId = playlistId;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<Playlist?> GetPlaylistForMonitorAsync(string monitorKey, CancellationToken ct = default)
    {
        await using var db = _createDb();
        var a = await db.MonitorPlaylistAssignments
            .FirstOrDefaultAsync(x => x.MonitorKey == monitorKey, ct);
        if (a?.PlaylistId == null) return null;
        var pl = await db.Playlists.Include(p => p.Members)
            .FirstOrDefaultAsync(p => p.Id == a.PlaylistId.Value, ct);
        if (pl != null)
            pl.Members = pl.Members.OrderBy(m => m.Order).ToList();
        return pl;
    }

    public async Task<string?> GetMonitorKeyForPlaylistAsync(Guid playlistId, CancellationToken ct = default)
    {
        await using var db = _createDb();
        return await db.MonitorPlaylistAssignments
            .Where(a => a.PlaylistId == playlistId)
            .OrderByDescending(a => a.UpdatedAtUtc)
            .Select(a => a.MonitorKey)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<(string monitorKey, Guid playlistId)>> GetAllAssignmentsAsync(CancellationToken ct = default)
    {
        await using var db = _createDb();
        return await db.MonitorPlaylistAssignments
            .Where(a => a.PlaylistId != null)
            .Select(a => new ValueTuple<string, Guid>(a.MonitorKey, a.PlaylistId!.Value))
            .ToListAsync(ct);
    }
}
