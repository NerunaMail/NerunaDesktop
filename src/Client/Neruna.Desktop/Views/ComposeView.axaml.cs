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

    // "An …" / "Cc …": choose one or more contacts or groups.
    private async void OnPickRecipients(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ComposeViewModel { Recipients: { } directory } vm || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var cc = sender == this.FindControl<Button>("CcButton");
        var chosen = await RecipientPicker.ShowAsync(owner, cc ? "Cc" : "An", directory);
        vm.AddRecipients(cc, chosen);
    }
}
