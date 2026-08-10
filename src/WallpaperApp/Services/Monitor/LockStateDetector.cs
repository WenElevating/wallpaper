using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Services.Monitor;

// Throttles wallpapers to 5 FPS while the desktop is locked. Unlike a pause,
// rendering continues: Windows shows the wallpaper as the lock-screen
// background, so pausing would blank it — throttling keeps it alive while the
// GPU present cost drops ~84%.
//
// Mirrors PowerAwareController's shape: the authoritative signal is the
// Microsoft.Win32.SystemEvents.SessionSwitch event (fires immediately on
// lock/unlock), with a low-frequency OpenInputDesktop poll as a safety net for
// the "locked before the app started" case. Secure-desktop prompts (UAC) also
// trigger the throttle — an acceptable and arguably desirable side effect.
public sealed class LockStateDetector : IDisposable
{
    private readonly FileLogger _logger;
    private readonly PlaybackManager _playback;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;
    private bool _locked;

    public bool IsLocked => _locked;

    public LockStateDetector(FileLogger logger, PlaybackManager playback, int pollIntervalMs = 2000)
    {
        _logger = logger;
        _playback = playback;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        _pollTimer.Start();
        Poll(); // establish the correct initial state immediately
        _logger.Debug("Lock-state detector started");
    }

    public void Stop()
    {
        _pollTimer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        // Clear the throttle we applied so nothing is left behind.
        SetLocked(false);
        _logger.Debug("Lock-state detector stopped");
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        try
        {
            SetLocked(e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Lock-state event failed: {ex.Message}");
        }
    }

    private void Poll()
    {
        try
        {
            SetLocked(IsDesktopLocked());
        }
        catch (Exception ex)
        {
            _logger.Warn($"Lock-state poll failed: {ex.Message}");
        }
    }

    // Exposed for tests (InternalsVisibleTo): applies the state transition and
    // pushes the scene throttle to the playback manager only on change.
    internal void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        _logger.Info($"Lock state changed: {locked}");
        _playback.SetSceneState(ScenePerformanceState.Locked, locked);
    }

    // A locked session (or a secure-desktop prompt like UAC) switches the
    // INPUT desktop away from the app's desktop. Comparing the input desktop
    // with the calling thread's desktop detects the lock without depending on
    // window class names ("LockAppHost" etc. change between Windows versions).
    // Fail-open: any error reports "not locked" so a detection hiccup can
    // never leave every wallpaper permanently throttled.
    internal static bool IsDesktopLocked()
    {
        var input = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (input == IntPtr.Zero) return false;
        try
        {
            var threadDesktop = NativeMethods.GetThreadDesktop(NativeMethods.GetCurrentThreadId());
            if (threadDesktop == IntPtr.Zero) return false;
            return input != threadDesktop;
        }
        finally
        {
            NativeMethods.CloseDesktop(input);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pollTimer.Dispose();
    }
}
