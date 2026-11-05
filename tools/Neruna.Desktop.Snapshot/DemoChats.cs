using Neruna.Core.Accounts;
using Neruna.Core.Chat;

namespace Neruna.Desktop.Snapshot;

/// <summary>
/// Two chats of the demo account for the screenshots of the chat tabs – a team chat and a Nextcloud Talk – without a
/// server: one over the demo calendar connection, one over the contacts connection.
/// </summary>
internal sealed class DemoChatFactory(ChatKind kind) : IAccountChatFactory
{
    /// <summary>Off until the chat screenshots: the pictures before stay without these chats.</summary>
    public static bool Offered { get; set; }

    public ChatKind Kind => kind;

    public ServiceConnectionRef? ChatConnectionOf(Account account)
    {
        if (!Offered)
        {
            return null;
        }

        var wanted = kind == ChatKind.NerunaCloud ? ServiceKind.Calendar : ServiceKind.Contacts;
        return account.Connections.FirstOrDefault(c => c.Kind == wanted) is { } connection
            ? new ServiceConnectionRef(connection, kind == ChatKind.NerunaCloud ? "Muster AG" : "cloud.muster.ch")
            : null;
    }

    public IChatService Create(Account account, ServiceConnection connection, Func<CancellationToken, Task<string>> presence) =>
        kind == ChatKind.NerunaCloud ? DemoChat.Business(connection.Id) : DemoChat.Private(connection.Id);
}

internal sealed class DemoChat(string key, string title, ChatKind kind, ChatSnapshot start) : IChatService
{
    private ChatSnapshot _current = start;

    public string Key => key;

    public string Title => title;

    public ChatKind Kind => kind;

    public ChatSnapshot Current => _current;

    public event EventHandler? Changed;

    public static DemoChat Business(Guid connection)
    {
        var now = DateTimeOffset.Now;
        ChatPerson[] people =
        [
            new("anna", "Anna Muster", "Verkauf", Presence.Available),
            new("beat", "Beat Beispiel", "Leitung Verkauf", Presence.Available),
            new("carla", "Carla Rossi", "Support", Presence.Away),
            new("david", "David Meier", "Technik", Presence.DoNotDisturb),
            new("elena", "Elena Weber", "Buchhaltung", Presence.Offline),
        ];
        ChatRoom[] rooms =
        [
            new("verkauf", "Verkauf", "Offerten und Kunden", ["anna", "beat", "carla", "elena"]),
            new("support", "Support", "Anfragen und Störungen", ["anna", "carla", "david"]),
            new("alle", "Alle", "Für das ganze Team", ["anna", "beat", "carla", "david", "elena"]),
        ];
        ChatMessage[] messages =
        [
            new(1, "verkauf", "beat", null, "Guten Morgen zusammen! Die Offerte für die Bernasconi AG ist raus.", now.AddMinutes(-95)),
            new(2, "verkauf", "carla", null, "Super, danke! Sie haben schon nach dem Liefertermin gefragt.", now.AddMinutes(-80)),
            new(3, "verkauf", "anna", null, "Ich rufe sie heute Nachmittag an und kläre das.", now.AddMinutes(-72)),
            new(4, "verkauf", "beat", null, "**Wichtig:** bitte die neuen Preise ab Januar erwähnen.", now.AddMinutes(-20)),
            new(5, "support", "david", null, "Der Drucker im 2. Stock läuft wieder.", now.AddMinutes(-15)),
            new(6, "support", "carla", null, "Danke dir! Ich gebe es weiter.", now.AddMinutes(-9)),
        ];
        var snapshot = new ChatSnapshot(true, true, 30, "anna", rooms, people, messages,
            new Dictionary<string, long> { [ChatConversation.Room("verkauf")] = 4, [ChatConversation.Room("support")] = 4 });
        return new DemoChat("account:" + connection.ToString("N"), "Muster AG", ChatKind.NerunaCloud, snapshot);
    }

    public static DemoChat Private(Guid connection)
    {
        var now = DateTimeOffset.Now;
        ChatPerson[] people =
        [
            new("anna", "Anna Muster", null, Presence.Available),
            new("luca", "Luca Muster", null, Presence.Available),
            new("mia", "Mia Muster", null, Presence.Away),
        ];
        ChatRoom[] rooms =
        [
            new("familie", "Familie", null, ["anna", "luca", "mia"]),
            new("luca", "Luca Muster", null, ["anna", "luca"], IsDirect: true, Presence: Presence.Available),
        ];
        ChatMessage[] messages =
        [
            new(1, "familie", "mia", null, "Wer kommt am Sonntag zum Zmittag?", now.AddMinutes(-50)),
            new(2, "familie", "luca", null, "Ich bin dabei, bringe den Dessert mit.", now.AddMinutes(-30)),
        ];
        var snapshot = new ChatSnapshot(true, true, 0, "anna", rooms, people, messages, new Dictionary<string, long>())
        {
            ServerUnread = new Dictionary<string, int> { [ChatConversation.Room("familie")] = 2 },
        };
        return new DemoChat("talk:" + connection.ToString("N"), "Privat", ChatKind.NextcloudTalk, snapshot);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task OpenAsync(string conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> OpenPrivateAsync(string personId, CancellationToken cancellationToken = default) =>
        Task.FromResult(kind == ChatKind.NextcloudTalk ? ChatConversation.Room(personId) : ChatConversation.Private(personId));

    public Task SendAsync(string conversation, string text, CancellationToken cancellationToken = default)
    {
        var id = _current.Messages.Count == 0 ? 1 : _current.Messages.Max(m => m.Id) + 1;
        var message = ChatConversation.IsRoom(conversation)
            ? new ChatMessage(id, ChatConversation.IdOf(conversation), "anna", null, text, DateTimeOffset.Now)
            : new ChatMessage(id, null, "anna", ChatConversation.IdOf(conversation), text, DateTimeOffset.Now);
        Publish(_current with { Messages = [.. _current.Messages, message] });
        return Task.CompletedTask;
    }

    public Task MarkReadAsync(string conversation, CancellationToken cancellationToken = default)
    {
        var reads = new Dictionary<string, long>(_current.Reads) { [conversation] = _current.MessagesOf(conversation).Select(m => m.Id).DefaultIfEmpty().Max() };
        var unread = _current.ServerUnread is null ? null : new Dictionary<string, int>(_current.ServerUnread) { [conversation] = 0 };
        Publish(_current with { Reads = reads, ServerUnread = unread });
        return Task.CompletedTask;
    }

    public Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Uri? CallUrl(string conversation) => kind == ChatKind.NextcloudTalk ? new Uri("https://cloud.muster.ch/index.php/call/" + ChatConversation.IdOf(conversation)) : null;

    public void Dispose()
    {
    }

    private void Publish(ChatSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
