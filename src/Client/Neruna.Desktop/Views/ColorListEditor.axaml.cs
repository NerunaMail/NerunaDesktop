using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Neruna.Desktop.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

/// <summary>Edits a <see cref="ColorListViewModel"/> (Schlagwörter, Kategorien).</summary>
internal sealed partial class ColorListEditor : UserControl
{
    public ColorListEditor()
    {
        InitializeComponent();
    }

    private void OnColorPicked(object? sender, ColorPickedEventArgs e)
    {
        // The palette takes the row as DataContext from the colour button its flyout opens from.
        if (sender is ColorPalette { DataContext: ColorListRow row } && e.Color is { } color)
        {
            row.Color = color;
        }
    }

    private async void OnNameLeft(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ColorListRow row })
        {
            await row.CommitNameAsync();
        }
    }

    private async void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox { DataContext: ColorListRow row })
        {
            e.Handled = true;
            await row.CommitNameAsync();
        }
    }

    private void OnNewKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ColorListViewModel vm && vm.AddCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.AddCommand.Execute(null);
        }
    }
}
