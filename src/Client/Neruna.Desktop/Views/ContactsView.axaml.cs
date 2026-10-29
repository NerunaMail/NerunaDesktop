using Neruna.Desktop.Infrastructure;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

internal sealed partial class ContactsView : UserControl
{
    public ContactsView()
    {
        InitializeComponent();
        ColumnMemory.Attach(this, "columns.contacts", 0, 2);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.ItemsRebuilding -= OnItemsRebuilding;
                _vm.ItemsRebuilt -= OnItemsRebuilt;
            }

            _vm = DataContext as ContactsViewModel;
            if (_vm is not null)
            {
                _vm.ItemsRebuilding += OnItemsRebuilding;
                _vm.ItemsRebuilt += OnItemsRebuilt;
            }
        };
    }

    private ContactsViewModel? _vm;
    private Avalonia.Vector? _offset;

    // After saving a contact (or a sync) the list is built anew: it stays where it was scrolled to instead of jumping
    // to the top – as long as the selected contact is still in it.
    private void OnItemsRebuilding(object? sender, EventArgs e) => _offset = ContactList.Scroll?.Offset;

    private void OnItemsRebuilt(object? sender, bool selectionKept)
    {
        if (!selectionKept || _offset is not { } offset)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ContactList.Scroll is ScrollViewer scroll)
            {
                scroll.Offset = offset;
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // Double-click opens the editor.
    private void OnContactDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ContactsViewModel { EditCommand: var edit } && edit.CanExecute(null))
        {
            edit.Execute(null);
        }
    }

    // A swatch (or "Standardfarbe") in an address book's color flyout; the color is in Tag.
    private async void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button swatch || DataContext is not ContactsViewModel vm)
        {
            return;
        }

        var book = swatch.GetLogicalAncestors().OfType<Control>().Select(c => c.DataContext).OfType<AddressBookItem>().FirstOrDefault();
        if (swatch.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        if (book is not null)
        {
            await vm.SetColorAsync(book, swatch.Tag as string);
        }
    }
}
