using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>
/// Finding CalDAV/CardDAV (RFC 6764 SRV/TXT, the DAV header), calendars of other people through delegation
/// (group membership, calendar proxies) and Nextcloud's sign-in in the browser (Login Flow v2).
/// </summary>
public class DavDiscoveryTests
{
    private sealed class FakeDns(Dictionary<string, SrvTarget[]> srv, Dictionary<string, string[]> txt) : IDavDns
    {
        public List<string> Asked { get; } = [];

        public Task<IReadOnlyList<SrvTarget>> SrvAsync(string name, CancellationToken cancellationToken)
        {
            Asked.Add(name);
            return Task.FromResult<IReadOnlyList<SrvTarget>>(srv.GetValueOrDefault(name) ?? []);
        }

        public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(txt.GetValueOrDefault(name) ?? []);
    }

    [Fact]
    public void The_srv_record_is_chosen_as_rfc_2782_says()
    {
        Assert.Null(DavServiceRecords.Best([]));
        var only = new SrvTarget("dav.example.com", 443, 10, 0);
        Assert.Equal(only, DavServiceRecords.Best([only]));

        // The lowest priority wins; among equals the weight decides how often.
        SrvTarget[] records = [new("backup.example.com", 443, 20, 100), new("a.example.com", 443, 10, 10), new("b.example.com", 8443, 10, 90)];
        var random = new Random(42);
        var picks = Enumerable.Range(0, 1000).Select(_ => DavServiceRecords.Best(records, random)!.Host).ToList();
        Assert.DoesNotContain("backup.example.com", picks);
        Assert.InRange(picks.Count(p => p == "b.example.com"), 820, 960);
    }

