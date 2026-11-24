using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();
    }

    // A click on the row shows the task in the reading pane; a click on its checkbox only ticks it.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TaskRow row } && !InCheckBox(e.Source))
        {
            row.SelectCommand.Execute(null);
        }
    }

    // A double click opens the editor.
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: TaskRow row } && !InCheckBox(e.Source))
        {
            row.EditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool InCheckBox(object? source) =>
        source is Visual visual && (visual is CheckBox || visual.FindAncestorOfType<CheckBox>() is not null);
}
