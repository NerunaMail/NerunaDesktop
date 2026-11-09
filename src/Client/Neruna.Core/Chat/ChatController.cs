using Microsoft.Extensions.Logging;
using Neruna.Core.Cloud;

namespace Neruna.Core.Chat;

/// <summary>What a chat shows: rooms, people, messages and read markers (immutable; replaced on every change).</summary>
/// <param name="Connected">The chat's server is set up (Neruna Cloud/Control connected, Nextcloud Talk switched on).</param>
/// <param name="Available">The chat may be used (Neruna Cloud: the organisation's licence includes it).</param>
/// <param name="RetentionDays">How long Neruna Cloud keeps messages; 0: the server keeps them (Nextcloud Talk).</param>
public sealed record ChatSnapshot(
    bool Connected,
    bool Available,
    int RetentionDays,
    string? Me,
    IReadOnlyList<ChatRoom> Rooms,
    IReadOnlyList<ChatPerson> People,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyDictionary<string, long> Reads)
{
    public static ChatSnapshot Empty { get; } = new(false, false, 14, null, [], [], [], new Dictionary<string, long>());

    /// <summary>
    /// Unread messages per conversation as the server counts them (Nextcloud Talk: only the conversations opened are
    /// loaded here); null: counted from <see cref="Messages"/> and <see cref="Reads"/>.
    /// </summary>
    public IReadOnlyDictionary<string, int>? ServerUnread { get; init; }

    public IEnumerable<ChatMessage> MessagesOf(string conversation) => Me is null ? [] : Messages.Where(m => m.ConversationFor(Me) == conversation);

    /// <summary>Messages of others after the read marker.</summary>
    public int UnreadOf(string conversation)
    {
        if (ServerUnread is not null)
        {
            return ServerUnread.GetValueOrDefault(conversation);
        }

        var read = Reads.GetValueOrDefault(conversation);
        return MessagesOf(conversation).Count(m => m.Id > read && m.SenderId != Me);
    }

    /// <summary>All unread messages in rooms the person sees and private conversations (the badge on the tab).</summary>
    public int TotalUnread
    {
        get
        {
            if (Me is null)
            {
                return 0;
            }

            if (ServerUnread is not null)
            {
                return ServerUnread.Values.Sum();
            }

            var rooms = Rooms.Select(r => ChatConversation.Room(r.Id)).ToHashSet();
            return Messages.Where(m => m.SenderId != Me)
                .Select(m => (Message: m, Conversation: m.ConversationFor(Me)))
                .Count(x => (x.Message.RoomId is null || rooms.Contains(x.Conversation)) && x.Message.Id > Reads.GetValueOrDefault(x.Conversation));
        }
    }
}

/// <summary>A chat an account could have (Nextcloud Talk), switched on or not (the chat page offers them).</summary>
public sealed record AccountChatOption(Guid ConnectionId, string Title, ChatKind Kind, bool IsEnabled);

