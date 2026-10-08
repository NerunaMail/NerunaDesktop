using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.Editor;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ComposeView : UserControl
{
    private ComposeViewModel? _attached;

    public ComposeView()
    {
        InitializeComponent();
        var editor = this.FindControl<HtmlEditor>("Editor")!;
        this.FindControl<TextBox>("SubjectBox")!.AddHandler(KeyDownEvent, (_, e) => editor.TabIntoFromPreviousField(e), RoutingStrategies.Tunnel);
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is ComposeViewModel vm && vm != _attached)
            {
                _attached = vm;
                await vm.AttachEditorAsync(this.FindControl<HtmlEditor>("Editor")!);
            }
        };
    }
}
