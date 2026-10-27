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
/// Ctrl/Shift-click, also into another account – or into a mail being written to attach them.
/// </summary>
internal sealed partial class MailView : UserControl
{
    private const double DragThreshold = 6;
    internal static readonly DataFormat<MessageItemViewModel[]> MessageFormat = DataFormat.CreateInProcessFormat<MessageItemViewModel[]>("neruna-messages");

    /// <summary>The dragged messages' content for a mail being written: loaded only when they are dropped there.</summary>
    internal static readonly DataFormat<Func<Task<IReadOnlyList<MimeKit.MimeMessage>>>> MessageContentFormat =
        DataFormat.CreateInProcessFormat<Func<Task<IReadOnlyList<MimeKit.MimeMessage>>>>("neruna-message-content");

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
            else if (!e.Handled && ViewModel is { } vm && Infrastructure.Shortcuts.Of(e) is { } shortcut && vm.RunQuickActionShortcut(shortcut))
            {
                e.Handled = true; // a quick action's own key combination (Einstellungen → Schnellaktionen)
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

            Avalonia.Threading.Dispatcher.UIThread.Post(() => KeepListFocus(list), Avalonia.Threading.DispatcherPriority.Background);
        };

        // Table: the column heads take the kept widths; after a border was dragged, the new ones are kept.
        var header = this.FindControl<Grid>("TableHeader")!;
        MailListLayout? layout = null;

        // Reading pane beside the list, below it (the list takes the width; its height is kept), or none.
        var columns = this.FindControl<Grid>("Columns")!;
        var listPane = this.FindControl<Border>("ListPane")!;
        var readingPane = this.FindControl<Border>("ReadingPaneBox")!;
        var paneSplitter = this.FindControl<GridSplitter>("PaneSplitter")!;
        var rowSplitter = this.FindControl<GridSplitter>("RowSplitter")!;
        void PlacePane()
        {
            var position = layout?.Pane ?? ReadingPanePosition.Right;
            var below = position == ReadingPanePosition.Below;
            var wide = position != ReadingPanePosition.Right;
            Grid.SetColumnSpan(listPane, wide ? 3 : 1);
            Grid.SetRowSpan(listPane, below ? 1 : 3);
            Grid.SetRow(readingPane, below ? 2 : 0);
            Grid.SetColumn(readingPane, below ? 2 : 4);
            Grid.SetColumnSpan(readingPane, below ? 3 : 1);
            Grid.SetRowSpan(readingPane, below ? 1 : 3);
            readingPane.IsVisible = position != ReadingPanePosition.None;
            paneSplitter.IsVisible = position == ReadingPanePosition.Right;
            rowSplitter.IsVisible = below;
            columns.RowDefinitions[0].Height = below && layout is not null ? new GridLength(layout.ListHeight) : new GridLength(1, GridUnitType.Star);
            columns.RowDefinitions[1].Height = new GridLength(below ? 6 : 0);
            columns.RowDefinitions[2].Height = below ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        rowSplitter.DragCompleted += (_, _) => layout?.SetListHeight(columns.RowDefinitions[0].ActualHeight);
        void ApplyWidths()
        {
            if (layout is not null)
            {
                header.ColumnDefinitions[1].Width = new GridLength(layout.FromWidth);
                header.ColumnDefinitions[5].Width = new GridLength(layout.DateWidth);
                header.ColumnDefinitions[7].Width = new GridLength(layout.SizeWidth);
            }
        }

        // One listener on the current layout: the DataContext changes (and is null for a moment) when switching pages.
        void OnLayoutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender != layout)
            {
                return;
            }

