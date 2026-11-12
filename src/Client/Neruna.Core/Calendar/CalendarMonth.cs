namespace Neruna.Core.Calendar;

/// <summary>The month view's days: whole weeks (Monday to Sunday) around one month.</summary>
public static class CalendarMonth
{
    /// <summary>From the Monday on or before the 1st of <paramref name="anyDay"/>'s month to the Sunday on or after its last day (4 to 6 weeks).</summary>
    public static IReadOnlyList<DateTime> Dates(DateTime anyDay)
    {
        var month = new DateTime(anyDay.Year, anyDay.Month, 1);
        var start = StartOfWeek(month);
        var end = StartOfWeek(month.AddMonths(1).AddDays(-1)).AddDays(7);
        return [.. Enumerable.Range(0, (end - start).Days).Select(i => start.AddDays(i))];
    }

    private static DateTime StartOfWeek(DateTime date) => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