    [Fact]
    public async Task Srv_and_txt_records_give_the_urls_to_try()
    {
        var ct = TestContext.Current.CancellationToken;
        var dns = new FakeDns(
            new() { ["_caldavs._tcp.example.com"] = [new("dav.hoster.example", 8443, 0, 0)], ["_carddavs._tcp.example.com"] = [new(".", 0, 0, 0)] },
            new() { ["_caldavs._tcp.example.com"] = ["path=/dav/calendars/"] });

        Assert.Equal(["https://dav.hoster.example:8443/dav/calendars/", "https://dav.hoster.example:8443/.well-known/caldav", "https://dav.hoster.example:8443/"],
            (await DavServiceRecords.CandidatesAsync(dns, "example.com", "caldav", cancellationToken: ct)).Select(u => u.AbsoluteUri));

        // Target "." means: no such service. Only the TLS variants are asked.
        Assert.Empty(await DavServiceRecords.CandidatesAsync(dns, "example.com", "carddav", cancellationToken: ct));
        Assert.DoesNotContain(dns.Asked, n => n.StartsWith("_caldav._tcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_probe_uses_srv_first_and_skips_servers_that_say_they_do_not_offer_the_service()
    {
        var ct = TestContext.Current.CancellationToken;
        var dns = new FakeDns(new() { ["_caldavs._tcp.example.com"] = [new("dav.hoster.example", 443, 0, 0)] }, new() { ["_caldavs._tcp.example.com"] = ["path=/cal/"] });
        var server = new Routes
        {
            ["PROPFIND https://dav.hoster.example/cal/"] = () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ["OPTIONS https://dav.hoster.example/cal/"] = () => Options("1, 2, 3, calendar-access, calendar-schedule"),
            // A server that answers PROPFIND but is no CardDAV server.
            ["PROPFIND https://example.com/.well-known/carddav"] = () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            ["OPTIONS https://example.com/.well-known/carddav"] = () => Options("1, 2, 3, calendar-access"),
        };
        using var http = new HttpClient(server);

        var caldav = await DavProbe.FindAsync(http, ["example.com"], "caldav", "example.com", dns, ct);
        Assert.Equal("https://dav.hoster.example/cal/", caldav!.Url.AbsoluteUri);
        Assert.False(caldav.IsElsewhere); // announced by the domain's owner in the DNS

        Assert.Null(await DavProbe.FindAsync(http, ["example.com"], "carddav", "example.com", dns, ct));
        Assert.False(await DavProbe.OffersAsync(http, new Uri("https://example.com/.well-known/carddav"), "carddav", ct));
        Assert.True(await DavProbe.OffersAsync(http, new Uri("https://dav.hoster.example/unknown/"), "carddav", ct)); // no answer: assumed
    }

    [Fact]
    public async Task Calendars_of_other_people_are_found_through_delegation_and_only_synced_once_chosen()
    {
        var ct = TestContext.Current.CancellationToken;
        var dav = new FakeDelegationServer();
        var http = new HttpClient(dav);
        await using var env = await TestEnvironment.CreateAsync(services =>
        {
            services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IProviderFactory<IContactProvider>>(sp => new CardDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        });
        var calendar = await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.CalDav, new DavSettings(new Uri("https://dav.example/"), "anna").ToDictionary());

        var found = await env.Calendar.DiscoverAsync(calendar, ct);
        Assert.Equal([("Arbeit", null), ("Persönlich", null), ("Lea privat", "Lea Keller"), ("Team-Termine", "Team")],
            found.Select(c => (c.Calendar.Name, c.Calendar.SharedBy)).OrderBy(c => c.SharedBy).ThenBy(c => c.Name));

        // Without a choice: the own calendars only (both home sets); chosen: Lea's too.
        await env.Calendar.SyncConnectionAsync(calendar, ct);
        Assert.Equal(["Arbeit", "Persönlich"], (await env.Calendar.GetCalendarsAsync(ct)).Select(c => c.Name).Order());
        await env.Calendar.SetSelectionAsync(calendar, [.. found.Where(c => c.Calendar.Name != "Team-Termine").Select(c => c.Calendar.RemoteId)], [.. found.Select(c => c.Calendar.RemoteId)], ct);
        Assert.Equal(["Arbeit", "Lea privat", "Persönlich"], (await env.Calendar.GetCalendarsAsync(ct)).Select(c => c.Name).Order());

        // Each principal is asked once per discovery, even if reached twice (proxy group and proxy-for).
        dav.Asked.Clear();
        await env.Calendar.DiscoverAsync(calendar, ct);
        Assert.Equal(1, dav.Asked.Count(a => a == "PROPFIND /principals/lea/"));

        // Address books: the same for CardDAV (group membership only).
        var contacts = await env.AddAccountAsync(ServiceKind.Contacts, ProviderIds.CardDav, new DavSettings(new Uri("https://dav.example/"), "anna").ToDictionary());
        var books = await env.Contacts.DiscoverAsync(contacts, ct);
        Assert.Equal([("Kontakte", null), ("Team-Adressen", "Team")], books.Select(b => (b.AddressBook.Name, b.AddressBook.SharedBy)).OrderBy(b => b.SharedBy));
    }

    [Fact]
    public async Task Nextcloud_gives_an_app_password_after_the_sign_in_in_the_browser()
    {
        var ct = TestContext.Current.CancellationToken;
        var polls = 0;
        var server = new Routes
        {
            ["POST https://cloud.example.com/nextcloud/index.php/login/v2"] = () => Json("""
                {"poll":{"token":"secret-token","endpoint":"https://cloud.example.com/nextcloud/login/v2/poll"},"login":"https://cloud.example.com/nextcloud/login/v2/flow/abc"}
                """),
            ["POST https://cloud.example.com/nextcloud/login/v2/poll"] = () => ++polls < 3
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("""{"server":"https://cloud.example.com/nextcloud","loginName":"anna","appPassword":"app-pass-123"}"""),
        };
        using var http = new HttpClient(server);
        var browser = new RecordingBrowser();

        var login = await NextcloudLoginFlow.SignInAsync(http, "https://cloud.example.com/nextcloud/index.php/apps/dashboard/", browser, TimeSpan.FromMilliseconds(5), ct);

        Assert.Equal(("anna", "app-pass-123", "https://cloud.example.com/nextcloud/remote.php/dav/"), (login.LoginName, login.AppPassword, login.DavUrl.AbsoluteUri));
        Assert.Equal("https://cloud.example.com/nextcloud/login/v2/flow/abc", browser.Opened.Single().AbsoluteUri);
        Assert.Equal(3, polls);
        Assert.Contains("token=secret-token", server.Bodies.Last(), StringComparison.Ordinal);
        Assert.Equal("Neruna", server.UserAgents.First());

        // Not a Nextcloud: said plainly.
        await Assert.ThrowsAsync<NextcloudLoginException>(() => NextcloudLoginFlow.SignInAsync(http, "mail.example.com", browser, TimeSpan.FromMilliseconds(5), ct));
        Assert.Equal("https://cloud.example.com/", NextcloudLoginFlow.ParseServer(" cloud.example.com ")!.AbsoluteUri);
        Assert.Null(NextcloudLoginFlow.ParseServer(""));
    }

    // ── helpers ──

    private static HttpResponseMessage Options(string dav)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("DAV", dav);
        return response;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];

        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            Opened.Add(uri);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers "METHOD absolute-url" from a table; records bodies and user agents.</summary>
    private sealed class Routes : HttpMessageHandler, IEnumerable<KeyValuePair<string, Func<HttpResponseMessage>>>
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<string> Bodies { get; } = [];

        public List<string> UserAgents { get; } = [];

        public Func<HttpResponseMessage> this[string key] { set => _routes[key] = value; }

        public IEnumerator<KeyValuePair<string, Func<HttpResponseMessage>>> GetEnumerator() => _routes.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return _routes.TryGetValue($"{request.Method.Method} {request.RequestUri!.AbsoluteUri}", out var answer) ? answer() : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// A DAV server where Anna has two calendar homes, is in Lea's write-proxy group (Apple/CalendarServer style) and
    /// reads the group "Team" as a proxy; Team also has an address book home.
    /// </summary>
    private sealed class FakeDelegationServer : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Asked.Add($"{request.Method.Method} {path}");
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var depth = request.Headers.TryGetValues("Depth", out var d) ? d.First() : "0";
            if (request.Method.Method != "PROPFIND")
            {
                // OPTIONS: fine; REPORT (sync-collection): not offered, the client compares ETags instead.
                return new HttpResponseMessage(request.Method == HttpMethod.Options ? HttpStatusCode.OK : HttpStatusCode.NotImplemented);
            }

            var xml = (path, depth) switch
            {
                ("/" or "/.well-known/caldav" or "/.well-known/carddav", _) => Resource(path, "<D:resourcetype><D:collection/></D:resourcetype><D:current-user-principal><D:href>/principals/anna/</D:href></D:current-user-principal>"),
                ("/principals/anna/", _) => Resource(path, "<D:resourcetype><D:principal/></D:resourcetype><D:displayname>Anna</D:displayname>"
                    + "<C:calendar-home-set><D:href>/cal/anna/</D:href><D:href>/cal/anna-work/</D:href></C:calendar-home-set>"
                    + "<R:addressbook-home-set><D:href>/card/anna/</D:href></R:addressbook-home-set>"
                    + "<D:group-membership><D:href>/principals/lea/calendar-proxy-write/</D:href><D:href>/principals/team/</D:href></D:group-membership>"
                    + "<S:calendar-proxy-read-for><D:href>/principals/lea/</D:href></S:calendar-proxy-read-for>"),
                ("/principals/lea/calendar-proxy-write/", _) => Resource(path, "<D:resourcetype><D:principal/><S:calendar-proxy-write/></D:resourcetype>"),
                ("/principals/lea/", _) => Resource(path, "<D:resourcetype><D:principal/></D:resourcetype><D:displayname>Lea Keller</D:displayname><C:calendar-home-set><D:href>/cal/lea/</D:href></C:calendar-home-set>"),
                ("/principals/team/", _) => Resource(path, "<D:resourcetype><D:principal/></D:resourcetype><D:displayname>Team</D:displayname>"
                    + "<C:calendar-home-set><D:href>/cal/team/</D:href></C:calendar-home-set><R:addressbook-home-set><D:href>/card/team/</D:href></R:addressbook-home-set>"),
                ("/cal/anna/", "1") => Home(path) + Calendar(path + "personal/", "Persönlich"),
                ("/cal/anna-work/", "1") => Home(path) + Calendar(path + "work/", "Arbeit"),
                ("/cal/lea/", "1") => Home(path) + Calendar(path + "private/", "Lea privat"),
                ("/cal/team/", "1") => Home(path) + Calendar(path + "events/", "Team-Termine"),
                ("/card/anna/", "1") => Home(path) + Book(path + "contacts/", "Kontakte"),
                ("/card/team/", "1") => Home(path) + Book(path + "shared/", "Team-Adressen"),
                (_, _) when path.StartsWith("/cal/", StringComparison.Ordinal) || path.StartsWith("/card/", StringComparison.Ordinal) =>
                    Resource(path, "<D:resourcetype><D:collection/></D:resourcetype>"),
                _ => null,
            };

            return xml is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage((HttpStatusCode)207)
                {
                    Content = new StringContent("<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:R=\"urn:ietf:params:xml:ns:carddav\" xmlns:S=\"http://calendarserver.org/ns/\">"
                        + xml + "</D:multistatus>", Encoding.UTF8, "application/xml"),
                };
        }

        private static string Resource(string href, string props) => $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";

        private static string Home(string href) => Resource(href, "<D:resourcetype><D:collection/></D:resourcetype>");

        private static string Calendar(string href, string name) =>
            Resource(href, $"<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>{name}</D:displayname><D:current-user-privilege-set><D:privilege><D:all/></D:privilege></D:current-user-privilege-set>");

        private static string Book(string href, string name) => Resource(href, $"<D:resourcetype><D:collection/><R:addressbook/></D:resourcetype><D:displayname>{name}</D:displayname>");
    }
}
