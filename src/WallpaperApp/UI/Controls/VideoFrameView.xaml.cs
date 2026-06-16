using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;

namespace WallpaperApp.UI.Controls;

// Live video preview that decodes frames into a WriteableBitmap, replacing the
// memory-hungry WPF MediaElement. Hardware decode keeps reference frames in GPU
// memory; only the current frame is copied to the bitmap.
//
// Concurrency / stop: each Start() launches an independent session on a
// background thread that owns its OWN FfmpegBackend + staging buffer and
// disposes them itself on exit. Stop() just cancels and clears the bitmap —
// it never blocks (no Join) and never touches the backend, so rapidly mousing
// across cards stays responsive. Stale sessions stop painting (Active=false).
public partial class VideoFrameView : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(VideoFrameView),
        new PropertyMetadata(null, OnSourceChanged));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(VideoFrameView),
        new PropertyMetadata(Stretch.UniformToFill));

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    // Set at startup (App). Null => preview is skipped (the poster still shows).
    public static FileLogger? Logger { get; set; }

    // One per Start(); cancelled/superseded on Stop. Carries its own decode
    // thread's lifetime so the UI thread never waits on it.
    private sealed class Session
    {
        public readonly CancellationTokenSource Cts = new();
        public volatile bool Active = true;
    }

    private Session? _session;
    private WriteableBitmap? _bitmap;
    private int _bmpW, _bmpH;

    public VideoFrameView()
    {
        InitializeComponent();
        Unloaded += (_, _) => Stop();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not VideoFrameView v) return;
        var path = e.NewValue as string;
        if (!string.IsNullOrEmpty(path)) v.Start(path);
        else v.Stop();
    }

    public void Start(string path)
    {
        Stop(); // supersede any running session
        if (Logger is null) return;
        var session = new Session();
        _session = session;
        new Thread(() => Run(path, session)) { IsBackground = true, Name = "PreviewDecode" }.Start();
    }

    public void Stop()
    {
        var s = _session;
        _session = null;
        if (s != null)
        {
            s.Active = false;     // ignore further paint from this session
            s.Cts.Cancel();       // decode thread exits and disposes its own backend
        }
        FrameImage.Source = null; // (called on the UI thread)
        _bitmap = null;
        _bmpW = _bmpH = 0;
    }

    private void Run(string path, Session session)
    {
        var logger = Logger!;
        var ct = session.Cts.Token;
        FfmpegBackend? backend = null;
        byte[] staging = Array.Empty<byte>();
        try
        {
            backend = new FfmpegBackend(logger, HwDecodeDevice.CreateNew);
            if (!backend.OpenAsync(path, ct).GetAwaiter().GetResult()) return;
            backend.PlayAsync(ct).GetAwaiter().GetResult();

            var sw = Stopwatch.StartNew();
            long lastPts = -1;
            var dispatcher = FrameImage.Dispatcher;

            while (!ct.IsCancellationRequested && session.Active)
            {
                var frame = backend.NextFrameAsync(ct).GetAwaiter().GetResult();
                if (frame == null)
                {
                    backend.SeekAsync(TimeSpan.Zero, ct).GetAwaiter().GetResult();
                    backend.PlayAsync(ct).GetAwaiter().GetResult();
                    lastPts = -1;
                    sw.Restart();
                    continue;
                }

                using (frame)
                {
                    // PTS pacing so the preview plays at a natural rate.
                    if (lastPts > 0 && frame.PtsUs > lastPts)
                    {
                        var durUs = frame.PtsUs - lastPts;
                        var elUs = sw.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
                        int waitMs = (int)Math.Max(0, (durUs - elUs) / 1000);
                        if (waitMs > 0) Thread.Sleep(Math.Min(waitMs, 100));
                    }
                    sw.Restart();
                    lastPts = frame.PtsUs;

                    // Snapshot into this session's reused staging buffer, then
                    // hand off to the UI thread to blit into the bitmap.
                    int size = frame.Height * frame.Stride;
                    if (staging.Length < size) staging = new byte[size];
                    Marshal.Copy(frame.Buffer, staging, 0, size);

                    int w = frame.Width, h = frame.Height, stride = frame.Stride;
                    var snapshot = staging;
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!session.Active) return; // superseded — drop
                        Paint(snapshot, w, h, stride);
                    }));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger?.Warn($"Preview decode error for {path}: {ex.Message}");
        }
        finally
        {
            backend?.Dispose();
            session.Cts.Dispose();
        }
    }

    // UI-thread only. Lazily creates / resizes the bitmap and blits the frame.
    private void Paint(byte[] staging, int w, int h, int srcStride)
    {
        if (_bitmap == null || _bmpW != w || _bmpH != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            _bmpW = w;
            _bmpH = h;
            FrameImage.Source = _bitmap;
        }
        try
        {
            _bitmap.Lock();
            int rowBytes = w * 4;
            int dstStride = _bitmap.BackBufferStride;
            unsafe
            {
                byte* dst = (byte*)_bitmap.BackBuffer;
                fixed (byte* src = staging)
                {
                    if (srcStride == dstStride)
                    {
                        long total = (long)rowBytes * h;
                        Buffer.MemoryCopy(src, dst, total, total);
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(src + y * srcStride, dst + y * dstStride, rowBytes, rowBytes);
                    }
                }
            }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            _bitmap.Unlock();
        }
        catch
        {
            try { _bitmap.Unlock(); } catch { }
        }
    }
}
