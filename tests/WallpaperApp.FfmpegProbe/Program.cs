using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;
using System.Runtime.InteropServices;

if (args.Length != 1)
    return 64;

var logDir = Path.Combine(Path.GetTempPath(), "WallpaperAppFfmpegProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logDir);

using var logger = new FileLogger(logDir);
using var backend = new FfmpegBackend(logger);

if (!await backend.OpenAsync(args[0]))
    return 2;

await backend.PlayAsync();
using var frame = await backend.NextFrameAsync();

if (frame == null)
    return 3;

var pixel = new byte[4];
Marshal.Copy(frame.Buffer, pixel, 0, pixel.Length);
Console.WriteLine($"first_pixel_bgra={pixel[0]:X2}{pixel[1]:X2}{pixel[2]:X2}{pixel[3]:X2}");

await backend.StopAsync();
return 0;
