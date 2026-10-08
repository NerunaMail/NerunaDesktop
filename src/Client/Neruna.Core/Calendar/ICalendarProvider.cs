namespace Neruna.Core.Calendar;

[Flags]
public enum CalendarProviderCapabilities
{
    None = 0,
    Write = 1,
    CreateCalendars = 2,
    FreeBusy = 4,
    Scheduling = 8,
    Push = 16,
}

/// <summary>
/// A calendar backend (CalDAV, ICS subscription, later EWS, Graph …) bound to one connection.
/// Events travel as iCalendar (RFC 5545) text, which every backend can produce, so storage and UI stay protocol-neutral.
/// </summary>
public interface ICalendarProvider : IAsyncDisposable
{
    CalendarProviderCapabilities Capabilities { get; }

    Task TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default);

    /// <param name="knownVersions">Locally stored objects: remote id → ETag (null if the backend has none).</param>
    Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates (<paramref name="item"/>.RemoteId empty) or updates an object, guarded by its ETag.
    /// Returns it with the server-assigned id and new ETag.
    /// </summary>
    /// <exception cref="NotSupportedException">The provider lacks <see cref="CalendarProviderCapabilities.Write"/>.</exception>
    /// <exception cref="RemoteConflictException">The object was changed or deleted on the server meanwhile.</exception>
    Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default);

    /// <exception cref="NotSupportedException">The provider lacks <see cref="CalendarProviderCapabilities.Write"/>.</exception>
    /// <exception cref="RemoteConflictException">The object was changed on the server meanwhile.</exception>
    Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default);
}

/// <summary>Optional: whether the server itself delivers invitations and replies (RFC 6638), or the client must e-mail them.</summary>
public interface ICalendarScheduling
{
    Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional for providers whose calendars are addressed by URL (CalDAV): add a calendar directly – e.g. one subscribed
/// in SOGo's web interface that the server does not list – and tell the user where discovery looked.
/// </summary>
public interface ICalendarUrlLookup
{
    /// <returns>The calendar at that address, or null if it is none (or not accessible).</returns>
    Task<CalendarInfo?> GetCalendarAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>After <see cref="ICalendarProvider.GetCalendarsAsync"/>: which home was listed, as which user.</summary>
    string? DiscoveryDetails { get; }
}

public sealed record CalendarSyncResult(
    string NewSyncState,
    bool IsFullResync,
    IReadOnlyList<CalendarObject> AddedOrChanged,
    IReadOnlyList<string> RemovedRemoteIds);