            if (e.PropertyName is nameof(MailListLayout.FromWidth) or nameof(MailListLayout.DateWidth) or nameof(MailListLayout.SizeWidth))
            {
                ApplyWidths();
            }
            else if (e.PropertyName == nameof(MailListLayout.IsTable) && layout.IsTable)
            {
                WidenListForTable();
            }
            else if (e.PropertyName is nameof(MailListLayout.Pane) or nameof(MailListLayout.ListHeight))
            {
                PlacePane();
            }
        }

        DataContextChanged += (_, _) =>
        {
            if (ViewModel?.ListLayout == layout)
            {
                return;
            }

            if (layout is not null)
            {
                layout.PropertyChanged -= OnLayoutChanged;
            }

            layout = ViewModel?.ListLayout;
            if (layout is not null)
            {
                layout.PropertyChanged += OnLayoutChanged;
                ApplyWidths();
                PlacePane();
            }
        };

        // The table needs room for its columns: the list becomes wider (kept like any width the user drags).
        void WidenListForTable()
        {
            if (this.FindControl<Grid>("Columns") is { } columns && columns.ColumnDefinitions[2].Width is { IsAbsolute: true, Value: < 640 })
            {
                columns.ColumnDefinitions[2].Width = new GridLength(680);
            }
        }

        foreach (var splitter in header.Children.OfType<GridSplitter>())
        {
            splitter.DragCompleted += (_, _) => layout?.SetWidths(
                header.ColumnDefinitions[1].ActualWidth, header.ColumnDefinitions[5].ActualWidth, header.ColumnDefinitions[7].ActualWidth);
        }

        foreach (var tree in new[] { this.FindControl<TreeView>("FolderTree")!, this.FindControl<TreeView>("FavoritesTree")! })
        {
            DragDrop.AddDragOverHandler(tree, OnTreeDragOver);
            DragDrop.AddDragLeaveHandler(tree, (_, e) =>
            {
                // DragLeave also comes when the pointer only moves from one part of a row to another (icon, name):
                // the highlight goes only when the pointer has really left the tree.
                if (!new Rect(tree.Bounds.Size).Contains(e.GetPosition(tree)))
                {
                    Highlight(null);
                }
            });
            DragDrop.AddDropHandler(tree, OnTreeDrop);
        }
    }

    private MailViewModel? ViewModel => DataContext as MailViewModel;

    // Deleting or moving removes the focused message from the list, and the focus would end up nowhere: the next
    // Delete would do nothing until the next message is clicked. The newly selected message takes the focus over.
    private void KeepListFocus(ListBox list)
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var lost = focused is null or TopLevel || !focused.IsAttachedToVisualTree();
        if ((!lost && focused?.FindAncestorOfType<ListBox>(includeSelf: true) != list) || list.SelectedItem is not { } selected)
        {
            return;
        }

        list.ScrollIntoView(selected);
        if (list.ContainerFromItem(selected) is ListBoxItem item && !item.IsFocused)
        {
            item.Focus(NavigationMethod.Directional);
        }
    }

    // Remember the press; the drag only starts after the pointer moved a few pixels, so a click still opens the mail.
    // Right click on a mail: the "Schlagwörter" submenu gets each tag (ticked when all selected mails have it), then "Verwalten …".
    private async void OnMessageMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || DataContext is not MailViewModel vm
            || menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == "tags") is not { } tagsItem)
        {
            return;
        }

        var manage = tagsItem.Items.OfType<MenuItem>().LastOrDefault();
        var (tags, supported) = await vm.TagMenuAsync();
        tagsItem.Items.Clear();
        foreach (var (tag, isSet) in tags)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Background = Converters.ColorBrush.Convert(tag.Color, typeof(Avalonia.Media.IBrush), null, System.Globalization.CultureInfo.InvariantCulture) as Avalonia.Media.IBrush,
                Child = isSet ? new TextBlock { Text = "✓", Foreground = Avalonia.Media.Brushes.White, FontSize = 11, FontWeight = Avalonia.Media.FontWeight.Bold,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } : null,
            };
            tagsItem.Items.Add(new MenuItem { Header = tag.Name, Icon = swatch, IsEnabled = supported, Command = vm.ToggleTagCommand, CommandParameter = tag });
        }

        if (!supported)
        {
            tagsItem.Items.Add(new MenuItem { Header = T("Dieses Konto speichert keine Schlagwörter."), IsEnabled = false });
        }

        if (tags.Count > 0)
        {
            tagsItem.Items.Add(new Separator());
        }

        tagsItem.Items.Add(manage ?? new MenuItem { Header = T("Verwalten …"), Command = vm.ManageTagsCommand });
    }

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
            if (ViewModel is { } vm)
            {
                data.Add(DataTransferItem.Create(MessageContentFormat, () => vm.LoadMessagesAsync(dragged)));
            }

            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Move | DragDropEffects.Copy);
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
