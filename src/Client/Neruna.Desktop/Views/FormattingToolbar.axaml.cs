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
    private void OnTextColorPicked(object? sender, RoutedEventArgs e) => this.FindControl<SplitButton>("TextColorButton")?.Flyout?.Hide();

    private void OnHighlightPicked(object? sender, RoutedEventArgs e) => this.FindControl<SplitButton>("HighlightButton")?.Flyout?.Hide();
}
