using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Logging;

namespace Neruna.Core.Calendar;

/// <summary>A reminder that is due: one occurrence of an event (several alarms of it count as one reminder).</summary>
/// <param name="Key">Identifies the occurrence; dismissing or snoozing is stored under it.</param>
public sealed record Reminder(string Key, CalendarOccurrence Occurrence, DateTimeOffset DueAt);

/// <summary>What the user did with a reminder (kept locally; the server's alarms are not changed).</summary>
/// <param name="Expires">After this, the state is no longer needed (the occurrence is long over).</param>
public sealed record ReminderState(string Key, DateTimeOffset? SnoozedUntil, bool Dismissed, DateTimeOffset Expires);

public interface IReminderStore
{
    Task<IReadOnlyDictionary<string, ReminderState>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SetAsync(ReminderState state, CancellationToken cancellationToken = default);

    Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reminders from the alarms (VALARM) in the events themselves – so they also work for events created in the web
/// interface or another program, and per occurrence of a series. Dismissing and snoozing are remembered locally.
/// </summary>
public sealed class ReminderService(ICalendarStore calendars, IReminderStore states, TimeProvider clock, ILogger<ReminderService> logger)
{
    // How far ahead an alarm may lie before its event (a week ahead is the usual maximum).
    private static readonly TimeSpan MaxLead = TimeSpan.FromDays(8);

    // A reminder stays until dismissed, but not for events that are already over (no flood after a weekend off).
    private static readonly TimeSpan ShowAfterEnd = TimeSpan.FromMinutes(15);

    /// <summary>All reminders due now, earliest event first.</summary>
    public async Task<IReadOnlyList<Reminder>> GetDueAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var known = await states.GetAllAsync(cancellationToken);
        var due = new List<Reminder>();

        foreach (var calendar in await calendars.GetCalendarsAsync(null, cancellationToken))
        {
            foreach (var item in await calendars.GetObjectsAsync(calendar.ConnectionId, calendar.RemoteId, cancellationToken))
            {
                try
                {
                    foreach (var (occurrence, alarmAt) in AlarmsOf(calendar, item, now - TimeSpan.FromDays(1), now + MaxLead))
                    {
                        if (alarmAt > now || occurrence.End.ToUniversalTime() < now - ShowAfterEnd)
                        {
                            continue;
                        }

                        var key = KeyOf(occurrence);
                        var dueAt = alarmAt;
                        if (known.TryGetValue(key, out var state))
                        {
                            if (state.Dismissed || state.SnoozedUntil > now)
                            {
                                continue;
                            }

                            dueAt = state.SnoozedUntil ?? alarmAt;
                        }

                        due.Add(new Reminder(key, occurrence, dueAt));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One malformed event from a foreign server must not stop the other reminders.
                    logger.LogWarning(ex, "Skipping reminders of {RemoteId}", item.RemoteId);
                }
            }
        }

        return due.OrderBy(r => r.Occurrence.Start).ThenBy(r => r.Occurrence.Summary, StringComparer.CurrentCulture).ToList();
    }

    public Task DismissAsync(Reminder reminder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return states.SetAsync(new ReminderState(reminder.Key, null, true, ExpiresOf(reminder)), cancellationToken);
    }

    /// <summary>Shows the reminder again at <paramref name="until"/>.</summary>
    public Task SnoozeAsync(Reminder reminder, DateTimeOffset until, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return states.SetAsync(new ReminderState(reminder.Key, until, false, ExpiresOf(reminder)), cancellationToken);
    }

    /// <summary>Forgets states of occurrences that are long over.</summary>
    public Task CleanUpAsync(CancellationToken cancellationToken = default) => states.DeleteExpiredAsync(clock.GetUtcNow(), cancellationToken);

    private static DateTimeOffset ExpiresOf(Reminder reminder) => reminder.Occurrence.End.AddDays(2);

    internal static string KeyOf(CalendarOccurrence occurrence) =>
        $"{occurrence.Calendar.ConnectionId:N}|{occurrence.Calendar.RemoteId}|{occurrence.Uid}|{occurrence.Start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

    /// <summary>Each occurrence in the window with the time of its earliest user alarm.</summary>
    internal static IEnumerable<(CalendarOccurrence Occurrence, DateTimeOffset AlarmAt)> AlarmsOf(CalendarInfo calendar, CalendarObject item, DateTimeOffset from, DateTimeOffset to)
    {
        var ical = Ical.Net.Calendar.Load(item.ICalendarData) ?? throw new FormatException("Empty iCalendar data.");
        var windowStart = new CalDateTime((from - CalendarController.MaxEventLength).UtcDateTime, "UTC");
        var windowEnd = new CalDateTime(to.UtcDateTime, "UTC");

        foreach (var occurrence in ical.GetOccurrences(windowStart, null).TakeWhileBefore(windowEnd))
        {
            if (occurrence.Source is not CalendarEvent evt || evt.Alarms.Count == 0
                || string.Equals(evt.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var shown = CalendarController.ToOccurrence(calendar, item, evt, occurrence);
            DateTimeOffset? earliest = null;
            foreach (var alarm in evt.Alarms.Where(EventDraft.IsUserAlarm))
            {
                DateTimeOffset? at = alarm.Trigger switch
                {
                    { Duration: { } duration } trigger => (string.Equals(trigger.Related, "END", StringComparison.OrdinalIgnoreCase) ? shown.End : shown.Start)
                                                          + duration.ToTimeSpanUnspecified(),
                    { DateTime: { } absolute } => new DateTimeOffset(absolute.AsUtc, TimeSpan.Zero),
                    _ => null,
                };
                if (at is { } value && (earliest is null || value < earliest))
                {
                    earliest = value;
                }
            }

            if (earliest is { } alarmAt)
            {
                yield return (shown, alarmAt);
            }
        }
    }
}
