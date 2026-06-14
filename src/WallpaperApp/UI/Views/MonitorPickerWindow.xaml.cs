using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WallpaperApp.Services.Monitor;

namespace WallpaperApp.UI.Views;

// Modal monitor picker. Returns the chosen monitor (Selected) or, if the user
// picks "set on all", AllMonitors=true with Selected=null.
public partial class MonitorPickerWindow : Window
{
    public MonitorInfo? Selected { get; private set; }
    public bool AllMonitors { get; private set; }

    public IList<MonitorInfo> Monitors
    {
        set => MonitorList.ItemsSource = value;
    }

    public MonitorPickerWindow()
    {
        InitializeComponent();
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is MonitorInfo m)
        {
            Selected = m;
            AllMonitors = false;
            DialogResult = true;
        }
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        Selected = null;
        AllMonitors = true;
        DialogResult = true;
    }
}
