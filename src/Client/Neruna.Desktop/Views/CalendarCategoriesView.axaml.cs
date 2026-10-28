using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

public partial class CalendarCategoriesView : UserControl
{
    public CalendarCategoriesView()
    {
        InitializeComponent();
    }

    private void OnColorPicked(object? sender, RoutedEventArgs e)
    {
        // The flyout's content takes the row as DataContext from the colour button it opens from.
        if (sender is Button { Tag: string color } swatch && swatch.FindLogicalAncestorOfType<ItemsControl>() is { DataContext: CalendarCategoryRow row })
        {
            row.Color = color;
            swatch.FindLogicalAncestorOfType<Avalonia.Controls.Primitives.Popup>()?.Close();
        }
    }

    private async void OnNameLeft(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: CalendarCategoryRow row })
        {
            await row.CommitNameAsync();
        }
    }

    private async void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: CalendarCategoryRow row })
        {
            e.Handled = true;
            await row.CommitNameAsync();
        }
    }

    private void OnNewKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is CalendarCategoriesViewModel vm && vm.AddCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.AddCommand.Execute(null);
        }
    }
}
