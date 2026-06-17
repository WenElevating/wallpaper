using System.Windows.Controls;

namespace WallpaperApp.UI.Views;

// F1 playlist management view. Minimal initial version: header + create button
// + empty-state hint. Member editing and monitor-binding UI will follow in a
// later iteration; the rotation engine (PlaylistCoordinator) works once a
// playlist is bound to a monitor via the DB / future binding UI.
public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
    }
}
