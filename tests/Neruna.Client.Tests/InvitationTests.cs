using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Neruna.Contracts.Discovery;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Mail;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;
using Neruna.Providers.Imap;

namespace Neruna.Client.Tests;

public class InvitationTests
{
    private static readonly string? ImapHost = Environment.GetEnvironmentVariable("NERUNA_TEST_IMAP_HOST");
    private static readonly string? DavUrl = Environment.GetEnvironmentVariable("NERUNA_TEST_DAV_URL");

    [Fact]
    public void Invitation_mail_is_read_answered_and_recorded()
    {
        var draft = EventDraft.New(new DateTime(2026, 10, 15, 14, 0, 0)) with
        {
            Summary = "Quartalsplanung",
            Location = "Sitzungszimmer Rigi",
            Attendees = [new EventAttendee("lea@example.com", "Lea Keller")],
            Organizer = "anna@example.com",
        };
        var data = draft.ToICalendar(null);
        var mail = ITip.Mail(new MailboxAddress("Anna", "anna@example.com"), [new MailboxAddress("Lea", "lea@example.com")], "Einladung", "Text", ITip.Request(data), "REQUEST");

        // Through MIME and back, as it arrives in Lea's inbox.
        using var stream = new MemoryStream();
        mail.WriteTo(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var invitation = ITip.FromMessage(MimeMessage.Load(stream, TestContext.Current.CancellationToken));

        Assert.NotNull(invitation);
        Assert.Equal(InvitationMethod.Request, invitation.Method);
        Assert.Equal("Quartalsplanung", invitation.Summary);
        Assert.Equal("anna@example.com", invitation.Organizer);
        Assert.Equal(Participation.NeedsAction, invitation.AttendeeFor("lea@example.com")?.Status);
        Assert.DoesNotContain("BEGIN:VALARM", invitation.ICalendarData, StringComparison.Ordinal);

        // Lea's copy carries her answer; the reply names only her.
        Assert.Contains("PARTSTAT=ACCEPTED", ITip.ForOwnCalendar(invitation.ICalendarData, "lea@example.com", Participation.Accepted), StringComparison.Ordinal);
        var reply = ITip.Parse(ITip.Reply(invitation, "lea@example.com", "Lea Keller", Participation.Tentative));
        Assert.Equal(InvitationMethod.Reply, reply?.Method);
        Assert.Equal(Participation.Tentative, Assert.Single(reply!.Attendees).Status);

        // Anna records it.
        var updated = ITip.ApplyReply(data, reply);
        Assert.NotNull(updated);
        Assert.Equal(Participation.Tentative, EventDraft.FromICalendar(updated).Attendees!.Single().Status);
        Assert.Null(ITip.ApplyReply(updated, reply));

        // A plain .ics attachment without METHOD is no invitation.
        Assert.Null(ITip.Parse(data));
    }

    [Fact]
    public async Task Invite_accept_update_and_cancel_between_two_accounts_on_real_servers()
    {
        Assert.SkipWhen(ImapHost is null || DavUrl is null, "NERUNA_TEST_IMAP_HOST / NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var anna = await PersonAsync("anna@example.com", "Anna Muster", "a" + tag, ct);
        await using var lea = await PersonAsync("lea@example.com", "Lea Keller", "l" + tag, ct);

        // Anna invites Lea (Radicale does not schedule, so Neruna e-mails the invitation).
        var annaCalendar = Assert.Single(await anna.Env.Calendar.GetCalendarsAsync(ct));
        Assert.False(await anna.Env.Calendar.SchedulesItselfAsync(annaCalendar, ct));
        var subject = "Planung " + tag;
        var draft = EventDraft.New(new DateTime(2026, 11, 3, 14, 0, 0)) with
        {
            Summary = subject,
            Location = "Rigi",
            Attendees = [new EventAttendee("lea@example.com", "Lea Keller")],
            Organizer = "anna@example.com",
        };
        var saved = await anna.Env.Calendar.SaveEventAsync(annaCalendar, draft, null, ct);
        Assert.Equal(1, await anna.Invitations.SendAfterSaveAsync(annaCalendar, null, saved.ICalendarData, ct));

        // Lea gets it, sees no conflict, accepts.
        var request = await WaitForInvitationAsync(lea, "Einladung: " + subject, ct);
        Assert.Equal(InvitationMethod.Request, request.Method);
        var state = await lea.Invitations.GetStateAsync(request, lea.Account, ct);
        Assert.Null(state.Item);
        await lea.Invitations.RespondAsync(request, Participation.Accepted, lea.Account, cancellationToken: ct);
        var inLeasCalendar = await lea.Env.Calendar.FindByUidAsync(request.Uid, ct);
        Assert.NotNull(inLeasCalendar);
        Assert.Equal(Participation.Accepted, (await lea.Invitations.GetStateAsync(request, lea.Account, ct)).MyAnswer);

        // Anna gets the answer and records it in her event.
        var reply = await WaitForInvitationAsync(anna, "Zugesagt: " + subject, ct);
        Assert.True(await anna.Invitations.ApplyReplyAsync(reply, ct));
        var annasEvent = (await anna.Env.Calendar.FindByUidAsync(request.Uid, ct))!.Value;
        Assert.Equal(Participation.Accepted, EventDraft.FromICalendar(annasEvent.Item.ICalendarData).Attendees!.Single().Status);

        // Anna moves it; Lea's accepted copy follows the update.
        var moved = EventDraft.FromICalendar(annasEvent.Item.ICalendarData) with { Start = new DateTime(2026, 11, 3, 16, 0, 0), End = new DateTime(2026, 11, 3, 17, 0, 0) };
        var resaved = await anna.Env.Calendar.SaveEventAsync(annaCalendar, moved, (annaCalendar, annasEvent.Item.RemoteId), ct);
        await anna.Invitations.SendAfterSaveAsync(annaCalendar, annasEvent.Item.ICalendarData, resaved.ICalendarData, ct);
        var update = await WaitForInvitationAsync(lea, "Aktualisiert: " + subject, ct);
        Assert.False((await lea.Invitations.GetStateAsync(update, lea.Account, ct)).IsOutdated);
        await lea.Invitations.RespondAsync(update, Participation.Accepted, lea.Account, cancellationToken: ct);
        var leasCopy = (await lea.Env.Calendar.FindByUidAsync(request.Uid, ct))!.Value;
        Assert.Equal(16, EventDraft.FromICalendar(leasCopy.Item.ICalendarData).Start.Hour);

        // Lea deletes it from her calendar after all: Anna is told "abgelehnt" and records it.
        Assert.True(await lea.Invitations.SendDeclineAsync(leasCopy.Calendar, leasCopy.Item.ICalendarData, ct));
        await lea.Env.Calendar.DeleteEventAsync(leasCopy.Calendar, leasCopy.Item.RemoteId, ct);
        var declined = await WaitForInvitationAsync(anna, "Abgelehnt: " + subject, ct);
        Assert.True(await anna.Invitations.ApplyReplyAsync(declined, ct));
        var annasNow = (await anna.Env.Calendar.FindByUidAsync(request.Uid, ct))!.Value;
        Assert.Equal(Participation.Declined, EventDraft.FromICalendar(annasNow.Item.ICalendarData).Attendees!.Single().Status);

        // Anna cancels the meeting anyway; Lea has nothing left to remove.
        Assert.Equal(1, await anna.Invitations.SendCancellationAsync(annaCalendar, annasNow.Item.ICalendarData, ct));
        await anna.Env.Calendar.DeleteEventAsync(annaCalendar, annasNow.Item.RemoteId, ct);
        var cancel = await WaitForInvitationAsync(lea, "Abgesagt: " + subject, ct);
        Assert.Equal(InvitationMethod.Cancel, cancel.Method);
        await lea.Invitations.ApplyCancelAsync(cancel, ct);
        Assert.Null(await lea.Env.Calendar.FindByUidAsync(request.Uid, ct));
    }

    private static async Task<Invitation> WaitForInvitationAsync(Person person, string subject, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await person.Env.Mail.SyncAllAsync(ct);
            var inbox = (await person.Env.Mail.GetFoldersAsync(person.Mail.Id, ct)).Single(f => f.Role == FolderRole.Inbox);
            if ((await person.Env.Mail.GetMessagesAsync(inbox, cancellationToken: ct)).FirstOrDefault(m => m.Subject.StartsWith(subject, StringComparison.Ordinal)) is { } found)
            {
                return ITip.FromMessage(await person.Env.Mail.GetMessageAsync(person.Mail, inbox, found.RemoteId, ct))
                       ?? throw new InvalidOperationException("No invitation in " + found.Subject);
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException("Mail not received: " + subject);
    }

    private static async Task<Person> PersonAsync(string address, string name, string davUser, CancellationToken ct)
    {
        var env = await TestEnvironment.CreateAsync(services =>
        {
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IProviderFactory<IMailProvider>>(sp => new ImapProviderFactory(sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        });

        // Each person gets an own calendar collection on Radicale.
        using (var raw = new HttpClient())
        {
            raw.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(davUser + ":geheim")));
            var body = "<?xml version=\"1.0\"?><D:mkcol xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\"><D:set><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Kalender</D:displayname></D:prop></D:set></D:mkcol>";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var response = await raw.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), new Uri(new Uri(DavUrl!), $"/{davUser}/kalender/")) { Content = new StringContent(body, Encoding.UTF8, "application/xml") }, ct);
                    response.EnsureSuccessStatusCode();
                    break;
                }
                catch (HttpRequestException) when (attempt < 3)
                {
                    await Task.Delay(500, ct);
                }
            }
        }

        var mail = new ServiceConnection(Guid.NewGuid(), ServiceKind.Mail, ProviderIds.Imap,
            new ImapSettings(ImapHost!, 3143, SocketSecurity.None, ImapHost!, 3025, SocketSecurity.None, address).ToDictionary());
        var calendar = new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav, new DavSettings(new Uri(DavUrl!), davUser).ToDictionary());
        var credentials = env.Get<ICredentialStore>();
        await credentials.SetSecretAsync(mail.Id, "geheim", ct);
        await credentials.SetSecretAsync(calendar.Id, "geheim", ct);
        var account = new Account(Guid.NewGuid(), name, address, [mail, calendar]);
        await env.Accounts.SaveAccountAsync(account, ct);
        await env.Calendar.SyncAllAsync(ct);
        return new Person(env, account, mail, env.Get<InvitationService>());
    }

    private sealed record Person(TestEnvironment Env, Account Account, ServiceConnection Mail, InvitationService Invitations) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Env.DisposeAsync();
    }
}
