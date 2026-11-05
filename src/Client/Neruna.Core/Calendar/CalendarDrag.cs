namespace Neruna.Core.Calendar;

/// <summary>What a drag in the time grid does: a new event, an event moved, or its start or end changed.</summary>
public enum CalendarDragKind
{
    Create,
    Move,
    ResizeStart,
    ResizeEnd,
}

/// <summary>
/// The times of a drag in the time grid (day, work week, week), snapped to the grid (60, 30 or 15 minutes).
/// The view only measures where the pointer was pressed and where it is now; the times come from here.
/// </summary>
public static class CalendarDrag
{
    /// <summary>A new event by dragging from <paramref name="from"/> to <paramref name="to"/> (either direction) on one day: at least one slot.</summary>
    /// <param name="day">The day (midnight) of the column.</param>
    /// <param name="from">Minutes after midnight where the pointer was pressed.</param>
    /// <param name="to">Minutes after midnight where it is now.</param>
    /// <param name="slot">Grid in minutes (15, 30 or 60).</param>
    public static (DateTime Start, DateTime End) Create(DateTime day, double from, double to, int slot)
    {
        slot = Slot(slot);
        var low = Math.Clamp(Math.Min(from, to), 0, 24 * 60);
        var high = Math.Clamp(Math.Max(from, to), 0, 24 * 60);
        var start = Math.Min(Math.Floor(low / slot) * slot, 24 * 60 - slot);
        var end = Math.Max(Math.Ceiling(high / slot) * slot, start + slot);
        return (day.Date.AddMinutes(start), day.Date.AddMinutes(end));
    }

    /// <summary>
    /// The new times of an event dragged by <paramref name="minutes"/> and <paramref name="days"/>. Moving keeps the length
    /// and moves by whole slots (an event at 9:10 lands at 9:40 with a 30-minute grid); resizing puts the dragged edge
    /// on the grid and keeps at least one slot.
    /// </summary>
    public static (DateTime Start, DateTime End) Change(CalendarDragKind kind, DateTime start, DateTime end, double minutes, int days, int slot)
    {
        slot = Slot(slot);
        switch (kind)
        {
            case CalendarDragKind.Move:
                var offset = TimeSpan.FromDays(days) + TimeSpan.FromMinutes(Math.Round(minutes / slot) * slot);
                return (start + offset, end + offset);

            case CalendarDragKind.ResizeStart:
                var newStart = SnapToGrid(start.AddMinutes(minutes), slot);
                var latestStart = end.AddMinutes(-slot);
                return (newStart > latestStart ? latestStart : newStart, end);

            case CalendarDragKind.ResizeEnd:
                var newEnd = SnapToGrid(end.AddMinutes(minutes), slot);
                var earliestEnd = start.AddMinutes(slot);
                return (start, newEnd < earliestEnd ? earliestEnd : newEnd);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    private static DateTime SnapToGrid(DateTime time, int slot)
    {
        var minutes = Math.Round(time.TimeOfDay.TotalMinutes / slot) * slot;
        return time.Date.AddMinutes(minutes);
    }

    private static int Slot(int slot) => Math.Clamp(slot, 15, 60);
}
