using WallpaperApp.Localization;
using WallpaperApp.Models;
using WallpaperApp.Services.Input;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;

namespace WallpaperApp.UI.ViewModels;

// Settings-page behavior is kept separate from library and playlist orchestration.
// The public properties remain on MainViewModel so existing WPF bindings and
// tests do not need a second DataContext during this incremental extraction.
public sealed partial class MainViewModel
{
    // Settings-page hotkey reset applies the new binding and persists it.
    public async Task ApplyHotkeysAsync(HotkeyBindings bindings)
    {
        Settings = Settings with { Hotkeys = bindings };
        _hotkeys.Apply(bindings);
        await SaveSettingsAsync(Settings);
    }

    public async Task SetLanguageAsync(string code)
    {
        Settings = Settings with { Language = code };
        await SaveSettingsAsync(Settings);
        LocalizationService.ApplyCulture(code);
        OnPropertyChanged(nameof(HeaderTitle));
        _logger.Info($"Language set to {code}");
    }

    // AppSettings is immutable, so these façade properties update a copy, apply
    // the live playback policy, and serialize persistence in order.
    public bool IsGlobalPauseOnFullscreen
    {
        get => Settings.GlobalPauseOnFullscreen;
        set => UpdatePerfSetting(s => s with { GlobalPauseOnFullscreen = value });
    }

    public bool IsPauseOnBattery
    {
        get => Settings.PauseOnBattery;
        set => UpdatePerfSetting(s => s with { PauseOnBattery = value });
    }

    public bool IsPauseOnRemoteSession
    {
        get => Settings.PauseOnRemoteSession;
        set => UpdatePerfSetting(s => s with { PauseOnRemoteSession = value });
    }

    public IReadOnlyList<WallpaperPerformanceProfile> PerformanceProfiles { get; } =
        Enum.GetValues<WallpaperPerformanceProfile>();

    public WallpaperPerformanceProfile SelectedPerformanceProfile
    {
        get => Settings.PerformanceProfile;
        set => UpdatePerfSetting(s => s with { PerformanceProfile = value });
    }

    private void UpdatePerfSetting(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        OnPropertyChanged(nameof(IsGlobalPauseOnFullscreen));
        OnPropertyChanged(nameof(IsPauseOnBattery));
        OnPropertyChanged(nameof(IsPauseOnRemoteSession));
        OnPropertyChanged(nameof(SelectedPerformanceProfile));
        _playback.UpdatePerformancePolicy(PlaybackPerformancePolicy.FromProfile(Settings.PerformanceProfile));
        _ = SaveSettingsAsync(Settings);
    }

    private async Task SaveSettingsAsync(AppSettings settings)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            await _settings.SaveAsync(settings);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to save settings: {ex.Message}");
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }
}
