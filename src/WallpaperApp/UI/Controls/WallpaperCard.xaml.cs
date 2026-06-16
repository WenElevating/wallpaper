using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperApp.Models;
using WallpaperApp.Services.Library;

namespace WallpaperApp.UI.Controls;

// A wallpaper tile. Shows a cached poster frame by default; on hover it fades in
// a live MediaElement preview of the actual video. Clicking invokes OpenCommand
// (the bound view-model command) with the card's WallpaperItem.
public partial class WallpaperCard : UserControl
{
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(
        nameof(OpenCommand), typeof(ICommand), typeof(WallpaperCard), new PropertyMetadata(null));

    public ICommand? OpenCommand
    {
        get => (ICommand?)GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    private string? _managedPath;
    private bool _isPlaying;

    public WallpaperCard()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // ClipToBounds on the rounded Border doesn't reliably clip media content
        // (the MediaElement video, and sometimes the poster Image), so apply an
        // explicit rounded clip geometry that follows the card size.
        CardContent.SizeChanged += (_, _) => UpdateClip();
    }

    private void UpdateClip()
    {
        var w = CardContent.ActualWidth;
        var h = CardContent.ActualHeight;
        if (w > 0 && h > 0)
            CardContent.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        StopPreview();
        _managedPath = (DataContext as WallpaperItem)?.ManagedFilePath;
        PosterImage.Source = null;
        if (DataContext is WallpaperItem item)
            _ = LoadPosterAsync(item);
    }

    private async Task LoadPosterAsync(WallpaperItem item)
    {
        var path = string.IsNullOrEmpty(_managedPath) ? null : await PosterCache.GetOrCreateAsync(_managedPath);
        // Bail if the card was recycled to a different item while we were waiting.
        if (path == null || !ReferenceEquals(DataContext, item)) return;
        try
        {
            // Cards render at ~300px, so decode the poster small. Posters are
            // otherwise saved at the video's full resolution (e.g. 3840x2160 for
            // 4K), and BitmapImage with no DecodePixelWidth decompresses them to
            // ~33MB EACH in RAM. With a non-virtualized library list that added up
            // to hundreds of MB resident. DecodePixelWidth makes the JPEG decoder
            // downscale during decode (cheap DCT scaling), cutting this to <1MB.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 640;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            PosterImage.Source = bmp;
        }
        catch { /* ignore — card just shows its surface color */ }
    }

    // Debounce hover so quickly sweeping across cards doesn't spin up a decoder
    // for each one (which janks the UI and churns decode sessions). Only cards
    // the mouse actually rests on (~150ms) start a preview.
    private DispatcherTimer? _hoverTimer;
    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += (_, _) => { _hoverTimer!.Stop(); StartPreview(); };
        _hoverTimer.Start();
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTimer = null;
        StopPreview();
    }

    private void StartPreview()
    {
        if (_isPlaying || string.IsNullOrEmpty(_managedPath)) return;
        try
        {
            // VideoFrameView.Source (a path string) starts its decode loop.
            Player.Source = _managedPath;
            _isPlaying = true;
            Player.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
        catch { }
    }

    private void StopPreview()
    {
        if (!_isPlaying) return;
        try
        {
            Player.BeginAnimation(OpacityProperty, null);
            Player.Opacity = 0;
            Player.Source = null; // stops the decode loop + frees the bitmap
        }
        catch { }
        _isPlaying = false;
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (OpenCommand?.CanExecute(DataContext) == true)
            OpenCommand.Execute(DataContext);
    }
}
