namespace WallpaperApp.Models;

// 轮播模式。本期实现 Interval(固定间隔);其余留作后续迭代。
public enum PlaylistMode
{
    Interval = 0, // 固定间隔轮播(分钟)
    // TimeOfDay = 1,        // 后续:按时段绑定
    // LoginSequential = 2,  // 后续:登录时下一张
}

public class Playlist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public PlaylistMode Mode { get; set; } = PlaylistMode.Interval;
    /// <summary>轮播间隔(分钟),仅 Interval 模式生效。默认 10。</summary>
    public int IntervalMinutes { get; set; } = 10;
    /// <summary>随机播放(false=顺序)。</summary>
    public bool Shuffle { get; set; }
    /// <summary>成员(有序)。Order 决定播放顺序。</summary>
    public List<PlaylistMember> Members { get; set; } = new();
    /// <summary>下次启动时从哪一项开始(0-based 索引;持久化"上次位置")。</summary>
    public int LastPlayedIndex { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class PlaylistMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlaylistId { get; set; }
    public Playlist? Playlist { get; set; }
    public Guid WallpaperId { get; set; }
    /// <summary>在列表中的顺序(0-based,升序)。</summary>
    public int Order { get; set; }
}
