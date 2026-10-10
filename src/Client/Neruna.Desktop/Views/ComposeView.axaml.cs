using Avalonia.Controls;
using Avalonia.Interactivity;
using Neruna.Desktop.Editor;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ComposeView : UserControl
{
    private ComposeViewModel? _attached;

    // "Senden" keeps its accent look; it follows the other buttons with or without text.
    public static readonly Avalonia.Data.Converters.IValueConverter SendPadding =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Thickness>(labels => labels ? new Avalonia.Thickness(10, 4) : new Avalonia.Thickness(12, 6));

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

                // New mail or forward (no recipient yet): start in "An". A reply has its recipient: start typing the answer.
                if (string.IsNullOrWhiteSpace(vm.To))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<Neruna.Desktop.Controls.RecipientBox>("ToBox")?.FocusText(),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => this.FindControl<HtmlEditor>("Editor")?.FocusEditor(),
                        Avalonia.Threading.DispatcherPriority.Loaded);
                }
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

        var field = sender == this.FindControl<Button>("CcButton") ? RecipientField.Cc
            : sender == this.FindControl<Button>("BccButton") ? RecipientField.Bcc
            : RecipientField.To;
        var chosen = await RecipientPicker.ShowAsync(owner, field switch { RecipientField.Cc => "Cc", RecipientField.Bcc => "Bcc", _ => "An" }, directory);
        vm.AddRecipients(field, chosen);
    }
}
