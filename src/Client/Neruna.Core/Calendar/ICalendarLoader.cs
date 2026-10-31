using Ical.Net.DataTypes;

namespace Neruna.Core.Calendar;

/// <summary>Loads iCalendar data and repairs what other programs write but Ical.Net refuses to evaluate.</summary>
internal static class ICalendarLoader
{
    public static Ical.Net.Calendar? Load(string data)
    {
        var calendar = Ical.Net.Calendar.Load(data);
        if (calendar is null)
        {
            return null;
        }

        // All-day events with an exact duration ("DURATION:PT24H" on a DATE start, e.g. old birthdays from SOGo): RFC 5545
        // wants days there, and Ical.Net throws on every occurrence.
        // The same for tasks. An all-day event ending at a time ("DTEND:20130831T000000") ends on that day.
        foreach (var evt in calendar.Events)
        {
            if (evt.DtStart is { HasTime: false } start)
            {
                if (evt.DtEnd is null && evt.Duration is { HasTime: true } duration)
                {
                    evt.Duration = InDays(duration);
                }
                else if (evt.DtEnd is { HasTime: true } end)
                {
                    evt.DtEnd = new CalDateTime(end.Date > start.Date ? end.Date : start.Date.AddDays(1));
                }
            }
        }

        foreach (var todo in calendar.Todos)
        {
            if (todo.DtStart is { HasTime: false } && todo.Duration is { HasTime: true } duration)
            {
                todo.Duration = InDays(duration);
            }
        }

        return calendar;
    }

    // A day for each started 24 hours.
    private static Duration InDays(Duration duration) =>
        Duration.FromDays(Math.Max(1, (int)Math.Ceiling(Math.Abs(duration.ToTimeSpanUnspecified().TotalDays))));
}
