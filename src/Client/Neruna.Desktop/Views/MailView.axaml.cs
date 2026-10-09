using Neruna.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>
/// Mail page. Drag messages from the list onto a folder in the tree to move them there – several at once with
/// Ctrl/Shift-click, also into another account.
/// </summary>
internal sealed partial class MailView : UserControl
{
    private const double DragThreshold = 6;
    internal static readonly DataFormat<MessageItemViewModel[]> MessageFormat = DataFormat.CreateInProcessFormat<MessageItemViewModel[]>("neruna-messages");

    private PointerPressedEventArgs? _pressed;
    private MessageItemViewModel? _pressedMessage;
    private ListBoxItem? _pressedItem;

    // Pressing an already selected message keeps a multi-selection (to drag it); a click without drag then selects
    // just that message on release, like in a file manager.
    private bool _keptSelection;
    private Point _pressedAt;
    private bool _dragging;
    private TreeViewItem? _highlighted;

    public MailView()
    {
        InitializeComponent();
        ColumnMemory.Attach(this, "columns.mail", 0, 2);

        var list = this.FindControl<ListBox>("MessageList")!;
        list.AddHandler(PointerPressedEvent, OnListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.AddHandler(PointerMovedEvent, OnListPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, OnListPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Near the end of the list the next messages load by themselves.
        list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer viewer && viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height < 200
                && ViewModel is { } vm && vm.LoadMoreCommand.CanExecute(null))
            {
                vm.LoadMoreCommand.Execute(null);
            }
        });

        // Ctrl+E: into the quick search.
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
            {
                this.FindControl<TextBox>("QuickSearch")?.Focus();
                e.Handled = true;
            }
        };

        // Double-click opens the message in its own window; in "Entwürfe" it continues the draft.
        list.DoubleTapped += async (_, e) =>
        {
            if (ViewModel is not { } vm
                || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not MessageItemViewModel message)
            {
                return;
            }

            if (vm.IsDraftsFolder)
            {
                if (vm.EditDraftCommand.CanExecute(null))
                {
                    await vm.EditDraftCommand.ExecuteAsync(null);
                }
            }
            else
            {
                await vm.OpenInWindowAsync(message);
            }
        };
        list.SelectionChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                vm.SelectedMessages = list.SelectedItems?.OfType<MessageItemViewModel>().ToList() ?? [];
            }
        };

        var tree = this.FindControl<TreeView>("FolderTree")!;
        DragDrop.AddDragOverHandler(tree, OnTreeDragOver);
        DragDrop.AddDragLeaveHandler(tree, (_, _) => Highlight(null));
        DragDrop.AddDropHandler(tree, OnTreeDrop);
    }

    private MailViewModel? ViewModel => DataContext as MailViewModel;

    // Remember the press; the drag only starts after the pointer moved a few pixels, so a click still opens the mail.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _keptSelection = false;
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && item?.DataContext is MessageItemViewModel message)
        {
            _pressed = e;
            _pressedMessage = message;
            _pressedItem = item;
            _pressedAt = e.GetPosition(this);

            if (e.KeyModifiers == KeyModifiers.None && item.IsSelected && ViewModel?.SelectedMessages.Count > 1)
            {
                _keptSelection = true;
                e.Handled = true;
            }
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // A plain click on one of several selected messages: now select just that one.
        if (_keptSelection && _pressed is not null && _pressedItem is { } item && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            list.SelectedItems?.Clear();
            list.SelectedItem = item.DataContext;
        }

        _pressed = null;
        _keptSelection = false;
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressed is not { } pressed || _pressedMessage is not { } message)
        {
            return;
        }

        var delta = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _pressed = null;
        _keptSelection = false;
        _dragging = true;
        try
        {
            // The whole selection if the dragged message is part of it, otherwise just this message.
            var selected = ViewModel?.SelectedMessages ?? [];
            MessageItemViewModel[] dragged = selected.Contains(message) ? [.. selected] : [message];
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(MessageFormat, dragged));
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            Highlight(null);
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e)
    {
        var folder = FolderAt(e, out var item);
        var allowed = folder is not null && ViewModel?.CanMoveTo(folder) == true && e.DataTransfer.Contains(MessageFormat);
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(allowed ? item : null);
        e.Handled = true;
    }

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        Highlight(null);
        var folder = FolderAt(e, out _);
        var messages = e.DataTransfer.TryGetValue(MessageFormat);
        if (folder is null || messages is not { Length: > 0 } || ViewModel is not { } vm)
        {
            return;
        }

        e.Handled = true;
        await vm.MoveToFolderAsync(messages, folder);
    }

    private static MailFolderNode? FolderAt(DragEventArgs e, out TreeViewItem? item)
    {
        item = (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        return item?.DataContext as MailFolderNode ?? (item?.DataContext as FavoriteFolderNode)?.Target;
    }

    private void Highlight(TreeViewItem? item)
    {
        if (ReferenceEquals(item, _highlighted))
        {
            return;
        }

        _highlighted?.Classes.Remove("drop-target");
        _highlighted = item;
        _highlighted?.Classes.Add("drop-target");
    }
}
