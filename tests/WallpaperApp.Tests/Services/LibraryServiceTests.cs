using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Library;

namespace WallpaperApp.Tests.Services;

public class LibraryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _testLibDir;

    public LibraryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _testLibDir = Path.Combine(Path.GetTempPath(), "WallpaperAppTest_" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_testLibDir))
        {
            try { Directory.Delete(_testLibDir, true); } catch { }
        }
    }

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var ctx = new AppDbContext(options);
        ctx.Database.EnsureCreated();
        return ctx;
    }

    private LibraryService CreateService()
    {
        // Register the context as Transient (factory) so each LibraryService method
        // — which uses `await using var db = CreateDbContext()` and disposes it —
        // gets a fresh context over the shared in-memory SQLite connection. A
        // Singleton registration (the trap) would let the first method's Dispose
        // invalidate the instance for every subsequent call in the same test.
        var services = new ServiceCollection();
        services.AddTransient(_ => CreateContext());
        var sp = services.BuildServiceProvider();
        var logger = new FileLogger(Path.Combine(Path.GetTempPath(), "WallpaperAppTestLogs_" + Guid.NewGuid().ToString("N")[..8]));
        return new LibraryService(logger, sp, _testLibDir);
    }

    [Fact]
    public async Task ImportAsync_InvalidFormat_ReturnsNull()
    {
        var service = CreateService();
        var tempFile = Path.Combine(Path.GetTempPath(), "test.txt");
        await File.WriteAllTextAsync(tempFile, "test content");

        try
        {
            var result = await service.ImportAsync(tempFile);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ImportAsync_NonexistentFile_ReturnsNull()
    {
        var service = CreateService();
        var result = await service.ImportAsync("/nonexistent/file.mp4");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_EmptyDb_ReturnsEmptyList()
    {
        var service = CreateService();
        var result = await service.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetByIdAsync_NonexistentId_ReturnsNull()
    {
        var service = CreateService();
        var result = await service.GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_NonexistentId_ReturnsFalse()
    {
        var service = CreateService();
        var result = await service.DeleteAsync(Guid.NewGuid());
        Assert.False(result);
    }

    private async Task<WallpaperItem> SeedWallpaperAsync(LibraryService service, string name)
    {
        var tempFile = Path.Combine(_testLibDir, name + ".mp4");
        await File.WriteAllBytesAsync(tempFile, new byte[] { 0x00 });
        return (await service.ImportAsync(tempFile))!;
    }

    [Fact]
    public async Task RenameAsync_UpdatesDisplayName()
    {
        var service = CreateService();
        var item = await SeedWallpaperAsync(service, "clip");

        var result = await service.RenameAsync(item.Id, "renamed clip");

        Assert.True(result);
        var refreshed = await service.GetByIdAsync(item.Id);
        Assert.Equal("renamed clip", refreshed!.DisplayName);
    }

    [Fact]
    public async Task RenameAsync_EmptyName_ReturnsFalseAndKeepsOriginal()
    {
        var service = CreateService();
        var item = await SeedWallpaperAsync(service, "clip");
        var original = item.DisplayName;

        var result = await service.RenameAsync(item.Id, "   ");

        Assert.False(result);
        var refreshed = await service.GetByIdAsync(item.Id);
        Assert.Equal(original, refreshed!.DisplayName);
    }

    [Fact]
    public async Task RenameAsync_NonexistentId_ReturnsFalse()
    {
        var service = CreateService();
        var result = await service.RenameAsync(Guid.NewGuid(), "anything");
        Assert.False(result);
    }
}
