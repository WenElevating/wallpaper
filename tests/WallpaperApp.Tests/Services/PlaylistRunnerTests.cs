using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playlists;

namespace WallpaperApp.Tests.Services;

public sealed class PlaylistRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public PlaylistRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RunnerTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    private static Playlist MakePlaylist(params Guid[] wallpaperIds) => new()
    {
        Id = Guid.NewGuid(),
        Mode = PlaylistMode.Interval,
        IntervalMinutes = 1,
        Shuffle = false,
        Members = wallpaperIds.Select((id, i) => new PlaylistMember { WallpaperId = id, Order = i }).ToList()
    };

    [Fact]
    public async Task Start_SwitchesToStartIndex()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var switched = new List<Guid>();
        var runner = new PlaylistRunner(_logger, "MON-1", MakePlaylist(ids),
            id => { switched.Add(id); return Task.FromResult(true); },
            _ => Task.CompletedTask);

        await runner.StartAsync(startIndex: 1);

        Assert.Equal(ids[1], Assert.Single(switched));
        runner.Stop();
    }

    [Fact]
    public async Task Tick_AdvancesToNextWallpaper()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var switched = new List<Guid>();
        var runner = new PlaylistRunner(_logger, "MON-1", MakePlaylist(ids),
            id => { switched.Add(id); return Task.FromResult(true); },
            _ => Task.CompletedTask);

        await runner.StartAsync(startIndex: 0);
        await runner.TickAsync();
        await runner.TickAsync();

        Assert.Equal(ids[0], switched[0]);
        Assert.Equal(ids[1], switched[1]);
        Assert.Equal(ids[2], switched[2]);
        runner.Stop();
    }

    [Fact]
    public async Task Tick_WrapsAround()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var switched = new List<Guid>();
        var runner = new PlaylistRunner(_logger, "MON-1", MakePlaylist(ids),
            id => { switched.Add(id); return Task.FromResult(true); },
            _ => Task.CompletedTask);

        await runner.StartAsync(1);
        await runner.TickAsync();

        Assert.Equal(ids[1], switched[0]);
        Assert.Equal(ids[0], switched[1]);
        runner.Stop();
    }

    [Fact]
    public async Task Tick_PersistsIndexAfterSwitch()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var savedIndex = -1;
        var runner = new PlaylistRunner(_logger, "MON-1", MakePlaylist(ids),
            _ => Task.FromResult(true),
            idx => { savedIndex = idx; return Task.CompletedTask; });

        await runner.StartAsync(0);
        await runner.TickAsync();

        Assert.Equal(1, savedIndex);
        runner.Stop();
    }

    [Fact]
    public async Task EmptyPlaylist_NoSwitchNoThrow()
    {
        var switched = false;
        var empty = new Playlist { Id = Guid.NewGuid(), Mode = PlaylistMode.Interval, IntervalMinutes = 1 };
        var runner = new PlaylistRunner(_logger, "MON-1", empty,
            _ => { switched = true; return Task.FromResult(true); },
            _ => Task.CompletedTask);

        await runner.StartAsync(0);
        await runner.TickAsync();

        Assert.False(switched);
        runner.Stop();
    }

    [Fact]
    public async Task Shuffle_NeverRepeatsImmediately()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var pl = MakePlaylist(ids);
        pl.Shuffle = true;
        var switched = new List<Guid>();
        var runner = new PlaylistRunner(_logger, "MON-1", pl,
            id => { switched.Add(id); return Task.FromResult(true); },
            _ => Task.CompletedTask);

        await runner.StartAsync(0);
        for (int i = 0; i < 8; i++) await runner.TickAsync();

        // No two consecutive switches should be the same wallpaper.
        for (int i = 1; i < switched.Count; i++)
            Assert.NotEqual(switched[i - 1], switched[i]);
        runner.Stop();
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
