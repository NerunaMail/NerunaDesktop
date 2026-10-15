using Ical.Net.DataTypes;

namespace Neruna.Core.Calendar;

/// <summary>
/// When a calendar object can show up at all: from its first start to the end of its last occurrence (null: a series
/// without end). Worked out once when the object is stored, so the calendar views and the reminders only parse the
/// events of the time they look at – not every event of ten years, every few seconds.
/// </summary>
public static class CalendarSpan
{
    /// <summary>Not an event, or not readable here: always looked at (the views skip it as before).</summary>
    public static (long Start, long? End) Always { get; } = (long.MinValue, null);

    // Floating times and all-day dates are the wall clock wherever the user is: a day around them.
    private static readonly TimeSpan Slack = TimeSpan.FromDays(1);
    private const int MaxOccurrences = 100_000;

    /// <summary>Start and end in Unix milliseconds (UTC).</summary>
    public static (long Start, long? End) Of(string iCalendarData)
    {
        try
        {
            var ical = Ical.Net.Calendar.Load(iCalendarData);
            var events = ical?.Events.ToList() ?? [];
            if (ical is null || events.Count == 0 || events.Any(e => e.DtStart is null))
            {
                return Always;
            }

            var first = events.Min(e => CalendarController.ToDateTimeOffset(e.DtStart!)) - Slack;
            if (events.Any(e => e.RecurrenceRule is { Until: null, Count: null or <= 0 }))
            {
                return (first.ToUnixTimeMilliseconds(), null);
            }

            // Single events and series with UNTIL or COUNT: the end of the last occurrence.
            var last = first;
            var count = 0;
            foreach (var occurrence in ical.GetOccurrences(new CalDateTime(first.UtcDateTime, "UTC"), null))
            {
                if (++count > MaxOccurrences)
                {
                    return (first.ToUnixTimeMilliseconds(), null);
                }

                var start = CalendarController.ToDateTimeOffset(occurrence.Period.StartTime);
                var end = occurrence.Period.EffectiveEndTime is { } effectiveEnd ? CalendarController.ToDateTimeOffset(effectiveEnd) : start;
                last = end > last ? end : last;
            }

            return (first.ToUnixTimeMilliseconds(), (last + Slack).ToUnixTimeMilliseconds());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Always;
        }
    }
}
