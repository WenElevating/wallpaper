using System.IO;
using System.Windows;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

var exitCode = 5;
var done = new ManualResetEventSlim(false);

var thread = new Thread(() =>
{
    var app = new Application
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown
    };

    var window = new Window
    {
        Width = 64,
        Height = 64,
        ShowInTaskbar = false,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        Left = -32000,
        Top = -32000,
        AllowsTransparency = false,
        ShowActivated = false,
    };

    window.Loaded += (_, _) =>
    {
        var logDir = Path.Combine(Path.GetTempPath(), "WallpaperAppRenderProbe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);

        using var logger = new FileLogger(logDir);
        var helper = new System.Windows.Interop.WindowInteropHelper(window);
        using var renderer = new D2dRenderer(helper.Handle, 64, 64, logger);

        var bytes = new byte[64 * 64 * 4];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = 0x20;
            bytes[i + 1] = 0x40;
            bytes[i + 2] = 0x80;
            bytes[i + 3] = 0xFF;
        }

        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes.Length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, buffer, bytes.Length);
            using var frame = new FrameData(buffer, 64, 64, 64 * 4, 0);
            exitCode = renderer.Present(frame) ? 0 : 2;
        }
        catch
        {
            exitCode = 3;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            window.Close();
            app.Shutdown(exitCode);
            done.Set();
        }
    };

    window.Show();
    app.Run();
});

thread.SetApartmentState(ApartmentState.STA);
thread.Start();
done.Wait(TimeSpan.FromSeconds(20));
thread.Join(TimeSpan.FromSeconds(5));

return exitCode;
