using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WallpaperApp.Services.Monitor;
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.LoadAsync();
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Media files|*.mp4;*.webm;*.avi;*.mov;*.gif;*.mkv|All files|*.*",
            Multiselect = true,
            Title = "Import Wallpaper Files"
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
            await _viewModel.AssignWallpaperAsync(monitor, _viewModel.SelectedWallpaper);
            MessageBox.Show($"Wallpaper set on {monitor.DeviceName}", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show("Please select a wallpaper first.", "No Selection",
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
