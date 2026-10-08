using Avalonia;
using Avalonia.Controls;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Lets a page remember the widths of its resizable columns (the grid named "Columns").</summary>
internal static class ColumnMemory
{
    /// <summary>Call from the view's constructor; restores once the view is in the main window.</summary>
    public static void Attach(UserControl view, string name, params int[] columns)
    {
        ArgumentNullException.ThrowIfNull(view);
        var attached = false;
        view.AttachedToVisualTree += (_, _) =>
        {
            if (attached || TopLevel.GetTopLevel(view)?.DataContext is not MainWindowViewModel { Layout: var layout }
                || view.FindControl<Grid>("Columns") is not { } grid)
            {
                return;
            }

            attached = true;
            layout.TrackColumns(grid, name, columns);
        };
    }
}
