using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

// Pure-logic tests for the random switcher: it picks a wallpaper id that is
// neither the one currently on the monitor nor anything in the recent-history
// window for that monitor. No WPF / Win32 / DB dependencies.
public sealed class RandomWallpaperSwitcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;

    public RandomWallpaperSwitcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShuffleTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
    }

    [Fact]
    public void PickNext_ExcludesCurrent()
    {
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var sw = new RandomWallpaperSwitcher(_logger);

        // Run many trials; every pick must differ from current.
        for (int i = 0; i < 200; i++)
        {
            var current = ids[i % ids.Count];
            var pick = sw.PickNext("MON-A", current, ids);
            Assert.NotNull(pick);
            Assert.NotEqual(current, pick);
        }
    }

    [Fact]
    public void PickNext_ExcludesRecent_WithinWindow()
    {
        // Library = 10, default window = 5; over the next 5 picks none should
        // repeat (from picks alone — current is also excluded each step).
        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        var sw = new RandomWallpaperSwitcher(_logger);

        var picks = new List<Guid>();
        Guid? current = null;
        for (int i = 0; i < 5; i++)
        {
            var pick = sw.PickNext("MON-A", current, ids);
            Assert.NotNull(pick);
            picks.Add(pick!.Value);
            current = pick;
        }

        Assert.Equal(picks.Count, picks.Distinct().Count());
    }

    [Fact]
    public void PickNext_DegradesGracefully_WhenLibraryTooSmall()
    {
        // Library = 2: window must shrink so a candidate is always available.
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var sw = new RandomWallpaperSwitcher(_logger);

        // From A → must be B; remember B; from B → must be A; and so on.
        Guid? current = a;
        for (int i = 0; i < 10; i++)
        {
            var pick = sw.PickNext("MON-A", current, new[] { a, b });
            Assert.NotNull(pick);
            Assert.NotEqual(current, pick);
            current = pick;
        }
    }

    [Fact]
    public void PickNext_PerMonitorIsolation()
    {
        // History on MON-A should NOT shrink the candidate set on MON-B.
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var sw = new RandomWallpaperSwitcher(_logger);

        // Saturate MON-A's history with two of the three ids.
        sw.PickNext("MON-A", null, ids);
        sw.PickNext("MON-A", null, ids);

        // MON-B must still be free to pick any id (other than its own current).
        var seenOnB = new HashSet<Guid>();
        for (int i = 0; i < 50; i++)
        {
            var pick = sw.PickNext("MON-B", null, ids);
            Assert.NotNull(pick);
            seenOnB.Add(pick!.Value);
        }
        Assert.Equal(3, seenOnB.Count);
    }

    [Fact]
    public void PickNext_EmptyLibrary_ReturnsNull()
    {
        var sw = new RandomWallpaperSwitcher(_logger);
        Assert.Null(sw.PickNext("MON-A", null, Array.Empty<Guid>()));
    }

    [Fact]
    public void PickNext_SingleItemLibrary_ReturnsThatItem()
    {
        var only = Guid.NewGuid();
        var sw = new RandomWallpaperSwitcher(_logger);
        // Even when "current" is that item we must hand it back — there is no
        // other choice; the caller still gets a valid wallpaper to assign.
        Assert.Equal(only, sw.PickNext("MON-A", only, new[] { only }));
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
