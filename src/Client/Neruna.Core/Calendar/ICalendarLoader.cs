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
        // wants days there, and Ical.Net throws on every occurrence. A day for each started 24 hours.
        foreach (var evt in calendar.Events)
        {
            if (evt.DtStart is { HasTime: false } && evt.Duration is { HasTime: true } duration)
            {
                var days = (int)Math.Ceiling(Math.Abs(duration.ToTimeSpanUnspecified().TotalDays));
                evt.Duration = Duration.FromDays(Math.Max(1, days));
            }
        }

        return calendar;
    }
}
