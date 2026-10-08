using System.Text.Json;
using Ical.Net;
using Ical.Net.DataTypes;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Providers;

namespace Neruna.Core.Calendar;

/// <summary>A single, concrete appearance of an event in the calendar view (recurrences already expanded).</summary>
public sealed record CalendarOccurrence(
    CalendarInfo Calendar,
    string ObjectRemoteId,
    string Uid,
    string Summary,
    string? Location,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    bool IsRecurring);

/// <summary>A calendar offered by the server, for choosing which ones to show (Thunderbird only offers this at setup).</summary>
/// <param name="IsSelected">Currently synchronized.</param>
/// <param name="IsNew">Appeared since the user last chose, e.g. a calendar subscribed in SOGo's web interface meanwhile.</param>
/// <param name="IsAddedByUrl">Added by its address because the server did not list it.</param>
public sealed record CalendarCandidate(CalendarInfo Calendar, bool IsSelected, bool IsNew, bool IsAddedByUrl = false);

/// <param name="Details">Where the server was asked (home URL, user), to explain a missing calendar.</param>
public sealed record CalendarDiscovery(IReadOnlyList<CalendarCandidate> Calendars, string? Details);

/// <summary>A calendar connection (CalDAV server, ICS subscription …) with the account it belongs to.</summary>
public sealed record CalendarSource(Account Account, ServiceConnection Connection);

