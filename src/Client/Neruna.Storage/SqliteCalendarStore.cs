using Microsoft.EntityFrameworkCore;
using Neruna.Core;
using Neruna.Core.Calendar;

namespace Neruna.Storage;

public sealed class SqliteCalendarStore(IDbContextFactory<NerunaDbContext> contexts) : ICalendarStore
{
    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(Guid? connectionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var query = db.Calendars.AsNoTracking();
        if (connectionId is { } id)
        {
            query = query.Where(c => c.ConnectionId == id);
        }

        var calendars = await query.ToListAsync(cancellationToken);
        return calendars.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<CalendarInfo>> MergeCalendarsAsync(Guid connectionId, IReadOnlyList<CalendarInfo> remoteCalendars, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteCalendars);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var existing = await db.Calendars.Where(c => c.ConnectionId == connectionId).ToDictionaryAsync(c => c.RemoteId, StringComparer.Ordinal, cancellationToken);
        var remoteIds = remoteCalendars.Select(c => c.RemoteId).ToHashSet(StringComparer.Ordinal);
        db.Calendars.RemoveRange(existing.Values.Where(c => !remoteIds.Contains(c.RemoteId)));

        foreach (var remote in remoteCalendars)
        {
            if (!existing.TryGetValue(remote.RemoteId, out var entity))
            {
                entity = new CalendarEntity { ConnectionId = connectionId, RemoteId = remote.RemoteId, Name = remote.Name };
                db.Calendars.Add(entity);
            }

            entity.Name = remote.Name;
            entity.Color = remote.Color;
            entity.IsReadOnly = remote.IsReadOnly;
            entity.Content = (int)remote.Content;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetCalendarsAsync(connectionId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetObjectVersionsAsync(Guid connectionId, string calendarRemoteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var calendarId = await CalendarIdAsync(db, connectionId, calendarRemoteId, cancellationToken);
        return await db.CalendarObjects.Where(o => o.CalendarId == calendarId)
            .ToDictionaryAsync(o => o.RemoteId, o => o.ETag, StringComparer.Ordinal, cancellationToken);
    }

    public async Task UpsertObjectAsync(CalendarInfo calendar, CalendarObject item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(item);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var calendarId = await CalendarIdAsync(db, calendar.ConnectionId, calendar.RemoteId, cancellationToken);

        var row = await db.CalendarObjects.FirstOrDefaultAsync(o => o.CalendarId == calendarId && o.RemoteId == item.RemoteId, cancellationToken);
        if (row is null)
        {
            row = new CalendarObjectEntity { CalendarId = calendarId, RemoteId = item.RemoteId, Data = item.ICalendarData };
            db.CalendarObjects.Add(row);
        }

        row.ETag = item.ETag;
        row.Data = item.ICalendarData;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteObjectAsync(CalendarInfo calendar, string remoteId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var calendarId = await CalendarIdAsync(db, calendar.ConnectionId, calendar.RemoteId, cancellationToken);
        await db.CalendarObjects.Where(o => o.CalendarId == calendarId && o.RemoteId == remoteId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ApplySyncResultAsync(CalendarInfo calendar, CalendarSyncResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(result);

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var entity = await db.Calendars.SingleAsync(c => c.ConnectionId == calendar.ConnectionId && c.RemoteId == calendar.RemoteId, cancellationToken);
        if (result.IsFullResync)
        {
            await db.CalendarObjects.Where(o => o.CalendarId == entity.Id).ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            var removed = result.RemovedRemoteIds.ToList();
            await db.CalendarObjects.Where(o => o.CalendarId == entity.Id && removed.Contains(o.RemoteId)).ExecuteDeleteAsync(cancellationToken);
        }

        var changedIds = result.AddedOrChanged.Select(o => o.RemoteId).ToList();
        var existing = await db.CalendarObjects
            .Where(o => o.CalendarId == entity.Id && changedIds.Contains(o.RemoteId))
            .ToDictionaryAsync(o => o.RemoteId, StringComparer.Ordinal, cancellationToken);

        foreach (var item in result.AddedOrChanged)
        {
            if (!existing.TryGetValue(item.RemoteId, out var obj))
            {
                obj = new CalendarObjectEntity { CalendarId = entity.Id, RemoteId = item.RemoteId, Data = item.ICalendarData };
                db.CalendarObjects.Add(obj);
                existing[item.RemoteId] = obj;
            }

            obj.ETag = item.ETag;
            obj.Data = item.ICalendarData;
        }

        entity.SyncState = result.NewSyncState;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarObject>> GetObjectsAsync(Guid connectionId, string calendarRemoteId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var calendarId = await CalendarIdAsync(db, connectionId, calendarRemoteId, cancellationToken);
        return await db.CalendarObjects.AsNoTracking()
            .Where(o => o.CalendarId == calendarId)
            .Select(o => new CalendarObject(o.RemoteId, o.ETag, o.Data))
            .ToListAsync(cancellationToken);
    }

    private static async Task<long> CalendarIdAsync(NerunaDbContext db, Guid connectionId, string remoteId, CancellationToken cancellationToken) =>
        await db.Calendars.Where(c => c.ConnectionId == connectionId && c.RemoteId == remoteId).Select(c => (long?)c.Id).SingleOrDefaultAsync(cancellationToken)
        ?? throw new InvalidOperationException($"Calendar '{remoteId}' is not known.");

    private static CalendarInfo ToModel(CalendarEntity c) => new(c.ConnectionId, c.RemoteId, c.Name, c.Color, c.IsReadOnly, c.SyncState, Content: c.Content == 0 ? CalendarContent.Events : (CalendarContent)c.Content);
}
