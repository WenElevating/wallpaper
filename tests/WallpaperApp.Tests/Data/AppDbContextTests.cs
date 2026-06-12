using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Data;
using Xunit;

namespace WallpaperApp.Tests.Data;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AppDbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task Database_CanBeCreated_AndHasSchemaVersion()
    {
        await using var context = CreateContext();
        await context.MigrateAsync();
        var version = await context.SchemaVersions.FirstOrDefaultAsync();
        Assert.NotNull(version);
        Assert.Equal(1, version.Version);
    }

    [Fact]
    public async Task CanInsertAndQueryWallpaperItem()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        var item = new WallpaperApp.Models.WallpaperItem
        {
            DisplayName = "Test Wallpaper",
            SourceType = "Video",
            OriginalFileName = "test.mp4",
            ManagedFilePath = "/managed/test.mp4",
            FileBytes = 1024,
            ValidationStatus = "Valid"
        };
        context.WallpaperItems.Add(item);
        await context.SaveChangesAsync();
        var retrieved = await context.WallpaperItems.FirstAsync();
        Assert.Equal("Test Wallpaper", retrieved.DisplayName);
        Assert.Equal("Video", retrieved.SourceType);
    }
}
