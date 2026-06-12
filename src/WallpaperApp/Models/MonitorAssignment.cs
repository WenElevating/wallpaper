namespace WallpaperApp.Models;

public class MonitorAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MonitorKey { get; set; } = string.Empty;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public Guid WallpaperId { get; set; }
    public FitMode FitMode { get; set; } = FitMode.Fill;
    public bool PausedByFullscreen { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
