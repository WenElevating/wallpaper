namespace WallpaperApp.Models;

// Settings bag, persisted as JSON by SettingsService. A record so language (and
// future) updates can use an immutable `with` copy instead of in-place mutation.
public record AppSettings
{
    public bool LaunchAtStartup { get; init; }
    public bool StartMinimizedToTray { get; init; }
    public bool GlobalPauseOnFullscreen { get; init; } = true;
    /// <summary>Pause all wallpapers while running on battery power (laptops).</summary>
    public bool PauseOnBattery { get; init; } = true;
    /// <summary>Pause all wallpapers during an RDP or Miracast session (bandwidth saver).</summary>
    public bool PauseOnRemoteSession { get; init; } = true;
    public FitMode DefaultFitMode { get; init; } = FitMode.Fill;
    public bool HardwareAccelerationEnabled { get; init; } = true;
    public string LogVerbosity { get; init; } = "Info";
    public string Theme { get; init; } = "Dark";
    /// <summary>Global hotkey bindings. Empty slots are unbound.</summary>
    public HotkeyBindings Hotkeys { get; init; } = new();
    /// <summary>UI language code ("zh-CN", "en"); empty = follow the OS UI language.</summary>
    public string Language { get; init; } = "";
    /// <summary>Library storage root. Empty = default (LocalAppData/WallpaperApp).
    /// Videos go in <root>/library, posters in <root>/posters.</summary>
    public string LibraryRoot { get; init; } = "";
}
