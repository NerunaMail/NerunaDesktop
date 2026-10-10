using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Core.Contacts;
using Neruna.Core.Mail;
using Neruna.Core.Security;
using Neruna.Providers.Graph;

namespace Neruna.Client.Tests;

/// <summary>Microsoft Graph without a Microsoft account: recorded-style answers (shapes as documented for Graph v1.0).</summary>
public class GraphProviderTests
{
    private static readonly OAuthEndpoints Endpoints = new(new Uri("https://login.example/authorize"), new Uri("https://login.example/token"), "client-id", ["Mail.ReadWrite"]);

    [Fact]
    public async Task Tokens_are_refreshed_before_they_expire_and_an_expired_sign_in_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var http = new FakeGraph();
        var tokenId = Guid.NewGuid();
        var source = new OAuthTokenSource(new OAuthClient(new HttpClient(http)), Endpoints, credentials, tokenId);

        await Assert.ThrowsAsync<OAuthException>(() => source.GetAccessTokenAsync(ct));

        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("valid", "refresh-1", DateTimeOffset.UtcNow.AddHours(1)), ct);
        Assert.Equal("valid", await source.GetAccessTokenAsync(ct));
        Assert.Empty(http.Requests);

        // About to expire: refreshed, and the rotated refresh token is kept.
        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30)), ct);
        http.Routes["POST /token"] = """{"access_token":"new","refresh_token":"refresh-2","expires_in":3600}""";
        Assert.Equal("new", await source.GetAccessTokenAsync(ct));
        Assert.Contains("refresh_token=refresh-1", http.Bodies.Single(), StringComparison.Ordinal);
        Assert.Contains("refresh-2", await credentials.GetSecretAsync(tokenId, ct), StringComparison.Ordinal);

        // Revoked: the user has to sign in again.
        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("old", "refresh-2", DateTimeOffset.UtcNow), ct);
        http.Routes["POST /token"] = """{"error":"invalid_grant","error_description":"AADSTS700082: expired"}""";
        http.Status["POST /token"] = HttpStatusCode.BadRequest;
        var expired = await Assert.ThrowsAsync<OAuthException>(() => source.GetAccessTokenAsync(ct));
        Assert.True(expired.SignInRequired);
    }

    [Fact]
    public async Task Mail_folders_have_their_roles_and_delta_brings_new_changed_and_removed_messages()
    {
        var ct = TestContext.Current.CancellationToken;
        var (graph, http) = await SignedInAsync();
        var mail = new GraphMailProvider(Guid.NewGuid(), graph, NullLogger<GraphMailProvider>.Instance);
        http.Routes["GET /v1.0/me/mailFolders/inbox"] = """{"id":"INBOX-ID"}""";
        http.Routes["GET /v1.0/me/mailFolders/sentitems"] = """{"id":"SENT-ID"}""";
        http.Routes["GET /v1.0/me/mailFolders"] = """{"value":[{"id":"INBOX-ID","displayName":"Inbox","totalItemCount":2,"unreadItemCount":1,"childFolderCount":1},{"id":"SENT-ID","displayName":"Sent Items","childFolderCount":0}]}""";
        http.Routes["GET /v1.0/me/mailFolders/INBOX-ID/childFolders"] = """{"value":[{"id":"PROJ-ID","displayName":"Projekte","childFolderCount":0}]}""";

        var folders = await mail.GetFoldersAsync(ct);
        Assert.Equal([FolderRole.Inbox, FolderRole.None, FolderRole.Sent], folders.Select(f => f.Role));
        Assert.Equal(("PROJ-ID", "INBOX-ID"), (folders[1].RemoteId, folders[1].ParentRemoteId));

        // First sync: the last 90 days, ending in a delta link.
        http.Routes["GET /v1.0/me/mailFolders/INBOX-ID/messages/delta"] = """
            {"value":[
              {"id":"M1","internetMessageId":"<1@example.com>","subject":"Offerte","from":{"emailAddress":{"name":"Marco","address":"marco@example.com"}},
               "toRecipients":[{"emailAddress":{"name":"Anna","address":"anna@example.com"}}],"receivedDateTime":"2026-10-09T08:00:00Z",
               "isRead":false,"isDraft":false,"flag":{"flagStatus":"flagged"},"hasAttachments":true,"bodyPreview":"Anbei"},
              {"id":"M2","subject":"Hallo","receivedDateTime":"2026-10-08T08:00:00Z","isRead":true,"flag":{"flagStatus":"notFlagged"}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/INBOX-ID/messages/delta?$deltatoken=ONE"}
            """;
        http.Routes["GET /v1.0/me/mailFolders/INBOX-ID"] = """{"totalItemCount":2,"unreadItemCount":1}""";
        var first = await mail.SyncFolderAsync(folders[0], [], ct);
        Assert.True(first.IsFullResync);
        Assert.Contains("$deltatoken=ONE", first.NewSyncState, StringComparison.Ordinal);
        Assert.Contains("receivedDateTime ge", http.Requested.Last(r => r.Contains("delta", StringComparison.Ordinal)), StringComparison.Ordinal);
        var offer = first.AddedOrChanged.Single(m => m.RemoteId == "M1");
        Assert.Equal(("Offerte", "marco@example.com", MessageFlags.Flagged, true), (offer.Subject, offer.From!.Address, offer.Flags, offer.HasAttachments));

        // Next sync from the delta link: M1 read now, M2 gone.
        http.Routes["GET /v1.0/me/mailFolders/INBOX-ID/messages/delta"] = """
            {"value":[{"id":"M1","isRead":true,"flag":{"flagStatus":"flagged"}},{"id":"M2","@removed":{"reason":"deleted"}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/INBOX-ID/messages/delta?$deltatoken=TWO"}
            """;
        var next = await mail.SyncFolderAsync(folders[0] with { SyncState = first.NewSyncState }, ["M1", "M2"], ct);
        Assert.False(next.IsFullResync);
        Assert.Equal(MessageFlags.Seen | MessageFlags.Flagged, next.FlagUpdates["M1"]);
        Assert.Equal(["M2"], next.RemovedRemoteIds);
        Assert.Contains("$deltatoken=TWO", next.NewSyncState, StringComparison.Ordinal);
        Assert.Contains(http.Requested, r => r.Contains("deltatoken=ONE", StringComparison.Ordinal));

        // Answered: Graph's "last verb" property, so other programs show the arrow as well.
        await mail.SetFlagsAsync(folders[0], ["M1"], MessageFlags.Answered, true, ct);
        Assert.Contains("0x1081", http.Bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_become_icalendar_with_time_zone_series_and_attendees_and_back()
    {
        var graphEvent = JsonDocument.Parse("""
            {"id":"E1","changeKey":"CK1","iCalUId":"040000008200E00074C5B7101A82E008","subject":"Wochenrapport","body":{"contentType":"text","content":"Traktanden folgen"},
             "start":{"dateTime":"2026-10-12T09:00:00.0000000","timeZone":"W. Europe Standard Time"},"end":{"dateTime":"2026-10-12T09:30:00.0000000","timeZone":"W. Europe Standard Time"},
             "isAllDay":false,"location":{"displayName":"Sitzungszimmer"},"isReminderOn":true,"reminderMinutesBeforeStart":10,
             "organizer":{"emailAddress":{"name":"Anna","address":"anna@example.com"}},
             "attendees":[{"type":"required","status":{"response":"accepted"},"emailAddress":{"name":"Lea","address":"lea@example.com"}}],
             "recurrence":{"pattern":{"type":"weekly","interval":1,"daysOfWeek":["monday","wednesday"]},"range":{"type":"endDate","startDate":"2026-10-12","endDate":"2026-12-31"}}}
            """).RootElement;

        var ical = GraphCalendarProvider.ToICalendar(graphEvent);
        var draft = EventDraft.FromICalendar(ical);
        Assert.Equal(("Wochenrapport", "Sitzungszimmer", "Traktanden folgen", 10), (draft.Summary, draft.Location, draft.Description, draft.ReminderMinutes));
        Assert.Equal(TimeSpan.FromMinutes(30), draft.End - draft.Start);
        Assert.Equal(RecurrenceKind.Custom, draft.Recurrence);
        Assert.Contains("BYDAY=MO,WE", ical, StringComparison.Ordinal);
        Assert.Contains("UNTIL=20261231", ical, StringComparison.Ordinal);
        Assert.Equal(("lea@example.com", Participation.Accepted), (draft.Attendees![0].Email, draft.Attendees[0].Status));
        Assert.Equal("anna@example.com", draft.Organizer);

        var back = JsonSerializer.SerializeToElement(GraphCalendarProvider.ToGraph(ical));
        Assert.Equal("weekly", back.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal(["monday", "wednesday"], back.GetProperty("recurrence").GetProperty("pattern").GetProperty("daysOfWeek").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal("endDate", back.GetProperty("recurrence").GetProperty("range").GetProperty("type").GetString());
        Assert.True(back.GetProperty("isReminderOn").GetBoolean());

        // All day: just the dates.
        var holiday = JsonDocument.Parse("""
            {"id":"E2","subject":"Feiertag","isAllDay":true,"start":{"dateTime":"2026-12-25T00:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-12-26T00:00:00","timeZone":"UTC"}}
            """).RootElement;
        var allDay = EventDraft.FromICalendar(GraphCalendarProvider.ToICalendar(holiday));
        Assert.True(allDay.IsAllDay);
        Assert.Equal(new DateTime(2026, 12, 25), allDay.Start.Date);
    }

    [Fact]
    public void Contacts_become_vcards_and_back()
    {
        var contact = JsonDocument.Parse("""
            {"id":"C1","changeKey":"K1","givenName":"Marco","surname":"Bernasconi","companyName":"Bernasconi AG","jobTitle":"Geschäftsführer",
             "emailAddresses":[{"name":"Marco","address":"marco@bernasconi.example"}],"businessPhones":["+41 44 555 01 01"],"homePhones":[],"mobilePhone":"+41 79 555 02 02","personalNotes":"Kunde seit 2019"}
            """).RootElement;

        var vcard = GraphContactProvider.ToVCard(contact);
        var card = VCardReader.Read(vcard);
        Assert.Equal(("C1", "Marco Bernasconi", "Bernasconi AG", "Geschäftsführer"), (card.Uid, card.DisplayName, card.Organization, card.Title));
        Assert.Equal(["marco@bernasconi.example"], card.EmailAddresses);
        Assert.Equal(2, card.Phones.Count);

        var back = JsonSerializer.SerializeToElement(GraphContactProvider.ToGraph(vcard));
        Assert.Equal("+41 79 555 02 02", back.GetProperty("mobilePhone").GetString());
        Assert.Equal(["+41 44 555 01 01"], back.GetProperty("businessPhones").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("Kunde seit 2019", back.GetProperty("personalNotes").GetString());
    }

    [Fact]
    public void A_microsoft_account_has_one_sign_in_for_mail_calendar_and_contacts()
    {
        var tokenId = Guid.NewGuid();
        var connections = GraphConnectionFactory.Connections("anna@example.com", tokenId);
        Assert.Equal(3, connections.Count);
        Assert.All(connections, c => Assert.Equal(tokenId, GraphSettings.FromDictionary(c.Settings).TokenId));
        // The token lives under the mail connection's id: removing the account removes it from the keychain too.
        Assert.Equal(tokenId, connections.Single(c => c.Kind == Neruna.Core.Accounts.ServiceKind.Mail).Id);
        Assert.False(MicrosoftAccount.IsConfigured && string.IsNullOrEmpty(MicrosoftAccount.ClientId));
    }

    [Fact]
    public async Task Microsoft_to_do_lists_need_the_tasks_permission_and_map_to_vtodo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (withoutTasks, http) = await SignedInAsync();
        http.Routes["GET /v1.0/me/calendars"] = """{"value":[{"id":"CAL","name":"Kalender","canEdit":true}]}""";
        http.Routes["GET /v1.0/me/todo/lists"] = """{"value":[{"id":"L1","displayName":"Aufgaben"}]}""";

        // Signed in before tasks existed: calendars only, and To Do is not even asked.
        var calendars = await new GraphCalendarProvider(Guid.NewGuid(), withoutTasks).GetCalendarsAsync(ct);
        Assert.Equal(["CAL"], calendars.Select(c => c.RemoteId));
        Assert.DoesNotContain(http.Requests, r => r.Contains("todo", StringComparison.Ordinal));

        var (graph, http2) = await SignedInAsync("Mail.ReadWrite Tasks.ReadWrite");
        http2.Routes["GET /v1.0/me/calendars"] = http.Routes["GET /v1.0/me/calendars"];
        http2.Routes["GET /v1.0/me/todo/lists"] = http.Routes["GET /v1.0/me/todo/lists"];
        var provider = new GraphCalendarProvider(Guid.NewGuid(), graph);
        var list = Assert.Single(await provider.GetCalendarsAsync(ct), c => c.HasTasks);
        Assert.Equal(("todo:L1", CalendarContent.Tasks), (list.RemoteId, list.Content));

        http2.Routes["GET /v1.0/me/todo/lists/L1/tasks"] = """
            {"value":[
              {"id":"T1","title":"Offerte schicken","status":"notStarted","importance":"high","lastModifiedDateTime":"2026-10-10T08:00:00Z",
               "body":{"content":"An Marco","contentType":"text"},"dueDateTime":{"dateTime":"2026-10-14T00:00:00.0000000","timeZone":"UTC"}},
              {"id":"T2","title":"Erledigt","status":"completed","importance":"normal","lastModifiedDateTime":"2026-10-09T08:00:00Z",
               "completedDateTime":{"dateTime":"2026-10-09T07:00:00.0000000","timeZone":"UTC"}}]}
            """;
        var synced = await provider.SyncCalendarAsync(list, new Dictionary<string, string?> { ["T2"] = "2026-10-09T08:00:00Z", ["GONE"] = "x" }, ct);
        var offer = TaskDraft.ToItem(list, Assert.Single(synced.AddedOrChanged))!;
        Assert.Equal(("Offerte schicken", "An Marco", TaskPriority.High, false), (offer.Summary, offer.Notes, offer.Priority, offer.IsCompleted));
        Assert.Equal(new DateOnly(2026, 10, 14), DateOnly.FromDateTime(offer.Due!.Value.DateTime));
        Assert.False(offer.DueHasTime);
        Assert.Equal(["GONE"], synced.RemovedRemoteIds);

        // Done in Neruna: PATCH with status completed.
        http2.Routes["GET /v1.0/me/todo/lists/L1/tasks/T1"] = """{"id":"T1","lastModifiedDateTime":"2026-10-10T08:00:00Z"}""";
        http2.Routes["PATCH /v1.0/me/todo/lists/L1/tasks/T1"] = """{"id":"T1","title":"Offerte schicken","status":"completed","lastModifiedDateTime":"2026-10-10T09:00:00Z"}""";
        var data = (TaskDraft.FromItem(offer) with { IsCompleted = true }).ToICalendar(synced.AddedOrChanged[0].ICalendarData);
        var saved = await provider.SaveAsync(list, synced.AddedOrChanged[0] with { ICalendarData = data }, ct);
        Assert.Contains("\"status\":\"completed\"", http2.Bodies.Last(), StringComparison.Ordinal);
        Assert.Contains("\"importance\":\"high\"", http2.Bodies.Last(), StringComparison.Ordinal);
        Assert.Equal("2026-10-10T09:00:00Z", saved.ETag);
    }

    [Fact]
    public async Task A_refresh_never_asks_for_permissions_the_user_has_not_granted_yet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var http = new FakeGraph();
        var endpoints = new OAuthEndpoints(new Uri("https://login.example/authorize"), new Uri("https://login.example/token"), "client-id",
            ["offline_access", "Mail.ReadWrite", "Tasks.ReadWrite"], ["Tasks.ReadWrite"]);
        var tokenId = Guid.NewGuid();
        var source = new OAuthTokenSource(new OAuthClient(new HttpClient(http)), endpoints, credentials, tokenId);

        // Signed in with an older version: no grant recorded – refreshed with the scopes of that time.
        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("old", "r1", DateTimeOffset.UtcNow), ct);
        http.Routes["POST /token"] = """{"access_token":"a2","refresh_token":"r2","expires_in":3600,"scope":"Mail.ReadWrite"}""";
        await source.GetAccessTokenAsync(ct);
        Assert.DoesNotContain("Tasks.ReadWrite", http.Bodies.Last(), StringComparison.Ordinal);
        Assert.False(await source.GrantsAsync("Tasks.ReadWrite", ct));

        // Signed in again with tasks: the grant is kept and used for the next refresh.
        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("old", "r3", DateTimeOffset.UtcNow, Scope: "https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Tasks.ReadWrite"), ct);
        Assert.True(await source.GrantsAsync("Tasks.ReadWrite", ct));
        await source.GetAccessTokenAsync(ct);
        Assert.Contains("Tasks.ReadWrite", http.Bodies.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_out_of_office_reply_needs_the_mailbox_settings_permission_and_maps_both_ways()
    {
        var ct = TestContext.Current.CancellationToken;
        var (without, _) = await SignedInAsync("Mail.ReadWrite MailboxSettings.Read");
        var old = new GraphMailProvider(Guid.NewGuid(), without, NullLogger<GraphMailProvider>.Instance);
        await Assert.ThrowsAsync<AutoReplyUnavailableException>(() => old.GetAutoReplyAsync(ct));

        var (graph, http) = await SignedInAsync("Mail.ReadWrite MailboxSettings.ReadWrite");
        http.Routes["GET /v1.0/me/mailboxSettings/automaticRepliesSetting"] = """
            {"status":"scheduled","externalAudience":"all",
             "scheduledStartDateTime":{"dateTime":"2026-10-11T22:00:00.0000000","timeZone":"UTC"},
             "scheduledEndDateTime":{"dateTime":"2026-10-16T22:00:00.0000000","timeZone":"UTC"},
             "internalReplyMessage":"<html><body><div>Bin weg.<br>Gruss Anna</div></body></html>","externalReplyMessage":""}
            """;
        var provider = new GraphMailProvider(Guid.NewGuid(), graph, NullLogger<GraphMailProvider>.Instance);
        var (reply, features) = await provider.GetAutoReplyAsync(ct);
        Assert.Equal(AutoReplyFeatures.Schedule, features);
        Assert.True(reply.IsEnabled && reply.IsScheduled);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero), reply.Start);
        Assert.Contains("Bin weg.", reply.Message, StringComparison.Ordinal);
        Assert.Contains("Gruss Anna", reply.Message, StringComparison.Ordinal);

        await provider.SetAutoReplyAsync(new AutoReply(true, "Ferien <bis> Freitag\nAnna", reply.Start, reply.End), ["anna@example.com"], ct);
        var body = Assert.Single(http.Bodies);
        Assert.Contains("\"status\":\"scheduled\"", body, StringComparison.Ordinal);
        Assert.Contains("2026-10-11T22:00:00", body, StringComparison.Ordinal);
        Assert.Contains("Ferien \\u0026lt;bis\\u0026gt; Freitag\\u003Cbr\\u003EAnna", body, StringComparison.Ordinal);
        Assert.Contains("PATCH /v1.0/me/mailboxSettings", http.Requests);
    }

    private static async Task<(GraphClient Graph, FakeGraph Http)> SignedInAsync(string? scope = null)
    {
        var env = await TestEnvironment.CreateAsync();
        var credentials = env.Get<ICredentialStore>();
        var tokenId = Guid.NewGuid();
        await OAuthTokenSource.StoreAsync(credentials, tokenId, new OAuthTokens("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), Scope: scope));
        var http = new FakeGraph();
        return (new GraphClient(new HttpClient(http), new OAuthTokenSource(new OAuthClient(new HttpClient(http)), Endpoints, credentials, tokenId)), http);
    }

    /// <summary>Answers by "METHOD /path" (query ignored); records what was asked and sent.</summary>
    private sealed class FakeGraph : HttpMessageHandler
    {
        public Dictionary<string, string> Routes { get; } = [];

        public Dictionary<string, HttpStatusCode> Status { get; } = [];

        public List<string> Requests { get; } = [];

        public List<string> Requested { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method.Method} {request.RequestUri!.AbsolutePath}";
            Requests.Add(key);
            Requested.Add(Uri.UnescapeDataString(request.RequestUri.AbsoluteUri));
            if (request.Content is not null)
            {
                Bodies.Add(Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken)));
            }

            return Routes.TryGetValue(key, out var body)
                ? new HttpResponseMessage(Status.GetValueOrDefault(key, HttpStatusCode.OK)) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(request.Method == HttpMethod.Patch ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new StringContent(request.Method == HttpMethod.Patch ? "{}" : """{"error":{"code":"ErrorItemNotFound","message":"not found"}}""", Encoding.UTF8, "application/json") };
        }
    }
}
