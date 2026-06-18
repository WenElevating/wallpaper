using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WallpaperApp.Models;

namespace WallpaperApp.UI.Views;

// Modal playlist picker for "Add to playlist" from a card. Returns the chosen
// playlist via Selected (null if cancelled). Mirrors MonitorPickerWindow's shape.
public partial class PlaylistPickerWindow : Window
{
    public Playlist? Selected { get; private set; }

    public PlaylistPickerWindow(IList<Playlist> playlists)
    {
        InitializeComponent();
        PlaylistList.ItemsSource = playlists;
    }

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Playlist p)
        {
            Selected = p;
            DialogResult = true;
        }
    }
}
