using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI.Views;

// Detail page. The MediaElement is driven from code-behind so it only plays
// while the view is actually visible (the library is still in the tree but
// collapsed) and tracks the active wallpaper as it changes.
public partial class WallpaperDetailView : UserControl
{
    private INotifyPropertyChanged? _vm;

    public WallpaperDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as INotifyPropertyChanged;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        ApplySource();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveWallpaper))
            ApplySource();
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ApplySource();
            if (Player.Source != null) Player.Play();
        }
        else
        {
            Player.Stop();
        }
    }

    private void ApplySource()
    {
        var path = (DataContext as MainViewModel)?.ActiveWallpaper?.ManagedFilePath;
        Player.Source = string.IsNullOrEmpty(path) ? null : new Uri(path);
        if (IsVisible && Player.Source != null) Player.Play();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        try { Player.Position = TimeSpan.Zero; Player.Play(); }
        catch { }
    }
}
