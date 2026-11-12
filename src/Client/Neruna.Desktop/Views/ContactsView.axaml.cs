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
        // Enter or F2 in the list opens the selected contact for editing (as a double-click).
        ContactList.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F2 && e.KeyModifiers == KeyModifiers.None
                && DataContext is ContactsViewModel { EditCommand: var edit } && edit.CanExecute(null))
            {
                edit.Execute(null);
                e.Handled = true;
            }
        };
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

    // A colour (or "Standardfarbe") in an address book's colour flyout; the palette has the book as DataContext.
    private async void OnColorPicked(object? sender, Neruna.Desktop.Controls.ColorPickedEventArgs e)
    {
        if (sender is Neruna.Desktop.Controls.ColorPalette { DataContext: AddressBookItem book } && DataContext is ContactsViewModel vm)
        {
            await vm.SetColorAsync(book, e.Color);
        }
    }
}
