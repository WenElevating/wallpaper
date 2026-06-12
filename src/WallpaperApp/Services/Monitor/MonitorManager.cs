using System.IO;
using System.Security.Cryptography;
using System.Text;
using WallpaperApp.Interop;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Monitor;

public sealed class MonitorIdentity
{
    private MonitorIdentity() { }

    public static string GenerateKey(string edidHash, string connectionType)
    {
        var input = $"{edidHash}|{connectionType}";
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string MonitorKey { get; init; } = string.Empty;
}

public sealed class MonitorManager
{
    private readonly FileLogger _logger;
    private readonly Dictionary<IntPtr, MonitorInfo> _monitors = new();
    private readonly object _lock = new();

    public IReadOnlyDictionary<IntPtr, MonitorInfo> Monitors
    {
        get { lock (_lock) return _monitors.ToDictionary(kvp => kvp.Key, kvp => kvp.Value); }
    }

    public MonitorManager(FileLogger logger)
    {
        _logger = logger;
    }

    public void Refresh()
    {
        lock (_lock) _monitors.Clear();

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr lParam) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                var monitor = new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = $"Monitor_{hMonitor:X}",
                    X = info.rcMonitor.Left,
                    Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    MonitorKey = MonitorIdentity.GenerateKey($"EDIS_{hMonitor:X}", "Internal")
                };
                lock (_lock) _monitors[hMonitor] = monitor;
            }
            return true;
        }, IntPtr.Zero);

        _logger.Debug($"Refreshed monitors: {_monitors.Count} found");
    }

    public MonitorInfo? GetMonitor(IntPtr hMonitor)
    {
        lock (_lock)
        {
            _monitors.TryGetValue(hMonitor, out var info);
            return info;
        }
    }
}
