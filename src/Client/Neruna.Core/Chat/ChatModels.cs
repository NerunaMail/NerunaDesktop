using static Neruna.Core.Localization.Texts;
namespace Neruna.Core.Chat;

/// <summary>A chat message kept on this computer (until the organisation's retention has passed).</summary>
/// <param name="Text">Plain text with **bold** and __underlined__; emoji as Unicode.</param>
public sealed record ChatMessage(long Id, string? RoomId, string SenderId, string? RecipientId, string Text, DateTimeOffset SentAt)
{
    /// <summary>"room:&lt;id&gt;" or "pm:&lt;the other person&gt;" – the same keys the server uses for read markers.</summary>
    public string ConversationFor(string me) =>
        RoomId is not null ? ChatConversation.Room(RoomId) : ChatConversation.Private(SenderId == me ? RecipientId ?? SenderId : SenderId);
}

public static class ChatConversation
{
    public static string Room(string roomId) => "room:" + roomId;

    public static string Private(string personId) => "pm:" + personId;

    public static bool IsRoom(string conversation) => conversation.StartsWith("room:", StringComparison.Ordinal);

    public static string IdOf(string conversation) => conversation[(conversation.IndexOf(':', StringComparison.Ordinal) + 1)..];
}

/// <summary>Online status keys (server and app); names for the UI in <see cref="Label"/>.</summary>
public static class Presence
{
    public const string Available = "available";
    public const string Away = "away";
    public const string BeRightBack = "brb";
    public const string DoNotDisturb = "dnd";
    public const string Offline = "offline";

    public static IReadOnlyList<string> All { get; } = [Available, Away, BeRightBack, DoNotDisturb, Offline];

    public static string Normalize(string? presence) => presence is not null && All.Contains(presence) ? presence : Available;

    public static string Label(string presence) => presence switch
    {
        Away => T("Abwesend"),
        BeRightBack => T("Bin gleich zurück"),
        DoNotDisturb => T("Nicht stören"),
        Offline => T("Offline"),
        _ => T("Verfügbar"),
    };
}

/// <summary>A room or group conversation; with <see cref="IsDirect"/> a conversation with one other person (Nextcloud Talk).</summary>
/// <param name="Presence">Of the other person in a direct conversation.</param>
public sealed record ChatRoom(string Id, string Name, string? Description, IReadOnlyList<string> MemberIds, bool IsDirect = false, string? Presence = null);

/// <param name="Presence">One of <see cref="Chat.Presence"/>.</param>
public sealed record ChatPerson(string Id, string Name, string? Position, string Presence);

/// <summary>Where a chat comes from: Neruna Cloud/Control (one tab per organisation) or Nextcloud Talk (one per account).</summary>
public enum ChatKind
{
    NerunaCloud,
    NextcloudTalk,
}

/// <summary>
/// One chat – a tab in the chat page. The UI calls <see cref="RefreshAsync"/> every few seconds while the chat is open
/// and less often otherwise; <see cref="Current"/> is replaced on every change.
/// </summary>
public interface IChatService : IDisposable
{
    /// <summary>"cloud:&lt;connection&gt;" or "talk:&lt;connection&gt;": stable, also across starts.</summary>
    string Key { get; }

    /// <summary>The tab's name: the organisation, or the account of the Nextcloud.</summary>
    string Title { get; }

    ChatKind Kind { get; }

    ChatSnapshot Current { get; }

    /// <summary>Something the chat shows changed (raised on the thread that made the change).</summary>
    event EventHandler? Changed;

    /// <summary>What was there at the last start (without network).</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches what is new; true when something changed.</summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>The conversation is shown: its messages are needed (Nextcloud Talk loads them only then).</summary>
    Task OpenAsync(string conversation, CancellationToken cancellationToken = default);

    /// <summary>The conversation with this person ("pm:…", or the Talk conversation with them – made if there is none yet).</summary>
    Task<string> OpenPrivateAsync(string personId, CancellationToken cancellationToken = default);

    Task SendAsync(string conversation, string text, CancellationToken cancellationToken = default);

    /// <summary>Everything in the conversation counts as read (here at once, on the server right after).</summary>
    Task MarkReadAsync(string conversation, CancellationToken cancellationToken = default);

    /// <summary>The own status (the same in every chat, see <see cref="Chat.Presence"/>).</summary>
    Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default);

    /// <summary>Where an audio or video call for the conversation opens (Nextcloud Talk in the browser); null: no calls.</summary>
    Uri? CallUrl(string conversation);
}

/// <summary>Chats that come with an account (Nextcloud Talk with a Nextcloud account); registered by its provider.</summary>
public interface IAccountChatFactory
{
    ChatKind Kind { get; }

    /// <summary>The connection of <paramref name="account"/> the chat would run over; null when the account has none.</summary>
    ServiceConnectionRef? ChatConnectionOf(Accounts.Account account);

    /// <param name="presence">The own status, asked for when the chat first calls in.</param>
    IChatService Create(Accounts.Account account, Accounts.ServiceConnection connection, Func<CancellationToken, Task<string>> presence);
}

/// <summary>An account's connection a chat can run over.</summary>
public sealed record ServiceConnectionRef(Accounts.ServiceConnection Connection, string ServerName);

/// <summary>Chat messages on this computer (SQLite), kept per chat (<see cref="IChatService.Key"/>).</summary>
public interface IChatStore
{
    Task<IReadOnlyList<ChatMessage>> GetAllAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>Adds messages; ones already there (same id) are left as they are.</summary>
    Task AddAsync(string source, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default);

    Task DeleteOlderThanAsync(string source, DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task ClearAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>Removes the messages of every chat not in <paramref name="keep"/> (gone connections, older versions).</summary>
    Task ClearOthersAsync(IReadOnlyCollection<string> keep, CancellationToken cancellationToken = default);
}
