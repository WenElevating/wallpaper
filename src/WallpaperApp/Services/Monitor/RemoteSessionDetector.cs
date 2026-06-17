using WallpaperApp.Interop;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Services.Monitor;

// 当本机处于远程桌面(RDP)或无线投屏(Miracast)会话中时,暂停所有壁纸。
// 形状与 PowerAwareController 一致:低频轮询 + 单一状态标志 + Apply/Clear 转发。
//
// 只操作 PauseReason.RemoteDesktop;其他原因(User/Fullscreen/Power/Occluded)
// 由各自的控制器独立管理,PlaybackSession 的原因记账保证它们互不覆盖。
//
// 通过 Func<AppSettings> 读取"当前"设置(非启动快照),用户可在设置页随时
// 开关;关闭后,此前因本检测器施加的暂停会被清除。
//
// 依赖 IPlaybackPauseController 而非具体 PlaybackManager,便于单元测试注入假实现
// (PlaybackManager 是 sealed 且构造时需要 HWND/GPU 设备,无法在测试中实例化)。
//
// 远程会话判定通过构造器注入的 Func<bool> 提供,生产路径传
// RemoteSessionProbe.IsRemoteSessionActive;测试可注入确定性 lambda。
public sealed class RemoteSessionDetector : IDisposable
{
    private readonly FileLogger _logger;
    private readonly IPlaybackPauseController _playback;
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<bool> _isRemoteSession;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;
    private bool _pausedForRemote; // 当前是否已因本检测器暂停(仅在状态变化时触发)

    public RemoteSessionDetector(
        FileLogger logger,
        IPlaybackPauseController playback,
        Func<AppSettings> getSettings,
        Func<bool>? isRemoteSession = null,
        int pollIntervalMs = 2000)
    {
        _logger = logger;
        _playback = playback;
        _getSettings = getSettings;
        _isRemoteSession = isRemoteSession ?? RemoteSessionProbe.IsRemoteSessionActive;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => PollOnce();
    }

    public void Start()
    {
        _pollTimer.Start();
        PollOnce(); // 立即建立正确的初始状态
        _logger.Debug("Remote-session detector started");
    }

    public void Stop()
    {
        _pollTimer.Stop();
        ClearRemotePause(); // 停止时清掉本检测器施加的暂停,避免遗留
        _logger.Debug("Remote-session detector stopped");
    }

    // 供单元测试同步调用(等价于一次定时器 tick)。internal + InternalsVisibleTo
    // 暴露给测试;生产路径走 Start() 的定时器,不直接调用。
    internal void PollOnce()
    {
        try
        {
            var enabled = _getSettings().PauseOnRemoteSession;
            var remote = _isRemoteSession();

            if (enabled && remote)
                ApplyRemotePause();
            else
                ClearRemotePause();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Remote-session poll failed: {ex.Message}");
        }
    }

    private void ApplyRemotePause()
    {
        if (_pausedForRemote) return;
        _pausedForRemote = true;
        _ = _playback.PauseAllAsync(PauseReason.RemoteDesktop);
        _logger.Info("Remote session active: pausing wallpapers");
    }

    private void ClearRemotePause()
    {
        if (!_pausedForRemote) return;
        _pausedForRemote = false;
        _ = _playback.ResumeAllAsync(PauseReason.RemoteDesktop);
        _logger.Info("Remote session ended (or pause disabled): resuming wallpapers");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pollTimer.Dispose();
    }
}

// 远程会话判定的纯函数实现(无状态,可独立测试)。
// 两个信号任一命中即视为远程:
//  (A) SM_REMOTESESSION —— 系统度量值,文档推荐的 RDP 快速判定。
//  (B) QueryDisplayConfig —— 枚举显示路径,发现 Miracast / 间接显示输出。
public static class RemoteSessionProbe
{
    public static bool IsRemoteSessionActive()
    {
        try
        {
            if (IsRdpSession()) return true;
            if (HasMiracastOrIndirectDisplay()) return true;
        }
        catch { /* 检测失败按"非远程"处理,不阻断播放 */ }
        return false;
    }

    private static bool IsRdpSession()
    {
        // GetSystemMetrics(SM_REMOTESESSION) 非 0 表示在远程桌面会话内。
        return NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;
    }

    private static bool HasMiracastOrIndirectDisplay()
    {
        var ret = NativeMethods.GetDisplayConfigBufferSizes(
            NativeMethods.QDC_ALL_PATHS, out var numPaths, out var numModes);
        if (ret != 0) return false;

        var paths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[numModes];

        ret = NativeMethods.QueryDisplayConfig(
            NativeMethods.QDC_ALL_PATHS,
            ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (ret != 0) return false;

        for (int i = 0; i < numPaths; i++)
        {
            var tech = paths[i].targetInfo.outputTechnology;
            if (tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_MIRACAST ||
                tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_WIRED ||
                tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INDIRECT_VIRTUAL ||
                tech == NativeMethods.DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_USB_TUNNELING)
            {
                return true;
            }
        }
        return false;
    }
}
