using System.Diagnostics;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Desktop;

public sealed class ExplorerWatcher : IDisposable
{
    private readonly FileLogger _logger;
    private readonly DesktopHost _desktopHost;
    private readonly System.Timers.Timer _pollTimer;
    private readonly object _restartLock = new();
    private int _lastExplorerPid;
    private bool _disposed;
    private Task? _restartTask;
    private CancellationTokenSource _restartCts = new();

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
        if (_disposed) return;
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

            lock (_restartLock)
            {
                if (_restartTask is null or { IsCompleted: true })
                    _restartTask = HandleExplorerRestartAsync(_restartCts.Token);
            }
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
        try
        {
            return processes.Length > 0 ? processes[0].Id : 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private async Task HandleExplorerRestartAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(1000, ct);
            if (ct.IsCancellationRequested || _disposed) return;
            _desktopHost.Detach();
            _desktopHost.Attach();
            ExplorerRestarted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.Error("Explorer restart handler failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _restartCts.Cancel();
        try { _restartTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) { _logger.Warn($"Explorer restart cleanup failed: {ex.InnerException?.Message}"); }
        _restartCts.Dispose();
    }
}
