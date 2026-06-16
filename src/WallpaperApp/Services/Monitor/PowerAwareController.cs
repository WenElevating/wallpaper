using System.Runtime.InteropServices;
using WallpaperApp.Interop;
using WallpaperApp.Models;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.Services.Monitor;

// Pauses all wallpapers while the machine is running on battery (laptops), and
// resumes them on AC. Mirrors FullscreenDetector's shape: a low-frequency poll
// (the cheap source of truth) plus an immediate Microsoft.Win32.SystemEvents
// power-mode notification so plugging/unplugging reacts instantly instead of
// waiting up to the poll interval.
//
// Only the Power pause reason is touched; if the user (or the fullscreen
// detector) has paused for another reason, that pause is preserved by the
// reason accounting in PlaybackSession — clearing Power alone won't resume a
// session still held by User/Fullscreen.
//
// Reading the current settings via the Func<AppSettings> accessor (not a
// startup snapshot) lets the user toggle this off in the settings page at any
// time; when disabled, any Power pause we previously applied is cleared.
public sealed class PowerAwareController : IDisposable
{
    private readonly FileLogger _logger;
    private readonly PlaybackManager _playback;
    private readonly Func<AppSettings> _getSettings;
    private readonly System.Timers.Timer _pollTimer;
    private bool _disposed;
    private bool _pausedForBattery; // tracks the current Power pause so we toggle only on change

    public PowerAwareController(
        FileLogger logger,
        PlaybackManager playback,
        Func<AppSettings> getSettings,
        int pollIntervalMs = 30000)
    {
        _logger = logger;
        _playback = playback;
        _getSettings = getSettings;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _pollTimer.Start();
        Poll(); // establish the correct initial state immediately
        _logger.Debug("Power-aware controller started");
    }

    public void Stop()
    {
        _pollTimer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        // Clear any Power pause we applied so we never leave the wallpapers
        // paused by a reason we're no longer managing.
        ClearBatteryPause();
        _logger.Debug("Power-aware controller stopped");
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        // AC power changed (plugged in / unplugged) — react immediately rather
        // than waiting for the next poll tick.
        if (e.Mode == Microsoft.Win32.PowerModes.StatusChange ||
            e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            Poll();
        }
    }

    private void Poll()
    {
        try
        {
            var enabled = _getSettings().PauseOnBattery;
            var onBattery = IsOnBattery();

            if (enabled && onBattery)
                ApplyBatteryPause();
            else
                ClearBatteryPause();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Power-aware poll failed: {ex.Message}");
        }
    }

    private void ApplyBatteryPause()
    {
        if (_pausedForBattery) return;
        _pausedForBattery = true;
        _ = _playback.PauseAllAsync(PauseReason.Power);
        _logger.Info("On battery: pausing wallpapers");
    }

    private void ClearBatteryPause()
    {
        if (!_pausedForBattery) return;
        _pausedForBattery = false;
        _ = _playback.ResumeAllAsync(PauseReason.Power);
        _logger.Info("On AC (or battery pause disabled): resuming wallpapers");
    }

    // True when the machine has a battery and is currently NOT on AC power.
    // Desktops report ACLineUnknown/AcLineOnline and no battery -> false.
    private static bool IsOnBattery()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return false;
        // ACLineStatus: 0 = battery, 1 = AC, 255 = unknown. Treat unknown as
        // "not on battery" (don't pause desktops we can't read a clear signal from).
        if (status.ACLineStatus == NativeMethods.ACLineOffline)
            return true;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pollTimer.Dispose();
    }
}
