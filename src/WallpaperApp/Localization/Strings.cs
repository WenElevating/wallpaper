using System.Globalization;
using System.Resources;

namespace WallpaperApp.Localization;

// Strongly-typed access to the RESX string table (Resources/Strings.resx) for use
// from C# code (tray menu, dialogs, MessageBox). Written by hand (not the VS
// custom tool) so it builds cleanly from the CLI. The base name matches the
// embedded resource manifest name "WallpaperApp.Resources.Strings.resources";
// zh-CN ships as a satellite assembly (bin/.../zh-CN/WallpaperApp.resources.dll).
//
// Get() honors the explicitly-set Culture (set by LocalizationService on switch);
// when Culture is null it falls back to the current thread's CurrentUICulture.
// Missing keys degrade to the key name rather than throwing.
internal static class Strings
{
    private static readonly ResourceManager Manager =
        new("WallpaperApp.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>ResourceManager used by TranslationSource for XAML bindings.</summary>
    public static ResourceManager ResourceManager => Manager;

    /// <summary>
    /// When non-null, overrides the thread UI culture for all lookups below.
    /// </summary>
    public static CultureInfo? Culture { get; set; }

    public static string Get(string key) => Manager.GetString(key, Culture) ?? key;

    public static string LanguageLabel => Get(nameof(LanguageLabel));
    public static string LibraryLabel => Get(nameof(LibraryLabel));
    public static string ImportFilesButton => Get(nameof(ImportFilesButton));
    public static string PauseAllText => Get(nameof(PauseAllText));
    public static string ResumeAllText => Get(nameof(ResumeAllText));
    public static string MonitorsLabel => Get(nameof(MonitorsLabel));
    public static string SetWallpaperButton => Get(nameof(SetWallpaperButton));
    public static string MenuOpen => Get(nameof(MenuOpen));
    public static string MenuExit => Get(nameof(MenuExit));
    public static string DlgImportTitle => Get(nameof(DlgImportTitle));
    public static string DlgImportFilterMedia => Get(nameof(DlgImportFilterMedia));
    public static string DlgImportFilterAll => Get(nameof(DlgImportFilterAll));
    public static string MsgSetSuccess => Get(nameof(MsgSetSuccess));
    public static string MsgSetSuccessCaption => Get(nameof(MsgSetSuccessCaption));
    public static string MsgSetFailed => Get(nameof(MsgSetFailed));
    public static string MsgSetFailedCaption => Get(nameof(MsgSetFailedCaption));
    public static string MsgNoSelection => Get(nameof(MsgNoSelection));
    public static string MsgNoSelectionCaption => Get(nameof(MsgNoSelectionCaption));
    public static string MsgStartupFailedPrefix => Get(nameof(MsgStartupFailedPrefix));
    public static string ErrorCaption => Get(nameof(ErrorCaption));
    public static string Back => Get(nameof(Back));
    public static string SetAsWallpaper => Get(nameof(SetAsWallpaper));
    public static string ChooseMonitor => Get(nameof(ChooseMonitor));
    public static string SetOnAllMonitors => Get(nameof(SetOnAllMonitors));
    public static string ResolutionLabel => Get(nameof(ResolutionLabel));
    public static string DurationLabel => Get(nameof(DurationLabel));
    public static string FormatLabel => Get(nameof(FormatLabel));
    public static string SizeLabel => Get(nameof(SizeLabel));
    public static string NoWallpapers => Get(nameof(NoWallpapers));
    public static string SearchPlaceholder => Get(nameof(SearchPlaceholder));
    public static string SortRecent => Get(nameof(SortRecent));
    public static string SortName => Get(nameof(SortName));
    public static string SortSize => Get(nameof(SortSize));
    public static string ImportLabel => Get(nameof(ImportLabel));
    public static string SettingsLabel => Get(nameof(SettingsLabel));
    public static string PauseOnFullscreenLabel => Get(nameof(PauseOnFullscreenLabel));
    public static string PauseOnBatteryLabel => Get(nameof(PauseOnBatteryLabel));
    public static string PauseOnRemoteSessionLabel => Get(nameof(PauseOnRemoteSessionLabel));
    public static string HotkeyTogglePauseLabel => Get(nameof(HotkeyTogglePauseLabel));
    public static string HotkeyResetButton => Get(nameof(HotkeyResetButton));
    public static string PlaylistLabel => Get(nameof(PlaylistLabel));
    public static string PlaylistCreateButton => Get(nameof(PlaylistCreateButton));
    public static string PlaylistEmptyHint => Get(nameof(PlaylistEmptyHint));
    public static string MsgPlaylistCreated => Get(nameof(MsgPlaylistCreated));
    public static string MsgSetSuccessDesktop => Get(nameof(MsgSetSuccessDesktop));
    public static string MsgSetSuccessAll => Get(nameof(MsgSetSuccessAll));
    public static string MsgSetFailedShort => Get(nameof(MsgSetFailedShort));
    public static string MenuShuffleWallpaper => Get(nameof(MenuShuffleWallpaper));
    public static string MsgShuffleDone => Get(nameof(MsgShuffleDone));
    public static string MsgShuffleNoLibrary => Get(nameof(MsgShuffleNoLibrary));
    public static string MenuSetAsWallpaper => Get(nameof(MenuSetAsWallpaper));
    public static string MenuOpenDetail => Get(nameof(MenuOpenDetail));
    public static string MenuOpenFileLocation => Get(nameof(MenuOpenFileLocation));
    public static string MenuRename => Get(nameof(MenuRename));
    public static string MenuAddToPlaylist => Get(nameof(MenuAddToPlaylist));
    public static string MenuCopyToFolder => Get(nameof(MenuCopyToFolder));
    public static string MenuDelete => Get(nameof(MenuDelete));
    public static string DlgCancel => Get(nameof(DlgCancel));
    public static string DlgDeleteTitle => Get(nameof(DlgDeleteTitle));
    public static string DlgDeletePrompt => Get(nameof(DlgDeletePrompt));
    public static string DlgDeletePlaying => Get(nameof(DlgDeletePlaying));
    public static string DlgDeletePlaylistRefs => Get(nameof(DlgDeletePlaylistRefs));
    public static string DlgDeleteConfirm => Get(nameof(DlgDeleteConfirm));
    public static string DlgRenameTitle => Get(nameof(DlgRenameTitle));
    public static string DlgRenamePrompt => Get(nameof(DlgRenamePrompt));
    public static string DlgPickPlaylistTitle => Get(nameof(DlgPickPlaylistTitle));
    public static string DlgPlaylistEmpty => Get(nameof(DlgPlaylistEmpty));
    public static string DlgCopyTitle => Get(nameof(DlgCopyTitle));
    public static string MsgDeleted => Get(nameof(MsgDeleted));
    public static string MsgRenamed => Get(nameof(MsgRenamed));
    public static string MsgAddedToPlaylist => Get(nameof(MsgAddedToPlaylist));
    public static string MsgCopySuccess => Get(nameof(MsgCopySuccess));
    public static string MsgCopyFailed => Get(nameof(MsgCopyFailed));
    public static string MsgNameEmpty => Get(nameof(MsgNameEmpty));
    public static string SettingsStorageLocation => Get(nameof(SettingsStorageLocation));
    public static string SettingsStorageChange => Get(nameof(SettingsStorageChange));
    public static string SettingsStorageHint => Get(nameof(SettingsStorageHint));
    public static string DlgPickLibraryFolder => Get(nameof(DlgPickLibraryFolder));
    public static string MsgLibraryMigrated => Get(nameof(MsgLibraryMigrated));
    public static string MsgLibraryMigratePartial => Get(nameof(MsgLibraryMigratePartial));
    public static string MsgLibraryMigrateFailed => Get(nameof(MsgLibraryMigrateFailed));
    public static string MsgLibrarySamePath => Get(nameof(MsgLibrarySamePath));
}
