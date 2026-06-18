using System.Windows.Input;

namespace WallpaperApp.UI.Controls;

// Aggregate of the 7 commands surfaced on each wallpaper card's context menu.
// Assembled once in MainViewModel and bound down to every WallpaperCard via its
// Commands dependency property, so we avoid adding 7 separate DPs to the card.
public sealed class WallpaperCommands
{
    public ICommand SetAsWallpaper { get; init; } = null!;
    public ICommand OpenDetail { get; init; } = null!;
    public ICommand OpenFileLocation { get; init; } = null!;
    public ICommand Rename { get; init; } = null!;
    public ICommand AddToPlaylist { get; init; } = null!;
    public ICommand CopyToFolder { get; init; } = null!;
    public ICommand Delete { get; init; } = null!;
}
