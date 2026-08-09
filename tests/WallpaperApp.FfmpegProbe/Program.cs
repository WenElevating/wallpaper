using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;
using System.Runtime.InteropServices;

if (args.Length < 1 || args.Length > 2)
    return 64;

var loopCheck = args.Length == 2 && args[0] == "--loop-check";
var filePath = loopCheck ? args[1] : args[0];

var logDir = Path.Combine(Path.GetTempPath(), "WallpaperAppFfmpegProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logDir);

using var logger = new FileLogger(logDir);
using var backend = new FfmpegBackend(logger);

if (!await backend.OpenAsync(filePath))
    return 2;

await backend.PlayAsync();

if (loopCheck)
{
    // Reproduces the render loop's restart path (PlaybackSession.RenderLoop):
    // decode until EOS (null), then seek to 0 and play, then decode again.
    // A decoder stuck in its drain state returns null forever after the first
    // EOS, so the probe exits non-zero when no frame flows after the restart.
    var framesBeforeEos = 0;
    while (await backend.NextFrameAsync() != null)
        framesBeforeEos++;

    await backend.SeekAsync(TimeSpan.Zero);
    await backend.PlayAsync();

    var framesAfterRestart = 0;
    for (var i = 0; i < 32; i++)
    {
        var restartFrame = await backend.NextFrameAsync();
        if (restartFrame == null)
            break;
        restartFrame.Dispose();
        framesAfterRestart++;
    }

    Console.WriteLine($"loop_check frames_before_eos={framesBeforeEos} frames_after_restart={framesAfterRestart}");
    await backend.StopAsync();
    return framesAfterRestart > 0 ? 0 : 3;
}

using var frame = await backend.NextFrameAsync();

if (frame == null)
    return 3;

var pixel = new byte[4];
Marshal.Copy(frame.Buffer, pixel, 0, pixel.Length);
Console.WriteLine($"first_pixel_bgra={pixel[0]:X2}{pixel[1]:X2}{pixel[2]:X2}{pixel[3]:X2}");

await backend.StopAsync();
return 0;
