using static Neruna.Core.Localization.Texts;

namespace Neruna.Core.Calendar;

/// <summary>
/// The UI's entry point for tasks (VTODO): CalDAV task lists, tasks in mixed calendars, Microsoft To Do. Stored and
/// synced like calendars (<see cref="CalendarController"/>); which lists show under "Aufgaben" and which also in the
/// calendar is decided here. The calendar asks for its tasks through <see cref="AddToCalendarAsync"/>.
/// </summary>
public sealed class TaskController(CalendarController calendars, ICalendarStore store, ISettingsStore settings)
{
    /// <summary>All calendars that hold tasks.</summary>
    public async Task<IReadOnlyList<CalendarInfo>> GetListsAsync(CancellationToken cancellationToken = default) =>
        [.. (await calendars.GetCalendarsAsync(cancellationToken)).Where(c => c.HasTasks)];

    /// <summary>All tasks of all task lists, also those in mixed calendars.</summary>
    public async Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<TaskItem>();
        foreach (var list in await GetListsAsync(cancellationToken))
        {
            foreach (var item in await store.GetObjectsAsync(list.ConnectionId, list.RemoteId, cancellationToken))
            {
                // Cheap text test first: mixed calendars hold mostly events.
                if (item.ICalendarData.Contains("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase) && TaskDraft.ToItem(list, item) is { } task)
                {
                    result.Add(task);
                }
            }
        }

        return result;
    }

    /// <summary>The task lists hidden under "Aufgaben".</summary>
    public async Task<IReadOnlySet<string>> GetHiddenListsAsync(CancellationToken cancellationToken = default) =>
        await settings.GetSetAsync(SettingKeys.TasksHiddenLists, cancellationToken);

    public Task SetListVisibleAsync(CalendarInfo list, bool visible, CancellationToken cancellationToken = default) =>
        settings.SetMembershipAsync(SettingKeys.TasksHiddenLists, CalendarController.CalendarKey(list), !visible, cancellationToken);

    /// <summary>The task lists whose open tasks also appear in the calendar.</summary>
    public async Task<IReadOnlySet<string>> GetListsInCalendarAsync(CancellationToken cancellationToken = default)
    {
        if (await settings.GetListAsync(SettingKeys.TasksInCalendar, cancellationToken) is { } chosen)
        {
            return chosen.ToHashSet(StringComparer.Ordinal);
        }

        // 0.1.14 had one switch for all lists: carry it over once.
        if (await settings.GetAsync(SettingKeys.CalendarShowTasks, cancellationToken) == "true")
        {
            var all = (await GetListsAsync(cancellationToken)).Select(CalendarController.CalendarKey).ToHashSet(StringComparer.Ordinal);
            await settings.SetListAsync(SettingKeys.TasksInCalendar, all, cancellationToken);
            await settings.SetAsync(SettingKeys.CalendarShowTasks, null, cancellationToken);
            return all;
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task SetListInCalendarAsync(CalendarInfo list, bool shown, CancellationToken cancellationToken = default)
    {
        await GetListsInCalendarAsync(cancellationToken); // carries the switch of 0.1.14 over first
        await settings.SetMembershipAsync(SettingKeys.TasksInCalendar, CalendarController.CalendarKey(list), shown, cancellationToken);
    }

    /// <summary>
    /// The events of a calendar view plus the open tasks with a due date from the lists marked for the calendar, by start.
    /// A calendar with events that is switched off in the view takes its tasks with it.
    /// </summary>
    /// <param name="shown">The calendars shown in the view.</param>
    /// <param name="includeTaskLists">Also pure task lists; false when the view shows just one calendar.</param>
    public async Task<IReadOnlyList<CalendarOccurrence>> AddToCalendarAsync(
        IReadOnlyList<CalendarOccurrence> events,
        IEnumerable<CalendarInfo> shown,
        DateTimeOffset from,
        DateTimeOffset to,
        bool includeTaskLists = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(shown);
        var inCalendar = await GetListsInCalendarAsync(cancellationToken);
        if (inCalendar.Count == 0)
        {
            return events;
        }

        var shownCalendars = shown.Select(CalendarController.CalendarKey).ToHashSet(StringComparer.Ordinal);
        var result = new List<CalendarOccurrence>(events);
        foreach (var task in await GetTasksAsync(cancellationToken))
        {
            var key = CalendarController.CalendarKey(task.List);
            if (!inCalendar.Contains(key) || !(task.List.HasEvents ? shownCalendars.Contains(key) : includeTaskLists)
                || task.IsCompleted || task.Due is not { } due)
            {
                continue;
            }

            var end = task.DueHasTime ? due.AddMinutes(30) : due.AddDays(1);
            if (due < to && end > from)
            {
                result.Add(new CalendarOccurrence(task.List, task.ObjectRemoteId, task.Uid, "☐ " + (task.Summary.Length > 0 ? task.Summary : T("(ohne Titel)")),
                    null, due, end, !task.DueHasTime, task.IsRecurring) { IsTask = true });
            }
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    /// <summary>Creates a task in <paramref name="target"/> or updates <paramref name="existing"/> (moving it if the list changed).</summary>
    /// <exception cref="RemoteConflictException">Someone changed the task on the server; sync and retry.</exception>
    public async Task<CalendarObject> SaveAsync(CalendarInfo target, TaskDraft draft, TaskItem? existing = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(draft);
        var current = existing is null ? null : await calendars.GetObjectAsync(existing.List, existing.ObjectRemoteId, cancellationToken);
        var moving = existing is not null && (existing.List.ConnectionId != target.ConnectionId || existing.List.RemoteId != target.RemoteId);
        var data = draft.ToICalendar(current?.ICalendarData);
        var saved = await calendars.SaveDataAsync(target, data, moving ? null : current, cancellationToken);
        if (moving && current is not null)
        {
            await calendars.DeleteEventAsync(existing!.List, current.RemoteId, cancellationToken);
        }

        return saved;
    }

    /// <exception cref="RemoteConflictException">Someone changed the task on the server; sync and retry.</exception>
    public Task<CalendarObject> SetCompletedAsync(TaskItem task, bool completed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return SaveAsync(task.List, TaskDraft.FromItem(task) with { IsCompleted = completed }, task, cancellationToken);
    }

    /// <exception cref="RemoteConflictException">Someone changed the task on the server; sync and retry.</exception>
    public Task DeleteAsync(TaskItem task, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return calendars.DeleteEventAsync(task.List, task.ObjectRemoteId, cancellationToken);
    }
}
