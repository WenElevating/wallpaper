namespace WallpaperApp.Models;

// 某显示器当前绑定的播放列表(可空 = 未绑定,用单张壁纸)。
public class MonitorPlaylistAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>显示器的持久身份键(MonitorIdentity.GenerateKey 产出)。</summary>
    public string MonitorKey { get; set; } = string.Empty;
    public Guid? PlaylistId { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
