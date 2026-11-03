using Avalonia.Controls;
using Avalonia.Interactivity;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>Font, size, emphasis, colors, lists, alignment, link, image – shared by compose and the signature editor.</summary>
internal sealed partial class FormattingToolbar : UserControl
{
    public FormattingToolbar()
    {
        InitializeComponent();
    }

    // Picking a swatch applies the color (via the command) and closes the palette.
    // Applied here, not by the swatch's Command: Click comes before the Command, and closing the palette detaches the
    // swatch – its $parent binding drops the Command before Avalonia runs it, so the color never changed.
    private void OnTextColorPicked(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: string color } && DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            formatting.ApplyTextColorCommand.Execute(color);
        }

        this.FindControl<SplitButton>("TextColorButton")?.Flyout?.Hide();
    }

    private void OnHighlightPicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is Editor.FormattingViewModel formatting)
        {
            e.Handled = true;
            // A swatch carries its color; "Keine Farbe" none.
            formatting.ApplyHighlightCommand.Execute((e.Source as Button)?.DataContext as string);
        }

        this.FindControl<SplitButton>("HighlightButton")?.Flyout?.Hide();
    }
}
