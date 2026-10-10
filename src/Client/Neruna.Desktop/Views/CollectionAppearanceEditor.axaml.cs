using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

/// <summary>The colour/name menu of a calendar or task list; the page that shows the list applies the choice.</summary>
internal sealed partial class CollectionAppearanceEditor : UserControl
{
    public CollectionAppearanceEditor()
    {
        InitializeComponent();
    }

    // The page (calendar or tasks) whose list this menu belongs to.
    private ICollectionListHost? Host => this.GetLogicalAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ICollectionListHost>().FirstOrDefault();

    private void Close()
    {
        if (this.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }
    }

    private async void OnColorPicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button swatch && DataContext is CollectionListItem item && Host is { } host)
        {
            Close();
            await host.SetColorAsync(item, swatch.Tag as string);
        }
    }

    private async void OnRename(object? sender, RoutedEventArgs e) =>
        await RenameAsync(reset: Equals((sender as Control)?.Tag, "reset"));

    private async void OnRenameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RenameAsync(reset: false);
        }
    }

    private async Task RenameAsync(bool reset)
    {
        if (DataContext is CollectionListItem item && Host is { } host)
        {
            Close();
            await host.RenameAsync(item, reset ? null : item.EditName);
        }
    }
}
