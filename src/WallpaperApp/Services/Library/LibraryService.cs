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
    private readonly VideoVariantService _variants;

    public LibraryService(FileLogger logger, IServiceProvider serviceProvider, string? libraryDir = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _libraryDir = libraryDir ?? DefaultLibraryDir();
        _variants = new VideoVariantService(logger);
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
            var fileLength = new FileInfo(sourceFilePath).Length;
            var hash = await ComputeSha256Async(sourceFilePath, ct);
            var managedFileName = $"{hash}{ext}";
            var destPath = Path.Combine(_libraryDir, managedFileName);

            if (!File.Exists(destPath))
            {
                var tempPath = destPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await CopyFileAsync(sourceFilePath, tempPath, ct);
                    try
                    {
                        File.Move(tempPath, destPath);
                    }
                    catch (IOException) when (File.Exists(destPath))
                    {
                        // Another importer won the race to install the same
                        // content-addressed file. The existing complete file is
                        // the canonical destination.
                    }
                }
                finally
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
            }

            await using var db = CreateDbContext();
            var existing = await db.WallpaperItems
                .FirstOrDefaultAsync(w => w.ManagedFilePath == destPath, ct);
            if (existing != null)
            {
                _logger.Info($"Already imported: {fileName} -> {managedFileName}");
                return existing;
            }

            var item = new WallpaperItem
            {
                DisplayName = Path.GetFileNameWithoutExtension(fileName),
                SourceType = ext == ".gif" ? "Gif" : "Video",
                OriginalFileName = fileName,
                ManagedFilePath = destPath,
                ContainerFormat = ext.TrimStart('.'),
                FileBytes = fileLength,
                ImportedAtUtc = DateTime.UtcNow,
                ValidationStatus = "Valid"
            };

            db.WallpaperItems.Add(item);
            await db.SaveChangesAsync(ct);
            _logger.Info($"Imported: {fileName} -> {managedFileName}");
            if (item.SourceType == "Video")
                _ = GenerateVariantsAsync(item.ManagedFilePath);
            return item;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to import: {fileName}", ex);
            return null;
        }
    }

    public string ResolvePlaybackPath(string sourcePath, WallpaperPerformanceProfile profile)
    {
        if (profile == WallpaperPerformanceProfile.Quality)
        {
            _logger.Debug($"Playback proxy bypass profile={profile} source={sourcePath}");
            return sourcePath;
        }

        var variant = VideoVariantService.ResolveVariantPath(_libraryDir, sourcePath, profile);
        if (File.Exists(variant) && new FileInfo(variant).Length > 0)
        {
            _logger.Info($"Playback proxy hit profile={profile} source={sourcePath} path={variant}");
            return variant;
        }

        _logger.Debug($"Playback proxy miss profile={profile} source={sourcePath}; using source");
        return sourcePath;
    }

    public Task GenerateVariantsAsync(string sourcePath, CancellationToken ct = default)
        => _variants.GenerateAsync(sourcePath, _libraryDir, ct);

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

        var variantDir = VideoVariantService.ResolveVariantDirectory(_libraryDir, item.ManagedFilePath);
        if (Directory.Exists(variantDir))
        {
            try { Directory.Delete(variantDir, recursive: true); }
            catch (Exception ex) { _logger.Warn($"Failed to delete playback proxies: {ex.Message}"); }
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
        var newVariantRoot = Path.Combine(newLibDir, VideoVariantService.VariantDirectoryName);
        Directory.CreateDirectory(newVariantRoot);
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
                var oldVariantDir = VideoVariantService.ResolveVariantDirectory(_libraryDir, item.ManagedFilePath);
                var newVariantDir = VideoVariantService.ResolveVariantDirectory(newLibDir, newVideoPath);
                if (Directory.Exists(oldVariantDir) && !Directory.Exists(newVariantDir))
                    Directory.Move(oldVariantDir, newVariantDir);
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

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await src.CopyToAsync(dst, bufferSize, ct);
        await dst.FlushAsync(ct);
    }
}
