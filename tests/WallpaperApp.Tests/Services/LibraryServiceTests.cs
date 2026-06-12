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
        var ctx = CreateContext();
        var services = new ServiceCollection();
        services.AddSingleton(ctx);
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
}
