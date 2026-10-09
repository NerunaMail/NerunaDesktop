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
        Away => "Abwesend",
        BeRightBack => "Bin gleich zurück",
        DoNotDisturb => "Nicht stören",
        Offline => "Offline",
        _ => "Verfügbar",
    };
}

/// <summary>Chat messages on this computer (SQLite).</summary>
public interface IChatStore
{
    Task<IReadOnlyList<ChatMessage>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds messages; ones already there (same id) are left as they are.</summary>
    Task AddAsync(IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default);

    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
