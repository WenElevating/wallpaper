using System.Runtime.InteropServices;
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
            return new Guid(hash[..16]).ToString("D").ToUpperInvariant();
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
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                var deviceName = GetMonitorDeviceName(hMonitor);
                var edidHash = ComputeEdidHash(hMonitor);
                var monitor = new MonitorInfo
                {
                    Handle = hMonitor,
                    DeviceName = deviceName,
                    X = info.rcMonitor.Left,
                    Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    MonitorKey = MonitorIdentity.GenerateKey(edidHash, "Internal")
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

    internal IEnumerable<(NativeMethods.RECT Rect, MonitorInfo Info)> GetMonitorRects()
    {
        lock (_lock)
        {
            foreach (var m in _monitors.Values)
                yield return (new NativeMethods.RECT
                {
                    Left = m.X, Top = m.Y,
                    Right = m.X + m.Width, Bottom = m.Y + m.Height
                }, m);
        }
    }

    private static string GetMonitorDeviceName(IntPtr hMonitor)
    {
        var mi = new NativeMethods.MONITORINFOEXW { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
        if (NativeMethods.GetMonitorInfoExW(hMonitor, ref mi) && !string.IsNullOrEmpty(mi.szDevice))
        {
            var nullIdx = mi.szDevice.IndexOf('\0');
            return nullIdx > 0 ? mi.szDevice[..nullIdx] : mi.szDevice;
        }
        return $"Monitor_{hMonitor:X}";
    }

    private static string ComputeEdidHash(IntPtr hMonitor)
    {
        var mi = new NativeMethods.MONITORINFOEXW { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEXW>() };
        if (NativeMethods.GetMonitorInfoExW(hMonitor, ref mi) && !string.IsNullOrEmpty(mi.szDevice))
        {
            var nullIdx = mi.szDevice.IndexOf('\0');
            var name = nullIdx > 0 ? mi.szDevice[..nullIdx] : mi.szDevice;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
            return Convert.ToHexString(hash);
        }
        return Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(hMonitor.ToInt64())));
    }
}
