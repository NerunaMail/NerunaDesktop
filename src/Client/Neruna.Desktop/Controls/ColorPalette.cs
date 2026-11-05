using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Controls;

/// <summary>A chosen colour (null: the reset button, e.g. "Standardfarbe").</summary>
internal sealed class ColorPickedEventArgs(RoutedEvent routedEvent, string? color) : RoutedEventArgs(routedEvent)
{
    public string? Color { get; } = color;
}

/// <summary>
/// The colour choices of Neruna (calendars, address books, tags, categories) as swatches, with an optional reset
/// button below. A click raises <see cref="PickedEvent"/> and closes the flyout the palette sits in; the DataContext
/// is that of the button the flyout opens from.
/// </summary>
internal sealed class ColorPalette : StackPanel
{
    public static readonly RoutedEvent<ColorPickedEventArgs> PickedEvent =
        RoutedEvent.Register<ColorPalette, ColorPickedEventArgs>(nameof(Picked), RoutingStrategies.Bubble);

    /// <summary>Text of the reset button; none: no button.</summary>
    public static readonly StyledProperty<string?> ResetTextProperty = AvaloniaProperty.Register<ColorPalette, string?>(nameof(ResetText));

    /// <summary>Text above the swatches; none: no title.</summary>
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<ColorPalette, string?>(nameof(Title));

    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, FontSize = 12, IsVisible = false };
    private readonly Button _reset = new() { FontSize = 12, IsVisible = false };

    public ColorPalette()
    {
        Spacing = 8;
        Width = 176;
        var swatches = new WrapPanel();
        foreach (var color in CalendarViewModel.ColorChoices)
        {
            var swatch = new Button
            {
                Width = 20, Height = 20, MinWidth = 0, Padding = default, Margin = new Thickness(1),
                CornerRadius = new CornerRadius(3), BorderThickness = default, Background = SolidColorBrush.Parse(color),
            };
            swatch.Click += (_, e) => Pick(color, e);
            swatches.Children.Add(swatch);
        }

        _reset.Click += (_, e) => Pick(null, e);
        Children.Add(_title);
        Children.Add(swatches);
        Children.Add(_reset);
    }

    public event EventHandler<ColorPickedEventArgs> Picked
    {
        add => AddHandler(PickedEvent, value);
        remove => RemoveHandler(PickedEvent, value);
    }

    public string? ResetText
    {
        get => GetValue(ResetTextProperty);
        set => SetValue(ResetTextProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResetTextProperty)
        {
            _reset.Content = ResetText;
            _reset.IsVisible = ResetText is not null;
        }
        else if (change.Property == TitleProperty)
        {
            _title.Text = Title;
            _title.IsVisible = Title is not null;
        }
    }

    private void Pick(string? color, RoutedEventArgs click)
    {
        click.Handled = true;
        // Raised while the palette still has its DataContext; the handler's saving goes on after its first await.
        var popup = this.FindLogicalAncestorOfType<Popup>();
        RaiseEvent(new ColorPickedEventArgs(PickedEvent, color));
        popup?.Close();
    }
}
