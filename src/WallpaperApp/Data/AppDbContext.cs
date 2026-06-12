using System.IO;
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Models;

namespace WallpaperApp.Data;

public class AppDbContext : DbContext
{
    public DbSet<WallpaperItem> WallpaperItems => Set<WallpaperItem>();
    public DbSet<MonitorAssignment> MonitorAssignments => Set<MonitorAssignment>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    private readonly string? _dbPath;

    public AppDbContext(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "wallpaper.db");
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dir = Path.GetDirectoryName(_dbPath)!;
            Directory.CreateDirectory(dir);
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallpaperItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
            e.Property(x => x.SourceType).IsRequired().HasMaxLength(10);
            e.Property(x => x.OriginalFileName).HasMaxLength(500);
            e.Property(x => x.ManagedFilePath).IsRequired().HasMaxLength(1000);
            e.Property(x => x.ThumbnailPath).HasMaxLength(1000);
            e.Property(x => x.ContainerFormat).HasMaxLength(20);
            e.Property(x => x.CodecSummary).HasMaxLength(100);
            e.Property(x => x.ValidationStatus).HasMaxLength(20);
        });

        modelBuilder.Entity<MonitorAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MonitorKey).IsRequired().HasMaxLength(200);
            e.Property(x => x.MonitorDeviceName).HasMaxLength(200);
        });

        modelBuilder.Entity<SchemaVersion>(e =>
        {
            e.HasKey(x => x.Id);
        });
    }

    public async Task MigrateAsync()
    {
        await Database.EnsureCreatedAsync();
        var current = await SchemaVersions.FirstOrDefaultAsync();
        var targetVersion = 1;
        if (current == null)
        {
            SchemaVersions.Add(new SchemaVersion { Version = targetVersion, AppliedAtUtc = DateTime.UtcNow });
            await SaveChangesAsync();
        }
        else if (current.Version < targetVersion)
        {
            current.Version = targetVersion;
            current.AppliedAtUtc = DateTime.UtcNow;
            await SaveChangesAsync();
        }
    }
}

public class SchemaVersion
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DateTime AppliedAtUtc { get; set; }
}