/// <summary>
/// The UI's single entry point for calendars, independent of whether a calendar comes from CalDAV, an ICS
/// subscription or (later) EWS.
/// </summary>
public sealed class CalendarController(
    IAccountStore accounts,
    ICalendarStore store,
    ProviderRegistry providers,
    ISettingsStore settings,
    ILogger<CalendarController> logger)
{
    // Events that started this long before the visible range are still checked for overlap.
    internal static readonly TimeSpan MaxEventLength = TimeSpan.FromDays(31);

    public Task<SyncReport> SyncAllAsync(CancellationToken cancellationToken = default) =>
        SyncReport.ForEachConnectionAsync(accounts, ServiceKind.Calendar, SyncConnectionAsync, logger, cancellationToken);

    public async Task SyncConnectionAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        await using var provider = providers.CreateCalendar(connection);

        var remote = await GetRemoteCalendarsAsync(provider, connection, cancellationToken);

        // Without an explicit choice every calendar of the server is shown (and new ones appear automatically).
        if (await GetIdsAsync(SelectedKey(connection.Id), cancellationToken) is { } selected)
        {
            remote = remote.Where(c => selected.Contains(c.RemoteId)).ToList();
        }

        foreach (var calendar in await store.MergeCalendarsAsync(connection.Id, remote, cancellationToken))
        {
            var known = await store.GetObjectVersionsAsync(connection.Id, calendar.RemoteId, cancellationToken);
            var result = await provider.SyncCalendarAsync(calendar, known, cancellationToken);
            await store.ApplySyncResultAsync(calendar, result, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CalendarSource>> GetSourcesAsync(CancellationToken cancellationToken = default) =>
        (await accounts.GetAccountsAsync(cancellationToken))
            .SelectMany(a => a.ConnectionsOf(ServiceKind.Calendar).Select(c => new CalendarSource(a, c)))
            .ToList();

    /// <summary>Asks the server again which calendars exist (own, shared, subscribed) and which of them are shown.</summary>
    public async Task<IReadOnlyList<CalendarCandidate>> DiscoverAsync(ServiceConnection connection, CancellationToken cancellationToken = default) =>
        (await DiscoverWithDetailsAsync(connection, cancellationToken)).Calendars;

    public async Task<CalendarDiscovery> DiscoverWithDetailsAsync(ServiceConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var provider = providers.CreateCalendar(connection);
        var remote = await GetRemoteCalendarsAsync(provider, connection, cancellationToken);
        var extra = await GetIdsAsync(ExtraKey(connection.Id), cancellationToken) ?? [];
        var local = (await store.GetCalendarsAsync(connection.Id, cancellationToken)).Select(c => c.RemoteId).ToHashSet(StringComparer.Ordinal);
        var known = await GetIdsAsync(KnownKey(connection.Id), cancellationToken);
        var candidates = remote
            .Select(c => new CalendarCandidate(c, local.Contains(c.RemoteId), known is null ? !local.Contains(c.RemoteId) : !known.Contains(c.RemoteId), extra.Contains(c.RemoteId)))
            .OrderBy(c => c.Calendar.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new CalendarDiscovery(candidates, (provider as ICalendarUrlLookup)?.DiscoveryDetails);
    }

    /// <summary>
    /// Adds a calendar by its address (e.g. SOGo → Kalender → "Links zu diesem Kalender") when the server does not
    /// list it. It is selected and synchronized right away.
    /// </summary>
    /// <returns>The calendar, or null if the address is no calendar this connection can read.</returns>
    public async Task<CalendarInfo?> AddByUrlAsync(ServiceConnection connection, Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(url);
        CalendarInfo? found;
        await using (var provider = providers.CreateCalendar(connection))
        {
            if (provider is not ICalendarUrlLookup lookup)
            {
                throw new NotSupportedException("Dieser Kalendertyp kann keine Kalender per Adresse hinzufügen.");
            }

            found = await lookup.GetCalendarAsync(url, cancellationToken);
        }

        if (found is null)
        {
            return null;
        }

        var extra = await GetIdsAsync(ExtraKey(connection.Id), cancellationToken) ?? [];
        extra.Add(found.RemoteId);
        await settings.SetAsync(ExtraKey(connection.Id), JsonSerializer.Serialize(extra), cancellationToken);

        if (await GetIdsAsync(SelectedKey(connection.Id), cancellationToken) is { } selected)
        {
            selected.Add(found.RemoteId);
            await settings.SetAsync(SelectedKey(connection.Id), JsonSerializer.Serialize(selected), cancellationToken);
        }

        await SyncConnectionAsync(connection, cancellationToken);
        return found;
    }

    // What the server lists, plus calendars the user added by address (unless they are listed anyway).
    private async Task<IReadOnlyList<CalendarInfo>> GetRemoteCalendarsAsync(ICalendarProvider provider, ServiceConnection connection, CancellationToken cancellationToken)
    {
        var remote = (await provider.GetCalendarsAsync(cancellationToken)).ToList();
        if (provider is ICalendarUrlLookup lookup && await GetIdsAsync(ExtraKey(connection.Id), cancellationToken) is { } extra)
        {
            foreach (var url in extra.Where(u => remote.All(c => c.RemoteId != u)))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && await lookup.GetCalendarAsync(uri, cancellationToken) is { } calendar)
                {
                    remote.Add(calendar);
                }
            }
        }

        return remote;
    }

    /// <summary>Shows exactly these calendars of the connection (others are removed locally) and synchronizes.</summary>
    /// <param name="offered">All calendars the user saw; later additions on the server count as new.</param>
    public async Task SetSelectionAsync(ServiceConnection connection, IReadOnlyCollection<string> selected, IReadOnlyCollection<string> offered, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await settings.SetAsync(SelectedKey(connection.Id), JsonSerializer.Serialize(selected), cancellationToken);
        await settings.SetAsync(KnownKey(connection.Id), JsonSerializer.Serialize(offered), cancellationToken);
        await SyncConnectionAsync(connection, cancellationToken);
    }

    private async Task<HashSet<string>?> GetIdsAsync(string key, CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(key, cancellationToken) is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SelectedKey(Guid connectionId) => $"calendars.selected.{connectionId:N}";

    private static string KnownKey(Guid connectionId) => $"calendars.known.{connectionId:N}";

    private static string ExtraKey(Guid connectionId) => $"calendars.extra.{connectionId:N}";

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default) =>
        store.GetCalendarsAsync(null, cancellationToken);

    public async Task<CalendarObject?> GetObjectAsync(CalendarInfo calendar, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return (await store.GetObjectsAsync(calendar.ConnectionId, calendar.RemoteId, cancellationToken)).FirstOrDefault(o => o.RemoteId == remoteId);
    }

    /// <summary>
    /// Creates an event in <paramref name="target"/>, or updates the one at <paramref name="existing"/>.
    /// If the target calendar differs from the existing one, the event moves (created there with the same UID, then deleted here).
    /// </summary>
    /// <exception cref="RemoteConflictException">Someone changed the event on the server; sync and retry.</exception>
    public async Task<CalendarObject> SaveEventAsync(
        CalendarInfo target,
        EventDraft draft,
        (CalendarInfo Calendar, string RemoteId)? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(draft);

        var current = existing is { } e ? await GetObjectAsync(e.Calendar, e.RemoteId, cancellationToken) : null;
        var data = draft.ToICalendar(current?.ICalendarData);
        var moving = existing is { } ex && (ex.Calendar.ConnectionId != target.ConnectionId || ex.Calendar.RemoteId != target.RemoteId);

        var toSave = moving || current is null
            ? new CalendarObject(string.Empty, null, data)
            : current with { ICalendarData = data };

        CalendarObject saved;
        await using (var provider = providers.CreateCalendar(await ConnectionAsync(target.ConnectionId, cancellationToken)))
        {
            saved = await provider.SaveAsync(target, toSave, cancellationToken);
        }

        await store.UpsertObjectAsync(target, saved, cancellationToken);

        if (moving && current is not null)
        {
            await DeleteEventAsync(existing!.Value.Calendar, current.RemoteId, cancellationToken);
        }

        return saved;
    }

    /// <summary>The stored event with this UID (an invitation's event), in whichever calendar it is.</summary>
    public async Task<(CalendarInfo Calendar, CalendarObject Item)?> FindByUidAsync(string uid, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        foreach (var calendar in await store.GetCalendarsAsync(null, cancellationToken))
        {
            foreach (var item in await store.GetObjectsAsync(calendar.ConnectionId, calendar.RemoteId, cancellationToken))
            {
                // Cheap text test first; parsing every event would be slow.
                if (item.ICalendarData.Contains(uid, StringComparison.Ordinal) && EventDraft.UidOf(item.ICalendarData) == uid)
                {
                    return (calendar, item);
                }
            }
        }

        return null;
    }

    /// <summary>Stores complete iCalendar data (an accepted invitation): creates it, or updates <paramref name="existing"/>.</summary>
    /// <exception cref="RemoteConflictException">Someone changed the event on the server; sync and retry.</exception>
    public async Task<CalendarObject> SaveDataAsync(CalendarInfo target, string data, CalendarObject? existing, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        CalendarObject saved;
        await using (var provider = providers.CreateCalendar(await ConnectionAsync(target.ConnectionId, cancellationToken)))
        {
            saved = await provider.SaveAsync(target, existing is null ? new CalendarObject(string.Empty, null, data) : existing with { ICalendarData = data }, cancellationToken);
        }

        await store.UpsertObjectAsync(target, saved, cancellationToken);
        return saved;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _schedulesItself = new(StringComparer.Ordinal);

    /// <summary>
    /// True if the calendar's server delivers invitations and replies itself (SOGo, Nextcloud …); then Neruna must not
    /// e-mail them as well. Asked once per calendar; if the server cannot be asked, Neruna sends them itself.
    /// </summary>
    public async Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var key = $"{calendar.ConnectionId:N}|{calendar.RemoteId}";
        if (_schedulesItself.TryGetValue(key, out var known))
        {
            return known;
        }

        var result = false;
        try
        {
            await using var provider = providers.CreateCalendar(await ConnectionAsync(calendar.ConnectionId, cancellationToken));
            result = provider is ICalendarScheduling scheduling && await scheduling.SchedulesItselfAsync(calendar, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Could not ask {Calendar} about scheduling; sending invitations by e-mail", calendar.RemoteId);
        }

        logger.LogInformation("Calendar {Calendar}: server sends invitations itself = {Result}", calendar.Name, result);
        _schedulesItself[key] = result;
        return result;
    }

    /// <exception cref="RemoteConflictException">Someone changed the event on the server; sync and retry.</exception>
    public async Task DeleteEventAsync(CalendarInfo calendar, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var current = await GetObjectAsync(calendar, remoteId, cancellationToken);
        if (current is null)
        {
            return;
        }

        await using (var provider = providers.CreateCalendar(await ConnectionAsync(calendar.ConnectionId, cancellationToken)))
        {
            await provider.DeleteAsync(calendar, current, cancellationToken);
        }

        await store.DeleteObjectAsync(calendar, remoteId, cancellationToken);
    }

    private async Task<ServiceConnection> ConnectionAsync(Guid connectionId, CancellationToken cancellationToken) =>
        (await accounts.GetAccountsAsync(cancellationToken)).SelectMany(a => a.Connections).FirstOrDefault(c => c.Id == connectionId)
        ?? throw new InvalidOperationException($"Connection {connectionId} no longer exists.");

    /// <summary>All occurrences overlapping [<paramref name="from"/>, <paramref name="to"/>) across the given calendars, ordered by start.</summary>
    public async Task<IReadOnlyList<CalendarOccurrence>> GetOccurrencesAsync(
        IEnumerable<CalendarInfo> calendars,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendars);
        var result = new List<CalendarOccurrence>();

        foreach (var calendar in calendars)
        {
            foreach (var item in await store.GetObjectsAsync(calendar.ConnectionId, calendar.RemoteId, cancellationToken))
            {
                try
                {
                    result.AddRange(Expand(calendar, item, from, to));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One malformed event from a foreign server must not break the whole view.
                    logger.LogWarning(ex, "Skipping unparsable calendar object {RemoteId}", item.RemoteId);
                }
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    internal static IEnumerable<CalendarOccurrence> Expand(CalendarInfo calendar, CalendarObject item, DateTimeOffset from, DateTimeOffset to)
    {
        var ical = Ical.Net.Calendar.Load(item.ICalendarData)
            ?? throw new FormatException("Empty iCalendar data.");

        var windowStart = new CalDateTime((from - MaxEventLength).UtcDateTime, "UTC");
        var windowEnd = new CalDateTime(to.UtcDateTime, "UTC");

        foreach (var occurrence in ical.GetOccurrences(windowStart, null).TakeWhileBefore(windowEnd))
        {
            if (occurrence.Source is not Ical.Net.CalendarComponents.CalendarEvent evt)
            {
                continue;
            }

            var start = ToDateTimeOffset(occurrence.Period.StartTime);
            var end = occurrence.Period.EffectiveEndTime is { } effectiveEnd ? ToDateTimeOffset(effectiveEnd) : start;
            if (end <= from && start < from)
            {
                continue;
            }

            yield return ToOccurrence(calendar, item, evt, occurrence);
        }
    }

    internal static CalendarOccurrence ToOccurrence(CalendarInfo calendar, CalendarObject item, Ical.Net.CalendarComponents.CalendarEvent evt, Occurrence occurrence)
    {
        var start = ToDateTimeOffset(occurrence.Period.StartTime);
        var end = occurrence.Period.EffectiveEndTime is { } effectiveEnd ? ToDateTimeOffset(effectiveEnd) : start;
        return new CalendarOccurrence(
            calendar,
            item.RemoteId,
            evt.Uid ?? item.RemoteId,
            string.IsNullOrWhiteSpace(evt.Summary) ? "(ohne Titel)" : evt.Summary,
            evt.Location,
            start,
            end,
            evt.IsAllDay,
            evt.RecurrenceRule is not null || evt.RecurrenceIdentifier is not null);
    }

    private static DateTimeOffset ToDateTimeOffset(CalDateTime value)
    {
        if (!value.HasTime || value.IsFloating)
        {
            // All-day dates and floating times mean "wall clock wherever the user is".
            var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Local);
            return new DateTimeOffset(local);
        }

        return new DateTimeOffset(value.AsUtc, TimeSpan.Zero).ToLocalTime();
    }
}
