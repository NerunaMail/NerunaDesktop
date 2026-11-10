using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.Serialization;
using ICalendar = Ical.Net.Calendar;

namespace Neruna.Core.Calendar;

/// <summary>One event of an .ics file to import: a single event, or a series with its changed occurrences.</summary>
public sealed record ImportedEvent(string Uid, string Summary, string ICalendarData);

/// <summary>How an import went.</summary>
/// <param name="FirstError">The first refusal of the server, to show; null without failures.</param>
public sealed record ImportResult(int Added, int Updated, int Failed, string? FirstError);

/// <summary>
/// Events of an .ics file copied into an own calendar (a timetable, a school plan): unlike a subscription they belong
/// to that calendar and can be changed like any other event.
/// </summary>
public static class CalendarImport
{
    /// <summary>The largest file read (a few thousand events).</summary>
    public const long MaxFileSize = 20 * 1024 * 1024;

    /// <summary>
    /// The events of iCalendar data, one per UID with the time zones they need. Attendees, organizer and METHOD are
    /// left out: an import is a copy for oneself, not an invitation (Exchange would otherwise send one). Tasks are skipped.
    /// </summary>
    /// <exception cref="FormatException">Not iCalendar data, or no event in it.</exception>
    public static IReadOnlyList<ImportedEvent> Read(string ics)
    {
        ArgumentNullException.ThrowIfNull(ics);
        CalendarCollection calendars;
        try
        {
            calendars = CalendarCollection.Load(ics);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new FormatException("The file is not iCalendar data.", ex);
        }

        var groups = new Dictionary<string, (ICalendar Source, List<CalendarEvent> Events)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var calendar in calendars)
        {
            foreach (var evt in calendar.Events)
            {
                var uid = string.IsNullOrWhiteSpace(evt.Uid) ? Guid.NewGuid().ToString("D") + "@neruna" : evt.Uid.Trim();
                if (!groups.TryGetValue(uid, out var group))
                {
                    groups[uid] = group = (calendar, []);
                    order.Add(uid);
                }

                group.Events.Add(evt);
            }
        }

        if (order.Count == 0)
        {
            throw new FormatException("The file contains no event.");
        }

        return [.. order.Select(uid => ToImported(uid, groups[uid].Source, groups[uid].Events))];
    }

    private static ImportedEvent ToImported(string uid, ICalendar source, List<CalendarEvent> events)
    {
        var calendar = new ICalendar { ProductId = "-//Neruna//Neruna Desktop//DE" };
        var zones = events.SelectMany(e => new[] { e.DtStart?.TzId, e.DtEnd?.TzId, e.RecurrenceIdentifier?.StartTime.TzId })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in source.TimeZones.Where(z => z.TzId is { } id && zones.Contains(id)))
        {
            calendar.TimeZones.Add(zone.Copy<VTimeZone>() ?? zone);
        }

        foreach (var evt in events)
        {
            var copy = evt.Copy<CalendarEvent>() ?? throw new FormatException("The event cannot be copied.");
            copy.Uid = uid;
            copy.Organizer = null;
            copy.Attendees.Clear();
            calendar.Events.Add(copy);
        }

        // Missing in the source (times with a TZID but no VTIMEZONE): Ical.Net knows the common ones.
        foreach (var zone in zones.Where(z => calendar.TimeZones.All(t => !string.Equals(t.TzId, z, StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                calendar.AddTimeZone(zone);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeZoneNotFoundException)
            {
                // Unknown zone: left as it is, as in the file.
            }
        }

        var master = events.FirstOrDefault(e => e.RecurrenceIdentifier is null) ?? events[0];
        return new ImportedEvent(uid, master.Summary ?? string.Empty,
            new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the event failed."));
    }

    /// <summary>The same event under another UID (its copy where the original is in another calendar of the account).</summary>
    public static ImportedEvent WithNewUid(ImportedEvent imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var calendar = ICalendarLoader.Load(imported.ICalendarData) ?? throw new FormatException("Empty iCalendar data.");
        var uid = Guid.NewGuid().ToString("D") + "@neruna";
        foreach (var evt in calendar.Events)
        {
            evt.Uid = uid;
        }

        return imported with
        {
            Uid = uid,
            ICalendarData = new CalendarSerializer().SerializeToString(calendar) ?? throw new InvalidOperationException("Serializing the event failed."),
        };
    }
}
