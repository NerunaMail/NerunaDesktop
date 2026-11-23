using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>
/// Nextcloud keeps deleted events in its trash bin and refuses a new one with the same UID (409, no-uid-conflict) –
/// an event the app no longer knows. Imported again, it is saved as a copy under a new UID.
/// </summary>
public class UidConflictImportTests
{
    [Fact]
    public async Task An_imported_event_whose_uid_is_in_the_servers_trash_is_saved_under_a_new_uid()
    {
        var (server, result) = await ImportAsync(live: false);

        Assert.Equal((1, 0, 0), (result.Added, result.Updated, result.Failed));
        Assert.Equal(2, server.Puts.Count);
        Assert.Contains(FakeNextcloud.TrashedUid, server.Puts[0].Data, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeNextcloud.TrashedUid, server.Puts[1].Data, StringComparison.Ordinal);
        Assert.Contains("Termin beim Coiffeur", server.Puts[1].Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_imported_event_already_in_the_calendar_but_not_yet_synced_is_updated_not_doubled()
    {
        var (server, result) = await ImportAsync(live: true);

        Assert.Equal((0, 1, 0), (result.Added, result.Updated, result.Failed));
        Assert.Equal(2, server.Puts.Count);
        Assert.Equal(FakeNextcloud.LiveHref, server.Puts[1].Path);
        Assert.Contains(FakeNextcloud.TrashedUid, server.Puts[1].Data, StringComparison.Ordinal);
    }

    private static async Task<(FakeNextcloud Server, ImportResult Result)> ImportAsync(bool live)
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeNextcloud();
        var http = new HttpClient(server);
        await using var env = await TestEnvironment.CreateAsync(services =>
            services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>())));
        var connection = await env.AddAccountAsync(ServiceKind.Calendar, ProviderIds.CalDav, new DavSettings(new Uri("https://cloud.example/remote.php/dav/"), "anna").ToDictionary());
        var offered = await env.Calendar.DiscoverAsync(connection, ct);
        await env.Calendar.SetSelectionAsync(connection, [.. offered.Select(c => c.Calendar.RemoteId)], [.. offered.Select(c => c.Calendar.RemoteId)], ct);
        var calendar = Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        server.Live = live; // put there by another device after the last sync

        var events = CalendarImport.Read("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//DE\r\nBEGIN:VEVENT\r\nUID:" + FakeNextcloud.TrashedUid
            + "\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:20261203T090000Z\r\nDTEND:20261203T100000Z\r\nSUMMARY:Termin beim Coiffeur\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return (server, await env.Calendar.ImportAsync(calendar, events, cancellationToken: ct));
    }

    // Live: the event is in the calendar under another name (put there by another device), else only in the trash bin.
    private sealed class FakeNextcloud : HttpMessageHandler
    {
        public const string TrashedUid = "termin-2026-12-03@example.com";
        public const string LiveHref = Home + "personal/frisoer-buchung.ics";
        private const string Home = "/remote.php/dav/calendars/anna/";

        private const string LiveData = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Other//DE\r\nBEGIN:VEVENT\r\nUID:" + TrashedUid
            + "\r\nDTSTAMP:20261101T000000Z\r\nDTSTART:20261203T090000Z\r\nDTEND:20261203T100000Z\r\nSUMMARY:Coiffeur\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

        public bool Live { get; set; }

        public List<(string Path, string Data)> Puts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var data = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Put)
            {
                Puts.Add((path, data));
                return path != LiveHref && data.Contains(TrashedUid, StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><d:error xmlns:d=\"DAV:\" xmlns:s=\"http://sabredav.org/ns\"><s:exception>OCA\\DAV\\Exception\\UidConflict</s:exception>"
                            + "<s:message>Calendar object with uid already exists in this calendar collection.</s:message><cal:no-uid-conflict xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><d:href>" + LiveHref + "</d:href></cal:no-uid-conflict></d:error>", Encoding.UTF8, "application/xml"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.Created) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"2\"") } };
            }

            if (request.Method.Method == "REPORT")
            {
                // No sync-collection here (the ETags are compared); the multiget brings the live event.
                return data.Contains("sync-collection", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.NotImplemented)
                    : MultiStatus(Live ? Response(LiveHref, "<D:getetag>\"1\"</D:getetag><C:calendar-data>" + System.Security.SecurityElement.Escape(LiveData) + "</C:calendar-data>") : string.Empty);
            }

            var body = path switch
            {
                "/remote.php/dav/" => Response(path, "<D:resourcetype><D:collection/></D:resourcetype><C:calendar-home-set><D:href>" + Home + "</D:href></C:calendar-home-set>"),
                Home => Response(Home, "<D:resourcetype><D:collection/></D:resourcetype>")
                        + Response(Home + "personal/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Persönlich</D:displayname>"),
                Home + "personal/" => Response(Home + "personal/", "<D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>Persönlich</D:displayname>")
                                      + (Live && request.Headers.TryGetValues("Depth", out var depth) && depth.Contains("1") ? Response(LiveHref, "<D:getetag>\"1\"</D:getetag><D:resourcetype/>") : string.Empty),
                _ => null,
            };

            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : MultiStatus(body);
        }

        private static HttpResponseMessage MultiStatus(string body) => new((HttpStatusCode)207)
        {
            Content = new StringContent($"<?xml version=\"1.0\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">{body}</D:multistatus>", Encoding.UTF8, "application/xml"),
        };

        private static string Response(string href, string props) =>
            $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{props}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";
    }
}
