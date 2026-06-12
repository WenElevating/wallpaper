using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIcon? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var logger = _serviceProvider.GetRequiredService<FileLogger>();
        var db = _serviceProvider.GetRequiredService<AppDbContext>();
        var settings = _serviceProvider.GetRequiredService<SettingsService>();

        logger.Info("WallpaperApp starting...");

        await db.MigrateAsync();
        logger.Info("Database migrated");

        var appSettings = await settings.LoadAsync();
        var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        await viewModel.LoadAsync();

        _trayIcon = new TrayIcon(viewModel);

        if (!appSettings.StartMinimizedToTray)
        {
            _trayIcon.ShowMainWindow();
        }

        logger.Info("Application started");
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<FileLogger>();
        services.AddSingleton<AppDbContext>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<PlaybackManager>();
        services.AddSingleton<DesktopHost>();
        services.AddSingleton<MonitorManager>();
        services.AddSingleton<FullscreenDetector>();
        services.AddSingleton<MainViewModel>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_serviceProvider != null)
        {
            var playback = _serviceProvider.GetService<PlaybackManager>();
            playback?.Dispose();

            var desktop = _serviceProvider.GetService<DesktopHost>();
            desktop?.Dispose();

            var fullscreen = _serviceProvider.GetService<FullscreenDetector>();
            fullscreen?.Dispose();

            var logger = _serviceProvider.GetService<FileLogger>();
            logger?.Dispose();

            _serviceProvider.Dispose();
        }

        base.OnExit(e);
    }
}
