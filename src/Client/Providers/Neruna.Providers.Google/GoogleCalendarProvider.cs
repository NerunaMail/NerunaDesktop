using Neruna.Core.Calendar;

namespace Neruna.Providers.Google;

/// <summary>
/// Google Calendar over CalDAV, with the Google Tasks lists beside the calendars (as Microsoft 365 shows To Do): one
/// connection, so the account has one calendar entry. Calendars and tasks each only where the sign-in includes them.
/// </summary>
internal sealed class GoogleCalendarProvider(Guid connectionId, ICalendarProvider dav, GoogleTasks tasks, Func<string, CancellationToken, Task<bool>> grants)
    : ICalendarProvider, ICalendarScheduling
{
    public CalendarProviderCapabilities Capabilities => dav.Capabilities;

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (await grants(GoogleAccount.CalendarScope, cancellationToken))
        {
            await dav.TestConnectionAsync(cancellationToken);
        }
        else
        {
            await tasks.GetListsAsync(connectionId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CalendarInfo> calendars = await grants(GoogleAccount.CalendarScope, cancellationToken) ? await dav.GetCalendarsAsync(cancellationToken) : [];
        return await grants(GoogleAccount.TasksScope, cancellationToken) ? [.. calendars, .. await tasks.GetListsAsync(connectionId, cancellationToken)] : calendars;
    }

    public Task<CalendarSyncResult> SyncCalendarAsync(CalendarInfo calendar, IReadOnlyDictionary<string, string?> knownVersions, CancellationToken cancellationToken = default) =>
        GoogleTasks.IsTaskList(calendar) ? tasks.SyncAsync(calendar, knownVersions, cancellationToken) : dav.SyncCalendarAsync(calendar, knownVersions, cancellationToken);

    public Task<CalendarObject> SaveAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        GoogleTasks.IsTaskList(calendar) ? tasks.SaveAsync(calendar, item, cancellationToken) : dav.SaveAsync(calendar, item, cancellationToken);

    public Task DeleteAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default) =>
        GoogleTasks.IsTaskList(calendar) ? tasks.DeleteAsync(calendar, item, cancellationToken) : dav.DeleteAsync(calendar, item, cancellationToken);

    public Task<bool> SchedulesItselfAsync(CalendarInfo calendar, CancellationToken cancellationToken = default) =>
        !GoogleTasks.IsTaskList(calendar) && dav is ICalendarScheduling scheduling ? scheduling.SchedulesItselfAsync(calendar, cancellationToken) : Task.FromResult(false);

    public ValueTask DisposeAsync() => dav.DisposeAsync();
}
