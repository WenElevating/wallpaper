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
    public async Task ImportAsync_SameContent_ReturnsExistingItem()
    {
        var service = CreateService();
        var source = Path.Combine(_testLibDir, "same-content.mp4");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3, 4 });

        var first = await service.ImportAsync(source);
        var second = await service.ImportAsync(source);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Single(await service.GetAllAsync());
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

    [Fact]
    public void ResolvePlaybackPath_UsesOnlyExistingVariantForPerformanceProfile()
    {
        var service = CreateService();
        var source = Path.Combine(_testLibDir, "ABC123.mp4");
        var variant = VideoVariantService.ResolveVariantPath(
            _testLibDir,
            source,
            WallpaperPerformanceProfile.Balanced);

        Directory.CreateDirectory(Path.GetDirectoryName(variant)!);
        File.WriteAllBytes(variant, new byte[] { 1 });

        Assert.Equal(variant, service.ResolvePlaybackPath(source, WallpaperPerformanceProfile.Balanced));
        Assert.Equal(source, service.ResolvePlaybackPath(source, WallpaperPerformanceProfile.Saver));
        Assert.Equal(source, service.ResolvePlaybackPath(source, WallpaperPerformanceProfile.Quality));
    }

    [Fact]
    public async Task DeleteAsync_RemovesContentAddressedVariantDirectory()
    {
        var service = CreateService();
        var item = await SeedWallpaperAsync(service, "clip");
        var variantDir = VideoVariantService.ResolveVariantDirectory(_testLibDir, item.ManagedFilePath);
        Directory.CreateDirectory(variantDir);
        await File.WriteAllBytesAsync(Path.Combine(variantDir, "balanced.mp4"), new byte[] { 1 });

        Assert.True(await service.DeleteAsync(item.Id));
        Assert.False(Directory.Exists(variantDir));
    }

    [Fact]
    public async Task DeleteAsync_WhenDatabaseDeleteFails_PreservesFilesAndRow()
    {
        var service = CreateService();
        var item = await SeedWallpaperAsync(service, "protected");

        await using (var db = CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER BlockWallpaperDelete BEFORE DELETE ON WallpaperItems BEGIN SELECT RAISE(ABORT, 'blocked'); END;");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => service.DeleteAsync(item.Id));

        Assert.True(File.Exists(item.ManagedFilePath));
        Assert.NotNull(await service.GetByIdAsync(item.Id));
    }

    [Fact]
    public async Task MigrateToAsync_ReplacesPreExistingIncompleteDestination()
    {
        var service = CreateService();
        var item = await SeedWallpaperAsync(service, "migrated");
        var newRoot = Path.Combine(_testLibDir, "new-root");
        var newLibraryDir = LibraryService.ResolveLibraryDir(newRoot);
        Directory.CreateDirectory(newLibraryDir);
        var destination = Path.Combine(newLibraryDir, Path.GetFileName(item.ManagedFilePath));
        await File.WriteAllBytesAsync(destination, new byte[] { 0xFF });

        var result = await service.MigrateToAsync(newRoot);

        Assert.Equal(1, result.success);
        Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(item.ManagedFilePath));
    }

    [Fact]
    public async Task WallpaperItem_ManagedFilePathHasDatabaseUniqueConstraint()
    {
        await using var first = CreateContext();
        var path = Path.Combine(_testLibDir, "same.mp4");
        first.WallpaperItems.Add(new WallpaperItem
        {
            DisplayName = "first",
            SourceType = "Video",
            OriginalFileName = "first.mp4",
            ManagedFilePath = path,
            ContainerFormat = "mp4",
            ValidationStatus = "Valid"
        });
        await first.SaveChangesAsync();

        await using var second = CreateContext();
        second.WallpaperItems.Add(new WallpaperItem
        {
            DisplayName = "second",
            SourceType = "Video",
            OriginalFileName = "second.mp4",
            ManagedFilePath = path,
            ContainerFormat = "mp4",
            ValidationStatus = "Valid"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }
}
