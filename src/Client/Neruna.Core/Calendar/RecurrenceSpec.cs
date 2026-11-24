using System.Globalization;
using Ical.Net;
using Ical.Net.DataTypes;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// A repetition as the editors offer it (for events and tasks alike): every n days, weeks, months or years; weekly on
/// chosen weekdays; monthly or yearly on the day of the start or on the n-th (or last) weekday; ending after a number
/// of times (<see cref="Count"/>) or – kept apart, see <see cref="EventDraft.RecurrenceUntil"/> – on a day. The same as
/// Microsoft 365 and Exchange can express, so nothing is lost on the way there. Other rules are left as they are.
/// </summary>
/// <param name="Interval">Every how many days, weeks, months or years (at least 1).</param>
public sealed record RecurrenceSpec(RecurrenceFrequency Frequency, int Interval = 1)
{
    /// <summary>Weekly: the weekdays (empty: the weekday of the start). Monthly/yearly with <see cref="Position"/>: the one weekday.</summary>
    public IReadOnlyList<DayOfWeek> Weekdays { get; init; } = [];

    /// <summary>Monthly/yearly: the n-th weekday of the month (1–4, -1 the last); null: the day of the month of the start.</summary>
    public int? Position { get; init; }

    /// <summary>Ends after this many times; null: no count (without end, or until a day).</summary>
    public int? Count { get; init; }

    public static RecurrenceFrequency? FrequencyOf(RecurrenceKind kind) => kind switch
    {
        RecurrenceKind.Daily => RecurrenceFrequency.Daily,
        RecurrenceKind.Weekly => RecurrenceFrequency.Weekly,
        RecurrenceKind.Monthly => RecurrenceFrequency.Monthly,
        RecurrenceKind.Yearly => RecurrenceFrequency.Yearly,
        _ => null,
    };

    public RecurrenceKind Kind => Frequency switch
    {
        RecurrenceFrequency.Daily => RecurrenceKind.Daily,
        RecurrenceFrequency.Weekly => RecurrenceKind.Weekly,
        RecurrenceFrequency.Monthly => RecurrenceKind.Monthly,
        _ => RecurrenceKind.Yearly,
    };

    /// <summary>The n-th weekday of the month <paramref name="date"/> falls on (1–5).</summary>
    public static int PositionOf(DateTime date) => (date.Day - 1) / 7 + 1;

    /// <summary>Whether <paramref name="date"/> is the last such weekday of its month.</summary>
    public static bool IsLastOfMonth(DateTime date) => date.AddDays(7).Month != date.Month;

    /// <summary>The rule as the editor shows it; null when it holds more than the editor offers (kept untouched then).</summary>
    public static RecurrenceSpec? Of(RecurrenceRule? rule, DateTime start)
    {
        if (rule is null || rule.ByHour.Count > 0 || rule.ByMinute.Count > 0 || rule.BySecond.Count > 0 || rule.ByYearDay.Count > 0
            || rule.ByWeekNo.Count > 0 || rule.BySetPosition.Count > 1)
        {
            return null;
        }

        var interval = Math.Max(1, rule.Interval);
        var count = rule.Count is > 0 ? rule.Count : null;
        switch (rule.Frequency)
        {
            case FrequencyType.Daily when rule.ByDay.Count == 0 && rule.ByMonthDay.Count == 0 && rule.ByMonth.Count == 0 && rule.BySetPosition.Count == 0:
                return new RecurrenceSpec(RecurrenceFrequency.Daily, interval) { Count = count };
            case FrequencyType.Weekly when rule.ByMonthDay.Count == 0 && rule.ByMonth.Count == 0 && rule.BySetPosition.Count == 0
                                           && rule.ByDay.All(d => d.Offset is null or 0):
                return new RecurrenceSpec(RecurrenceFrequency.Weekly, interval) { Weekdays = [.. rule.ByDay.Select(d => d.DayOfWeek).Distinct()], Count = count };
            case FrequencyType.Monthly when rule.ByMonth.Count == 0:
                return DayOrPosition(rule, start, new RecurrenceSpec(RecurrenceFrequency.Monthly, interval) { Count = count });
            case FrequencyType.Yearly when rule.ByMonth.Count == 0 || (rule.ByMonth.Count == 1 && rule.ByMonth[0] == start.Month):
                return DayOrPosition(rule, start, new RecurrenceSpec(RecurrenceFrequency.Yearly, interval) { Count = count });
            default:
                return null;
        }
    }

    // Monthly and yearly: on the start's day of the month, or on one n-th weekday.
    private static RecurrenceSpec? DayOrPosition(RecurrenceRule rule, DateTime start, RecurrenceSpec spec)
    {
        if (rule.ByDay.Count == 0)
        {
            return rule.BySetPosition.Count == 0 && (rule.ByMonthDay.Count == 0 || (rule.ByMonthDay.Count == 1 && rule.ByMonthDay[0] == start.Day)) ? spec : null;
        }

        if (rule.ByDay.Count != 1 || rule.ByMonthDay.Count > 0)
        {
            return null;
        }

        var day = rule.ByDay[0];
        var position = day.Offset is { } offset and not 0 ? offset : rule.BySetPosition.Count == 1 ? rule.BySetPosition[0] : 0;
        return position is >= 1 and <= 4 or -1 ? spec with { Weekdays = [day.DayOfWeek], Position = position } : null;
    }

