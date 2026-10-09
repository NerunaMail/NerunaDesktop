using Avalonia.Controls;

namespace Neruna.Desktop.Views;

internal sealed partial class CloudAccountPasswordView : UserControl
{
    public CloudAccountPasswordView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => PasswordBox.Focus();
    }
}
