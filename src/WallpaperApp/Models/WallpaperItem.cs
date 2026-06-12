namespace WallpaperApp.Models;

public class WallpaperItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string SourceType { get; set; } = "Video";
    public string OriginalFileName { get; set; } = string.Empty;
    public string ManagedFilePath { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContainerFormat { get; set; } = string.Empty;
    public string CodecSummary { get; set; } = string.Empty;
    public long FileBytes { get; set; }
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
    public string ValidationStatus { get; set; } = "Pending";
}
