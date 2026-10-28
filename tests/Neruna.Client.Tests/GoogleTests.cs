using System.Net;
using System.Text.Json;
using System.Web;
using System.Xml.Linq;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Providers.Dav;
using Neruna.Providers.Google;

namespace Neruna.Client.Tests;

/// <summary>Google: the sign-in as Google wants it, Gmail's servers, bearer tokens for DAV and Google Tasks as VTODO.</summary>
public class GoogleTests
{
    private sealed class Browser(Func<Uri, Task> visit) : IBrowserLauncher
    {
        public Task? Visits { get; private set; }

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Visits = Task.Run(() => visit(uri), cancellationToken);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_sign_in_asks_for_offline_access_on_127_0_0_1_and_sends_the_client_secret()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoints = GoogleAccount.Endpoints with { ClientSecret = "GOCSPX-test" };
        var stub = new StubHttpHandler();
        stub.Responses[endpoints.Token.AbsoluteUri] = (HttpStatusCode.OK, """{"access_token":"A","refresh_token":"R","expires_in":3599,"scope":"https://mail.google.com/ https://www.googleapis.com/auth/tasks"}""");
        using var tokenHttp = new HttpClient(stub);
        using var local = new HttpClient();

        var browser = new Browser(async authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            Assert.Equal(GoogleAccount.ClientId, query["client_id"]);
            Assert.Equal("offline", query["access_type"]);
            Assert.Equal("select_account consent", query["prompt"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.StartsWith("http://127.0.0.1:", query["redirect_uri"], StringComparison.Ordinal);
            Assert.Equal(["openid", "email", "profile", GoogleAccount.MailScope, GoogleAccount.CalendarScope, GoogleAccount.ContactsScope, GoogleAccount.TasksScope],
                query["scope"]!.Split(' '));
            await local.GetAsync($"{query["redirect_uri"]}?code=C0DE&state={query["state"]}", ct);
        });

        var tokens = await new OAuthClient(tokenHttp).SignInAsync(endpoints, "anna@gmail.com", browser, ct);
        await browser.Visits!;
        Assert.True(tokens.Grants(GoogleAccount.TasksScope));
        var body = HttpUtility.ParseQueryString(Assert.Single(stub.Sent).Body);
        Assert.Equal("GOCSPX-test", body["client_secret"]);
        Assert.NotNull(body["code_verifier"]);
        Assert.Null(body["scope"]); // Google keeps the granted scopes itself
    }

    [Fact]
    public void Signing_in_again_adds_newly_allowed_services_and_keeps_the_others()
    {
        var token = Guid.NewGuid();
        var mailOnly = GoogleConnectionFactory.Connections("eva@gmail.com", token, calendar: false, contacts: false);
        Assert.Equal([Neruna.Core.Accounts.ServiceKind.Mail], mailOnly.Select(c => c.Kind));

        // Calendar allowed now: added; mail keeps its id (and so its mails).
        var withCalendar = GoogleConnectionFactory.Adjust(mailOnly, "eva@gmail.com", token, calendar: true, contacts: false);
        Assert.Equal(mailOnly[0], withCalendar[0]);
        var calendar = Assert.Single(withCalendar, c => c.ProviderId == Neruna.Core.Providers.ProviderIds.GoogleCalendar);

        // Contacts too: the calendar stays the same connection; then calendar withdrawn: it is gone, contacts stay.
        var all = GoogleConnectionFactory.Adjust(withCalendar, "eva@gmail.com", token, calendar: true, contacts: true);
        Assert.Contains(calendar, all);
        Assert.Equal(3, all.Count);
        var withoutCalendar = GoogleConnectionFactory.Adjust(all, "eva@gmail.com", token, calendar: false, contacts: true);
        Assert.DoesNotContain(withoutCalendar, c => c.ProviderId == Neruna.Core.Providers.ProviderIds.GoogleCalendar);
        Assert.Equal(all.Where(c => c.ProviderId != Neruna.Core.Providers.ProviderIds.GoogleCalendar), withoutCalendar);
    }

    [Fact]
    public void Gmail_is_imap_and_smtp_over_tls_and_files_sent_mail_itself()
    {
        var settings = GoogleMailProviderFactory.Settings(new GoogleSettings("anna@gmail.com", Guid.NewGuid()).ToDictionary());
        Assert.Equal(("imap.gmail.com", 993, "smtp.gmail.com", 465, "anna@gmail.com"), (settings.ImapHost, settings.ImapPort, settings.SmtpHost, settings.SmtpPort, settings.Username));
        Assert.True(settings.ServerSavesSent);
        Assert.Equal(new Uri("https://apidata.googleusercontent.com/caldav/v2/anna%40gmail.com/user"), GoogleAccount.CalDav("anna@gmail.com"));
    }

