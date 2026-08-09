using System.IO;
using System.Windows;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;
using Vortice.Direct3D11;
using Vortice.DXGI;

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

        // 1. D2D fallback path (legacy probe behavior).
        using (var renderer = new D2dRenderer(helper.Handle, 64, 64, logger))
        {
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
            }
        }

        // 2. DXGI zero-copy path: a 16x16 NV12 GPU frame presented into a 64x64
        // window. With the swap chain sized to the WINDOW, Present must succeed
        // with a window-sized back buffer + viewport sampling a smaller texture
        // (the exact scaling pattern a 4K video on a smaller display hits).
        if (exitCode == 0)
            exitCode = RunDxgiZeroCopyProbe(helper.Handle, logger);

        window.Close();
        app.Shutdown(exitCode);
        done.Set();
    };

    window.Show();
    app.Run();
});

thread.SetApartmentState(ApartmentState.STA);
thread.Start();
done.Wait(TimeSpan.FromSeconds(20));
thread.Join(TimeSpan.FromSeconds(5));

return exitCode;

static int RunDxgiZeroCopyProbe(IntPtr hwnd, FileLogger logger)
{
    using var gpu = new GpuDevice(logger);
    Console.WriteLine($"DXGI probe device adapter: {gpu.AdapterDescription ?? "<none>"}");
    if (!gpu.IsAvailable)
    {
        Console.WriteLine("DXGI probe skipped: no D3D11 device available");
        return 0;
    }
    if (!gpu.SupportsVideo)
    {
        // WARP or a driver without D3D11VA cannot host NV12 textures; skip
        // silently so the smoke test stays green on GPU-less machines.
        Console.WriteLine("DXGI probe skipped: device has no video support");
        return 0;
    }

    using var renderer = new DxgiRenderer(hwnd, 64, 64, logger, gpu);
    if (!renderer.TryInitZeroCopy(16, 16))
    {
        Console.WriteLine("DXGI probe failed: TryInitZeroCopy returned false");
        return 4;
    }

    using var tex = gpu.Device.CreateTexture2D(new Texture2DDescription
    {
        Width = 16,
        Height = 16,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.NV12,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource,
    });

    using var frame = FrameData.Gpu(tex.NativePointer, 0, 16, 16, 0);
    if (!renderer.Present(frame))
    {
        Console.WriteLine("DXGI probe failed: Present returned false");
        return 4;
    }

    Console.WriteLine("DXGI zero-copy probe succeeded (window-sized swap chain)");
    return 0;
}
