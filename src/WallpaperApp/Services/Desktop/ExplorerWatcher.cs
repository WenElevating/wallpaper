using System.Diagnostics;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class ExplorerWatcher : IDisposable
{
    private readonly FileLogger _logger;
    private readonly DesktopHost _desktopHost;
    private readonly System.Timers.Timer _pollTimer;
    private int _lastExplorerPid;
    private bool _disposed;

    public event EventHandler? ExplorerRestarted;

    public ExplorerWatcher(FileLogger logger, DesktopHost desktopHost, int pollIntervalMs = 2000)
    {
        _logger = logger;
        _desktopHost = desktopHost;
        _pollTimer = new System.Timers.Timer(pollIntervalMs);
        _pollTimer.Elapsed += (_, _) => Poll();
    }

    public void Start()
    {
        _lastExplorerPid = FindExplorerPid();
        _pollTimer.Start();
        _logger.Debug($"ExplorerWatcher started, tracking PID {_lastExplorerPid}");
    }

    public void Stop()
    {
        _pollTimer.Stop();
    }

    private void Poll()
    {
        var currentPid = FindExplorerPid();
        if (_lastExplorerPid == 0)
        {
            _lastExplorerPid = currentPid;
            return;
        }

        if (currentPid != _lastExplorerPid && currentPid != 0)
        {
            _logger.Info($"Explorer restarted: {_lastExplorerPid} -> {currentPid}");
            _lastExplorerPid = currentPid;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000);
                    _desktopHost.Attach();
                    ExplorerRestarted?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    _logger?.Error("Explorer restart handler failed", ex);
                }
            });
        }
        else if (currentPid == 0 && _lastExplorerPid != 0)
        {
            _logger.Warn("Explorer process not found");
            _lastExplorerPid = 0;
        }
    }

    private static int FindExplorerPid()
    {
        var processes = Process.GetProcessesByName("Explorer");
        return processes.Length > 0 ? processes[0].Id : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
    }
}
