using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ChatView : UserControl
{
    // A new sender (or a pause) starts with some room; follow-up lines of the same person sit close together.
    public static readonly IValueConverter MessageMargin = new FuncValueConverter<bool, Thickness>(header => header ? new Thickness(0, 10, 0, 0) : new Thickness(0, 1, 0, 0));

    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        // Before the text box: Enter sends (Shift+Enter is a new line), Ctrl+B / Ctrl+U format.
        DraftBox.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        }
    }

    // Another conversation: to the newest message. New messages: follow when already at the bottom or when it is my own.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var atBottom = MessageScroller.Offset.Y >= MessageScroller.Extent.Height - MessageScroller.Viewport.Height - 80;
        var mine = e.NewItems?.OfType<ChatMessageItem>().Any(m => m.IsMine) == true;
        if (e.Action == NotifyCollectionChangedAction.Reset || atBottom || mine)
        {
            Dispatcher.UIThread.Post(MessageScroller.ScrollToEnd, DispatcherPriority.Background);
        }
    }

    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !ctrl)
        {
            e.Handled = true;
            if (_viewModel?.SendCommand.CanExecute(null) == true)
            {
                _viewModel.SendCommand.Execute(null);
            }
        }
        else if (ctrl && e.Key == Key.B)
        {
            e.Handled = true;
            Wrap("**");
        }
        else if (ctrl && e.Key == Key.U)
        {
            e.Handled = true;
            Wrap("__");
        }
    }

    private void OnBold(object? sender, RoutedEventArgs e) => Wrap("**");

    private void OnUnderline(object? sender, RoutedEventArgs e) => Wrap("__");

    // The selection between the markers; without one, a pair with the caret in between.
    private void Wrap(string marker)
    {
        var text = DraftBox.Text ?? string.Empty;
        var start = Math.Min(DraftBox.SelectionStart, DraftBox.SelectionEnd);
        var end = Math.Max(DraftBox.SelectionStart, DraftBox.SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        DraftBox.Text = text[..start] + marker + text[start..end] + marker + text[end..];
        DraftBox.SelectionStart = start + marker.Length;
        DraftBox.SelectionEnd = end + marker.Length;
        DraftBox.Focus();
    }

    private void OnEmoji(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string emoji })
        {
            return;
        }

        var text = DraftBox.Text ?? string.Empty;
        var caret = Math.Clamp(DraftBox.CaretIndex, 0, text.Length);
        DraftBox.Text = text[..caret] + emoji + text[caret..];
        DraftBox.CaretIndex = caret + emoji.Length;
        EmojiButton.Flyout?.Hide();
        DraftBox.Focus();
    }

    // A person picked in the "new private message" list: close the list.
    private void OnFlyoutPick(object? sender, RoutedEventArgs e) => NewPrivateButton.Flyout?.Hide();
}
