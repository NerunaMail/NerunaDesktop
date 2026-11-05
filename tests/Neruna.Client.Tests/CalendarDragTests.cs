using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

/// <summary>Dragging in the time grid: new events, moving, and changing start or end – always on the grid.</summary>
public class CalendarDragTests
{
    private static readonly DateTime Day = new(2026, 11, 4);

    private static DateTime At(int hour, int minute = 0, int days = 0) => Day.AddDays(days).AddHours(hour).AddMinutes(minute);

    [Fact]
    public void Dragging_from_nine_to_half_past_ten_gives_that_time()
    {
        Assert.Equal((At(9), At(10, 30)), CalendarDrag.Create(Day, 9 * 60 + 5, 10 * 60 + 20, 30));
    }

    [Fact]
    public void Dragging_upwards_gives_the_same_time()
    {
        Assert.Equal((At(9), At(10, 30)), CalendarDrag.Create(Day, 10 * 60 + 20, 9 * 60 + 5, 30));
    }

    [Fact]
    public void A_short_drag_gives_at_least_one_slot()
    {
        Assert.Equal((At(14), At(14, 15)), CalendarDrag.Create(Day, 14 * 60 + 2, 14 * 60 + 2, 15));
        Assert.Equal((At(23), At(0, 0, 1)), CalendarDrag.Create(Day, 24 * 60, 24 * 60, 60));
    }

    [Fact]
    public void Moving_keeps_the_length_and_moves_by_whole_slots()
    {
        // 9:10–10:00, dragged down by 40 minutes with a 30-minute grid: one slot.
        Assert.Equal((At(9, 40), At(10, 30)), CalendarDrag.Change(CalendarDragKind.Move, At(9, 10), At(10), 40, 0, 30));
    }

    [Fact]
    public void Moving_to_another_day_keeps_the_time()
    {
        Assert.Equal((At(9, 0, 2), At(10, 30, 2)), CalendarDrag.Change(CalendarDragKind.Move, At(9), At(10, 30), 5, 2, 15));
        Assert.Equal((At(8, 0, -1), At(9, 30, -1)), CalendarDrag.Change(CalendarDragKind.Move, At(9), At(10, 30), -60, -1, 30));
    }

    [Fact]
    public void Dragging_the_top_edge_changes_the_start_on_the_grid()
    {
        Assert.Equal((At(8, 30), At(10)), CalendarDrag.Change(CalendarDragKind.ResizeStart, At(9), At(10), -35, 0, 30));
    }

    [Fact]
    public void Dragging_the_bottom_edge_changes_the_end_on_the_grid()
    {
        Assert.Equal((At(9), At(11, 15)), CalendarDrag.Change(CalendarDragKind.ResizeEnd, At(9), At(10), 80, 0, 15));
    }

    [Fact]
    public void Start_and_end_keep_at_least_one_slot_apart()
    {
        Assert.Equal((At(9, 30), At(10)), CalendarDrag.Change(CalendarDragKind.ResizeStart, At(9), At(10), 120, 0, 30));
        Assert.Equal((At(9), At(9, 15)), CalendarDrag.Change(CalendarDragKind.ResizeEnd, At(9), At(10), -120, 0, 15));
    }
}
