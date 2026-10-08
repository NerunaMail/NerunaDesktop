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
