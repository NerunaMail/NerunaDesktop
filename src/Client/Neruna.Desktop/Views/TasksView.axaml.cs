using Avalonia.Controls;
using Avalonia.Input;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();
    }

    // A click shows the task in the reading pane, a double click opens the editor.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TaskRow row })
        {
            row.EditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
