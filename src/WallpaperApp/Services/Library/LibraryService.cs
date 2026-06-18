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
    private string _libraryDir;
    private readonly IServiceProvider _serviceProvider;

    public LibraryService(FileLogger logger, IServiceProvider serviceProvider, string? libraryDir = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryDir = libraryDir ?? DefaultLibraryDir();
        Directory.CreateDirectory(_libraryDir);
    }

    // Default library directory under LocalAppData/WallpaperApp/library.
    public static string DefaultLibraryDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperApp", "library");

    // Resolves the library directory for a given root: <root>/library, or the
    // default when root is empty/whitespace.
    public static string ResolveLibraryDir(string? root)
        => string.IsNullOrWhiteSpace(root)
            ? DefaultLibraryDir()
            : Path.Combine(root, "library");

    public static string ResolvePosterDir(string? root)
        => string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WallpaperApp", "posters")
            : Path.Combine(root, "posters");

    // Switch the active library directory at runtime (used at startup to apply
    // the configured LibraryRoot, and after a migration to point at the new path).
    public void UseRoot(string? root)
    {
        _libraryDir = ResolveLibraryDir(root);
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

    public async Task UpdateMetadataAsync(Guid id, int width, int height, long durationMs, CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var item = await db.WallpaperItems.FindAsync(new object[] { id }, ct);
        if (item == null) return;
        item.Width = width;
        item.Height = height;
        item.DurationMs = durationMs;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RenameAsync(Guid id, string newName, CancellationToken ct = default)
    {
        var trimmed = newName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        await using var db = CreateDbContext();
        var item = await db.WallpaperItems.FindAsync(new object[] { id }, ct);
        if (item == null) return false;

        item.DisplayName = trimmed;
        await db.SaveChangesAsync(ct);
        _logger.Info($"Renamed wallpaper {id} -> '{trimmed}'");
        return true;
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

    // Migrates all wallpapers (videos + posters) from their current locations into
    // a new library root. Per-file fault tolerance: each wallpaper is copied
    // independently; a copy failure skips that item (its DB row keeps pointing at
    // the old path, so it stays playable). Only after a file copies successfully
    // do we update its DB paths and delete the original — so a mid-migration crash
    // never loses data (the original is still on disk + DB still points at it).
    //
    // The caller is responsible for pausing playback beforehand so the decoder
    // isn't holding the source files open. Returns (successCount, failedCount).
    public async Task<(int success, int failed)> MigrateToAsync(string newRoot, CancellationToken ct = default)
    {
        var newLibDir = ResolveLibraryDir(newRoot);
        var newPosterDir = ResolvePosterDir(newRoot);
        Directory.CreateDirectory(newLibDir);
        Directory.CreateDirectory(newPosterDir);

        await using var db = CreateDbContext();
        var items = await db.WallpaperItems.ToListAsync(ct);

        int success = 0, failed = 0;
        // Track per-item new paths; we only commit DB changes for items that fully
        // copied (video, and poster if present). Defer the SaveChanges to the end
        // so a failure mid-loop doesn't leave a half-migrated DB.
        var pendingUpdates = new List<(WallpaperItem item, string newVideo, string? newPoster)>();

        foreach (var item in items)
        {
            try
            {
                if (string.IsNullOrEmpty(item.ManagedFilePath) || !File.Exists(item.ManagedFilePath))
                {
                    // Missing source file — can't migrate, but don't count as a hard
                    // failure (the row may already reference a removed file).
                    failed++;
                    continue;
                }

                var videoName = Path.GetFileName(item.ManagedFilePath);
                var newVideoPath = Path.Combine(newLibDir, videoName);
                if (!File.Exists(newVideoPath))
                    await CopyAsync(item.ManagedFilePath, newVideoPath, ct);

                string? newPosterPath = null;
                if (!string.IsNullOrEmpty(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                {
                    var posterName = Path.GetFileName(item.ThumbnailPath);
                    newPosterPath = Path.Combine(newPosterDir, posterName);
                    if (!File.Exists(newPosterPath))
                        await CopyAsync(item.ThumbnailPath, newPosterPath, ct);
                }

                pendingUpdates.Add((item, newVideoPath, newPosterPath));
                success++;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Migration copy failed for '{item.DisplayName}': {ex.Message}");
                failed++;
            }
        }

        // Commit: update DB paths and delete originals, only for fully-copied items.
        foreach (var (item, newVideo, newPoster) in pendingUpdates)
        {
            var oldVideo = item.ManagedFilePath;
            var oldPoster = item.ThumbnailPath;

            item.ManagedFilePath = newVideo;
            item.ThumbnailPath = newPoster ?? "";

            try { if (File.Exists(oldVideo)) File.Delete(oldVideo); }
            catch (Exception ex) { _logger.Warn($"Migration: failed to delete old video {oldVideo}: {ex.Message}"); }
            if (!string.IsNullOrEmpty(oldPoster))
            {
                try { if (File.Exists(oldPoster)) File.Delete(oldPoster); }
                catch (Exception ex) { _logger.Warn($"Migration: failed to delete old poster {oldPoster}: {ex.Message}"); }
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.Info($"Migration to '{newRoot}': {success} succeeded, {failed} failed");
        return (success, failed);
    }

    private static async Task CopyAsync(string source, string dest, CancellationToken ct)
    {
        const int bufferSize = 81920;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await src.CopyToAsync(dst, ct);
    }
}
