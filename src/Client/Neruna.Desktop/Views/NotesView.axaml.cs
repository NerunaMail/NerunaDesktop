using Avalonia.Controls;
using Neruna.Desktop.Editor;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class NotesView : UserControl
{
    public NotesView()
    {
        InitializeComponent();
        var editor = this.FindControl<HtmlEditor>("Editor")!;
        AttachedToVisualTree += (_, _) => (DataContext as NotesViewModel)?.AttachEditor(editor);
        DataContextChanged += (_, _) =>
        {
            if (VisualRoot is not null)
            {
                (DataContext as NotesViewModel)?.AttachEditor(editor);
            }
        };
        if (this.FindControl<Button>("TagButton")?.Flyout is Avalonia.Controls.Primitives.PopupFlyoutBase flyout)
        {
            flyout.Opening += (_, _) => (DataContext as NotesViewModel)?.PrepareTagMenu();
        }
    }
}
