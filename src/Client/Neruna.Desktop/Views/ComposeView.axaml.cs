using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

        // Files dragged into the mail: attach; with Ctrl a link; with the right mouse button ask. Into the text
        // (the WebView) the editor reports them; anywhere else in the window (fields, attachments, plain-text box) here.
        editor.FilesDropped += (_, drop) => _ = HandleDropAsync(drop.Paths, drop.Link, drop.Ask, editor, drop.Position);
        editor.FileDataDropped += (_, file) => (DataContext as ComposeViewModel)?.AttachData(file.Name, file.Data, file.TooLarge);
        // Ctrl+V with files copied in the file manager: attached, wherever the cursor is (the text itself: the editor).
        AddHandler(KeyDownEvent, OnPasteKey, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnDrop, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => _secondaryDrag = false, handledEventsToo: true);
        // Leaving for another page: the next ComposeView gets a new editor, so what was typed must stay behind.
        DetachedFromVisualTree += (_, _) =>
        {
            if (_attached is { } parked)
            {
                parked.Park(editor);
                _attached = null;
            }
        };
        // The same view back on screen (the WebView has reloaded its page): the parked text goes into it again.
        AttachedToVisualTree += async (_, _) => await AttachAsync();
        DataContextChanged += async (_, _) => await AttachAsync();
    }

    private async Task AttachAsync()
    {
        if (DataContext is not ComposeViewModel vm || vm == _attached || VisualRoot is null)
        {
            return;
        }

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

    private bool _secondaryDrag;

    private void OnPasteKey(object? sender, KeyEventArgs e)
    {
        var paste = (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control) || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift);
        if (!paste || e.Source is not TextBox box || DataContext is not ComposeViewModel vm)
        {
            return;
        }

        e.Handled = true;
        _ = PasteAsync();

        async Task PasteAsync()
        {
            if (await Neruna.Desktop.Editor.ClipboardFiles.PathsAsync(TopLevel.GetTopLevel(this)?.Clipboard) is { Count: > 0 } paths)
            {
                await vm.AttachFilesAsync(paths);
            }
            else
            {
                box.Paste();
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Messages from the mail list: attached (not over the text itself – the editor there only takes files).
        if (e.DataTransfer.Contains(MailView.MessageContentFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            _secondaryDrag |= DragKeys.IsSecondaryButtonDown();
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var ask = _secondaryDrag;
        _secondaryDrag = false;
        if (e.DataTransfer.TryGetValue(MailView.MessageContentFormat) is { } load)
        {
            e.Handled = true;
            if (DataContext is ComposeViewModel compose)
            {
                _ = compose.AttachMessagesAsync(load);
            }

            return;
        }

        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } items)
        {
            return;
        }

        e.Handled = true;
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        _ = HandleDropAsync(paths, e.KeyModifiers.HasFlag(KeyModifiers.Control), ask, this, e.GetPosition(this));
    }

    private async Task HandleDropAsync(IReadOnlyList<string> paths, bool link, bool ask, Control target, Point position)
    {
        if (DataContext is not ComposeViewModel vm || paths.Count == 0)
        {
            return;
        }

        if (ask)
        {
            var choice = await AskAsync(target, position);
            if (choice is null)
            {
                return;
            }

            link = choice.Value;
        }

        if (link)
        {
            await vm.InsertFileLinksAsync(paths);
        }
        else
        {
            await vm.AttachFilesAsync(paths);
        }
    }

    // Right mouse button: "Datei anhängen", "Datei als Link einfügen" or "Abbrechen" where the files were dropped.
    // A context menu is its own window, so it shows above the WebView too. Null: cancelled.
    private static Task<bool?> AskAsync(Control target, Point position)
    {
        var done = new TaskCompletionSource<bool?>();
        var attach = new MenuItem { Header = Neruna.Core.Localization.Texts.T("Datei anhängen") };
        var link = new MenuItem { Header = Neruna.Core.Localization.Texts.T("Datei als Link einfügen") };
        var cancel = new MenuItem { Header = Neruna.Core.Localization.Texts.T("Abbrechen") };
        attach.Click += (_, _) => done.TrySetResult(false);
        link.Click += (_, _) => done.TrySetResult(true);
        cancel.Click += (_, _) => done.TrySetResult(null);
        var menu = new ContextMenu
        {
            ItemsSource = new object[] { attach, link, new Separator(), cancel },
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            HorizontalOffset = position.X,
            VerticalOffset = position.Y,
        };
        menu.Closed += (_, _) => done.TrySetResult(null);
        menu.Open(target);
        return done.Task;
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
