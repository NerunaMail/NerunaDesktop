using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Core;
using Neruna.Core.Accounts;
using Neruna.Core.Chat;
using Neruna.Core.Cloud;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using Neruna.Providers.Dav;

namespace Neruna.Client.Tests;

/// <summary>Nextcloud Talk as a chat tab of a Nextcloud account: conversations, messages, read markers, calls in the browser.</summary>
public class NextcloudTalkTests
{
    private const string Nextcloud = "https://nc.example/";
    private const string Spreed = Nextcloud + "ocs/v2.php/apps/spreed/api/";

    private static string Ocs(object data) => JsonSerializer.Serialize(new { ocs = new { meta = new { status = "ok" }, data } });

    private static object Comment(long id, string actor, string name, string text, object? parameters = null, string system = "") => new
    {
        id,
        actorType = "users",
        actorId = actor,
        actorDisplayName = name,
        timestamp = DateTimeOffset.UtcNow.AddMinutes(-60 + id).ToUnixTimeSeconds(),
        message = text,
        messageParameters = parameters ?? Array.Empty<object>(),
        systemMessage = system,
        messageType = system.Length > 0 ? "system" : "comment",
    };

    [Fact]
    public void A_nextcloud_account_offers_talk_others_do_not()
    {
        var factory = new NextcloudTalkFactory(new HttpClient(), new NoCredentials(), NullLoggerFactory.Instance);
        var nextcloud = Account(Nextcloud + "remote.php/dav/");
        var sub = Account("https://example.com/cloud/remote.php/dav/calendars/anna/");
        var other = Account("https://dav.example.com/caldav/");

        Assert.Equal("nc.example", factory.ChatConnectionOf(nextcloud)?.ServerName);
        Assert.Equal(new Uri("https://example.com/cloud/"), NextcloudTalkChat.ServerOf(new Uri("https://example.com/cloud/remote.php/dav/calendars/anna/")));
        Assert.NotNull(factory.ChatConnectionOf(sub));
        Assert.Null(factory.ChatConnectionOf(other));
    }

    [Fact]
    public async Task Conversations_messages_read_markers_and_calls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var account = Account(Nextcloud + "remote.php/dav/");
        await env.Accounts.SaveAccountAsync(account, ct);
        await env.Get<ICredentialStore>().SetSecretAsync(account.Connections[0].Id, "app-password", ct);

        env.Http.Responses[Nextcloud + "ocs/v2.php/cloud/user"] = (HttpStatusCode.OK, Ocs(new Dictionary<string, object> { ["id"] = "anna", ["display-name"] = "Anna Muster" }));
        env.Http.Responses[Nextcloud + "ocs/v2.php/apps/user_status/api/v1/user_status/status"] = (HttpStatusCode.OK, Ocs(new { }));
        env.Http.Responses[Spreed + "v4/room?includeStatus=true"] = (HttpStatusCode.OK, Ocs(new object[]
        {
            new { token = "team", type = 2, name = "team", displayName = "Team", description = "Alle", unreadMessages = 2, lastReadMessage = 10, lastMessage = new { id = 12 }, lastActivity = 200 },
            new { token = "dm", type = 1, name = "beat", displayName = "Beat Beispiel", description = "", unreadMessages = 0, lastReadMessage = 5, lastMessage = new { id = 5 }, lastActivity = 100, status = "dnd" },
            new { token = "news", type = 4, name = "changelog", displayName = "Talk-Neuigkeiten", description = "", unreadMessages = 1, lastReadMessage = 0, lastMessage = new { id = 3 }, lastActivity = 50 },
        }));
        env.Http.Responses[Spreed + "v1/chat/team?lookIntoFuture=0&limit=100&setReadMarker=0"] = (HttpStatusCode.OK, Ocs(new[]
        {
            Comment(12, "beat", "Beat Beispiel", "Hallo {mention-user1}, **kurz** Zeit?", new Dictionary<string, object> { ["mention-user1"] = new { type = "user", id = "anna", name = "Anna Muster" } }),
            Comment(11, "beat", "Beat Beispiel", "Beat started a call", system: "call_started"),
            Comment(10, "carla", "Carla Muster", "Guten Morgen"),
        }));
        env.Http.Responses[Spreed + "v4/room/team/participants?includeStatus=true"] = (HttpStatusCode.OK, Ocs(new[]
        {
            new { actorType = "users", actorId = "anna", displayName = "Anna Muster", status = "online" },
            new { actorType = "users", actorId = "beat", displayName = "Beat Beispiel", status = "dnd" },
            new { actorType = "users", actorId = "carla", displayName = "Carla Muster", status = "away" },
        }));

        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        var factory = new NextcloudTalkFactory(new HttpClient(env.Http), env.Get<ICredentialStore>(), NullLoggerFactory.Instance);
        using var chat = new ChatController(cloud, env.Get<IChatStore>(), settings, env.Accounts, [factory], TimeProvider.System, NullLoggerFactory.Instance);
        await chat.LoadAsync(ct);

