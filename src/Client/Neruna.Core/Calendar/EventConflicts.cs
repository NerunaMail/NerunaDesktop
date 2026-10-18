namespace Neruna.Core.Calendar;

/// <summary>Someone invited already has something at that time (the subject only if known).</summary>
/// <param name="Person">The attendee's (or own) address.</param>
public sealed record EventConflict(string Person, string? Name, string? Subject, DateTimeOffset Start, DateTimeOffset End, bool IsAllDay);

/// <summary>
/// While planning an event: who of the people in it is already busy then. First the calendars Neruna has: those of
/// the own accounts (also a shared mailbox assigned to the user) and calendars in a person's own calendar home on the
/// server (the address in its path, as CalDAV servers name them). For everyone else the server is asked for free/busy
/// (Microsoft 365, Exchange) – no subscription needed. Free time does not count, nor does the event being edited.
/// </summary>
public static class EventConflicts
{
    /// <param name="editing">The event being edited: its own entry is not a conflict.</param>
    /// <param name="editingTimes">Its times before the change: free/busy shows the event itself there.</param>
    /// <param name="connection">Whose server to ask for free/busy first (the calendar the event goes into).</param>
    public static async Task<IReadOnlyList<EventConflict>> FindAsync(
        CalendarController calendar,
        IEnumerable<EventAttendee> people,
        DateTime start,
        DateTime end,
        (CalendarInfo Calendar, string RemoteId)? editing,
        (DateTime Start, DateTime End)? editingTimes = null,
        Guid? connection = null,
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
        var unknown = new List<EventAttendee>();
        foreach (var person in people.DistinctBy(p => p.Email, StringComparer.OrdinalIgnoreCase))
        {
            var theirs = calendars.Where(c => Belongs(c, person.Email, sources)).ToList();
            if (theirs.Count == 0)
            {
                unknown.Add(person);
                continue;
            }

            foreach (var o in await calendar.GetOccurrencesAsync(theirs, new DateTimeOffset(start), new DateTimeOffset(end), cancellationToken))
            {
                var isEdited = editing is { } e && o.ObjectRemoteId == e.RemoteId && o.Calendar.ConnectionId == e.Calendar.ConnectionId;
                if (!isEdited && o.ShowAs != ShowAs.Free && o.Start.LocalDateTime < end && o.End.LocalDateTime > start
                    && !result.Any(r => string.Equals(r.Person, person.Email, StringComparison.OrdinalIgnoreCase) && r.Start == o.Start && r.Subject == o.Summary))
                {
                    result.Add(new EventConflict(person.Email, person.Name, o.Summary, o.Start, o.End, o.IsAllDay));
                }
            }
        }

        if (unknown.Count > 0
            && await calendar.GetFreeBusyAsync([.. unknown.Select(p => p.Email)], new DateTimeOffset(start), new DateTimeOffset(end), connection, cancellationToken) is { } answers)
        {
            foreach (var (person, availability) in unknown.Zip(answers))
            {
                foreach (var slot in availability.Slots.Where(s => s.ShowAs != ShowAs.Free && s.Start.LocalDateTime < end && s.End.LocalDateTime > start))
                {
                    if (editingTimes is { } times && slot.Start.LocalDateTime == times.Start && slot.End.LocalDateTime == times.End)
                    {
                        continue; // the edited event itself
                    }

                    result.Add(new EventConflict(person.Email, person.Name, slot.Subject, slot.Start, slot.End, false));
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

        // Someone else's calendar: the address in its path (SOGo: ".../dav/lea@firma.ch/...", also in the name of a
        // subscription), as its name or as whose it is ("geteilt von"), or their user name as the home on the server
        // (Nextcloud: ".../calendars/lea.keller/..." for lea.keller@…).
        var path = Uri.UnescapeDataString(calendar.RemoteId);
        if (path.Contains(email, StringComparison.OrdinalIgnoreCase)
            || calendar.Name.Contains(email, StringComparison.OrdinalIgnoreCase)
            || calendar.SharedBy?.Contains(email, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        // Only in another user's home: the account's own home has the same user name as many other people's addresses.
        return HomeOwner(path) is { } owner && owner.Equals(email.Split('@')[0], StringComparison.OrdinalIgnoreCase)
               && sources.FirstOrDefault(s => s.Connection.Id == calendar.ConnectionId) is { } account
               && InOthersHome(calendar, account.Account.EmailAddress);
    }

    // The user a CalDAV calendar home belongs to: ".../calendars/<user>/<calendar>/".
    private static string? HomeOwner(string path)
    {
        var home = path.IndexOf("/calendars/", StringComparison.OrdinalIgnoreCase);
        return home < 0 ? null : path[(home + "/calendars/".Length)..].Split('/')[0] is { Length: > 0 } segment ? segment : null;
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
