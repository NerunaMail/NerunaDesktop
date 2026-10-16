using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class FolderPickerView : UserControl
{
    public FolderPickerView()
    {
        InitializeComponent();
        // Typing filters at once.
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FolderPickerViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.PickOnlyCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CancelCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
