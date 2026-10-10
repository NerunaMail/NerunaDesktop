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
    public async Task Tasks_of_mixed_calendars_stay_out_of_the_calendar_unless_wanted_and_hidden_lists_stay_hidden()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav, new Dictionary<string, string> { ["url"] = "https://dav.example/" });
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna", "anna@example.com", [connection]), ct);
        var store = env.Get<ICalendarStore>();
        var mixed = (await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "mixed", "Privat", null, false, Content: CalendarContent.Events | CalendarContent.Tasks)], ct)).Single();
        Assert.Equal(CalendarContent.Events | CalendarContent.Tasks, mixed.Content);
        var evt = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:e1\r\nDTSTAMP:20261001T080000Z\r\nDTSTART;VALUE=DATE:20261015\r\nDTEND;VALUE=DATE:20261016\r\nSUMMARY:Termin\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await store.ApplySyncResultAsync(mixed, new CalendarSyncResult("s", true, [new CalendarObject("e1", null, evt), new CalendarObject("t1", null, ForeignTodo)], []), ct);

        var calendar = env.Calendar;
        var task = Assert.Single(await calendar.GetTasksAsync(ct));
        Assert.Equal("Bericht schreiben", task.Summary);

        var from = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.FromHours(2));
        var to = from.AddDays(7);
        Assert.Equal(["Termin"], (await calendar.GetOccurrencesAsync([mixed], from, to, ct)).Select(o => o.Summary));

        await env.Get<ISettingsStore>().SetAsync(SettingKeys.CalendarShowTasks, "true", ct);
        var shown = await calendar.GetOccurrencesAsync([mixed], from, to, ct);
        Assert.Equal(2, shown.Count);
        Assert.True(Assert.Single(shown, o => o.IsTask).IsAllDay);

        // Hidden under "Aufgaben": not in the calendar either.
        await calendar.SetTaskListVisibleAsync(mixed, false, ct);
        Assert.Contains(CalendarController.TaskListKey(mixed), await calendar.GetHiddenTaskListsAsync(ct));
        Assert.DoesNotContain(await calendar.GetOccurrencesAsync([mixed], from, to, ct), o => o.IsTask);
    }
}
