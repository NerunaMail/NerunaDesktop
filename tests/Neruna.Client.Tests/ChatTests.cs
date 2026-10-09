using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Neruna.Contracts;
using Neruna.Core;
using Neruna.Core.Chat;
using Neruna.Core.Cloud;
using Neruna.Core.Security;

namespace Neruna.Client.Tests;

public class ChatTests
{
    private const string Server = "https://cloud.example/";
    private const string Anna = "01ANNA";
    private const string Beat = "01BEAT";
    private const string Room = "01ROOM";

    private static string Chat(int retentionDays, object reads, params object[] messages) => JsonSerializer.Serialize(new
    {
        available = true,
        retentionDays,
        me = Anna,
        rooms = new[] { new { id = Room, name = "Allgemein", description = (string?)null, memberIds = new[] { Anna, Beat } } },
        people = new[]
        {
            new { id = Anna, name = "Anna Muster", position = (string?)null, presence = "available" },
            new { id = Beat, name = "Beat Beispiel", position = (string?)"Support", presence = "dnd" },
        },
        messages,
        reads,
        more = false,
    });

    private static object Message(long id, string? room, string sender, string? recipient, string text, DateTimeOffset sentAt) =>
        new { id, roomId = room, senderId = sender, recipientId = recipient, text, sentAt };

    [Fact]
    public async Task Messages_are_kept_counted_as_unread_read_and_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var store = env.Get<IChatStore>();
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        using var chat = new ChatController(cloud, store, settings, TimeProvider.System, NullLogger<ChatController>.Instance);

        // Without a cloud connection there is no chat.
        await chat.LoadAsync(ct);
        Assert.False(chat.Current.Connected);
        Assert.False(await chat.RefreshAsync(ct));

        using var key = DeviceKey.Create();
        await env.Get<ICredentialStore>().SetSecretAsync(CloudController.KeyId, key.ExportPrivateKey(), ct);
        await settings.SetAsync(SettingKeys.CloudConnection, JsonSerializer.Serialize(
            new CloudConnection(new Uri(Server), "01DEVICE", "Example AG", "Anna Muster", DateTimeOffset.Now), NerunaJson.Options), ct);
        env.Http.Responses[Server + "api/v1/token"] = (HttpStatusCode.OK, """{"accessToken":"t","tokenType":"Bearer","expiresIn":900}""");
        env.Http.Responses[Server + "api/v1/presence"] = (HttpStatusCode.OK, """{"presence":"available"}""");
        var now = DateTimeOffset.UtcNow;
        env.Http.Responses[Server + "api/v1/chat?after=0"] = (HttpStatusCode.OK, Chat(14, new { },
            Message(40, Room, Beat, null, "alt", now.AddDays(-20)),        // older than the retention
            Message(41, Room, Beat, null, "Guten **Morgen**", now.AddHours(-1)),
            Message(42, null, Beat, Anna, "Kaffee?", now.AddMinutes(-30))));
        env.Http.Responses[Server + "api/v1/chat?after=42"] = (HttpStatusCode.OK, Chat(14, new { }));

        var changes = 0;
        chat.Changed += (_, _) => changes++;
        Assert.True(await chat.RefreshAsync(ct));
        var current = chat.Current;
        Assert.True(current.Connected && current.Available);
        Assert.Equal([41L, 42L], current.Messages.Select(m => m.Id));
        Assert.Equal([41L, 42L], (await store.GetAllAsync(ct)).Select(m => m.Id));
        Assert.Equal(2, current.TotalUnread);
        Assert.Equal(1, current.UnreadOf(ChatConversation.Private(Beat)));
        Assert.Contains(env.Http.Sent, s => s.Method == "PUT" && s.Url == Server + "api/v1/presence" && s.Body.Contains("\"available\"", StringComparison.Ordinal));

        // Nothing new: nothing changed; only what is newer than the last message is asked for.
        Assert.False(await chat.RefreshAsync(ct));
        Assert.Equal(Server + "api/v1/chat?after=42", env.Http.Requested[^1]);

        // Answering Beat privately: the own message counts as read.
        env.Http.Responses[Server + "api/v1/chat/messages"] = (HttpStatusCode.Created, JsonSerializer.Serialize(
            Message(43, null, Anna, Beat, "Gerne __gleich__ ☕", now)));
        await chat.SendAsync(ChatConversation.Private(Beat), "Gerne __gleich__ ☕", ct);
        Assert.Contains(env.Http.Sent, s => s.Url == Server + "api/v1/chat/messages" && s.Body.Contains($"\"recipientId\":\"{Beat}\"", StringComparison.Ordinal));
        Assert.Equal(0, chat.Current.UnreadOf(ChatConversation.Private(Beat)));
        Assert.Equal(1, chat.Current.TotalUnread);

        env.Http.Responses[Server + "api/v1/chat/read"] = (HttpStatusCode.NoContent, string.Empty);
        await chat.MarkReadAsync(ChatConversation.Room(Room), ct);
        Assert.Equal(0, chat.Current.TotalUnread);
        Assert.Contains(env.Http.Sent, s => s.Url == Server + "api/v1/chat/read" && s.Body.Contains("\"lastId\":41", StringComparison.Ordinal));

        // The next start shows the same without network.
        using var restarted = new ChatController(cloud, store, settings, TimeProvider.System, NullLogger<ChatController>.Instance);
        await restarted.LoadAsync(ct);
        Assert.Equal([41L, 42L, 43L], restarted.Current.Messages.Select(m => m.Id));
        Assert.Equal("Beat Beispiel", restarted.Current.People[1].Name);

        // Disconnected: the chat is gone here, too.
        await settings.SetAsync(SettingKeys.CloudConnection, null, ct);
        Assert.True(await chat.RefreshAsync(ct));
        Assert.False(chat.Current.Connected);
        Assert.Empty(await store.GetAllAsync(ct));
        Assert.True(changes >= 4);
    }

    [Fact]
    public async Task Presence_is_remembered_and_normalized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var env = await TestEnvironment.CreateAsync();
        var settings = env.Get<ISettingsStore>();
        var cloud = new CloudController(new HttpClient(env.Http), settings, env.Get<ICredentialStore>(), NullLogger<CloudController>.Instance);
        using var chat = new ChatController(cloud, env.Get<IChatStore>(), settings, TimeProvider.System, NullLogger<ChatController>.Instance);

        Assert.Equal(Presence.Available, await chat.GetPresenceAsync(ct));
        await chat.SetPresenceAsync(Presence.DoNotDisturb, ct); // not connected: only remembered
        Assert.Equal("dnd", await chat.GetPresenceAsync(ct));
        await chat.SetPresenceAsync("sleeping", ct);
        Assert.Equal(Presence.Available, await chat.GetPresenceAsync(ct));
        Assert.Equal("Bin gleich zurück", Presence.Label(Presence.BeRightBack));
    }

    [Theory]
    [InlineData("Hallo **Welt**", "Hallo |Welt*")]
    [InlineData("__unter__ und **fett __beides__**", "unter_| und |fett *|beides*_")]
    [InlineData("2 ** 3 und __offen", "2 ** 3 und __offen")]
    [InlineData("😀 **👍**", "😀 |👍*")]
    public void Simple_formatting_is_recognized(string markup, string expected)
    {
        var parts = ChatFormatting.Parse(markup).Select(p => p.Text + (p.Bold ? "*" : string.Empty) + (p.Underline ? "_" : string.Empty));
        Assert.Equal(expected, string.Join("|", parts));
    }
}
