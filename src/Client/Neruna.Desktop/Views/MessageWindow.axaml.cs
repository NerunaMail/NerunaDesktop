using Avalonia.Controls;

namespace Neruna.Desktop.Views;

internal sealed partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
        // A quick action's own key combination, for the mail of this window.
        KeyDown += (_, e) =>
        {
            if (!e.Handled && DataContext is ViewModels.MessageWindowViewModel vm && Infrastructure.Shortcuts.Of(e) is { } shortcut
                && vm.QuickActions.Any(b => b.RunIf(shortcut)))
            {
                e.Handled = true;
            }
        };
    }
}
