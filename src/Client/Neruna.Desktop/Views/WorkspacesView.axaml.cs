using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class WorkspacesView : UserControl
{
    public WorkspacesView()
    {
        InitializeComponent();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is WorkspacesViewModel vm && vm.EditCommand.CanExecute(null))
        {
            _ = vm.EditCommand.ExecuteAsync(null);
        }
    }
}
