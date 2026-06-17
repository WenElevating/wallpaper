using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Tests.Services;

public sealed class RemoteSessionDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileLogger _logger;
    private readonly FakePlayback _playback;

    public RemoteSessionDetectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemoteSessionDetectorTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _logger = new FileLogger(_tempDir);
        _playback = new FakePlayback();
    }

    // 普通桌面(无 RDP、无 Miracast):Poll 不应触发任何暂停。
    [Fact]
    public void Poll_NoRemoteSession_DoesNotPause()
    {
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = true },
            isRemoteSession: () => false);

        detector.PollOnce();
        detector.PollOnce();

        Assert.Equal(0, _playback.PauseCalls);
        Assert.Equal(0, _playback.ResumeCalls);
    }

    // 远程会话出现:第一次 Poll 触发一次暂停。
    [Fact]
    public void Poll_RemoteSessionAppears_PausesOnce()
    {
        var remote = false;
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = true },
            isRemoteSession: () => remote);

        remote = true;
        detector.PollOnce();
        detector.PollOnce(); // 第二次不应重复暂停

        Assert.Equal(1, _playback.PauseCalls);
        Assert.Equal(0, _playback.ResumeCalls);
    }

    // 远程会话结束:恢复一次。
    [Fact]
    public void Poll_RemoteSessionDisappears_ResumesOnce()
    {
        var remote = true;
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = true },
            isRemoteSession: () => remote);

        detector.PollOnce();            // 进入远程 -> 暂停
        remote = false;
        detector.PollOnce();            // 离开远程 -> 恢复
        detector.PollOnce();            // 再次轮询不应重复恢复

        Assert.Equal(1, _playback.PauseCalls);
        Assert.Equal(1, _playback.ResumeCalls);
    }

    // 设置关闭:即使处于远程会话,也不暂停;若之前因本检测器施加过暂停,应清除。
    [Fact]
    public void Poll_SettingDisabled_NeverPauses_AndClearsExistingPause()
    {
        var remote = true;
        var enabled = true;
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = enabled },
            isRemoteSession: () => remote);

        detector.PollOnce();            // enabled + remote -> 暂停
        Assert.Equal(1, _playback.PauseCalls);

        enabled = false;
        detector.PollOnce();            // disabled -> 清除之前施加的暂停
        Assert.Equal(1, _playback.ResumeCalls);
    }

    // 验证用的是 RemoteDesktop 原因(而非 User)。
    [Fact]
    public void Poll_UsesRemoteDesktopReason_NotUser()
    {
        var remote = true;
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = true },
            isRemoteSession: () => remote);

        detector.PollOnce();
        detector.PollOnce(); // remote 仍为 true
        remote = false;
        detector.PollOnce();

        Assert.Equal(PauseReason.RemoteDesktop, _playback.LastPauseReason);
        Assert.Equal(PauseReason.RemoteDesktop, _playback.LastResumeReason);
    }

    // Stop() 必须清除本检测器施加的暂停,避免停止后留下遗留暂停。
    [Fact]
    public void Stop_ClearsPauseAppliedByThisDetector()
    {
        var remote = true;
        var detector = new RemoteSessionDetector(
            _logger, _playback, () => new AppSettings { PauseOnRemoteSession = true },
            isRemoteSession: () => remote);

        detector.PollOnce();        // 进入远程 -> 暂停
        Assert.Equal(1, _playback.PauseCalls);
        Assert.Equal(0, _playback.ResumeCalls);

        detector.Stop();            // 停止 -> 清除暂停

        Assert.Equal(1, _playback.ResumeCalls);
    }

    // P/Invoke struct 大小必须与 Win32 原生一致,否则 [Out] 数组 marshal 步长错误,
    // 导致多 path 读取错位甚至堆损坏。锁住 C1/C2 修复,防止未来回归。
    [Fact]
    public void DisplayConfigStructs_MatchNativeSize()
    {
        Assert.Equal(72, System.Runtime.InteropServices.Marshal.SizeOf<WallpaperApp.Interop.NativeMethods.DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(64, System.Runtime.InteropServices.Marshal.SizeOf<WallpaperApp.Interop.NativeMethods.DISPLAYCONFIG_MODE_INFO>());
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}

// 实现 IPlaybackPauseController,使 RemoteSessionDetector 可在无需实例化
// sealed 的 PlaybackManager(构造需 HWND/GPU 设备)的情况下被测试。
internal sealed class FakePlayback : IPlaybackPauseController
{
    public int PauseCalls;
    public int ResumeCalls;
    public PauseReason LastPauseReason;
    public PauseReason LastResumeReason;

    public Task PauseAllAsync(PauseReason reason, CancellationToken ct = default)
    {
        PauseCalls++;
        LastPauseReason = reason;
        return Task.CompletedTask;
    }

    public Task ResumeAllAsync(PauseReason reason, CancellationToken ct = default)
    {
        ResumeCalls++;
        LastResumeReason = reason;
        return Task.CompletedTask;
    }
}
