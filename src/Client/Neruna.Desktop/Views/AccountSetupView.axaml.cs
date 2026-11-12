using Avalonia.Controls;
using Avalonia.Threading;

namespace Neruna.Desktop.Views;

internal sealed partial class AccountSetupView : UserControl
{
    public AccountSetupView()
    {
        InitializeComponent();

        // A new account starts with the address: ready to type.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (AddressBox.IsEffectivelyVisible)
            {
                AddressBox.Focus();
            }
        }, DispatcherPriority.Input);
    }
}
