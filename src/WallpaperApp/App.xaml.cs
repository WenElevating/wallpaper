using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Localization;
using WallpaperApp.Models;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI;
using WallpaperApp.UI.Controls;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIcon? _trayIcon;
    private PowerAwareController? _powerAware;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var msg = $"UI exception: {args.Exception.Message}\n{args.Exception.StackTrace}";
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "logs", "crash.log"), msg);
            }
            catch { }
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                var ex = args.ExceptionObject as Exception;
                var msg = $"Crash: {ex?.Message}\n{ex?.StackTrace}";
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "logs", "crash.log"), msg);
            }
            catch { }
        };

        _ = StartupAsync(e);
    }

    private async Task StartupAsync(StartupEventArgs e)
    {
        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            var logger = _serviceProvider.GetRequiredService<FileLogger>();
            PosterCache.Logger = logger;
            HwDecodeDevice.Logger = logger;
            VideoFrameView.Logger = logger;

            // Create the shared GPU device and hand it to the playback manager so
            // decode + render share one D3D11 device (enables zero-copy GPU render).
            var gpu = _serviceProvider.GetRequiredService<GpuDevice>();
            _serviceProvider.GetRequiredService<PlaybackManager>().Gpu = gpu;

            logger.Info("WallpaperApp starting...");

            // Run migration with a standalone context to avoid threading issues
            using (var migrateDb = new AppDbContext())
            {
                await migrateDb.MigrateAsync();
            }
            logger.Info("Database migrated");

            var settings = _serviceProvider.GetRequiredService<SettingsService>();
            var appSettings = await settings.LoadAsync();

            // Apply the UI language BEFORE any window is created so the first
            // render and all {loc:Loc} bindings start in the right culture.
            LocalizationService.ApplyCulture(appSettings.Language);
            logger.Info($"UI language: {LocalizationService.EffectiveCode(appSettings.Language)}");

            var viewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            await viewModel.LoadAsync();

            // Load monitors first (DesktopHost needs monitor info)
            var monitorManager = _serviceProvider.GetRequiredService<MonitorManager>();
            monitorManager.Refresh();

            // DesktopHost is readied but no wallpaper window is created here.
            // PlaybackManager creates a window per monitor on demand when a
            // wallpaper is actually assigned; creating one at startup leaves an
            // orphan window (no renderer) embedded in the desktop WorkerW.
            var desktopHost = _serviceProvider.GetRequiredService<DesktopHost>();
            desktopHost.Attach();
            logger.Info($"DesktopHost attached: {desktopHost.IsAttached}");

            // Wire the fullscreen detector (registered but previously never
            // started) to pause wallpapers while a fullscreen app is in the
            // foreground, and the power controller to pause on battery. Both
            // read the CURRENT settings (the user can toggle them at runtime), so
            // they take a Func<AppSettings> accessor rather than a snapshot.
            var playback = _serviceProvider.GetRequiredService<PlaybackManager>();
            var fullscreen = _serviceProvider.GetRequiredService<FullscreenDetector>();
            System.Func<AppSettings> currentSettings = () => viewModel.Settings;
            fullscreen.FullscreenStateChanged += (s, isFullscreen) =>
            {
                if (!currentSettings().GlobalPauseOnFullscreen) return;
                _ = isFullscreen
                    ? playback.PauseAllAsync(PauseReason.Fullscreen)
                    : playback.ResumeAllAsync(PauseReason.Fullscreen);
            };
            // Start after monitors are known (the detector evaluates window
            // coverage against MonitorManager rects).
            fullscreen.Start();

            _powerAware = new PowerAwareController(logger, playback, currentSettings);
            _powerAware.Start();

            _trayIcon = new TrayIcon(viewModel, logger);

            if (!appSettings.StartMinimizedToTray)
            {
                _trayIcon.ShowMainWindow();
            }

            logger.Info("Application started");
        }
        catch (Exception ex)
        {
            try
            {
                var logger2 = _serviceProvider?.GetService<FileLogger>();
                logger2?.Error("Startup failed", ex);
            }
            catch { }
            MessageBox.Show(
                $"{Strings.MsgStartupFailedPrefix}\n\n{ex.Message}\n\n{ex.StackTrace}",
                Strings.ErrorCaption,
                MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        services.AddSingleton(new FileLogger(logDir));
        services.AddSingleton<GpuDevice>();
        services.AddTransient<AppDbContext>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ThumbnailService>();
        services.AddSingleton<GifTranscoder>();
        // PlaybackManager constructs D2dRenderer per-session using WallpaperWindow HWND
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

            var gpu = _serviceProvider.GetService<GpuDevice>();
            gpu?.Dispose();

            HwDecodeDevice.Shutdown();

            var desktop = _serviceProvider.GetService<DesktopHost>();
            desktop?.Dispose();

            var fullscreen = _serviceProvider.GetService<FullscreenDetector>();
            fullscreen?.Dispose();

            _powerAware?.Dispose();

            var logger = _serviceProvider.GetService<FileLogger>();
            logger?.Dispose();

            _serviceProvider.Dispose();
        }

        base.OnExit(e);
    }
}
