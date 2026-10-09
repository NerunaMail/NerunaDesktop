using Neruna.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Threading;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

internal sealed partial class CalendarView : UserControl
{
    private const int FirstVisibleHour = 7;
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _scrolledToMorning;

    public CalendarView()
    {
        InitializeComponent();
        ColumnMemory.Attach(this, "columns.calendar", 0);
        _nowTimer.Tick += (_, _) => (DataContext as CalendarViewModel)?.RefreshNow();

        // The time grid opens at 07:00 (start of the working day); afterwards the user's scroll position stays.
        var scroll = this.FindControl<ScrollViewer>("TimeScroll")!;
        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty && !_scrolledToMorning && scroll.Bounds.Height > 0 && scroll.Extent.Height > 0)
            {
                _scrolledToMorning = true;
                scroll.Offset = new Vector(0, (FirstVisibleHour - 0.25) * TimedEventItem.HourHeight);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _nowTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _nowTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // A swatch (or "Farbe vom Server") in a calendar's color flyout. The flyout content inherits the calendar as
    // data context; the swatch itself carries the color in Tag.
    // "Übernehmen" / "Originalname" in the colour/name flyout.
    private async void OnRename(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            await RenameAsync(control, reset: Equals(control.Tag, "reset"));
        }
    }

    private async void OnRenameKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && sender is Control control)
        {
            e.Handled = true;
            await RenameAsync(control, reset: false);
        }
    }

    private async Task RenameAsync(Control control, bool reset)
    {
        if (DataContext is not CalendarViewModel vm || control.DataContext is not CalendarListItem item)
        {
            return;
        }

        if (control.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        await vm.RenameAsync(item, reset ? null : item.EditName);
    }

    private async void OnColorPicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button swatch || DataContext is not CalendarViewModel vm)
        {
            return;
        }

        var item = swatch.GetLogicalAncestors().OfType<Control>().Select(c => c.DataContext).OfType<CalendarListItem>().FirstOrDefault();
        if (swatch.FindLogicalAncestorOfType<Popup>() is { } popup)
        {
            popup.IsOpen = false;
        }

        if (item is not null)
        {
            await vm.SetColorAsync(item, swatch.Tag as string);
        }
    }

    // Double-click on a free spot: new event at that time, rounded down to the grid (60/30/15 minutes).
    private void OnTimeGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: CalendarDay day } column || DataContext is not CalendarViewModel vm)
        {
            return;
        }

        var slot = Math.Clamp(vm.GridMinutes, 15, 60);
        var minutes = Math.Clamp(e.GetPosition(column).Y / TimedEventItem.HourHeight * 60, 0, 24 * 60 - slot);
        var start = day.Date.AddMinutes(Math.Floor(minutes / slot) * slot);
        vm.NewEventAtCommand.Execute(start);
        e.Handled = true;
    }
}
