using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playlists;

namespace WallpaperApp.Tests.Services;

public sealed class PlaylistServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly FileLogger _logger;

    public PlaylistServiceTests()
    {
        // In-memory SQLite, single shared context for the fixture. PlaylistService
        // is Transient in production (one context per service instance); here a
        // single context keeps the tracked-entity graph simple and matches a
        // single consumer's lifetime.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var tempDir = Path.Combine(Path.GetTempPath(), "PlaylistSvcTests_" + Guid.NewGuid().ToString("N")[..8]);
        _logger = new FileLogger(tempDir);
    }

    [Fact]
    public async Task Create_PersistsAndCanBeRead()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("MyList");
        var all = await svc.GetAllAsync();

        Assert.Single(all);
        Assert.Equal("MyList", all[0].Name);
        Assert.Equal(id, all[0].Id);
    }

    [Fact]
    public async Task AddMembers_PersistsInOrder()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("L");
        var w1 = Guid.NewGuid();
        var w2 = Guid.NewGuid();
        await svc.AddMemberAsync(id, w1);
        await svc.AddMemberAsync(id, w2);

        var pl = await svc.GetByIdAsync(id);
        Assert.Equal(2, pl!.Members.Count);
        Assert.Equal(w1, pl.Members[0].WallpaperId);
        Assert.Equal(0, pl.Members[0].Order);
        Assert.Equal(1, pl.Members[1].Order);
    }

    [Fact]
    public async Task NextIndex_Sequential_WrapsAround()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("L");
        await svc.AddMemberAsync(id, Guid.NewGuid());
        await svc.AddMemberAsync(id, Guid.NewGuid());
        await svc.AddMemberAsync(id, Guid.NewGuid());

        Assert.Equal(1, svc.ComputeNextIndex(id, 0, shuffle: false, count: 3));
        Assert.Equal(0, svc.ComputeNextIndex(id, 2, shuffle: false, count: 3));
    }

    [Fact]
    public async Task RemoveMember_RenumbersOrder()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("L");
        var w1 = Guid.NewGuid();
        var w2 = Guid.NewGuid();
        var w3 = Guid.NewGuid();
        await svc.AddMemberAsync(id, w1);
        await svc.AddMemberAsync(id, w2);
        await svc.AddMemberAsync(id, w3);

        await svc.RemoveMemberAsync(id, w2); // delete middle item

        var pl = await svc.GetByIdAsync(id);
        Assert.Equal(2, pl!.Members.Count);
        Assert.Equal(0, pl.Members[0].Order);
        Assert.Equal(1, pl.Members[1].Order);
        Assert.Equal(w3, pl.Members[1].WallpaperId);
    }

    [Fact]
    public async Task AssignMonitor_SetsAndPersists()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("L");
        await svc.AssignMonitorAsync("MON-1", id);
        var bound = await svc.GetPlaylistForMonitorAsync("MON-1");
        Assert.Equal(id, bound!.Id);

        await svc.AssignMonitorAsync("MON-1", null);
        Assert.Null(await svc.GetPlaylistForMonitorAsync("MON-1"));
    }

    [Fact]
    public async Task SaveLastIndex_Persists()
    {
        var svc = new PlaylistService(_logger, _db);
        var id = await svc.CreateAsync("L");
        await svc.SaveLastIndexAsync(id, 5);
        var pl = await svc.GetByIdAsync(id);
        Assert.Equal(5, pl!.LastPlayedIndex);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _logger.Dispose();
    }
}
