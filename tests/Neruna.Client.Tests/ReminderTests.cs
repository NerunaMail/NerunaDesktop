using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class ReminderTests
{

    [Fact]
    public async Task Due_reminder_can_be_snoozed_and_dismissed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var clock = new Clock(new DateTimeOffset(2026, 10, 12, 8, 50, 0, TimeSpan.Zero));
        var service = await CreateAsync(env, clock, Event("a", "Teamsitzung", clock.Now.AddMinutes(10), reminderMinutes: 15));

        var due = Assert.Single(await service.GetDueAsync(ct));
        Assert.Equal("Teamsitzung", due.Occurrence.Summary);
        Assert.Equal(clock.Now.AddMinutes(-5), due.DueAt);

        await service.SnoozeAsync(due, clock.Now.AddMinutes(5), ct);
        Assert.Empty(await service.GetDueAsync(ct));
        clock.Now = clock.Now.AddMinutes(5);
        var again = Assert.Single(await service.GetDueAsync(ct));

        await service.DismissAsync(again, ct);
        Assert.Empty(await service.GetDueAsync(ct));
    }

    [Fact]
    public async Task Reminders_come_at_their_time_per_occurrence_and_not_for_events_long_over()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var clock = new Clock(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var service = await CreateAsync(env, clock,
            Event("later", "Später", clock.Now.AddHours(3), reminderMinutes: 15),
            Event("none", "Ohne Erinnerung", clock.Now.AddMinutes(5), reminderMinutes: null),
            Event("over", "Vorbei", clock.Now.AddHours(-3), reminderMinutes: 15),
            Event("daily", "Stand-up", clock.Now.AddDays(-5).AddMinutes(10), reminderMinutes: 10, rule: "FREQ=DAILY"));

        // Only today's stand-up: not the not-yet-due, the alarm-less or the past one, not yesterday's occurrence.
        var due = Assert.Single(await service.GetDueAsync(ct));
        Assert.Equal("Stand-up", due.Occurrence.Summary);
        Assert.Equal(clock.Now.AddMinutes(10), due.Occurrence.Start);

        // Dismissing today's occurrence does not dismiss tomorrow's.
        await service.DismissAsync(due, ct);
        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(clock.Now.AddMinutes(10), Assert.Single(await service.GetDueAsync(ct)).Occurrence.Start);
    }

    [Fact]
    public void Editor_reminder_round_trips_and_keeps_foreign_alarms_when_unchanged()
    {
        var draft = EventDraft.New(new DateTime(2026, 10, 12, 9, 0, 0)) with { Summary = "Termin" };
        Assert.Equal(15, draft.ReminderMinutes);
        var data = draft.ToICalendar(null);
        Assert.Equal(15, EventDraft.FromICalendar(data).ReminderMinutes);

        var changed = EventDraft.FromICalendar(data) with { ReminderMinutes = 60 };
        Assert.Equal(60, EventDraft.FromICalendar(changed.ToICalendar(data)).ReminderMinutes);
        var none = EventDraft.FromICalendar(data) with { ReminderMinutes = null };
        Assert.DoesNotContain("BEGIN:VALARM", none.ToICalendar(data), StringComparison.Ordinal);

        // A server's e-mail alarm and a second pop-up survive editing the title.
        var foreign = data.Replace("END:VEVENT", "BEGIN:VALARM\r\nACTION:EMAIL\r\nTRIGGER:-P1D\r\nSUMMARY:x\r\nDESCRIPTION:x\r\nATTENDEE:mailto:anna@example.com\r\nEND:VALARM\r\nEND:VEVENT", StringComparison.Ordinal);
        var edited = (EventDraft.FromICalendar(foreign) with { Summary = "Neu" }).ToICalendar(foreign);
        Assert.Contains("ACTION:EMAIL", edited, StringComparison.Ordinal);
        Assert.Contains("TRIGGER:-PT15M", edited, StringComparison.Ordinal);
    }

    private static async Task<ReminderService> CreateAsync(TestEnvironment env, Clock clock, params CalendarObject[] objects)
    {
        var store = env.Get<ICalendarStore>();
        var connection = await env.AddAccountAsync(ServiceKind.Calendar, "caldav");
        var calendar = (await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "/anna/kalender/", "Kalender", null, false)]))[0];
        foreach (var item in objects)
        {
            await store.UpsertObjectAsync(calendar, item);
        }

        return new ReminderService(store, env.Get<IReminderStore>(), clock, NullLogger<ReminderService>.Instance);
    }

    private static CalendarObject Event(string uid, string summary, DateTimeOffset start, int? reminderMinutes, string? rule = null)
    {
        var alarm = reminderMinutes is { } minutes ? $"BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:{summary}\r\nTRIGGER:-PT{minutes}M\r\nEND:VALARM\r\n" : string.Empty;
        var recurrence = rule is null ? string.Empty : $"RRULE:{rule}\r\n";
        var data = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//DE\r\nBEGIN:VEVENT\r\n" +
                   $"UID:{uid}\r\nDTSTAMP:20261001T000000Z\r\nSUMMARY:{summary}\r\n" +
                   $"DTSTART:{start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{start.UtcDateTime.AddHours(1):yyyyMMdd'T'HHmmss'Z'}\r\n" +
                   recurrence + alarm + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        return new CalendarObject(uid + ".ics", null, data);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
