using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WallpaperApp.Localization;

namespace WallpaperApp.UI.Views;

// Modal delete confirmation. Shows the wallpaper name plus, when relevant,
// the live-impact rows (currently playing / referenced by N playlists) so the
// user knows what else gets torn down by the delete.
public partial class ConfirmDeleteWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDeleteWindow(string displayName, bool isPlaying, int playlistRefCount)
    {
        InitializeComponent();
        PromptText.Text = string.Format(Strings.DlgDeletePrompt, displayName);

        if (isPlaying)
            ImpactPanel.Children.Add(MakeImpactRow("⚠ " + Strings.DlgDeletePlaying));
        if (playlistRefCount > 0)
            ImpactPanel.Children.Add(MakeImpactRow("📋 " + string.Format(Strings.DlgDeletePlaylistRefs, playlistRefCount)));
    }

    private static TextBlock MakeImpactRow(string text)
        => new()
        {
            Text = text,
            Foreground = (Brush)Application.Current.FindResource("MutedTextBrush"),
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 13
        };

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
