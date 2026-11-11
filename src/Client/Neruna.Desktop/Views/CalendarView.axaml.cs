using Neruna.Desktop.Infrastructure;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Neruna.Core.Calendar;
using Neruna.Desktop.ViewModels;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

internal sealed partial class CalendarView : UserControl
{
    private readonly DispatcherTimer _nowTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private bool _scrolledToMorning;

    // A pick in the navigator: the day pressed, the days there were before (Ctrl adds to them), the day under the pointer.
    private sealed class DayPick
    {
        public required DateTime Anchor { get; init; }

        public required IReadOnlyCollection<DateTime> Before { get; init; }

        public required bool Adding { get; init; }

        public DateTime Current { get; set; }

        public bool Moved { get; set; }

        public HashSet<DateTime> Days()
        {
            var (from, to) = Anchor <= Current ? (Anchor, Current) : (Current, Anchor);
            var range = Enumerable.Range(0, (int)(to - from).TotalDays + 1).Select(i => from.AddDays(i));
            return Adding ? [.. Before, .. range] : [.. range];
        }
    }

    private DayPick? _dayPick;

    private static MiniDay? DayAt(IInputElement root, Point point) =>
        (root.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<MiniDay>().FirstOrDefault();

    private void OnNavigatorPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control navigator || DataContext is not CalendarViewModel vm || !e.GetCurrentPoint(navigator).Properties.IsLeftButtonPressed
            || DayAt(navigator, e.GetPosition(navigator)) is not { } day)
        {
            return;
        }

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        _dayPick = new DayPick { Anchor = day.Date, Current = day.Date, Adding = ctrl, Before = [.. vm.ShownDates] };
        if (ctrl)
        {
            // Ctrl: the button must not show the week; the navigator follows the pointer itself.
            e.Handled = true;
            e.Pointer.Capture(navigator);
        }
    }

    private void OnNavigatorMoved(object? sender, PointerEventArgs e)
    {
        if (_dayPick is not { } pick || sender is not Control navigator || DataContext is not CalendarViewModel vm
            || DayAt(navigator, e.GetPosition(navigator)) is not { } day || day.Date == pick.Current)
        {
            return;
        }

        pick.Current = day.Date;
        pick.Moved = true;
        vm.PreviewChosenDays(pick.Days());
    }

    private void OnNavigatorReleased(object? sender, PointerReleasedEventArgs e) => EndDayPick(commit: true);

    private void EndDayPick(bool commit)
    {
        if (_dayPick is not { } pick || DataContext is not CalendarViewModel vm)
        {
            return;
        }

        _dayPick = null;
        if (!commit)
        {
            vm.PreviewChosenDays(null);
        }
        else if (pick.Adding && !pick.Moved)
        {
            // Ctrl+click: the day in or out (the last one stays).
            var days = new HashSet<DateTime>(pick.Before);
            if (!days.Remove(pick.Anchor))
            {
                days.Add(pick.Anchor);
            }

            _ = vm.ShowChosenDaysAsync(days.Count > 0 ? days : pick.Before);
        }
        else if (pick.Moved)
        {
            _ = vm.ShowChosenDaysAsync(pick.Days());
        }

        // A plain click (no move, no Ctrl): the day's button shows its week as before.
    }

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
            else if (e.Key == Key.Escape && _drag is { IsActive: true })
            {
                CancelDrag();
                e.Handled = true;
            }
        };

        // The navigator: drag over days (also into the second month) or Ctrl+click to show just those days.
        var navigator = this.FindControl<ItemsControl>("Navigator")!;
        navigator.AddHandler(PointerPressedEvent, OnNavigatorPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        navigator.AddHandler(PointerMovedEvent, OnNavigatorMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        navigator.AddHandler(PointerReleasedEvent, OnNavigatorReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        navigator.AddHandler(PointerCaptureLostEvent, (_, _) => EndDayPick(commit: false));

        // Drag in the time grid: tunnelling, so a press on an event is seen before its button takes the pointer.
        // The button keeps working for a plain click; once the pointer has moved a few pixels, the grid takes it over.
        var grid = this.FindControl<Panel>("TimeGrid")!;
        grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        grid.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (_drag is { IsActive: true })
            {
                CancelDrag();
            }
        });

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

    // A click on a calendar's row chooses it for new events – not one on its tick, colour or "only this" button.
    private void OnCalendarTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: CalendarListItem item } && DataContext is CalendarViewModel vm
            && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null
            && (e.Source as Visual)?.FindAncestorOfType<ToggleButton>(includeSelf: true) is null)
        {
            vm.SelectCalendarCommand.Execute(item);
        }
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
            menu.Items.Add(new MenuItem { Header = T("Notiz zum Termin"), Command = vm.NewNoteCommand, CommandParameter = item });
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

    // ---- Drag in the time grid ------------------------------------------------------------------------------------

    /// <summary>A press in the time grid that may become a drag (after a few pixels).</summary>
    private sealed class DragState(CalendarDragKind kind, IPointer pointer, Point origin, int originDay, TimedEventItem? target, Control? targetControl)
    {
        public CalendarDragKind Kind { get; } = kind;
        public IPointer Pointer { get; } = pointer;
        public Point Origin { get; } = origin;
        public int OriginDay { get; } = originDay;
        public TimedEventItem? Target { get; } = target;
        public Control? TargetControl { get; } = targetControl;
        public bool IsActive { get; set; }
        public (DateTime Start, DateTime End) Times { get; set; }
    }

    private const double DragThreshold = 4;
    private DragState? _drag;
    private bool _dragSaving;

    private Panel TimeGridPanel => this.FindControl<Panel>("TimeGrid")!;

    private int DayAt(double x)
    {
        var count = (DataContext as CalendarViewModel)?.Days.Count ?? 0;
        var width = TimeGridPanel.Bounds.Width;
        return count == 0 || width <= 0 ? 0 : Math.Clamp((int)(x / (width / count)), 0, count - 1);
    }

    private static double MinutesAt(double y) => Math.Clamp(y / TimedEventItem.HourHeight * 60, 0, 24 * 60);

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var grid = TimeGridPanel;
        if (_dragSaving || DataContext is not CalendarViewModel { Days.Count: > 0 } || e.ClickCount != 1
            || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed || e.KeyModifiers != KeyModifiers.None)
        {
            _drag = null;
            return;
        }

        // What was hit: an edge of an event, an event, or free time.
        var kind = CalendarDragKind.Create;
        TimedEventItem? target = null;
        Control? targetControl = null;
        for (var visual = e.Source as Visual; visual is not null && visual != grid; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement element && element.Classes.Contains("resize-start") && kind == CalendarDragKind.Create)
            {
                kind = CalendarDragKind.ResizeStart;
            }
            else if (visual is StyledElement edge && edge.Classes.Contains("resize-end") && kind == CalendarDragKind.Create)
            {
                kind = CalendarDragKind.ResizeEnd;
            }
            else if (visual is Button { DataContext: TimedEventItem timed } button)
            {
                target = timed;
                targetControl = button;
                break;
            }
            else if (visual is Button)
            {
                return; // another button: nothing to drag
            }
        }

        if (target is not null)
        {
            if (!target.CanChange)
            {
                _drag = null;
                return;
            }

            if (kind == CalendarDragKind.Create)
            {
                kind = CalendarDragKind.Move;
            }
        }

        var origin = e.GetPosition(grid);
        _drag = new DragState(kind, e.Pointer, origin, DayAt(origin.X), target, targetControl);
    }

    private void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is not { } drag || drag.Pointer != e.Pointer || DataContext is not CalendarViewModel vm)
        {
            return;
        }

        var grid = TimeGridPanel;
        var position = e.GetPosition(grid);
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
        {
            // Released outside the grid: that press is over.
            CancelDrag();
            return;
        }

        if (!drag.IsActive)
        {
            var moved = position - drag.Origin;
            if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
            {
                return;
            }

            drag.IsActive = true;
            e.Pointer.Capture(grid); // the event's button lets go: no click
            if (drag.Kind == CalendarDragKind.Move && drag.TargetControl is { } control)
            {
                control.Opacity = 0.4;
            }
        }

        AutoScroll(e);
        // Always to the quarter hour, whatever the grid shows (60, 30 or 15 minutes).
        const int slot = 15;
        var minutes = MinutesAt(position.Y);
        int column;
        if (drag.Kind == CalendarDragKind.Create)
        {
            column = drag.OriginDay;
            drag.Times = CalendarDrag.Create(vm.Days[column].Date, MinutesAt(drag.Origin.Y), minutes, slot);
        }
        else
        {
            var occurrence = drag.Target!.Item.Occurrence;
            column = drag.Kind == CalendarDragKind.Move ? DayAt(position.X) : drag.OriginDay;
            // Days between the two columns' dates, not columns: days picked in the mini calendar (1 and 10 December)
            // need not follow each other.
            var days = (vm.Days[column].Date.Date - vm.Days[drag.OriginDay].Date.Date).Days;
            drag.Times = CalendarDrag.Change(drag.Kind, occurrence.Start.LocalDateTime, occurrence.End.LocalDateTime,
                minutes - MinutesAt(drag.Origin.Y), days, slot);
        }

        ShowPreview(vm, drag, column);
        e.Handled = true;
    }

    private async void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || drag.Pointer != e.Pointer)
        {
            return;
        }

        _drag = null;
        if (!drag.IsActive || DataContext is not CalendarViewModel vm)
        {
            return; // a plain click: the event's button opens it, a double-click on free time a new event
        }

        e.Handled = true;
        e.Pointer.Capture(null);
        var (start, end) = drag.Times;
        if (drag.Kind == CalendarDragKind.Create)
        {
            HidePreview(drag);
            await vm.NewEventInRangeAsync(start, end);
            return;
        }

        // The preview stays where the event was put until it is saved and the week reloaded.
        _dragSaving = true;
        try
        {
            await vm.ChangeEventTimeAsync(drag.Target!.Item, start, end);
        }
        finally
        {
            _dragSaving = false;
            HidePreview(drag);
        }
    }

    private void CancelDrag()
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        HidePreview(drag);
        if (drag.Pointer.Captured is not null)
        {
            drag.Pointer.Capture(null);
        }
    }

    // Near the top or bottom edge of the visible hours, the grid scrolls along.
    private void AutoScroll(PointerEventArgs e)
    {
        var scroll = this.FindControl<ScrollViewer>("TimeScroll")!;
        var y = e.GetPosition(scroll).Y;
        var step = TimedEventItem.HourHeight / 4;
        var delta = y < 16 ? -step : y > scroll.Viewport.Height - 16 ? step : 0;
        if (delta != 0)
        {
            var max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, max));
        }
    }

    private void ShowPreview(CalendarViewModel vm, DragState drag, int column)
    {
        var preview = this.FindControl<Border>("DragPreview")!;
        var text = this.FindControl<TextBlock>("DragPreviewText")!;
        var width = TimeGridPanel.Bounds.Width / vm.Days.Count;
        var (start, end) = drag.Times;
        var day = vm.Days[column].Date;
        // Of an event reaching into another day, the part on the column's day.
        var from = start < day ? day : start;
        var to = end > day.AddDays(1) ? day.AddDays(1) : end;
        var top = Math.Max(0, (from - day).TotalHours * TimedEventItem.HourHeight);
        var height = Math.Max(18, (to - from).TotalHours * TimedEventItem.HourHeight);

        if (drag.Target is { } target)
        {
            preview.Background = target.Item.Background;
            preview.BorderBrush = target.Item.Brush;
            text.Text = $"{target.Item.Title}\n{TimeRange(start, end, drag.Kind == CalendarDragKind.Move && start.Date != target.Item.Occurrence.Start.LocalDateTime.Date)}";
        }
        else
        {
            var accent = this.TryFindResource("AccentTextBrush", ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid
                ? solid.Color : Colors.SteelBlue;
            preview.Background = new SolidColorBrush(accent, 0.2);
            preview.BorderBrush = new SolidColorBrush(accent);
            text.Text = TimeRange(start, end, false);
        }

        Canvas.SetLeft(preview, column * width + 1);
        Canvas.SetTop(preview, top + 1);
        preview.Width = Math.Max(0, width - 3);
        preview.Height = height - 1;
        preview.IsVisible = true;
    }

    private static string TimeRange(DateTime start, DateTime end, bool withDay) =>
        (withDay ? start.ToString("ddd d.M. ", System.Globalization.CultureInfo.CurrentCulture) : string.Empty)
        + $"{start:HH:mm}–{(end.Date > start.Date && end.TimeOfDay == TimeSpan.Zero ? "24:00" : end.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))}";

    private void HidePreview(DragState drag)
    {
        this.FindControl<Border>("DragPreview")!.IsVisible = false;
        if (drag.TargetControl is { } control)
        {
            control.Opacity = 1;
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
