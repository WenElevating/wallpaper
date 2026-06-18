using System.Windows;
using System.Windows.Input;

namespace WallpaperApp.UI.Views;

// Modal rename input. Pre-fills the current name, selects all, focuses. The
// confirm button stays disabled until the trimmed value differs from the original.
public partial class RenameWindow : Window
{
    private readonly string _original;

    public string NewName { get; private set; } = "";

    public RenameWindow(string currentName)
    {
        InitializeComponent();
        _original = currentName;
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
        NameBox.TextChanged += (_, _) => UpdateConfirmEnabled();
        UpdateConfirmEnabled();
    }

    private void UpdateConfirmEnabled()
    {
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text)
                                  && NameBox.Text.Trim() != _original.Trim();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConfirmButton.IsEnabled) Confirm_Click(sender, e);
        if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        NewName = "";
        DialogResult = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        NewName = NameBox.Text;
        DialogResult = true;
    }
}