    /// <summary>Whether both repeat the same way (the end date is compared apart).</summary>
    public bool SameAs(RecurrenceSpec? other) =>
        other is not null && other.Frequency == Frequency && other.Interval == Interval && other.Position == Position && other.Count == Count
        && other.Weekdays.Order().SequenceEqual(Weekdays.Order());

    /// <param name="until">The last day (null: no end date); ignored with <see cref="Count"/>.</param>
    public RecurrenceRule ToRule(DateTime start, bool allDay, DateTime? until)
    {
        var rule = new RecurrenceRule(Frequency switch
        {
            RecurrenceFrequency.Daily => FrequencyType.Daily,
            RecurrenceFrequency.Weekly => FrequencyType.Weekly,
            RecurrenceFrequency.Monthly => FrequencyType.Monthly,
            _ => FrequencyType.Yearly,
        });
        if (Interval > 1)
        {
            rule.Interval = Interval;
        }

        if (Frequency == RecurrenceFrequency.Weekly && Weekdays.Count > 0)
        {
            rule.ByDay = [.. Weekdays.Distinct().OrderBy(d => ((int)d + 6) % 7).Select(d => new WeekDay(d))];
        }
        else if (Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly && Position is { } position && Weekdays.Count > 0)
        {
            rule.ByDay = [new WeekDay(Weekdays[0], position)];
            if (Frequency == RecurrenceFrequency.Yearly)
            {
                rule.ByMonth = [start.Month];
            }
        }

        if (Count is > 0)
        {
            rule.Count = Count;
        }
        else if (until is { } last)
        {
            rule.Until = UntilValue(last, start, allDay);
        }

        return rule;
    }

    /// <summary>
    /// UNTIL for the last day: a date for all-day items; for timed ones the start time on that day in UTC (RFC 5545
    /// with a TZID start), so the occurrence on the last day is included.
    /// </summary>
    public static CalDateTime UntilValue(DateTime last, DateTime start, bool allDay)
    {
        if (allDay)
        {
            return new CalDateTime(DateOnly.FromDateTime(last));
        }

        var local = DateTime.SpecifyKind(last.Date + start.TimeOfDay, DateTimeKind.Unspecified);
        return new CalDateTime(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).UtcDateTime, "UTC");
    }

    /// <summary>The last day of a rule (local), from UNTIL; null without one.</summary>
    public static DateTime? UntilOf(RecurrenceRule? rule) => rule?.Until is not { } until
        ? null
        : until.HasTime ? until.AsUtc.ToLocalTime().Date : until.Value.Date;

    /// <summary>"Alle 2 Wochen am Mo, Mi bis 14. Dezember 2026" – for the reading views.</summary>
    public string Describe(DateTime start, DateTime? until)
    {
        var culture = Culture;
        var every = (Frequency, Interval) switch
        {
            (RecurrenceFrequency.Daily, 1) => T("Täglich"),
            (RecurrenceFrequency.Daily, var n) => F("Alle {0} Tage", n),
            (RecurrenceFrequency.Weekly, 1) => T("Wöchentlich"),
            (RecurrenceFrequency.Weekly, var n) => F("Alle {0} Wochen", n),
            (RecurrenceFrequency.Monthly, 1) => T("Monatlich"),
            (RecurrenceFrequency.Monthly, var n) => F("Alle {0} Monate", n),
            (_, 1) => T("Jährlich"),
            (_, var n) => F("Alle {0} Jahre", n),
        };
        var days = Weekdays.Count > 0 ? Weekdays : [start.DayOfWeek];
        var on = Frequency switch
        {
            RecurrenceFrequency.Weekly => F("am {0}", string.Join(", ", days.OrderBy(d => ((int)d + 6) % 7).Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d)))),
            RecurrenceFrequency.Monthly when Position is { } p => F("am {0} {1}", Ordinal(p), culture.DateTimeFormat.GetDayName(days[0])),
            RecurrenceFrequency.Monthly => F("am {0}.", start.Day),
            RecurrenceFrequency.Yearly when Position is { } p => F("am {0} {1} im {2}", Ordinal(p), culture.DateTimeFormat.GetDayName(days[0]), culture.DateTimeFormat.GetMonthName(start.Month)),
            RecurrenceFrequency.Yearly => F("am {0}", start.ToString("d. MMMM", culture)),
            _ => null,
        };
        var end = Count is { } count ? F("{0} Mal", count) : until is { } last ? F("bis {0}", last.ToString("d. MMMM yyyy", culture)) : null;
        return string.Join(" ", new[] { every, on, end }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>"ersten" … "letzten" for "am zweiten Dienstag".</summary>
    public static string Ordinal(int position) => position switch
    {
        1 => T("ersten"),
        2 => T("zweiten"),
        3 => T("dritten"),
        4 => T("vierten"),
        _ => T("letzten"),
    };
}
