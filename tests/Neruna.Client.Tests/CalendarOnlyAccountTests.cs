using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;

namespace Neruna.Client.Tests;

/// <summary>Accounts with calendars only (CalDAV without mail) and calendars marked "nur ansehen".</summary>
public class CalendarOnlyAccountTests
{
    private static readonly DateTime Start = new(2026, 11, 3, 14, 0, 0);

    [Fact]
    public async Task A_view_only_calendar_is_read_only_and_no_target_until_unmarked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var store = env.Get<ICalendarStore>();
        var connection = await env.AddAccountAsync(ServiceKind.Calendar, "caldav");
        CalendarInfo[] remote = [new(connection.Id, "/u/personal/", "Personal", null, false), new(connection.Id, "/u/holidays/", "Feiertage", null, false)];
        await store.MergeCalendarsAsync(connection.Id, remote, ct);
        var account = Assert.Single(await env.Accounts.GetAccountsAsync(ct));
        var holidays = (await env.Calendar.GetCalendarsAsync(ct)).Single(c => c.Name == "Feiertage");

        await env.Calendar.SetViewOnlyAsync(holidays, true, ct);
        var marked = (await env.Calendar.GetCalendarsAsync(ct)).Single(c => c.Name == "Feiertage");
        Assert.True(marked is { IsReadOnly: true, IsViewOnly: true });
        Assert.Equal(["Personal"], (await env.Get<InvitationService>().TargetCalendarsAsync(account, cancellationToken: ct)).Select(c => c.Name));

        // The server's answer at the next sync does not undo it.
        await store.MergeCalendarsAsync(connection.Id, remote, ct);
        Assert.True((await env.Calendar.GetCalendarsAsync(ct)).Single(c => c.Name == "Feiertage").IsViewOnly);

        await env.Calendar.SetViewOnlyAsync(marked, false, ct);
        Assert.False((await env.Calendar.GetCalendarsAsync(ct)).Single(c => c.Name == "Feiertage").IsReadOnly);
    }

    [Fact]
    public async Task Invitations_of_a_calendar_only_account_go_through_the_mail_account_with_the_same_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, "caldav", new Dictionary<string, string>());
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "Anna Muster", "anna@example.com", [calendar], "SOGo"), ct);
        await env.AddAccountAsync(ServiceKind.Mail, FakeMailProviderFactory.Id);
        var info = new CalendarInfo(calendar.Id, "/u/personal/", "Personal", null, false);
        var data = Meeting("anna@example.com");

        Assert.Equal(1, await env.Get<InvitationService>().SendAfterSaveAsync(info, null, data, ct));
        var sent = Assert.Single(env.MailServer.Sent);
        Assert.Equal("anna@example.com", sent.From.Mailboxes.Single().Address);
    }

    [Fact]
    public async Task Without_an_address_only_events_with_attendees_report_that_nothing_was_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, "caldav", new Dictionary<string, string>());
        await env.Accounts.SaveAccountAsync(new Account(Guid.NewGuid(), "dav", null, [calendar], "Radicale"), ct);
        var info = new CalendarInfo(calendar.Id, "/u/personal/", "Personal", null, false);
        var invitations = env.Get<InvitationService>();

        var alone = EventDraft.New(Start) with { Summary = "Zahnarzt" };
        Assert.Equal(0, await invitations.SendAfterSaveAsync(info, null, alone.ToICalendar(null), ct));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => invitations.SendAfterSaveAsync(info, null, Meeting(null), ct));
        Assert.Contains("keine E-Mail-Adresse", error.Message, StringComparison.Ordinal);
    }

    private static string Meeting(string? organizer) => (EventDraft.New(Start) with
    {
        Summary = "Planung",
        Attendees = [new EventAttendee("lea@example.com", "Lea Keller")],
        Organizer = organizer,
    }).ToICalendar(null);
}
