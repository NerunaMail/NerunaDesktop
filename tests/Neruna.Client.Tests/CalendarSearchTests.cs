using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

/// <summary>"Termine suchen": title, place, description and people; each event once, a series with its next date.</summary>
public class CalendarSearchTests
{
    private static string Event(string uid, string summary, string start, string end, string extra = "") => $"""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:{uid}
        DTSTAMP:20260101T000000Z
        DTSTART:{start}
        DTEND:{end}
        SUMMARY:{summary}
        {extra}END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public async Task Words_are_found_in_title_place_and_people_and_a_series_shows_its_next_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var store = env.Get<Neruna.Core.ICalendarStore>();
        var connection = await env.AddAccountAsync(Neruna.Core.Accounts.ServiceKind.Calendar, Neruna.Core.Providers.ProviderIds.Ics);
        var calendar = (await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "cal", "Kalender", null, false, null)], ct)).Single();
        await store.UpsertObjectAsync(calendar, new CalendarObject("a", null, Event("a", "Bauherrensitzung Ägeristrasse", "20261105T090000Z", "20261105T100000Z", "LOCATION:Baar\\, Sitzungszimmer\n")), ct);
        await store.UpsertObjectAsync(calendar, new CalendarObject("b", null, Event("b", "Mittagessen", "20250310T110000Z", "20250310T120000Z", "ATTENDEE;CN=Rainer Hager:mailto:rainer@example.com\n")), ct);
        await store.UpsertObjectAsync(calendar, new CalendarObject("c", null, Event("c", "Jahresgespräch", "20200115T080000Z", "20200115T090000Z", "RRULE:FREQ=YEARLY\nDESCRIPTION:Mit Rainer\n")), ct);
        await store.UpsertObjectAsync(calendar, new CalendarObject("d", null, Event("d", "Zahnarzt", "20261020T070000Z", "20261020T080000Z")), ct);

        var controller = env.Get<CalendarController>();
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        var place = Assert.Single(await controller.SearchAsync([calendar], "baar sitzung", now, ct));
        Assert.Equal("a", place.ObjectRemoteId);

        // "Rainer": an attendee of one, the description of a series – which shows its next date, once.
        var rainer = await controller.SearchAsync([calendar], "Rainer", now, ct);
        Assert.Equal(["b", "c"], rainer.Select(o => o.ObjectRemoteId));
        Assert.Equal(new DateTime(2027, 1, 15), rainer[1].Start.UtcDateTime.Date);

        Assert.Empty(await controller.SearchAsync([calendar], "Ferien", now, ct));
        Assert.Empty(await controller.SearchAsync([calendar], "  ", now, ct));
    }
}
