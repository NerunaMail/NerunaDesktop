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
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _scrolledToMorning;

    public CalendarView()
    {
        InitializeComponent();
        ColumnMemory.Attach(this, "columns.calendar", 0);
        _nowTimer.Tick += (_, _) => (DataContext as CalendarViewModel)?.RefreshNow();

        // Ctrl+E: into "Termine suchen" (as in the mail list).
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
            {
                this.FindControl<TextBox>("EventSearch")?.Focus();
                e.Handled = true;
            }
        };

        // The chosen hours (Einstellungen → Kalender, 07:00–21:00) fill the view; the time grid opens at their start.
        // Afterwards the user's scroll position stays – also when the window is resized.
        var scroll = this.FindControl<ScrollViewer>("TimeScroll")!;
        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty && scroll.Bounds.Height > 0)
            {
                Fit(scroll, toStart: !_scrolledToMorning);
            }
            else if (e.Property == ScrollViewer.ExtentProperty)
            {
                ScrollToPending(scroll);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CalendarViewModel calendar)
            {
                calendar.VisibleRangeChanged += (_, _) => Fit(scroll, toStart: true);
                Fit(scroll, toStart: !_scrolledToMorning);
            }
        };
    }

    private async void Fit(ScrollViewer scroll, bool toStart)
    {
        if (DataContext is not CalendarViewModel calendar || scroll.Bounds.Height <= 0)
        {
            return;
        }

        var hours = Math.Max(1, calendar.VisibleTo - calendar.VisibleFrom);
        // A position not yet applied (the size changes several times while opening) wins over the current one.
        // A quarter hour of room above and below, so the first hour's label is not cut off.
        var top = toStart ? Math.Max(0, calendar.VisibleFrom - 0.25) : _pendingTop ?? scroll.Offset.Y / TimedEventItem.HourHeight;
        _scrolledToMorning = true;
        // Noted before the relayout: a resize arriving meanwhile keeps this target. Applied once the grid is tall
        // enough (the week may still be loading), else the offset would be cut to 0.
        _pendingTop = top;
        await calendar.SetHourHeightAsync(Math.Max(TimedEventItem.MinHourHeight, Math.Floor(scroll.Bounds.Height / (hours + 0.5))));
        Dispatcher.UIThread.Post(() => ScrollToPending(scroll), DispatcherPriority.Background);
    }

    private double? _pendingTop;

    private void ScrollToPending(ScrollViewer scroll)
    {
        if (_pendingTop is not { } top)
        {
            return;
        }

        var offset = top * TimedEventItem.HourHeight;
        if (scroll.Extent.Height - scroll.Viewport.Height + 0.5 < offset)
        {
            return; // not yet tall enough: again when the extent grows
        }

        scroll.Offset = new Vector(0, offset);
        _pendingTop = null;
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
    // Double-click on a free spot: new event at that time, rounded down to the grid (60/30/15 minutes).
    // Right-click on an event: open, join its meeting or copy the link, duplicate, delete. The menu is built once the
    // event is read (whether it has a meeting, whether its calendar may be changed).
    private async void OnEventContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: var data } control || DataContext is not CalendarViewModel vm
            || (data as CalendarEventItem ?? (data as TimedEventItem)?.Item) is not { } item)
        {
            return;
        }

        e.Handled = true;
        var (meetingUrl, canChange) = await vm.EventMenuAsync(item.Occurrence);
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = T("Öffnen"), Command = vm.EditEventCommand, CommandParameter = item });
        if (meetingUrl is not null && Uri.TryCreate(meetingUrl, UriKind.Absolute, out var join) && join.Scheme is "https" or "http")
        {
            menu.Items.Add(new Separator());
            var open = new MenuItem { Header = T("Teilnehmen") };
            open.Click += async (_, _) => await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(join);
            menu.Items.Add(open);
            var copy = new MenuItem { Header = T("Link kopieren") };
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, meetingUrl);
                }
            };
            menu.Items.Add(copy);
        }

        if (!item.Occurrence.IsTask)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = T("Duplizieren"), Command = vm.DuplicateEventCommand, CommandParameter = item });
            menu.Items.Add(new MenuItem { Header = T("Löschen"), Command = vm.DeleteEventCommand, CommandParameter = item, IsEnabled = canChange });
        }

        menu.Open(control);
    }

    // Planung: double-click on a block opens the event.
    private void OnPlanningBlockDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PlanningBlock { Occurrence: { } occurrence } } && DataContext is CalendarViewModel vm)
        {
            _ = vm.OpenOccurrenceAsync(occurrence);
            e.Handled = true;
        }
    }

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
