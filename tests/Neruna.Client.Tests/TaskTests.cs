using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;

namespace Neruna.Client.Tests;

/// <summary>Tasks (VTODO): read, edited without losing what other programs wrote, and kept apart from the calendar.</summary>
public class TaskTests
{
    private const string ForeignTodo = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Other//EN
        BEGIN:VTODO
        UID:task-1@other
        DTSTAMP:20261001T080000Z
        SUMMARY:Bericht schreiben
        DESCRIPTION:Kapitel 3
        DUE;VALUE=DATE:20261015
        PRIORITY:5
        CATEGORIES:Projekt X
        X-OTHER-FIELD:bleibt
        STATUS:NEEDS-ACTION
        END:VTODO
        END:VCALENDAR
        """;

    [Fact]
    public void Tasks_are_read_and_edited_without_losing_foreign_fields()
    {
        var list = new CalendarInfo(Guid.NewGuid(), "list", "Aufgaben", null, false, Content: CalendarContent.Tasks);
        var task = TaskDraft.ToItem(list, new CalendarObject("r1", "e1", ForeignTodo))!;
        Assert.Equal(("task-1@other", "Bericht schreiben", "Kapitel 3", TaskPriority.Normal, false), (task.Uid, task.Summary, task.Notes, task.Priority, task.IsCompleted));
        Assert.False(task.DueHasTime);
        Assert.True(task.IsOverdue(new DateTimeOffset(2026, 10, 16, 8, 0, 0, TimeSpan.FromHours(2))));
        Assert.False(task.IsOverdue(new DateTimeOffset(2026, 10, 15, 23, 0, 0, TimeSpan.FromHours(2))));

        var done = (TaskDraft.FromItem(task) with { IsCompleted = true, Priority = TaskPriority.High }).ToICalendar(ForeignTodo);
        Assert.Contains("STATUS:COMPLETED", done, StringComparison.Ordinal);
        Assert.Contains("COMPLETED:", done, StringComparison.Ordinal);
        Assert.Contains("PRIORITY:1", done, StringComparison.Ordinal);
        Assert.Contains("X-OTHER-FIELD:bleibt", done, StringComparison.Ordinal);
        Assert.Contains("Projekt X", done, StringComparison.Ordinal);
        Assert.Contains("DUE;VALUE=DATE:20261015", done, StringComparison.Ordinal);

        var reopened = (TaskDraft.FromItem(TaskDraft.ToItem(list, new CalendarObject("r1", null, done))!) with { IsCompleted = false }).ToICalendar(done);
        Assert.Contains("STATUS:NEEDS-ACTION", reopened, StringComparison.Ordinal);
        Assert.DoesNotContain("\nCOMPLETED:", reopened, StringComparison.Ordinal);

        var fresh = new TaskDraft("Anrufen", null, new DateTime(2026, 10, 20, 14, 30, 0), true, TaskPriority.None, false).ToICalendar(null);
        var item = TaskDraft.ToItem(list, new CalendarObject("r2", null, fresh))!;
        Assert.True(item.DueHasTime);
        Assert.Equal(new DateTime(2026, 10, 20, 14, 30, 0), item.Due!.Value.LocalDateTime);
    }

    [Fact]
    public async Task Each_list_decides_on_its_own_whether_its_tasks_appear_in_the_calendar()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav, new Dictionary<string, string> { ["url"] = "https://dav.example/" });
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection]), ct);
        var store = env.Get<ICalendarStore>();
        var merged = await store.MergeCalendarsAsync(connection.Id,
        [
            new CalendarInfo(connection.Id, "mixed", "Privat", null, false, Content: CalendarContent.Events | CalendarContent.Tasks),
            new CalendarInfo(connection.Id, "info", "info@", null, false, Content: CalendarContent.Tasks),
        ], ct);
        var mixed = merged.Single(c => c.RemoteId == "mixed");
        var info = merged.Single(c => c.RemoteId == "info");
        Assert.Equal(CalendarContent.Events | CalendarContent.Tasks, mixed.Content);
        var evt = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\nDTSTAMP:20261001T080000Z\r\nDTSTART;VALUE=DATE:20261015\r\nDTEND;VALUE=DATE:20261016\r\nSUMMARY:Termin\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await store.ApplySyncResultAsync(mixed, new CalendarSyncResult("s", true, [new CalendarObject("e1", null, evt), new CalendarObject("t1", null, ForeignTodo)], []), ct);
        await store.ApplySyncResultAsync(info, new CalendarSyncResult("s", true, [new CalendarObject("t2", null, ForeignTodo.Replace("task-1@other", "task-2@other", StringComparison.Ordinal).Replace("Bericht schreiben", "Rechnung prüfen", StringComparison.Ordinal))], []), ct);

        var calendar = env.Calendar;
        Assert.Equal(2, (await calendar.GetTasksAsync(ct)).Count);
        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(2));
        var to = from.AddDays(7);
        async Task<string[]> TasksInCalendarAsync(params CalendarInfo[] calendars) =>
            [.. (await calendar.GetOccurrencesAsync(calendars, from, to, ct)).Where(o => o.IsTask).Select(o => o.Summary).Order(StringComparer.Ordinal)];

        // Default: tasks stay under "Aufgaben".
        Assert.Equal(["Termin"], (await calendar.GetOccurrencesAsync([mixed], from, to, ct)).Select(o => o.Summary));

        // The one switch of 0.1.14 is carried over to every list once.
        await env.Get<ISettingsStore>().SetAsync(SettingKeys.CalendarShowTasks, "true", ct);
        Assert.Equal(["☐ Bericht schreiben", "☐ Rechnung prüfen"], await TasksInCalendarAsync(mixed));
        Assert.True((await calendar.GetOccurrencesAsync([mixed], from, to, ct)).Single(o => o.Summary == "☐ Bericht schreiben").IsAllDay);
        Assert.Null(await env.Get<ISettingsStore>().GetAsync(SettingKeys.CalendarShowTasks, ct));

        // info@ only under "Aufgaben", not in the calendar.
        await calendar.SetTaskListInCalendarAsync(info, false, ct);
        Assert.Equal(["☐ Bericht schreiben"], await TasksInCalendarAsync(mixed));

        // Independent of "Aufgaben": hidden there, still in the calendar if wanted there.
        await calendar.SetTaskListVisibleAsync(mixed, false, ct);
        Assert.Equal(["☐ Bericht schreiben"], await TasksInCalendarAsync(mixed));

        // A mixed calendar switched off in the calendar takes its tasks with it.
        Assert.Empty(await TasksInCalendarAsync());

        // "Nur diesen Kalender anzeigen": pure task lists marked for the calendar step aside too.
        await calendar.SetTaskListInCalendarAsync(info, true, ct);
        Assert.Equal(["☐ Bericht schreiben", "☐ Rechnung prüfen"], await TasksInCalendarAsync(mixed));
        Assert.Equal(["☐ Bericht schreiben"], (await calendar.GetOccurrencesAsync([mixed], from, to, includeTaskLists: false, ct)).Where(o => o.IsTask).Select(o => o.Summary));
    }
}
