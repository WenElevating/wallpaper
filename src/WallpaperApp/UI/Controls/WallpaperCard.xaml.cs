using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            PosterImage.Source = bmp;
        }
        catch { /* ignore — card just shows its surface color */ }
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e) => StartPreview();
    private void Card_MouseLeave(object sender, MouseEventArgs e) => StopPreview();

    private void StartPreview()
    {
        if (_isPlaying || string.IsNullOrEmpty(_managedPath)) return;
        try
        {
            Player.Source = new Uri(_managedPath);
            Player.Play();
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
            Player.Stop();
            Player.Source = null;
        }
        catch { }
        _isPlaying = false;
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        try { Player.Position = TimeSpan.Zero; Player.Play(); }
        catch { }
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (OpenCommand?.CanExecute(DataContext) == true)
            OpenCommand.Execute(DataContext);
    }
}
