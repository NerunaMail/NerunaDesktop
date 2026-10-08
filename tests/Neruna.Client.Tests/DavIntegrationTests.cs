using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>
/// CalDAV/CardDAV against a real server. Skipped unless NERUNA_TEST_DAV_URL is set, e.g.:
/// <code>
/// docker run -d --rm -p 5232:5232 tomsquest/docker-radicale
/// NERUNA_TEST_DAV_URL=http://127.0.0.1:5232/ dotnet test
/// </code>
/// Every test uses its own user, so runs never interfere.
/// </summary>
public sealed class DavIntegrationTests : IDisposable
{
    private static readonly string? ServerUrl = Environment.GetEnvironmentVariable("NERUNA_TEST_DAV_URL");

    private readonly string _user = "t" + Guid.NewGuid().ToString("N")[..10];
    private readonly HttpClient _raw = new(new HttpClientHandler { AllowAutoRedirect = false });

    public DavIntegrationTests()
    {
        _raw.DefaultRequestHeaders.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_user + ":geheim")));
    }

    public void Dispose() => _raw.Dispose();

    [Fact]
    public async Task CalDav_discovers_syncs_writes_and_detects_conflicts()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/arbeit/", "calendar", "Arbeit", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav);

        // Discovery from the server root via current-user-principal → calendar-home-set.
        Assert.Empty((await env.Calendar.SyncAllAsync(ct)).Failures);
        var calendar = Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        Assert.Equal("Arbeit", calendar.Name);
        Assert.Equal("#C239B3", calendar.Color);
        Assert.False(calendar.IsReadOnly);

        // Create through Neruna.
        var start = new DateTime(2026, 10, 14, 9, 0, 0);
        var saved = await env.Calendar.SaveEventAsync(calendar, EventDraft.New(start) with { Summary = "Planung", Location = "Pilatus" }, null, ct);
        Assert.NotNull(saved.ETag);
        var onServer = await GetStringAsync(saved.RemoteId, ct);
        Assert.Contains("SUMMARY:Planung", onServer, StringComparison.Ordinal);

        // Another client edits the event → incremental sync picks it up.
        await PutAsync(saved.RemoteId, onServer.Replace("SUMMARY:Planung", "SUMMARY:Planung (verschoben)", StringComparison.Ordinal), "text/calendar", ct);
        Assert.Empty((await env.Calendar.SyncAllAsync(ct)).Failures);
        calendar = Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        Assert.StartsWith("sync:", calendar.SyncState, StringComparison.Ordinal);
        var occurrences = await env.Calendar.GetOccurrencesAsync([calendar], new DateTimeOffset(start.Date), new DateTimeOffset(start.Date.AddDays(1)), ct);
        Assert.Equal("Planung (verschoben)", Assert.Single(occurrences).Summary);

        // The other client edits again; our copy is now stale → conflict instead of silent overwrite.
        var current = await GetStringAsync(saved.RemoteId, ct);
        await PutAsync(saved.RemoteId, current.Replace("Pilatus", "Rigi", StringComparison.Ordinal), "text/calendar", ct);
        var draft = EventDraft.FromICalendar(current) with { Summary = "Meine Änderung" };
        await Assert.ThrowsAsync<RemoteConflictException>(() => env.Calendar.SaveEventAsync(calendar, draft, (calendar, saved.RemoteId), ct));

        // After a sync the edit goes through.
        await env.Calendar.SyncAllAsync(ct);
        await env.Calendar.SaveEventAsync(calendar, draft, (calendar, saved.RemoteId), ct);
        Assert.Contains("SUMMARY:Meine Änderung", await GetStringAsync(saved.RemoteId, ct), StringComparison.Ordinal);

        // Delete through Neruna.
        await env.Calendar.DeleteEventAsync(calendar, saved.RemoteId, ct);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(HttpMethod.Get, saved.RemoteId, ct));

        // Deleted by another client → disappears locally.
        var foreign = $"/{_user}/arbeit/fremd.ics";
        await PutAsync(foreign, "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:fremd\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261014T120000Z\r\nDTEND:20261014T130000Z\r\nSUMMARY:Fremd\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "text/calendar", ct);
        await env.Calendar.SyncAllAsync(ct);
        Assert.Single(await env.Calendar.GetOccurrencesAsync([calendar], new DateTimeOffset(start.Date), new DateTimeOffset(start.Date.AddDays(1)), ct));
        await StatusAsync(HttpMethod.Delete, foreign, ct);
        await env.Calendar.SyncAllAsync(ct);
        Assert.Empty(await env.Calendar.GetOccurrencesAsync([calendar], new DateTimeOffset(start.Date), new DateTimeOffset(start.Date.AddDays(1)), ct));
    }

    [Fact]
    public async Task CalDav_moves_event_between_calendars()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/a/", "calendar", "A", ct);
        await MakeCollectionAsync($"{_user}/b/", "calendar", "B", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav);
        await env.Calendar.SyncAllAsync(ct);
        var calendars = await env.Calendar.GetCalendarsAsync(ct);
        var a = calendars.Single(c => c.Name == "A");
        var b = calendars.Single(c => c.Name == "B");

        var saved = await env.Calendar.SaveEventAsync(a, EventDraft.New(new DateTime(2026, 10, 15, 8, 0, 0)) with { Summary = "Umzug" }, null, ct);
        var moved = await env.Calendar.SaveEventAsync(b, EventDraft.FromICalendar(saved.ICalendarData), (a, saved.RemoteId), ct);

        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(HttpMethod.Get, saved.RemoteId, ct));
        Assert.Contains($"/{_user}/b/", moved.RemoteId, StringComparison.Ordinal);
        Assert.Null(await env.Calendar.GetObjectAsync(a, saved.RemoteId, ct));
    }

    [Fact]
    public async Task CalDav_calendars_can_be_rediscovered_and_chosen()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/privat/", "calendar", "Privat", ct);
        await MakeCollectionAsync($"{_user}/team/", "calendar", "Team", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav);
        var connection = Assert.Single(await env.Calendar.GetSourcesAsync(ct)).Connection;

        // Without a choice everything is shown, as before.
        await env.Calendar.SyncAllAsync(ct);
        Assert.Equal(2, (await env.Calendar.GetCalendarsAsync(ct)).Count);

        // Only "Team": "Privat" disappears locally.
        var offered = await env.Calendar.DiscoverAsync(connection, ct);
        Assert.All(offered, c => Assert.True(c.IsSelected && !c.IsNew));
        var team = offered.Single(c => c.Calendar.Name == "Team").Calendar.RemoteId;
        await env.Calendar.SetSelectionAsync(connection, [team], offered.Select(c => c.Calendar.RemoteId).ToList(), ct);
        Assert.Equal("Team", Assert.Single(await env.Calendar.GetCalendarsAsync(ct)).Name);

        // A calendar subscribed on the server later (e.g. in SOGo's web interface) is not added on its own, but offered as new.
        await MakeCollectionAsync($"{_user}/abo/", "calendar", "Abo von Lea", ct);
        await env.Calendar.SyncAllAsync(ct);
        Assert.Single(await env.Calendar.GetCalendarsAsync(ct));
        var again = await env.Calendar.DiscoverAsync(connection, ct);
        Assert.Equal(["Abo von Lea", "Privat", "Team"], again.Select(c => c.Calendar.Name));
        Assert.True(again[0].IsNew && !again[0].IsSelected);
        Assert.False(again[1].IsNew || again[1].IsSelected);
        Assert.True(again[2].IsSelected);

        await env.Calendar.SetSelectionAsync(connection, [team, again[0].Calendar.RemoteId], again.Select(c => c.Calendar.RemoteId).ToList(), ct);
        Assert.Equal(["Abo von Lea", "Team"], (await env.Calendar.GetCalendarsAsync(ct)).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task CalDav_url_of_one_calendar_still_finds_all_calendars_of_the_user()
    {
        // Like SOGo set up for Thunderbird: …/Calendar/personal/ – subscriptions live next to it in the home.
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/personal/", "calendar", "Persönlich", ct);
        await MakeCollectionAsync($"{_user}/abo-lea/", "calendar", "Lea (abonniert)", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav, Url($"/{_user}/personal/").AbsoluteUri);
        var connection = Assert.Single(await env.Calendar.GetSourcesAsync(ct)).Connection;

        var offered = await env.Calendar.DiscoverAsync(connection, ct);

        Assert.Equal(["Lea (abonniert)", "Persönlich"], offered.Select(c => c.Calendar.Name));
    }

    [Fact]
    public async Task CalDav_calendar_not_listed_by_the_server_can_be_added_by_its_address()
    {
        // Like a SOGo subscription the server does not list: another user's calendar, reachable by its URL only
        // (the testlab's Radicale lets every user read all calendars).
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        var other = _user + "lea";
        await MakeCollectionAsync($"{_user}/personal/", "calendar", "Persönlich", ct);
        await MakeCollectionAsync($"{other}/", "plain", "Lea", ct);
        await MakeCollectionAsync($"{other}/personal/", "calendar", "Lea", ct);
        await PutAsync($"/{other}/personal/ferien.ics", "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:ferien\r\nDTSTAMP:20261001T000000Z\r\nDTSTART:20261020T080000Z\r\nDTEND:20261020T090000Z\r\nSUMMARY:Ferien Lea\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n", "text/calendar", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav);
        var connection = Assert.Single(await env.Calendar.GetSourcesAsync(ct)).Connection;

        var before = await env.Calendar.DiscoverWithDetailsAsync(connection, ct);
        Assert.Equal(["Persönlich"], before.Calendars.Select(c => c.Calendar.Name));
        Assert.Contains($"/{_user}/", before.Details, StringComparison.Ordinal);
        Assert.Contains(_user, before.Details, StringComparison.Ordinal);

        Assert.Null(await env.Calendar.AddByUrlAsync(connection, Url($"/{other}/"), ct));
        var added = await env.Calendar.AddByUrlAsync(connection, Url($"/{other}/personal/"), ct);

        Assert.Equal("Lea", added?.Name);
        var lea = (await env.Calendar.GetCalendarsAsync(ct)).Single(c => c.Name == "Lea");
        var events = await env.Calendar.GetOccurrencesAsync([lea], new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), ct);
        Assert.Equal("Ferien Lea", Assert.Single(events).Summary);

        // It stays across syncs and shows up in the dialog as added by address.
        await env.Calendar.SyncAllAsync(ct);
        Assert.Equal(2, (await env.Calendar.GetCalendarsAsync(ct)).Count);
        Assert.True((await env.Calendar.DiscoverAsync(connection, ct)).Single(c => c.Calendar.Name == "Lea").IsAddedByUrl);
    }

    [Fact]
    public async Task CardDav_round_trip_keeps_unknown_properties()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/kontakte/", "addressbook", "Kontakte", ct);
        var href = $"/{_user}/kontakte/anna.vcf";
        await PutAsync(href, "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:anna\r\nFN:Anna Muster\r\nN:Muster;Anna;;;\r\nEMAIL;TYPE=INTERNET,WORK:anna@example.com\r\nADR;TYPE=WORK:;;Bahnhofstrasse 1;Zürich;;8001;Schweiz\r\nBDAY:1990-05-17\r\nEND:VCARD\r\n", "text/vcard", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Contacts, ProviderIds.CardDav);

        Assert.Empty((await env.Contacts.SyncAllAsync(ct)).Failures);
        var anna = Assert.Single(await env.Contacts.SearchAsync("anna", ct));
        Assert.Equal("work", anna.Card.Emails[0].Kind);

        // Edit through Neruna: phone added, address and birthday must survive.
        var draft = ContactDraft.FromVCard((await env.Contacts.GetContactAsync(anna.AddressBook, anna.RemoteId, ct))!.VCardData);
        await env.Contacts.SaveContactAsync(anna.AddressBook, draft with { Phones = [new ContactField("+41 44 555 00 00", "cell")], Title = "Leiterin IT" }, (anna.AddressBook, anna.RemoteId), ct);
        var stored = await GetStringAsync(href, ct);
        Assert.Contains("TEL;TYPE=CELL:+41 44 555 00 00", stored, StringComparison.Ordinal);
        Assert.Contains("TITLE:Leiterin IT", stored, StringComparison.Ordinal);
        Assert.Contains("ADR;TYPE=WORK:;;Bahnhofstrasse 1;Zürich;;8001;Schweiz", stored, StringComparison.Ordinal);
        Assert.Contains("BDAY:1990-05-17", stored, StringComparison.Ordinal);
        Assert.Contains("UID:anna", stored, StringComparison.Ordinal);

        // Create and delete.
        var created = await env.Contacts.SaveContactAsync(anna.AddressBook, ContactDraft.Empty with { GivenName = "Beat", FamilyName = "Brunner", Emails = [new ContactField("beat@example.com")] }, null, ct);
        Assert.Equal(2, (await env.Contacts.SearchAsync(null, ct)).Count);
        await env.Contacts.DeleteContactAsync(anna.AddressBook, created.RemoteId, ct);
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(HttpMethod.Get, created.RemoteId, ct));

        // Change by another client arrives via sync.
        await PutAsync(href, stored.Replace("Leiterin IT", "CIO", StringComparison.Ordinal), "text/vcard", ct);
        var report = await env.Contacts.SyncAllAsync(ct);
        Assert.True(report.Failures.Count == 0, string.Join("; ", report.Failures.Select(f => f.Error.ToString())));
        Assert.Equal("CIO", Assert.Single(await env.Contacts.SearchAsync(null, ct)).Card.Title);
    }

    [Fact]
    public async Task CardDav_group_with_chosen_addresses_resolves_to_recipients()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/kontakte/", "addressbook", "Kontakte", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Contacts, ProviderIds.CardDav);
        await env.Contacts.SyncAllAsync(ct);
        var book = Assert.Single(await env.Contacts.GetAddressBooksAsync(ct));
        await env.Contacts.SaveContactAsync(book, ContactDraft.Empty with { GivenName = "Lea", FamilyName = "Keller", Emails = [new ContactField("lea@work.example", "work"), new ContactField("lea@home.example", "home")] }, null, ct);
        await env.Contacts.SaveContactAsync(book, ContactDraft.Empty with { GivenName = "Marco", Emails = [new ContactField("marco@example.com")] }, null, ct);
        var lea = (await env.Contacts.SearchAsync("Lea", ct)).Single().Card;
        var marco = (await env.Contacts.SearchAsync("Marco", ct)).Single().Card;

        // Lea with her private address in this list, Marco with his default, plus an external address.
        await env.Contacts.SaveGroupAsync(book, new GroupDraft("Grillfest", [new GroupMember(lea.MemberUid, "lea@home.example"), new GroupMember(marco.MemberUid, null), new GroupMember(null, "gast@example.org")]), null, ct);
        await env.Contacts.SyncAllAsync(ct);

        var group = (await env.Contacts.SearchAsync("Grillfest", ct)).Single();
        Assert.True(group.Card.IsGroup);
        var members = await env.Contacts.ResolveMembersAsync(group.Card, ct);
        Assert.Equal(["lea@home.example", "marco@example.com", "gast@example.org"], members.Select(m => m.Address));
        Assert.Equal("\"Lea Keller\" <lea@home.example>", members[0].Recipient);
    }

    [Fact]
    public async Task CardDav_address_books_can_be_rediscovered_and_chosen()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        var ct = TestContext.Current.CancellationToken;
        await MakeCollectionAsync($"{_user}/privat/", "addressbook", "Privat", ct);
        await MakeCollectionAsync($"{_user}/domain/", "addressbook", "Domain Address Book", ct);
        await PutAsync($"/{_user}/domain/lea.vcf", "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:lea\r\nFN:Lea Keller\r\nEMAIL:lea@example.com\r\nEND:VCARD\r\n", "text/vcard", ct);
        await using var env = await CreateEnvironmentAsync(ServiceKind.Contacts, ProviderIds.CardDav);
        var connection = Assert.Single(await env.Contacts.GetSourcesAsync(ct)).Connection;

        // Without a choice everything is shown, as before.
        await env.Contacts.SyncAllAsync(ct);
        Assert.Equal(2, (await env.Contacts.GetAddressBooksAsync(ct)).Count);
        Assert.Single(await env.Contacts.SearchAsync("Lea", ct));

        // Deselect the domain address book: it and its contacts disappear locally (not on the server).
        var offered = await env.Contacts.DiscoverAsync(connection, ct);
        Assert.All(offered, c => Assert.True(c.IsSelected && !c.IsNew));
        var privat = offered.Single(c => c.AddressBook.Name == "Privat").AddressBook.RemoteId;
        await env.Contacts.SetSelectionAsync(connection, [privat], offered.Select(c => c.AddressBook.RemoteId).ToList(), ct);
        Assert.Equal("Privat", Assert.Single(await env.Contacts.GetAddressBooksAsync(ct)).Name);
        Assert.Empty(await env.Contacts.SearchAsync("Lea", ct));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(HttpMethod.Get, $"/{_user}/domain/lea.vcf", ct));

        // A new address book on the server is not added on its own, but offered as new.
        await MakeCollectionAsync($"{_user}/team/", "addressbook", "Team", ct);
        await env.Contacts.SyncAllAsync(ct);
        Assert.Single(await env.Contacts.GetAddressBooksAsync(ct));
        var again = await env.Contacts.DiscoverAsync(connection, ct);
        Assert.Equal(["Domain Address Book", "Privat", "Team"], again.Select(c => c.AddressBook.Name));
        Assert.False(again[0].IsSelected || again[0].IsNew);
        Assert.True(again[1].IsSelected);
        Assert.True(again[2].IsNew && !again[2].IsSelected);
    }

    [Fact]
    public async Task Wrong_url_reports_failure_instead_of_crashing()
    {
        Assert.SkipWhen(ServerUrl is null, "NERUNA_TEST_DAV_URL not set");
        await using var env = await CreateEnvironmentAsync(ServiceKind.Calendar, ProviderIds.CalDav, url: "http://127.0.0.1:1/");

        var report = await env.Calendar.SyncAllAsync(TestContext.Current.CancellationToken);

        Assert.Single(report.Failures);
    }

    private async Task<TestEnvironment> CreateEnvironmentAsync(ServiceKind kind, string providerId, string? url = null)
    {
        var env = await TestEnvironment.CreateAsync(services =>
        {
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            services.AddSingleton<IProviderFactory<ICalendarProvider>>(sp => new CalDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IProviderFactory<IContactProvider>>(sp => new CardDavProviderFactory(http, sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ILoggerFactory>()));
        });

        var connection = await env.AddAccountAsync(kind, providerId, new DavSettings(new Uri(url ?? ServerUrl!), _user).ToDictionary());
        await env.Get<ICredentialStore>().SetSecretAsync(connection.Id, "geheim");
        return env;
    }

    private async Task MakeCollectionAsync(string path, string type, string name, CancellationToken ct)
    {
        var resourceType = type switch { "calendar" => "<C:calendar/>", "addressbook" => "<A:addressbook/>", _ => string.Empty };
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:A="urn:ietf:params:xml:ns:carddav" xmlns:I="http://apple.com/ns/ical/">
              <D:set><D:prop>
                <D:resourcetype><D:collection/>{resourceType}</D:resourcetype>
                <D:displayname>{name}</D:displayname>
                <I:calendar-color>#C239B3FF</I:calendar-color>
              </D:prop></D:set>
            </D:mkcol>
            """;
        var response = await RawAsync(() => new HttpRequestMessage(new HttpMethod("MKCOL"), Url("/" + path)) { Content = new StringContent(body, Encoding.UTF8, "application/xml") }, ct);
        Assert.True(response.IsSuccessStatusCode, $"MKCOL {path}: {(int)response.StatusCode}");
    }

    private async Task PutAsync(string path, string content, string mediaType, CancellationToken ct)
    {
        var response = await RawAsync(() => new HttpRequestMessage(HttpMethod.Put, Url(path)) { Content = new StringContent(content, Encoding.UTF8, mediaType) }, ct);
        Assert.True(response.IsSuccessStatusCode, $"PUT {path}: {(int)response.StatusCode}");
    }

    private async Task<string> GetStringAsync(string path, CancellationToken ct) =>
        await (await RawAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(path)), ct)).Content.ReadAsStringAsync(ct);

    private async Task<HttpStatusCode> StatusAsync(HttpMethod method, string path, CancellationToken ct) =>
        (await RawAsync(() => new HttpRequestMessage(method, Url(path)), ct)).StatusCode;

    // Radicale's built-in server occasionally drops a response; the test's own setup calls retry like the product does.
    private async Task<HttpResponseMessage> RawAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _raw.SendAsync(request(), ct);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(200, ct);
            }
        }
    }

    private static Uri Url(string path) => new(new Uri(ServerUrl!), path);
}
