using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;

namespace Neruna.Core.Chat;

/// <summary>What the chat shows: rooms, people, messages and read markers (immutable; replaced on every change).</summary>
/// <param name="Connected">This computer is connected to Neruna Cloud/Control.</param>
/// <param name="Available">The organisation's licence includes the chat.</param>
public sealed record ChatSnapshot(
    bool Connected,
    bool Available,
    int RetentionDays,
    string? Me,
    IReadOnlyList<CloudChatRoom> Rooms,
    IReadOnlyList<CloudChatPerson> People,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyDictionary<string, long> Reads)
{
    public static ChatSnapshot Empty { get; } = new(false, false, 14, null, [], [], [], new Dictionary<string, long>());

    public IEnumerable<ChatMessage> MessagesOf(string conversation) => Me is null ? [] : Messages.Where(m => m.ConversationFor(Me) == conversation);

    /// <summary>Messages of others after the read marker.</summary>
    public int UnreadOf(string conversation)
    {
        var read = Reads.GetValueOrDefault(conversation);
        return MessagesOf(conversation).Count(m => m.Id > read && m.SenderId != Me);
    }

    /// <summary>All unread messages in rooms the person sees and private conversations (the badge on the chat button).</summary>
    public int TotalUnread
    {
        get
        {
            if (Me is null)
            {
                return 0;
            }

            var rooms = Rooms.Select(r => ChatConversation.Room(r.Id)).ToHashSet();
            return Messages.Where(m => m.SenderId != Me)
                .Select(m => (Message: m, Conversation: m.ConversationFor(Me)))
                .Count(x => (x.Message.RoomId is null || rooms.Contains(x.Conversation)) && x.Message.Id > Reads.GetValueOrDefault(x.Conversation));
        }
    }
}

