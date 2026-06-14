using System.Globalization;
using System.Windows.Data;

namespace WallpaperApp.UI;

// Formats a DurationMs value as m:ss / h:mm:ss.
public sealed class DurationMsToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long ms && ms > 0)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Formats a byte count as a human-readable size.
public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double sz = bytes;
            int u = 0;
            while (sz >= 1024 && u < units.Length - 1) { sz /= 1024; u++; }
            return $"{sz:0.#} {units[u]}";
        }
        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
