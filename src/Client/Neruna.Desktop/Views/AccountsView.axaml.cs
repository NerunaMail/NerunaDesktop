using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace Neruna.Desktop.Views;

internal sealed partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
    }

    // A color chosen: which one is taken first – closing the picker detaches the button from its color – then the
    // picker closes and the color is set.
    private void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var choice = (sender as Control)?.DataContext as ViewModels.AccountColorChoice;
        if ((sender as Avalonia.Visual)?.FindLogicalAncestorOfType<Avalonia.Controls.Primitives.Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        if (choice is not null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => choice.ChooseCommand.Execute(null));
        }
    }
}
