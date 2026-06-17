using System.IO;
using Microsoft.EntityFrameworkCore;
using WallpaperApp.Models;

namespace WallpaperApp.Data;

public class AppDbContext : DbContext
{
    public DbSet<WallpaperItem> WallpaperItems => Set<WallpaperItem>();
    public DbSet<MonitorAssignment> MonitorAssignments => Set<MonitorAssignment>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();
    // F1: 播放列表相关表
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistMember> PlaylistMembers => Set<PlaylistMember>();
    public DbSet<MonitorPlaylistAssignment> MonitorPlaylistAssignments => Set<MonitorPlaylistAssignment>();

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

        // F1: 播放列表
        modelBuilder.Entity<Playlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.HasMany(x => x.Members).WithOne(m => m.Playlist!)
                .HasForeignKey(m => m.PlaylistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaylistMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.PlaylistId, x.Order }).IsUnique();
        });

        modelBuilder.Entity<MonitorPlaylistAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.MonitorKey).IsRequired().HasMaxLength(200);
            e.HasIndex(x => x.MonitorKey).IsUnique();
        });
    }

    public async Task MigrateAsync()
    {
        await Database.EnsureCreatedAsync();
        var current = await SchemaVersions.FirstOrDefaultAsync();
        var targetVersion = 2;
        if (current == null)
        {
            // 全新库:EnsureCreatedAsync 已按 OnModelCreating 建好所有表(含播放列表),
            // 直接记录目标版本即可。
            SchemaVersions.Add(new SchemaVersion { Version = targetVersion, AppliedAtUtc = DateTime.UtcNow });
            await SaveChangesAsync();
        }
        else
        {
            // 既有库:逐版本补表(EnsureCreatedAsync 对已存在库不补建新表)。
            if (current.Version < 2)
            {
                // v2: 播放列表相关表(Playlist / PlaylistMember / MonitorPlaylistAssignment)
                await Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""Playlists"" (
                        ""Id"" TEXT NOT NULL PRIMARY KEY,
                        ""Name"" TEXT NOT NULL,
                        ""Mode"" INTEGER NOT NULL,
                        ""IntervalMinutes"" INTEGER NOT NULL,
                        ""Shuffle"" INTEGER NOT NULL,
                        ""LastPlayedIndex"" INTEGER NOT NULL,
                        ""CreatedAtUtc"" TEXT NOT NULL,
                        ""UpdatedAtUtc"" TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS ""PlaylistMembers"" (
                        ""Id"" TEXT NOT NULL PRIMARY KEY,
                        ""PlaylistId"" TEXT NOT NULL,
                        ""WallpaperId"" TEXT NOT NULL,
                        ""Order"" INTEGER NOT NULL,
                        CONSTRAINT ""FK_PlaylistMembers_Playlists_PlaylistId""
                            FOREIGN KEY (""PlaylistId"") REFERENCES ""Playlists"" (""Id"") ON DELETE CASCADE
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PlaylistMembers_PlaylistId_Order""
                        ON ""PlaylistMembers"" (""PlaylistId"", ""Order"");
                    CREATE TABLE IF NOT EXISTS ""MonitorPlaylistAssignments"" (
                        ""Id"" TEXT NOT NULL PRIMARY KEY,
                        ""MonitorKey"" TEXT NOT NULL,
                        ""PlaylistId"" TEXT,
                        ""UpdatedAtUtc"" TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MonitorPlaylistAssignments_MonitorKey""
                        ON ""MonitorPlaylistAssignments"" (""MonitorKey"");
                ");
            }
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
