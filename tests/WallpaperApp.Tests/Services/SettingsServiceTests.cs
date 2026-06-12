using WallpaperApp.Models;
using WallpaperApp.Services.Settings;
using Xunit;

namespace WallpaperApp.Tests.Services;

public class SettingsServiceTests
{
    [Fact]
    public async Task SaveAndLoad_PersistsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_settings_{Guid.NewGuid()}.json");
        try
        {
            var service = new SettingsService(path);
            var settings = new AppSettings
            {
                LaunchAtStartup = true,
                Theme = "Light",
                DefaultFitMode = FitMode.Stretch
            };
            await service.SaveAsync(settings);
            var loaded = await service.LoadAsync();
            Assert.True(loaded.LaunchAtStartup);
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal(FitMode.Stretch, loaded.DefaultFitMode);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
