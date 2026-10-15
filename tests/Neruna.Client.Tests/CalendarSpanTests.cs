using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

/// <summary>When an event can show up: the views and the reminders only parse those of the time they look at.</summary>
public class CalendarSpanTests
{
    private static string Event(string rule, string start = "20261005T090000Z", string end = "20261005T100000Z") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:e@example.com
        DTSTAMP:20261001T000000Z
        DTSTART:{start}
        DTEND:{end}
        {rule}SUMMARY:Termin
        END:VEVENT
        END:VCALENDAR
        """;

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void A_single_event_a_counted_series_and_an_endless_one()
    {
        var single = CalendarSpan.Of(Event(""));
        Assert.Equal(At("2026-10-04T09:00Z").ToUnixTimeMilliseconds(), single.Start); // a day of slack around it
        Assert.Equal(At("2026-10-06T10:00Z").ToUnixTimeMilliseconds(), single.End);

        var counted = CalendarSpan.Of(Event("RRULE:FREQ=WEEKLY;COUNT=3\n"));
        Assert.Equal(At("2026-10-19T10:00Z").AddDays(1).ToUnixTimeMilliseconds(), counted.End); // third occurrence, a day of slack

        var endless = CalendarSpan.Of(Event("RRULE:FREQ=YEARLY\n"));
        Assert.Equal(single.Start, endless.Start);
        Assert.Null(endless.End);

        Assert.Equal(CalendarSpan.Always, CalendarSpan.Of("not iCalendar"));
    }

    [Fact]
    public async Task Views_only_get_the_events_of_their_time_and_old_rows_get_their_span()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var store = env.Get<Neruna.Core.ICalendarStore>();
        var connection = await env.AddAccountAsync(Neruna.Core.Accounts.ServiceKind.Calendar, Neruna.Core.Providers.ProviderIds.Ics);
        var calendar = (await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "cal", "Kalender", null, false, null)], ct)).Single();
        await store.UpsertObjectAsync(calendar, new CalendarObject("old", null, Event("", "20160105T090000Z", "20160105T100000Z")), ct);
        await store.UpsertObjectAsync(calendar, new CalendarObject("now", null, Event("")), ct);
        await store.UpsertObjectAsync(calendar, new CalendarObject("birthday", null, Event("RRULE:FREQ=YEARLY\n", "20100105T090000Z", "20100105T100000Z")), ct);

        string[] Ids(IEnumerable<CalendarObject> items) => [.. items.Select(i => i.RemoteId).Order(StringComparer.Ordinal)];
        Assert.Equal(["birthday", "now"], Ids(await store.GetObjectsAsync(connection.Id, "cal", At("2026-10-01T00:00Z"), At("2026-10-08T00:00Z"), ct)));
        Assert.Equal(["birthday", "now", "old"], Ids(await store.GetObjectsAsync(connection.Id, "cal", ct)));

        // A row from before the span was kept: included, and worked out on the way.
        await using (var db = await env.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Neruna.Storage.NerunaDbContext>>().CreateDbContextAsync(ct))
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "UPDATE CalendarObjects SET SpanStart = NULL, SpanEnd = NULL", ct);
        }

        Assert.Equal(["birthday", "now"], Ids(await store.GetObjectsAsync(connection.Id, "cal", At("2026-10-01T00:00Z"), At("2026-10-08T00:00Z"), ct)));
    }
}
