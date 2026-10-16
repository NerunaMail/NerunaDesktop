namespace Neruna.Core.Calendar;

/// <summary>Someone invited already has something at that time.</summary>
/// <param name="Person">The attendee's (or own) address.</param>
public sealed record EventConflict(string Person, string? Name, CalendarOccurrence Occurrence);

/// <summary>
/// While planning an event: who of the people in it is already busy then – as far as Neruna knows their calendar. Known
/// are the calendars of the own accounts (also a shared mailbox assigned to the user) and calendars in a person's own
/// calendar home on the server (the address or user name in its path, as CalDAV servers name them). Free events do
/// not count, nor does the event being edited.
/// </summary>
public static class EventConflicts
{
    public static async Task<IReadOnlyList<EventConflict>> FindAsync(
        CalendarController calendar,
        IEnumerable<EventAttendee> people,
        DateTime start,
        DateTime end,
        (CalendarInfo Calendar, string RemoteId)? editing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(people);
        if (end <= start)
        {
            return [];
        }

        var sources = await calendar.GetSourcesAsync(cancellationToken);
        var calendars = (await calendar.GetCalendarsAsync(cancellationToken)).Where(c => c.HasEvents).ToList();
        var result = new List<EventConflict>();
        foreach (var person in people.DistinctBy(p => p.Email, StringComparer.OrdinalIgnoreCase))
        {
            var theirs = calendars.Where(c => Belongs(c, person.Email, sources)).ToList();
            if (theirs.Count == 0)
            {
                continue;
            }

            foreach (var o in await calendar.GetOccurrencesAsync(theirs, new DateTimeOffset(start), new DateTimeOffset(end), cancellationToken))
            {
                var isEdited = editing is { } e && o.ObjectRemoteId == e.RemoteId && o.Calendar.ConnectionId == e.Calendar.ConnectionId;
                if (!isEdited && o.ShowAs != ShowAs.Free && o.Start.LocalDateTime < end && o.End.LocalDateTime > start
                    && !result.Any(r => r.Occurrence.Uid == o.Uid && string.Equals(r.Person, person.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Add(new EventConflict(person.Email, person.Name, o));
                }
            }
        }

        return result;
    }

    // The account's own calendars (not ones found in another person's home), or a calendar in this person's home.
    internal static bool Belongs(CalendarInfo calendar, string email, IReadOnlyList<CalendarSource> sources)
    {
        if (sources.FirstOrDefault(s => s.Connection.Id == calendar.ConnectionId) is { } source
            && source.Account.Owns(email) && !InOthersHome(calendar, source.Account.EmailAddress))
        {
            return true;
        }

        var path = Uri.UnescapeDataString(calendar.RemoteId);
        return path.Contains("/" + email + "/", StringComparison.OrdinalIgnoreCase);
    }

    // A CalDAV calendar of another principal reached through this account (delegation): its path names someone else.
    private static bool InOthersHome(CalendarInfo calendar, string? own)
    {
        var path = Uri.UnescapeDataString(calendar.RemoteId);
        var home = path.IndexOf("/calendars/", StringComparison.OrdinalIgnoreCase);
        if (home < 0 || own is null)
        {
            return false;
        }

        var segment = path[(home + "/calendars/".Length)..].Split('/')[0];
        var local = own.Split('@')[0];
        return segment.Length > 0 && !segment.Equals(own, StringComparison.OrdinalIgnoreCase) && !segment.Equals(local, StringComparison.OrdinalIgnoreCase);
    }
}
