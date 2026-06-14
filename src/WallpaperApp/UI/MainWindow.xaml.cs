using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        SetAppIcon();
    }

    // Use the same generated icon as the tray (TrayIconImage) so the window's
    // title bar, taskbar and Alt-Tab icon all match the tray.
    private void SetAppIcon()
    {
        try
        {
            using var icon = TrayIconImage.CreateDefault();
            using var ms = new MemoryStream();
            icon.Save(ms);
            ms.Position = 0;
            Icon = BitmapFrame.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch
        {
            // Falling back to the default window icon is harmless.
        }
    }

    // Apply Mica once the HWND exists. The window Background is Transparent, so
    // with Mica the system backdrop shows through; without it (older Windows) we
    // paint an opaque dark gradient so the client is never see-through.
    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        bool mica = WindowBackdrop.TryApply(this);
        Root.Background = mica
            ? new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x1E, 0x2E))
            : new LinearGradientBrush(Color.FromRgb(0x1E, 0x1E, 0x2E), Color.FromRgb(0x18, 0x18, 0x25), 90.0);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortComboBox?.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse(tag, out LibrarySort sort))
        {
            _viewModel.Sort = sort;
        }
    }
}
