using System.IO;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Library;

public sealed class LibraryService
{
    private readonly FileLogger _logger;
    private readonly string _libraryDir;
    private readonly IServiceProvider _serviceProvider;

    public LibraryService(FileLogger logger, IServiceProvider serviceProvider, string? libraryDir = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryDir = libraryDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "library");
        Directory.CreateDirectory(_libraryDir);
    }

    private AppDbContext CreateDbContext()
    {
        return _serviceProvider.GetRequiredService<AppDbContext>();
    }

    public async Task<WallpaperItem?> ImportAsync(string sourceFilePath, CancellationToken ct = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            _logger.Error($"Source file not found: {sourceFilePath}");
            return null;
        }

        var fileName = Path.GetFileName(sourceFilePath);
        var ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        var allowedExts = new[] { ".mp4", ".webm", ".avi", ".mov", ".gif", ".mkv" };
        if (!allowedExts.Contains(ext))
        {
            _logger.Error($"Unsupported format: {ext}");
            return null;
        }

        try
        {
            var fileBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
            var hash = Convert.ToHexString(SHA256.HashData(fileBytes));
            var managedFileName = $"{hash}{ext}";
            var destPath = Path.Combine(_libraryDir, managedFileName);

            if (!File.Exists(destPath))
            {
                await File.WriteAllBytesAsync(destPath, fileBytes, ct);
            }

            var item = new WallpaperItem
            {
                DisplayName = Path.GetFileNameWithoutExtension(fileName),
                SourceType = ext == ".gif" ? "Gif" : "Video",
                OriginalFileName = fileName,
                ManagedFilePath = destPath,
                ContainerFormat = ext.TrimStart('.'),
                FileBytes = fileBytes.Length,
                ImportedAtUtc = DateTime.UtcNow,
                ValidationStatus = "Valid"
            };

            await using var db = CreateDbContext();
            db.WallpaperItems.Add(item);
            await db.SaveChangesAsync(ct);
            _logger.Info($"Imported: {fileName} -> {managedFileName}");
            return item;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to import: {fileName}", ex);
            return null;
        }
    }

    public async Task<List<WallpaperItem>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        return await db.WallpaperItems
            .OrderByDescending(w => w.ImportedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<WallpaperItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        return await db.WallpaperItems.FindAsync(new object[] { id }, ct);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var item = await db.WallpaperItems.FindAsync(new object[] { id }, ct);
        if (item == null) return false;

        if (File.Exists(item.ManagedFilePath))
        {
            try { File.Delete(item.ManagedFilePath); }
            catch (Exception ex) { _logger.Warn($"Failed to delete file: {ex.Message}"); }
        }

        if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
        {
            try { File.Delete(item.ThumbnailPath); }
            catch (Exception ex) { _logger.Warn($"Failed to delete thumbnail: {ex.Message}"); }
        }

        db.WallpaperItems.Remove(item);
        await db.SaveChangesAsync(ct);
        _logger.Info($"Deleted wallpaper: {item.DisplayName}");
        return true;
    }
}
