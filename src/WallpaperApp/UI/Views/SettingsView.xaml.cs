using System.Windows;
using System.Windows.Controls;
using WallpaperApp.Localization;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI.Views;

// Settings page. Currently a single language row; the combo is synced whenever
// the page becomes visible and routes changes to MainViewModel.SetLanguageAsync.
public partial class SettingsView : UserControl
{
    private bool _suppress;

    public SettingsView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => SyncSelection();
    }

    private void SyncSelection()
    {
        if (!IsVisible) return;
        var effective = LocalizationService.EffectiveCode((DataContext as MainViewModel)?.Settings.Language);
        _suppress = true;
        try
        {
            foreach (var item in LanguageComboBox.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string t && t == effective)
                {
                    LanguageComboBox.SelectedItem = cbi;
                    break;
                }
            }
        }
        finally { _suppress = false; }
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (LanguageComboBox.SelectedItem is ComboBoxItem item
            && item.Tag is string code
            && DataContext is MainViewModel vm)
        {
            await vm.SetLanguageAsync(code);
        }
    }
}
