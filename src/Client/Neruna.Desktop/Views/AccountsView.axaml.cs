using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Neruna.Desktop.Views;

internal sealed partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
    }

    // A color chosen: the picker closes (the choice itself runs through its command).
    private void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Avalonia.Visual)?.FindLogicalAncestorOfType<Avalonia.Controls.Primitives.Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }
    }
}
