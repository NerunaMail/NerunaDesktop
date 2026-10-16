using Avalonia;
using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

/// <summary>
/// One row of the planning view: places each <see cref="PlanningBlock"/> along the time axis (fractions of the width).
/// Items are the containers created by an ItemsControl.
/// </summary>
internal sealed class PlanningPanel : Panel
{
    public const double RowHeight = 26;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        foreach (var child in Children)
        {
            if (child.DataContext is PlanningBlock block)
            {
                child.Measure(new Size(Math.Max(2, width * block.Length), RowHeight));
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, RowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child.DataContext is PlanningBlock block)
            {
                // At least 3 px, so a short event stays visible.
                child.Arrange(new Rect(finalSize.Width * block.Start, 0, Math.Max(3, finalSize.Width * block.Length - 1), RowHeight));
            }
        }

        return finalSize;
    }
}
