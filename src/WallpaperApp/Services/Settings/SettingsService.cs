using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WallpaperApp.Models;

namespace WallpaperApp.Services.Settings;

public sealed class SettingsService
{
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string AutoStartRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "WallpaperApp";

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperApp", "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = await File.ReadAllTextAsync(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveGate.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }

            UpdateAutoStartRegistry(settings.LaunchAtStartup);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void UpdateAutoStartRegistry(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoStartRegKey, true);
            if (key == null) return;
            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? "";
                key.SetValue(AppName, $"\"{exePath}\" --minimized");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }
}
