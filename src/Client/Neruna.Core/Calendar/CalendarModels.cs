namespace Neruna.Core.Calendar;

/// <param name="RemoteId">Provider-owned id (CalDAV: collection URL; ICS: subscription URL).</param>
/// <param name="Color">Hex color like <c>#3A87AD</c>, if the server provides one.</param>
public sealed record CalendarInfo(
    Guid ConnectionId,
    string RemoteId,
    string Name,
    string? Color,
    bool IsReadOnly,
    string? SyncState = null);

/// <summary>
/// One calendar resource: an event or task together with its recurrence overrides, i.e. everything sharing one UID.
/// </summary>
/// <param name="RemoteId">Provider-owned id (CalDAV: resource href; ICS: the UID).</param>
/// <param name="ETag">Version tag for optimistic concurrency, if the backend has one.</param>
/// <param name="ICalendarData">Complete VCALENDAR text; the source of truth.</param>
public sealed record CalendarObject(
    string RemoteId,
    string? ETag,
    string ICalendarData);
