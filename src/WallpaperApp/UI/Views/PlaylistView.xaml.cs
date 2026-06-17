using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.UI.Views;

public partial class PlaylistView : UserControl
{
    private Point _dragStartPoint;
    private PlaylistMemberRow? _dragSource;

    public PlaylistView()
    {
        InitializeComponent();
    }

    private void MemberList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragSource = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as PlaylistMemberRow;
    }

    private void MemberList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource == null) return;

        var point = e.GetPosition(null);
        if (Math.Abs(point.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        DragDrop.DoDragDrop(MemberList, _dragSource, DragDropEffects.Move);
    }

    private async void MemberList_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (e.Data.GetData(typeof(PlaylistMemberRow)) is not PlaylistMemberRow source) return;
        var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as PlaylistMemberRow;
        if (target == null || ReferenceEquals(source, target)) return;

        await vm.MovePlaylistMemberAsync(source, target);
        _dragSource = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