/// <summary>
/// Chat with the people of the organisation via Neruna Cloud/Control (rooms, private messages, online status). The UI
/// calls <see cref="RefreshAsync"/> every few seconds while the chat is open and less often otherwise – plain HTTP
/// polling. Messages are kept here only as long as the organisation's retention allows.
/// </summary>
public sealed class ChatController(CloudController cloud, IChatStore store, ISettingsStore settings, TimeProvider time, ILogger<ChatController> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _presenceSent;

    public ChatSnapshot Current { get; private set; } = ChatSnapshot.Empty;

    /// <summary>Something the chat shows changed (raised on the thread that made the change).</summary>
    public event EventHandler? Changed;

    /// <summary>What was there at the last start: messages from SQLite, rooms and people from the settings.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var connected = await cloud.GetConnectionAsync(cancellationToken) is not null;
            var state = await LoadStateAsync(cancellationToken);
            var messages = connected ? await store.GetAllAsync(cancellationToken) : [];
            Current = state is null || !connected
                ? ChatSnapshot.Empty with { Connected = connected }
                : new ChatSnapshot(true, state.Available, state.RetentionDays, state.Me, state.Rooms, state.People, Within(messages, state.RetentionDays), state.Reads);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fetches what is new; returns true when something changed. Without a cloud connection the local chat is cleared.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            changed = await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    private async Task<bool> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var before = Current;
        if (await cloud.GetConnectionAsync(cancellationToken) is null)
        {
            if (before.Connected || before.Messages.Count > 0)
            {
                await ClearAsync(cancellationToken);
                Current = ChatSnapshot.Empty;
                return true;
            }

            return false;
        }

        if (!_presenceSent)
        {
            await cloud.SetPresenceAsync(await GetPresenceAsync(cancellationToken), cancellationToken);
            _presenceSent = true;
        }

        var messages = before.Messages.ToList();
        var after = messages.Count == 0 ? 0 : messages.Max(m => m.Id);
        ChatResponse response;
        var added = new List<ChatMessage>();
        while (true)
        {
            response = await cloud.GetChatAsync(after, cancellationToken);
            if (before.Me is not null && response.Me != before.Me && (messages.Count > 0 || added.Count > 0))
            {
                // Connected as someone else (other organisation): the old conversations are not theirs.
                logger.LogInformation("Chat belongs to another person now; local messages cleared");
                await store.ClearAsync(cancellationToken);
                messages.Clear();
                added.Clear();
                before = ChatSnapshot.Empty;
                after = 0;
                continue;
            }

            added.AddRange(response.Messages.Select(m => new ChatMessage(m.Id, m.RoomId, m.SenderId, m.RecipientId, m.Text, m.SentAt)));
            if (!response.More || response.Messages.Count == 0)
            {
                break;
            }

            after = response.Messages[^1].Id;
        }

        if (!response.Available)
        {
            added.Clear();
            messages.Clear();
            await store.ClearAsync(cancellationToken);
        }
        else if (added.Count > 0)
        {
            await store.AddAsync(added, cancellationToken);
        }

        var known = messages.Select(m => m.Id).ToHashSet();
        messages.AddRange(added.Where(m => known.Add(m.Id)));
        var kept = Within(messages, response.RetentionDays);
        if (kept.Count < messages.Count)
        {
            await store.DeleteOlderThanAsync(Cutoff(response.RetentionDays), cancellationToken);
        }

        var state = new ChatState(response.Available, response.RetentionDays, response.Me, response.Rooms, response.People, response.Reads);
        var stateJson = JsonSerializer.Serialize(state, NerunaJson.Options);
        var stateChanged = stateJson != await settings.GetAsync(SettingKeys.ChatState, cancellationToken);
        if (stateChanged)
        {
            await settings.SetAsync(SettingKeys.ChatState, stateJson, cancellationToken);
        }

        Current = new ChatSnapshot(true, response.Available, response.RetentionDays, response.Me, response.Rooms, response.People, kept, response.Reads);
        return stateChanged || added.Count > 0 || kept.Count < messages.Count || !before.Connected;
    }

    /// <summary>Sends a message to a room or a person ("room:…" / "pm:…").</summary>
    public async Task SendAsync(string conversation, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var id = ChatConversation.IdOf(conversation);
        var request = ChatConversation.IsRoom(conversation) ? new ChatSendRequest(id, null, text) : new ChatSendRequest(null, id, text);
        var sent = await cloud.SendChatMessageAsync(request, cancellationToken);
        var message = new ChatMessage(sent.Id, sent.RoomId, sent.SenderId, sent.RecipientId, sent.Text, sent.SentAt);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await store.AddAsync([message], cancellationToken);
            var current = Current;
            if (current.Messages.All(m => m.Id != message.Id))
            {
                var reads = new Dictionary<string, long>(current.Reads) { [conversation] = message.Id };
                Current = current with { Messages = [.. current.Messages, message], Reads = reads };
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Everything in the conversation counts as read (here at once, on the server in the background).</summary>
    public async Task MarkReadAsync(string conversation, CancellationToken cancellationToken = default)
    {
        var current = Current;
        var last = current.MessagesOf(conversation).Select(m => m.Id).DefaultIfEmpty(0).Max();
        if (last == 0 || last <= current.Reads.GetValueOrDefault(conversation))
        {
            return;
        }

        Current = current with { Reads = new Dictionary<string, long>(current.Reads) { [conversation] = last } };
        Changed?.Invoke(this, EventArgs.Empty);
        await cloud.MarkChatReadAsync(new ChatReadRequest(conversation, last), cancellationToken);
    }

    public async Task<string> GetPresenceAsync(CancellationToken cancellationToken = default) =>
        Presence.Normalize(await settings.GetAsync(SettingKeys.ChatPresence, cancellationToken));

    /// <summary>Remembers the own status and tells the server (when connected).</summary>
    public async Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default)
    {
        presence = Presence.Normalize(presence);
        await settings.SetAsync(SettingKeys.ChatPresence, presence, cancellationToken);
        if (await cloud.GetConnectionAsync(cancellationToken) is not null)
        {
            await cloud.SetPresenceAsync(presence, cancellationToken);
            _presenceSent = true;
        }
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        await store.ClearAsync(cancellationToken);
        await settings.SetAsync(SettingKeys.ChatState, null, cancellationToken);
        _presenceSent = false;
    }

    private DateTimeOffset Cutoff(int retentionDays) => time.GetUtcNow().AddDays(-Math.Max(1, retentionDays));

    private List<ChatMessage> Within(IEnumerable<ChatMessage> messages, int retentionDays)
    {
        var cutoff = Cutoff(retentionDays);
        return messages.Where(m => m.SentAt >= cutoff).OrderBy(m => m.Id).ToList();
    }

    private async Task<ChatState?> LoadStateAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(SettingKeys.ChatState, cancellationToken);
        try
        {
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ChatState>(json, NerunaJson.Options);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored chat state unreadable");
            return null;
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record ChatState(
        bool Available,
        int RetentionDays,
        string Me,
        IReadOnlyList<CloudChatRoom> Rooms,
        IReadOnlyList<CloudChatPerson> People,
        IReadOnlyDictionary<string, long> Reads);
}