/// <summary>
/// All chats, each a tab: one per Neruna Cloud/Control connection, plus Nextcloud Talk for each Nextcloud account it is
/// switched on for. The own status is the same in all of them.
/// </summary>
public sealed class ChatController(
    CloudController cloud,
    IChatStore store,
    ISettingsStore settings,
    IAccountStore accounts,
    IEnumerable<IAccountChatFactory> accountChats,
    TimeProvider time,
    ILoggerFactory loggers) : IDisposable
{
    private readonly ILogger _logger = loggers.CreateLogger<ChatController>();
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, string> _errors = [];
    private readonly Dictionary<string, string?> _presenceOverrides = [];
    private IReadOnlyList<IChatService> _services = [];

    /// <summary>The chats in the order of the tabs: the organisations first, then the accounts.</summary>
    public IReadOnlyList<IChatService> Services => _services;

    /// <summary>A chat came or went, or something in one changed (raised on the thread that made the change).</summary>
    public event EventHandler? Changed;

    /// <summary>Why the last refresh of this chat failed; null when it worked.</summary>
    public string? ErrorOf(string key)
    {
        lock (_errors)
        {
            return _errors.GetValueOrDefault(key);
        }
    }

    /// <summary>The badge on the chat button: unread messages of all usable chats.</summary>
    public int TotalUnread => _services.Where(s => s.Current.Available).Sum(s => s.Current.TotalUnread);

    /// <summary>The key of a chat that comes with an account connection.</summary>
    public static string KeyFor(ChatKind kind, Guid connectionId) => (kind == ChatKind.NextcloudTalk ? "talk:" : "account:") + connectionId.ToString("N");

    /// <summary>What was there at the last start (messages from SQLite, rooms and people from the settings).</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await SyncAsync(cancellationToken);

        // Kept by a version with one chat only: not for any chat in particular any more (the server has them again).
        if (await settings.GetAsync(SettingKeys.ChatState, cancellationToken) is not null)
        {
            await settings.SetAsync(SettingKeys.ChatState, null, cancellationToken);
            await settings.SetAsync(SettingKeys.ChatConnection, null, cancellationToken);
        }

        // Chosen when only one organisation's chat was shown; each has its own tab now.
        if (await settings.GetAsync(SettingKeys.CloudChatConnection, cancellationToken) is not null)
        {
            await settings.SetAsync(SettingKeys.CloudChatConnection, null, cancellationToken);
        }

        await store.ClearOthersAsync(_services.Where(s => s.Kind == ChatKind.NerunaCloud).Select(s => s.Key).ToList(), cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fetches what is new in every chat (chats that fail keep what they have, see <see cref="ErrorOf"/>); true when something changed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var changed = await SyncAsync(cancellationToken);
        foreach (var service in _services)
        {
            string? error = null;
            try
            {
                changed |= await service.RefreshAsync(cancellationToken);
            }
            catch (Exception ex) when (IsUnreachable(ex) && !cancellationToken.IsCancellationRequested)
            {
                // Offline or server away: the chat shows what it has and tries again with the next refresh.
                _logger.LogDebug(ex, "Chat {Key} not refreshed", service.Key);
                error = ex is ChatException ? ex.Message : Localization.Texts.T("Keine Verbindung zum Chat-Server – neuer Versuch läuft.");
            }

            lock (_errors)
            {
                if (_errors.GetValueOrDefault(service.Key) != error)
                {
                    changed = true;
                    if (error is null)
                    {
                        _errors.Remove(service.Key);
                    }
                    else
                    {
                        _errors[service.Key] = error;
                    }
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>Errors a chat server can answer with (or none at all); the chat keeps what it has.</summary>
    public static bool IsUnreachable(Exception exception) =>
        exception is CloudException or HttpRequestException or TaskCanceledException or ChatException or System.Text.Json.JsonException;

    public IChatService? Find(string key) => _services.FirstOrDefault(s => s.Key == key);

    /// <summary>The own status in the window header: for every chat without one of its own.</summary>
    public async Task<string> GetPresenceAsync(CancellationToken cancellationToken = default) =>
        Presence.Normalize(await settings.GetAsync(SettingKeys.ChatPresence, cancellationToken));

    /// <summary>The status a chat was given of its own (e.g. offline at work, available in private); null: the header's.</summary>
    public string? PresenceOverrideOf(string key)
    {
        lock (_presenceOverrides)
        {
            return _presenceOverrides.GetValueOrDefault(key);
        }
    }

    /// <summary>The status this chat shows to the others: its own, else the header's.</summary>
    public async Task<string> PresenceOfAsync(string key, CancellationToken cancellationToken = default) =>
        PresenceOverrideOf(key) ?? await GetPresenceAsync(cancellationToken);

    /// <summary>Gives a chat a status of its own (null: back to the header's) and tells it.</summary>
    public async Task SetPresenceOfAsync(string key, string? presence, CancellationToken cancellationToken = default)
    {
        presence = presence is null ? null : Presence.Normalize(presence);
        await settings.SetAsync(SettingKeys.ChatPresence + "." + key, presence, cancellationToken);
        lock (_presenceOverrides)
        {
            _presenceOverrides[key] = presence;
        }

        if (Find(key) is { } service)
        {
            try
            {
                await service.SetPresenceAsync(await PresenceOfAsync(key, cancellationToken), cancellationToken);
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                _logger.LogInformation(ex, "Presence not sent to chat {Key}", key);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The same status in every chat: statuses of their own are dropped.</summary>
    public async Task SetPresenceForAllAsync(string presence, CancellationToken cancellationToken = default)
    {
        List<string> own;
        lock (_presenceOverrides)
        {
            own = [.. _presenceOverrides.Where(p => p.Value is not null).Select(p => p.Key)];
            foreach (var key in own)
            {
                _presenceOverrides[key] = null;
            }
        }

        foreach (var key in own)
        {
            await settings.SetAsync(SettingKeys.ChatPresence + "." + key, null, cancellationToken);
        }

        await SetPresenceAsync(presence, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Remembers the header's status and tells every chat without one of its own (those unreachable get it with their next refresh).</summary>
    public async Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default)
    {
        presence = Presence.Normalize(presence);
        await settings.SetAsync(SettingKeys.ChatPresence, presence, cancellationToken);
        foreach (var service in _services.Where(s => PresenceOverrideOf(s.Key) is null))
        {
            try
            {
                await service.SetPresenceAsync(presence, cancellationToken);
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                _logger.LogInformation(ex, "Presence not sent to chat {Key}", service.Key);
            }
        }
    }

    /// <summary>
    /// The chat is switched off here (its button hidden): in Neruna Cloud others see this person offline; the chosen
    /// status is kept and sent again once the chat is back. Nextcloud keeps its own status (it is used there, too).
    /// </summary>
    public async Task GoOfflineAsync(CancellationToken cancellationToken = default)
    {
        foreach (var service in _services.Where(s => s.Kind == ChatKind.NerunaCloud))
        {
            try
            {
                await service.SetPresenceAsync(Presence.Offline, cancellationToken);
            }
            catch (Exception ex) when (IsUnreachable(ex))
            {
                // The server shows offline anyway after 5 minutes without contact.
                _logger.LogDebug(ex, "Offline status not sent to chat {Key}", service.Key);
            }
        }
    }

    /// <summary>
    /// The organisation is gone (device blocked, disconnected): its messages leave this device at once – also those
    /// not loaded yet since the start.
    /// </summary>
    public async Task ForgetAsync(CloudConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (Find(CloudChat.KeyOf(connection)) is CloudChat chat)
        {
            await chat.ForgetAsync(cancellationToken);
        }
        else
        {
            await CloudChat.ForgetAsync(store, settings, connection, cancellationToken);
        }

        await RefreshAsync(cancellationToken);
    }

    /// <summary>The chats the accounts could have (Nextcloud Talk for each Nextcloud account), switched on or not.</summary>
    public async Task<IReadOnlyList<AccountChatOption>> GetAccountChatsAsync(CancellationToken cancellationToken = default)
    {
        var enabled = await EnabledAccountChatsAsync(cancellationToken);
        var result = new List<AccountChatOption>();
        foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
        {
            foreach (var factory in accountChats)
            {
                if (factory.ChatConnectionOf(account) is { } found)
                {
                    result.Add(new AccountChatOption(found.Connection.Id, $"{account.Title} ({found.ServerName})", factory.Kind, enabled.Contains(found.Connection.Id)));
                }
            }
        }

        return result;
    }

    /// <summary>Switches an account's chat on (a tab of its own) or off.</summary>
    public async Task SetAccountChatAsync(Guid connectionId, bool enabled, CancellationToken cancellationToken = default)
    {
        var ids = await EnabledAccountChatsAsync(cancellationToken);
        if (enabled ? !ids.Add(connectionId) : !ids.Remove(connectionId))
        {
            return;
        }

        await settings.SetAsync(SettingKeys.ChatAccounts, ids.Count == 0 ? null : string.Join(',', ids.Select(i => i.ToString("N"))), cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task<HashSet<Guid>> EnabledAccountChatsAsync(CancellationToken cancellationToken) =>
        ((await settings.GetAsync(SettingKeys.ChatAccounts, cancellationToken)) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToHashSet();

    // The chats as they should be now: one per cloud connection, one per account chat switched on. New ones are
    // loaded, gone ones closed (an organisation's messages are removed with it).
    private async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken);
        try
        {
            var wanted = new List<(string Key, Func<IChatService> Create)>();
            foreach (var connection in await cloud.GetConnectionsAsync(cancellationToken))
            {
                var key = CloudChat.KeyOf(connection);
                wanted.Add((key, () => new CloudChat(cloud, connection, store, settings, time, ct => PresenceOfAsync(key, ct), loggers.CreateLogger<CloudChat>())));
            }

            var enabled = await EnabledAccountChatsAsync(cancellationToken);
            if (enabled.Count > 0)
            {
                foreach (var account in await accounts.GetAccountsAsync(cancellationToken))
                {
                    foreach (var factory in accountChats)
                    {
                        if (factory.ChatConnectionOf(account) is { } found && enabled.Contains(found.Connection.Id))
                        {
                            var key = KeyFor(factory.Kind, found.Connection.Id);
                            wanted.Add((key, () => factory.Create(account, found.Connection, ct => PresenceOfAsync(key, ct))));
                        }
                    }
                }
            }

            var current = _services;
            if (current.Select(s => s.Key).SequenceEqual(wanted.Select(w => w.Key)))
            {
                return false;
            }

            var next = new List<IChatService>();
            foreach (var (key, create) in wanted)
            {
                if (current.FirstOrDefault(s => s.Key == key) is { } kept)
                {
                    next.Add(kept);
                    continue;
                }

                var own = await settings.GetAsync(SettingKeys.ChatPresence + "." + key, cancellationToken);
                lock (_presenceOverrides)
                {
                    _presenceOverrides[key] = own is null ? null : Presence.Normalize(own);
                }

                var service = create();
                service.Changed += OnServiceChanged;
                try
                {
                    await service.LoadAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Local chat {Key} not loaded", key);
                }

                next.Add(service);
            }

            _services = next;
            foreach (var gone in current.Where(s => next.All(n => n.Key != s.Key)))
            {
                gone.Changed -= OnServiceChanged;
                if (gone is CloudChat chat)
                {
                    await chat.ForgetAsync(cancellationToken);
                }

                gone.Dispose();
                lock (_errors)
                {
                    _errors.Remove(gone.Key);
                }
            }

            return true;
        }
        finally
        {
            _sync.Release();
        }
    }

    private void OnServiceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _sync.Dispose();
    }
}

/// <summary>A chat server answered with something the chat cannot use (e.g. Nextcloud Talk is not installed); the message is for the user.</summary>
public sealed class ChatException(string message, Exception? inner = null) : Exception(message, inner);
