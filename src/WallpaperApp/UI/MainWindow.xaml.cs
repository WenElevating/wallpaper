using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WallpaperApp.Localization;
using WallpaperApp.Services.Monitor;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _suppressLanguageChange;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
        SyncLanguageComboBox();
    }

    // Reflect the current language setting in the combo box without retriggering
    // the SelectionChanged handler.
    private void SyncLanguageComboBox()
    {
        var effective = LocalizationService.EffectiveCode(_viewModel.Settings.Language);
        _suppressLanguageChange = true;
        try
        {
            foreach (var item in LanguageComboBox.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag == effective)
                {
                    LanguageComboBox.SelectedItem = cbi;
                    break;
                }
            }
        }
        finally { _suppressLanguageChange = false; }
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange) return;
        if (LanguageComboBox.SelectedItem is ComboBoxItem item && item.Tag is string code)
        {
            await _viewModel.SetLanguageAsync(code);
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = $"{Strings.DlgImportFilterMedia}|*.mp4;*.webm;*.avi;*.mov;*.gif;*.mkv|{Strings.DlgImportFilterAll}|*.*",
            Multiselect = true,
            Title = Strings.DlgImportTitle
        };

        if (dialog.ShowDialog() == true)
        {
            await _viewModel.ImportFilesAsync(dialog.FileNames);
        }
    }

    private void WallpaperListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox)
            _viewModel.SelectedWallpaper = listBox.SelectedItem as Models.WallpaperItem;
    }

    private async void SetWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is MonitorInfo monitor && _viewModel.SelectedWallpaper != null)
        {
            var assigned = await _viewModel.AssignWallpaperAsync(monitor, _viewModel.SelectedWallpaper);
            if (assigned)
            {
                MessageBox.Show(string.Format(Strings.MsgSetSuccess, monitor.DeviceName),
                    Strings.MsgSetSuccessCaption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(string.Format(Strings.MsgSetFailed, monitor.DeviceName),
                    Strings.MsgSetFailedCaption, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show(Strings.MsgNoSelection, Strings.MsgNoSelectionCaption,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PauseAllButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.PauseAllAsync();
    }

    private async void ResumeAllButton_Click(object sender, RoutedEventArgs e)
    {
        await _viewModel.ResumeAllAsync();
    }
}
