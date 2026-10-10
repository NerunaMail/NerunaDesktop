using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();
    }

    private async void OnRename(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            await RenameAsync(control, reset: Equals(control.Tag, "reset"));
        }
    }

    private async void OnRenameKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && sender is Control control)
        {
            e.Handled = true;
            await RenameAsync(control, reset: false);
        }
    }

    private async Task RenameAsync(Control control, bool reset)
    {
        if (DataContext is not TasksViewModel vm || control.DataContext is not TaskListItem item)
        {
            return;
        }

        if (control.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        await vm.RenameAsync(item, reset ? null : item.EditName);
    }

    private async void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button swatch || DataContext is not TasksViewModel vm)
        {
            return;
        }

        var item = swatch.GetLogicalAncestors().OfType<Control>().Select(c => c.DataContext).OfType<TaskListItem>().FirstOrDefault();
        if (swatch.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        if (item is not null)
        {
            await vm.SetColorAsync(item, swatch.Tag as string);
        }
    }
}