    [Fact]
    public async Task A_bad_gateway_now_and_then_is_retried_for_reads_only()
    {
        // Google's CardDAV answers a PROPFIND with 502 every few syncs; the next attempt works.
        var ct = TestContext.Current.CancellationToken;
        var flaky = new FlakyHandler(failures: 1);
        using var http = new HttpClient(flaky);
        var client = new DavClient(http, DavClient.Bearer(_ => Task.FromResult("t")), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var found = await client.PropfindAsync(new Uri("https://www.googleapis.com/carddav/v1/principals/anna@gmail.com/lists/"), 1, [XName.Get("displayname", "DAV:")], ct);
        Assert.NotNull(found);
        Assert.Equal(2, flaky.Requests);

        // Not for a write: a repeated PUT could report a false conflict.
        flaky = new FlakyHandler(failures: 1);
        using var writes = new HttpClient(flaky);
        client = new DavClient(writes, DavClient.Bearer(_ => Task.FromResult("t")), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        try
        {
            await client.PutAsync(new Uri("https://dav.example/a.vcf"), "BEGIN:VCARD\r\nEND:VCARD\r\n", "text/vcard", null, create: true, ct);
        }
        catch (DavException)
        {
            // How the 502 is reported does not matter here – only that it was sent once.
        }

        Assert.Equal(1, flaky.Requests);
    }

    private sealed class FlakyHandler(int failures) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Requests <= failures
                ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("Bad Gateway") }
                : new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent("""<d:multistatus xmlns:d="DAV:"/>""", System.Text.Encoding.UTF8, "application/xml") });
        }
    }

    [Fact]
    public async Task Dav_requests_carry_the_current_bearer_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHttpHandler();
        var url = "https://dav.example/caldav/";
        stub.Responses[url] = (HttpStatusCode.MultiStatus, """<d:multistatus xmlns:d="DAV:"/>""");
        using var http = new HttpClient(stub);
        var token = "first";
        var client = new DavClient(http, DavClient.Bearer(_ => Task.FromResult(token)), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        await client.GetAsync(new Uri(url), ct);
        token = "renewed";
        await client.GetAsync(new Uri(url), ct);
        Assert.Equal(["Bearer first", "Bearer renewed"], stub.Authorizations);
    }

    [Fact]
    public async Task Google_tasks_are_read_page_by_page_and_written_back_as_google_wants_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubHttpHandler();
        const string api = "https://tasks.googleapis.com/tasks/v1/";
        stub.Responses[api + "users/@me/lists?maxResults=100"] = (HttpStatusCode.OK, """{"items":[{"id":"L1","title":"Meine Aufgaben"}]}""");
        stub.Responses[api + "lists/L1/tasks?maxResults=100&showCompleted=true&showHidden=true"] = (HttpStatusCode.OK,
            """{"items":[{"id":"T1","title":"Offerte, prüfen","notes":"Zeile 1\nZeile 2","status":"needsAction","due":"2026-11-03T00:00:00.000Z","updated":"2026-10-27T10:00:00.000Z"}],"nextPageToken":"P2"}""");
        stub.Responses[api + "lists/L1/tasks?maxResults=100&showCompleted=true&showHidden=true&pageToken=P2"] = (HttpStatusCode.OK,
            """{"items":[{"id":"T2","title":"Erledigt","status":"completed","completed":"2026-10-26T08:30:00.000Z","updated":"2026-10-26T08:30:00.000Z"}]}""");
        using var http = new HttpClient(stub);
        var tasks = new GoogleTasks(http, _ => Task.FromResult("TOKEN"));
        var connection = Guid.NewGuid();

        var list = Assert.Single(await tasks.GetListsAsync(connection, ct));
        Assert.Equal(("gtasks:L1", "Meine Aufgaben", CalendarContent.Tasks), (list.RemoteId, list.Name, list.Content));
        Assert.All(stub.Authorizations, a => Assert.Equal("Bearer TOKEN", a));

        // Only what is new or changed; T9 is gone at Google.
        var sync = await tasks.SyncAsync(list, new Dictionary<string, string?> { ["T2"] = "2026-10-26T08:30:00.000Z", ["T9"] = "x" }, ct);
        var open = Assert.Single(sync.AddedOrChanged);
        Assert.Equal(["T9"], sync.RemovedRemoteIds);
        var item = TaskDraft.ToItem(list, open)!;
        Assert.Equal(("Offerte, prüfen", "Zeile 1\nZeile 2", false), (item.Summary, item.Notes, item.IsCompleted));
        Assert.Equal(new DateTime(2026, 11, 3), item.Due!.Value.Date);

        // Done in Neruna: status and completion go to Google; reopened, the completion is cleared again.
        var done = TaskDraft.FromItem(item) with { IsCompleted = true };
        var body = GoogleTasks.ToGoogle(done.ToICalendar(open.ICalendarData));
        Assert.Equal(("completed", "2026-11-03T00:00:00.000Z"), (body["status"], body["due"]));
        Assert.NotNull(body["completed"]);
        var reopened = GoogleTasks.ToGoogle((done with { IsCompleted = false }).ToICalendar(open.ICalendarData));
        Assert.Equal("needsAction", reopened["status"]);
        Assert.Null(reopened["completed"]);
        Assert.Contains("\"completed\":null", JsonSerializer.Serialize(reopened), StringComparison.Ordinal);
    }
}
