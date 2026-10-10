using System.Net;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using Neruna.Providers.Ics;

namespace Neruna.Client.Tests;

public class CalendarControllerTests
{
    private const string FeedUrl = "https://calendar.example.com/team.ics";

    // Weekly stand-up (Mondays 09:00 Zurich, 4 times) with one moved occurrence, a single all-day event, and a VTIMEZONE.
    private const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        X-WR-CALNAME:Team
        BEGIN:VTIMEZONE
        TZID:Europe/Zurich
        BEGIN:STANDARD
        DTSTART:19701025T030000
        RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:19700329T020000
        RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:standup@example.com
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Zurich:20261005T090000
        DTEND;TZID=Europe/Zurich:20261005T091500
        RRULE:FREQ=WEEKLY;COUNT=4
        SUMMARY:Stand-up
        END:VEVENT
        BEGIN:VEVENT
        UID:standup@example.com
        RECURRENCE-ID;TZID=Europe/Zurich:20261012T090000
        DTSTAMP:20261001T000000Z
        DTSTART;TZID=Europe/Zurich:20261012T100000
        DTEND;TZID=Europe/Zurich:20261012T101500
        SUMMARY:Stand-up (verschoben)
        END:VEVENT
        BEGIN:VEVENT
        UID:feiertag@example.com
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261008
        DTEND;VALUE=DATE:20261009
        SUMMARY:Betriebsausflug
        LOCATION:Rigi
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public async Task Ics_subscription_syncs_and_expands_recurrences()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.Http.Responses[FeedUrl] = (HttpStatusCode.OK, Feed);
        await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = "webcal://calendar.example.com/team.ics" });

        var report = await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, report.SucceededConnections);

        var calendar = Assert.Single(await env.Calendar.GetCalendarsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Team", calendar.Name);
        Assert.True(calendar.IsReadOnly);

        var from = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(2));
        var occurrences = await env.Calendar.GetOccurrencesAsync([calendar], from, from.AddDays(14), TestContext.Current.CancellationToken);

        Assert.Equal(
            ["Stand-up", "Betriebsausflug", "Stand-up (verschoben)"],
            occurrences.Select(o => o.Summary));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero), occurrences[0].Start.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero), occurrences[2].Start.ToUniversalTime());
        Assert.True(occurrences[1].IsAllDay);
        Assert.Equal("Rigi", occurrences[1].Location);
        Assert.True(occurrences[0].IsRecurring);
    }

    [Fact]
    public async Task Unchanged_feed_is_not_rewritten_and_changed_feed_replaces_events()
    {
        await using var env = await TestEnvironment.CreateAsync();
        env.Http.Responses[FeedUrl] = (HttpStatusCode.OK, Feed);
        await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = FeedUrl });
        await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);
        var calendar = Assert.Single(await env.Calendar.GetCalendarsAsync(TestContext.Current.CancellationToken));
        var stateAfterFirstSync = calendar.SyncState;

        await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);
        Assert.Equal(stateAfterFirstSync, Assert.Single(await env.Calendar.GetCalendarsAsync(TestContext.Current.CancellationToken)).SyncState);

        var withoutTrip = Feed[..Feed.IndexOf("BEGIN:VEVENT\nUID:feiertag", StringComparison.Ordinal)] + "END:VCALENDAR\n";
        env.Http.Responses[FeedUrl] = (HttpStatusCode.OK, withoutTrip);
        await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);

        var from = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.FromHours(2));
        var occurrences = await env.Calendar.GetOccurrencesAsync([calendar], from, from.AddDays(14), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(occurrences, o => o.Summary == "Betriebsausflug");
    }

    [Fact]
    public async Task Unreachable_feed_is_reported_as_failure()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = FeedUrl });

        var report = await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task Ics_provider_rejects_writes()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var connection = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.Ics, new Dictionary<string, string> { [IcsCalendarProvider.UrlSetting] = FeedUrl });
        await using var provider = env.Providers.CreateCalendar(connection);

        Assert.Equal(Neruna.Core.Calendar.CalendarProviderCapabilities.None, provider.Capabilities);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(null!, null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Calendars_switched_off_are_remembered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var calendar = new CalendarInfo(Guid.NewGuid(), "team", "Team", null, true);

        await env.Calendar.SetCalendarVisibleAsync(calendar, false, ct);
        Assert.NotNull(await env.Get<ISettingsStore>().GetAsync(SettingKeys.CalendarsHidden, ct)); // stored, so it outlives a restart
        Assert.Equal([CalendarController.CalendarKey(calendar)], await env.Calendar.GetHiddenCalendarsAsync(ct));

        await env.Calendar.SetCalendarVisibleAsync(calendar, true, ct);
        Assert.Empty(await env.Calendar.GetHiddenCalendarsAsync(ct));
    }
}