        // Offered, but only shown once switched on.
        var option = Assert.Single(await chat.GetAccountChatsAsync(ct));
        Assert.Equal(("Nextcloud (nc.example)", false), (option.Title, option.IsEnabled));
        Assert.Empty(chat.Services);
        await chat.SetPresenceAsync(Presence.BeRightBack, ct);
        await chat.SetAccountChatAsync(option.ConnectionId, true, ct);

        var talk = Assert.Single(chat.Services);
        Assert.Equal((ChatKind.NextcloudTalk, "Nextcloud"), (talk.Kind, talk.Title));
        Assert.Equal("Basic " + Convert.ToBase64String("anna:app-password"u8.ToArray()), env.Http.Authorizations[^1]);
        Assert.Contains(env.Http.Sent, s => s.Method == "PUT" && s.Body.Contains("\"statusType\":\"away\"", StringComparison.Ordinal));

        // A status of its own (e.g. available in private while away at work): kept when the header's changes.
        await chat.SetPresenceOfAsync(talk.Key, Presence.Available, ct);
        Assert.Contains("\"statusType\":\"online\"", env.Http.Sent[^1].Body, StringComparison.Ordinal);
        var sent = env.Http.Sent.Count;
        await chat.SetPresenceAsync(Presence.DoNotDisturb, ct);
        Assert.Equal(sent, env.Http.Sent.Count);
        Assert.Equal(Presence.Available, await chat.PresenceOfAsync(talk.Key, ct));
        await chat.SetPresenceOfAsync(talk.Key, null, ct); // back to the header's
        Assert.Contains("\"statusType\":\"dnd\"", env.Http.Sent[^1].Body, StringComparison.Ordinal);

        // Group conversations as rooms, one-to-one ones as private conversations (with the person's status); no changelog.
        var snapshot = talk.Current;
        Assert.Equal("anna", snapshot.Me);
        Assert.Equal(["Team", "Beat Beispiel"], snapshot.Rooms.Select(r => r.Name));
        Assert.Equal((false, true, Presence.DoNotDisturb), (snapshot.Rooms[0].IsDirect, snapshot.Rooms[1].IsDirect, snapshot.Rooms[1].Presence));
        Assert.Equal(2, snapshot.UnreadOf(ChatConversation.Room("team")));
        Assert.Equal(2, chat.TotalUnread);
        Assert.Empty(snapshot.Messages); // loaded when opened

        // Opened: comments only, placeholders resolved, people with their status.
        await talk.OpenAsync(ChatConversation.Room("team"), ct);
        snapshot = talk.Current;
        Assert.Equal([10L, 12L], snapshot.MessagesOf(ChatConversation.Room("team")).Select(m => m.Id));
        Assert.Equal("Hallo @Anna Muster, **kurz** Zeit?", snapshot.Messages[^1].Text);
        Assert.Equal(["anna", "beat", "carla"], snapshot.Rooms[0].MemberIds);
        Assert.Equal(Presence.Away, snapshot.People.Single(p => p.Id == "carla").Presence);

        // Read: here at once, then on the Nextcloud.
        env.Http.Responses[Spreed + "v1/chat/team/read"] = (HttpStatusCode.OK, Ocs(new { }));
        await talk.MarkReadAsync(ChatConversation.Room("team"), ct);
        Assert.Equal(0, chat.TotalUnread);
        Assert.Contains(env.Http.Sent, s => s.Url == Spreed + "v1/chat/team/read" && s.Body.Contains("\"lastReadMessage\":12", StringComparison.Ordinal));

        // Sent: shown right away.
        env.Http.Responses[Spreed + "v1/chat/team"] = (HttpStatusCode.Created, Ocs(Comment(13, "anna", "Anna Muster", "Gleich ☕")));
        await talk.SendAsync(ChatConversation.Room("team"), "Gleich ☕", ct);
        Assert.Equal(13L, talk.Current.Messages[^1].Id);
        Assert.Contains(env.Http.Sent, s => s.Url == Spreed + "v1/chat/team" && s.Body.Contains("\"message\":\"Gleich", StringComparison.Ordinal));

        // Calls: Talk's page for the conversation, in the browser.
        Assert.Equal(new Uri(Nextcloud + "index.php/call/team"), talk.CallUrl(ChatConversation.Room("team")));

        // Without Talk on the Nextcloud: the tab says so.
        env.Http.Responses.Remove(Spreed + "v4/room?includeStatus=true");
        await chat.RefreshAsync(ct);
        Assert.Contains("nicht installiert", chat.ErrorOf(talk.Key), StringComparison.Ordinal);
        Assert.False(talk.Current.Available);

        // Switched off: the tab goes.
        await chat.SetAccountChatAsync(option.ConnectionId, false, ct);
        Assert.Empty(chat.Services);
    }

    private static Account Account(string davUrl)
    {
        var settings = new Dictionary<string, string> { [DavSettings.UrlKey] = davUrl, [DavSettings.UsernameKey] = "anna" };
        return new Account(Guid.NewGuid(), "Anna Muster", null, [new ServiceConnection(Guid.NewGuid(), ServiceKind.Calendar, ProviderIds.CalDav, settings)], Label: "Nextcloud");
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public string Location => "test";

        public Task<string?> GetSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSecretAsync(Guid connectionId, string secret, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteSecretAsync(Guid connectionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
