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

    // A click on a folder picks it (an account opens or closes); not on the arrow, which TreeView handles itself.
    private void OnNodeTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FolderPickerViewModel vm && sender is Control { DataContext: FolderPickNode node })
        {
            vm.PickNodeCommand.Execute(node);
            e.Handled = true;
        }
    }

    // Keyboard: arrows move through the tree, Enter picks the selected folder.
    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not FolderPickerViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && Tree.SelectedItem is FolderPickNode { IsAccount: false } node)
        {
            vm.PickNodeCommand.Execute(node);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
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
            case Key.Down when !vm.IsSearching:
                // From the search box into the tree.
                Tree.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.CancelCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
