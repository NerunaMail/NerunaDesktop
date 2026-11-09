using Avalonia;
using Avalonia.Controls;

namespace Neruna.Desktop.Controls;

/// <summary>
/// A toolbar of two parts side by side: the big buttons (first child) and the small tools (second child). The small
/// tools always keep room for at least <see cref="SmallMinWidth"/> and wrap among themselves; the big buttons take the
/// rest and only wrap among themselves when even that is too narrow. Neither part ever goes below the other.
/// </summary>
internal sealed class SplitToolbar : Panel
{
    public static readonly StyledProperty<double> SmallMinWidthProperty =
        AvaloniaProperty.Register<SplitToolbar, double>(nameof(SmallMinWidth), 96);

    static SplitToolbar() => AffectsMeasure<SplitToolbar>(SmallMinWidthProperty);

    /// <summary>The room the small tools keep in any case (three icons by default).</summary>
    public double SmallMinWidth
    {
        get => GetValue(SmallMinWidthProperty);
        set => SetValue(SmallMinWidthProperty, value);
    }

    private (Control? Big, Control? Small) Parts() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);

    protected override Size MeasureOverride(Size availableSize)
    {
        var (big, small) = Parts();
        if (big is null)
        {
            return default;
        }

        var width = availableSize.Width;
        var bigRoom = double.IsInfinity(width) ? width : Math.Max(0, width - (small is null ? 0 : SmallMinWidth));
        big.Measure(new Size(bigRoom, availableSize.Height));
        var smallRoom = double.IsInfinity(width) ? width : Math.Max(0, width - big.DesiredSize.Width);
        small?.Measure(new Size(smallRoom, availableSize.Height));
        var smallSize = small?.DesiredSize ?? default;
        return new Size(big.DesiredSize.Width + smallSize.Width, Math.Max(big.DesiredSize.Height, smallSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (big, small) = Parts();
        if (big is null)
        {
            return finalSize;
        }

        var bigWidth = Math.Min(big.DesiredSize.Width, finalSize.Width);
        big.Arrange(new Rect(0, 0, bigWidth, finalSize.Height));
        small?.Arrange(new Rect(bigWidth, 0, Math.Max(0, finalSize.Width - bigWidth), finalSize.Height));
        return finalSize;
    }
}
