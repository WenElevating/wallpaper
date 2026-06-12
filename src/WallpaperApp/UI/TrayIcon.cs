using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MainWindow? _mainWindow;
    private readonly MainViewModel _viewModel;
    private bool _disposed;

    public TrayIcon(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open" };
        openItem.Click += (_, _) => ShowMainWindow();

        var pauseItem = new System.Windows.Controls.MenuItem { Header = "Pause All" };
        pauseItem.Click += async (_, _) => await _viewModel.PauseAllAsync();

        var resumeItem = new System.Windows.Controls.MenuItem { Header = "Resume All" };
        resumeItem.Click += async (_, _) => await _viewModel.ResumeAllAsync();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(pauseItem);
        menu.Items.Add(resumeItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "WallpaperApp",
            ContextMenu = menu,
            DoubleClickCommand = new DelegateCommand(ShowMainWindow)
        };

        _mainWindow = new MainWindow(viewModel);
        _mainWindow.Closed += (_, _) => _mainWindow.Hide();
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Dispose();
        _mainWindow?.Close();
    }
}

internal sealed class DelegateCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public DelegateCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
