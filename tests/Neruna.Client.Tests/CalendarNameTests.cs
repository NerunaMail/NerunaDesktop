using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

public class CalendarNameTests
{
    [Fact]
    public async Task Own_display_name_is_shown_survives_a_sync_and_can_be_reset()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var store = env.Get<ICalendarStore>();
        var connection = await env.AddAccountAsync(ServiceKind.Calendar, "caldav");
        await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "/u/personal/", "Personal", null, false)], ct);

        var server = Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        Assert.Null(server.ServerName);

        await env.Calendar.SetDisplayNameAsync(server, "  Privat  ", ct);
        var renamed = Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        Assert.Equal(("Privat", "Personal"), (renamed.Name, renamed.ServerName));

        // The server's name comes again with every sync; the own one stays.
        await store.MergeCalendarsAsync(connection.Id, [new CalendarInfo(connection.Id, "/u/personal/", "Personal", null, false)], ct);
        Assert.Equal("Privat", Assert.Single(await env.Calendar.GetCalendarsAsync(ct)).Name);

        // The server's name typed in again, or empty: back to the server's name.
        await env.Calendar.SetDisplayNameAsync(renamed, "Personal", ct);
        Assert.Null(Assert.Single(await env.Calendar.GetCalendarsAsync(ct)).ServerName);
        await env.Calendar.SetDisplayNameAsync(renamed, "Arbeit", ct);
        await env.Calendar.SetDisplayNameAsync(renamed, null, ct);
        Assert.Equal("Personal", Assert.Single(await env.Calendar.GetCalendarsAsync(ct)).Name);
    }
}
