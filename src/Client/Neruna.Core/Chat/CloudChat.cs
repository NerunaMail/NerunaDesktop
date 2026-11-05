using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Contracts;
using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;

namespace Neruna.Core.Chat;

/// <summary>
/// The chat of one organisation via Neruna Cloud/Control (rooms, private messages, online status) – plain HTTP polling.
/// Messages are kept here only as long as the organisation's retention allows.
/// </summary>
public sealed class CloudChat(
    CloudController cloud,
    CloudConnection connection,
    IChatStore store,
    ISettingsStore settings,
    TimeProvider time,
    Func<CancellationToken, Task<string>> presence,
    ILogger logger) : IChatService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _presenceSent;

    public static string KeyOf(CloudConnection connection) => "cloud:" + (connection ?? throw new ArgumentNullException(nameof(connection))).Id;

    public CloudConnection Connection { get; } = connection;

    public string Key { get; } = KeyOf(connection);

    public string Title => Connection.OrganizationName;

    public ChatKind Kind => ChatKind.NerunaCloud;

    public ChatSnapshot Current { get; private set; } = ChatSnapshot.Empty;

    public event EventHandler? Changed;

    private string StateKey => SettingKeys.ChatState + "." + Connection.Id;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await LoadStateAsync(cancellationToken);
            Current = state is null
                ? ChatSnapshot.Empty with { Connected = true }
                : new ChatSnapshot(true, state.Available, state.RetentionDays, state.Me, state.Rooms, state.People,
                    Within(await store.GetAllAsync(Key, cancellationToken), state.RetentionDays), state.Reads);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

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
        if (!_presenceSent)
        {
            await cloud.SetPresenceAsync(Connection, await presence(cancellationToken), cancellationToken);
            _presenceSent = true;
        }

        var messages = before.Messages.ToList();
        var after = messages.Count == 0 ? 0 : messages.Max(m => m.Id);
        ChatResponse response;
        var added = new List<ChatMessage>();
        while (true)
        {
            response = await cloud.GetChatAsync(Connection, after, cancellationToken);
            if (before.Me is not null && response.Me != before.Me && (messages.Count > 0 || added.Count > 0))
            {
                // Connected as someone else: the old conversations are not theirs.
                logger.LogInformation("Chat belongs to another person now; local messages cleared");
                await store.ClearAsync(Key, cancellationToken);
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
            await store.ClearAsync(Key, cancellationToken);
        }
        else if (added.Count > 0)
        {
            await store.AddAsync(Key, added, cancellationToken);
        }

        var known = messages.Select(m => m.Id).ToHashSet();
        messages.AddRange(added.Where(m => known.Add(m.Id)));
        var kept = Within(messages, response.RetentionDays);
        if (kept.Count < messages.Count)
        {
            await store.DeleteOlderThanAsync(Key, Cutoff(response.RetentionDays), cancellationToken);
        }

        // Read markers only move forward: one set here a moment ago may not have reached this answer yet.
        var reads = new Dictionary<string, long>(response.Reads);
        foreach (var (conversation, lastRead) in Current.Reads)
        {
            if (lastRead > reads.GetValueOrDefault(conversation) && response.Me == Current.Me)
            {
                reads[conversation] = lastRead;
            }
        }

        var rooms = response.Rooms.Select(r => new ChatRoom(r.Id, r.Name, r.Description, r.MemberIds)).ToList();
        var people = response.People.Select(p => new ChatPerson(p.Id, p.Name, p.Position, Presence.Normalize(p.Presence))).ToList();
        var state = new ChatState(response.Available, response.RetentionDays, response.Me, rooms, people, reads);
        var stateJson = JsonSerializer.Serialize(state, NerunaJson.Options);
        var stateChanged = stateJson != await settings.GetAsync(StateKey, cancellationToken);
        if (stateChanged)
        {
            await settings.SetAsync(StateKey, stateJson, cancellationToken);
        }

        Current = new ChatSnapshot(true, response.Available, response.RetentionDays, response.Me, rooms, people, kept, reads);
        return stateChanged || added.Count > 0 || kept.Count < messages.Count || !before.Connected;
    }

    // Everything is here already.
    public Task OpenAsync(string conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string> OpenPrivateAsync(string personId, CancellationToken cancellationToken = default) => Task.FromResult(ChatConversation.Private(personId));

    public async Task SendAsync(string conversation, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var id = ChatConversation.IdOf(conversation);
        var request = ChatConversation.IsRoom(conversation) ? new ChatSendRequest(id, null, text) : new ChatSendRequest(null, id, text);
        var sent = await cloud.SendChatMessageAsync(Connection, request, cancellationToken);
        var message = new ChatMessage(sent.Id, sent.RoomId, sent.SenderId, sent.RecipientId, sent.Text, sent.SentAt);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await store.AddAsync(Key, [message], cancellationToken);
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
        await cloud.MarkChatReadAsync(Connection, new ChatReadRequest(conversation, last), cancellationToken);
    }

    public async Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default)
    {
        await cloud.SetPresenceAsync(Connection, Presence.Normalize(presence), cancellationToken);
        // Offline (chat switched off): the chosen status goes out again with the next refresh once it is back.
        _presenceSent = presence != Presence.Offline;
    }

    public Uri? CallUrl(string conversation) => null;

    /// <summary>The organisation is gone (disconnected, device blocked): its messages leave this device at once.</summary>
    public async Task ForgetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ForgetAsync(store, settings, Connection, cancellationToken);
            Current = ChatSnapshot.Empty;
            _presenceSent = false;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes what is kept here of an organisation's chat (also when it is not loaded).</summary>
    public static async Task ForgetAsync(IChatStore store, ISettingsStore settings, CloudConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        await store.ClearAsync(KeyOf(connection), cancellationToken);
        await settings.SetAsync(SettingKeys.ChatState + "." + connection.Id, null, cancellationToken);
    }

    private DateTimeOffset Cutoff(int retentionDays) => time.GetUtcNow().AddDays(-Math.Max(1, retentionDays));

    private List<ChatMessage> Within(IEnumerable<ChatMessage> messages, int retentionDays)
    {
        var cutoff = Cutoff(retentionDays);
        return messages.Where(m => m.SentAt >= cutoff).OrderBy(m => m.Id).ToList();
    }

    private async Task<ChatState?> LoadStateAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(StateKey, cancellationToken);
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
        IReadOnlyList<ChatRoom> Rooms,
        IReadOnlyList<ChatPerson> People,
        IReadOnlyDictionary<string, long> Reads);
}
