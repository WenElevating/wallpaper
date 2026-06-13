namespace WallpaperApp.Models;

public class AppSettings
{
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimizedToTray { get; set; } = false;
    public bool GlobalPauseOnFullscreen { get; set; } = true;
    public FitMode DefaultFitMode { get; set; } = FitMode.Fill;
    public bool HardwareAccelerationEnabled { get; set; } = true;
    public string LogVerbosity { get; set; } = "Info";
    public string Theme { get; set; } = "Dark";
}
