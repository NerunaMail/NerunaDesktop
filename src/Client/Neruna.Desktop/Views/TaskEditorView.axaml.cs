using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Neruna.Desktop.Views;

internal sealed partial class TaskEditorView : UserControl
{
    public TaskEditorView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        this.FindControl<TextBox>("SummaryBox")?.Focus();
    }
}
