using Avalonia;
using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

/// <summary>
/// One day column of the time grid: places each <see cref="TimedEventItem"/> at its time (vertical) and in its
/// overlap column (horizontal). Items are the containers created by an ItemsControl.
/// </summary>
internal sealed class TimeGridPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (child.DataContext is TimedEventItem item)
            {
                var width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width * item.WidthFraction;
                child.Measure(new Size(Math.Max(0, width - 2), item.Height));
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, TimedEventItem.HourHeight * 24);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child.DataContext is TimedEventItem item)
            {
                var left = finalSize.Width * item.LeftFraction;
                var width = Math.Max(0, finalSize.Width * item.WidthFraction - 2);
                child.Arrange(new Rect(left, item.Top, width, item.Height));
            }
        }

        return finalSize;
    }
}
