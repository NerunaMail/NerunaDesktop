using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Neruna.Core.Accounts;
using Neruna.Core.Chat;
using Neruna.Core.Providers;
using Neruna.Core.Security;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Providers.Dav;

/// <summary>
/// Nextcloud Talk for a Nextcloud account (calendars and contacts via remote.php/dav): the same login and app password.
/// Switched on per account in the chat page.
/// </summary>
public sealed class NextcloudTalkFactory(HttpClient http, ICredentialStore credentials, ILoggerFactory loggers) : IAccountChatFactory
{
    public ChatKind Kind => ChatKind.NextcloudTalk;

    public ServiceConnectionRef? ChatConnectionOf(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        foreach (var connection in account.Connections.Where(c => c.ProviderId is ProviderIds.CalDav or ProviderIds.CardDav).OrderBy(c => c.Kind))
        {
            if (connection.Settings.TryGetValue(DavSettings.UrlKey, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var dav)
                && NextcloudTalkChat.ServerOf(dav) is { } server)
            {
                return new ServiceConnectionRef(connection, server.Host);
            }
        }

        return null;
    }

    public IChatService Create(Account account, ServiceConnection connection, Func<CancellationToken, Task<string>> presence)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(connection);
        var settings = DavSettings.FromDictionary(connection.Settings);
        var server = NextcloudTalkChat.ServerOf(settings.Url) ?? throw new InvalidOperationException("Not a Nextcloud connection.");
        return new NextcloudTalkChat(ChatController.KeyFor(ChatKind.NextcloudTalk, connection.Id), account.Title, server, settings.Username,
            DavHttp.For(http, connection.Settings), ct => credentials.GetSecretAsync(connection.Id, ct), presence, loggers.CreateLogger<NextcloudTalkChat>());
    }
}

/// <summary>
/// The conversations of a Nextcloud Talk account over its OCS API (polling). Messages stay on the Nextcloud: here only
/// those of the conversations opened since the start, in memory. Calls open in Talk in the browser.
/// </summary>
/// <seealso href="https://nextcloud-talk.readthedocs.io/en/latest/"/>
public sealed class NextcloudTalkChat(
    string key,
    string title,
    Uri server,
    string username,
    HttpClient http,
    Func<CancellationToken, Task<string?>> password,
    Func<CancellationToken, Task<string>> presence,
    ILogger logger) : IChatService
{
    private const string Spreed = "ocs/v2.php/apps/spreed/api/";
    private const int HistoryLimit = 100;
    private const int KeptPerConversation = 500;
    private static readonly TimeSpan ParticipantsAge = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
    private readonly Dictionary<string, (DateTimeOffset At, List<ChatPerson> People)> _participants = [];
    private readonly Dictionary<string, ChatPerson> _senders = [];
    private List<TalkRoom> _rooms = [];
    private string? _me;
    private string? _meName;
    private bool _presenceSent;
    private bool _installed = true;

    public string Key { get; } = key;

    public string Title { get; } = title;

    public ChatKind Kind => ChatKind.NextcloudTalk;

    public ChatSnapshot Current { get; private set; } = ChatSnapshot.Empty with { Connected = true, RetentionDays = 0 };

    public event EventHandler? Changed;

    /// <summary>"https://cloud.example.com/nextcloud/" for ".../nextcloud/remote.php/dav/…"; null when the address is no Nextcloud's.</summary>
    public static Uri? ServerOf(Uri davUrl)
    {
        ArgumentNullException.ThrowIfNull(davUrl);
        var path = davUrl.AbsolutePath;
        var at = path.IndexOf("/remote.php/dav", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : new Uri(davUrl, path[..(at + 1)]);
    }

    // Nothing is kept between starts: the conversations come from the Nextcloud.
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        bool changed;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var before = Signature();
            await RefreshCoreAsync(cancellationToken);
            changed = Signature() != before;
            if (changed)
            {
                Publish();
            }
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

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (_me is null)
        {
            var user = await OcsAsync(HttpMethod.Get, "ocs/v2.php/cloud/user", null, cancellationToken);
            _me = Text(user, "id") ?? username;
            _meName = Text(user, "display-name") ?? Text(user, "displayname") ?? _me;
        }

        if (!_presenceSent)
        {
            await SendPresenceAsync(await presence(cancellationToken), cancellationToken);
        }

        JsonElement rooms;
        try
        {
            rooms = await OcsAsync(HttpMethod.Get, Spreed + "v4/room?includeStatus=true", null, cancellationToken);
            _installed = true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _installed = false;
            _rooms = [];
            Publish();
            throw new ChatException(T("Nextcloud Talk ist auf dieser Nextcloud nicht installiert oder nicht freigegeben."), ex);
        }

        _rooms = rooms.ValueKind == JsonValueKind.Array
            ? [.. rooms.EnumerateArray().Select(TalkRoom.Parse).Where(r => r.Type != TalkRoom.Changelog)]
            : [];

        // What is new in the conversations already opened.
        foreach (var (token, messages) in _messages)
        {
            var room = _rooms.FirstOrDefault(r => r.Token == token);
            var last = messages.Count == 0 ? 0 : messages[^1].Id;
            if (room is not null && room.LastMessageId > last)
            {
                await FetchNewerAsync(token, last, cancellationToken);
            }
        }

        foreach (var token in _participants.Where(p => DateTimeOffset.UtcNow - p.Value.At > ParticipantsAge).Select(p => p.Key).ToList())
        {
            await FetchParticipantsAsync(token, cancellationToken);
        }
    }

    public async Task OpenAsync(string conversation, CancellationToken cancellationToken = default)
    {
        var token = ChatConversation.IdOf(conversation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_messages.ContainsKey(token) || _rooms.All(r => r.Token != token))
            {
                return;
            }

            var history = await OcsAsync(HttpMethod.Get,
                $"{Spreed}v1/chat/{Uri.EscapeDataString(token)}?lookIntoFuture=0&limit={HistoryLimit}&setReadMarker=0", null, cancellationToken);
            // Newest first.
            _messages[token] = [.. Parse(token, history).OrderBy(m => m.Id)];
            await FetchParticipantsAsync(token, cancellationToken);
            Publish();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> OpenPrivateAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(personId);
        if (_rooms.FirstOrDefault(r => r.Type == TalkRoom.OneToOne && r.Name == personId) is { } existing)
        {
            return ChatConversation.Room(existing.Token);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Talk gives the existing conversation if there is one.
            var room = TalkRoom.Parse(await OcsAsync(HttpMethod.Post, Spreed + "v4/room", new { roomType = TalkRoom.OneToOne, invite = personId }, cancellationToken));
            _rooms = [room, .. _rooms.Where(r => r.Token != room.Token)];
            Publish();
            return ChatConversation.Room(room.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(string conversation, string text, CancellationToken cancellationToken = default)
    {
        var token = ChatConversation.IdOf(conversation);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sent = await OcsAsync(HttpMethod.Post, $"{Spreed}v1/chat/{Uri.EscapeDataString(token)}", new { message = text }, cancellationToken);
            if (Parse(token, sent).FirstOrDefault() is { } message && _messages.TryGetValue(token, out var messages) && messages.All(m => m.Id != message.Id))
            {
                messages.Add(message);
            }

            Read(token, sent.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) ? value : 0);
            Publish();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task MarkReadAsync(string conversation, CancellationToken cancellationToken = default)
    {
        var token = ChatConversation.IdOf(conversation);
        long last;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_rooms.FirstOrDefault(r => r.Token == token) is not { } room || room.Unread == 0)
            {
                return;
            }

            last = Math.Max(room.LastMessageId, _messages.TryGetValue(token, out var messages) && messages.Count > 0 ? messages[^1].Id : 0);
            Read(token, last);
            Publish();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await OcsAsync(HttpMethod.Post, $"{Spreed}v1/chat/{Uri.EscapeDataString(token)}/read", new { lastReadMessage = last }, cancellationToken);
    }

    public Task SetPresenceAsync(string presence, CancellationToken cancellationToken = default) => SendPresenceAsync(presence, cancellationToken);

    // Nextcloud's status: online, away, dnd, invisible (Neruna's "offline": shown offline, still able to write).
    private async Task SendPresenceAsync(string value, CancellationToken cancellationToken)
    {
        var status = Presence.Normalize(value) switch
        {
            Presence.Away or Presence.BeRightBack => "away",
            Presence.DoNotDisturb => "dnd",
            Presence.Offline => "invisible",
            _ => "online",
        };
        try
        {
            await OcsAsync(HttpMethod.Put, "ocs/v2.php/apps/user_status/api/v1/user_status/status", new { statusType = status }, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // The status app is switched off on this Nextcloud: there is no status to set.
            logger.LogInformation(ex, "Nextcloud status not set");
        }

        _presenceSent = true;
    }

    /// <summary>Talk's page for the conversation: there the call starts (audio, video, screen sharing).</summary>
    public Uri? CallUrl(string conversation) =>
        _installed && ChatConversation.IsRoom(conversation) ? new Uri(server, "index.php/call/" + Uri.EscapeDataString(ChatConversation.IdOf(conversation))) : null;

    private void Read(string token, long last)
    {
        _rooms = [.. _rooms.Select(r => r.Token == token ? r with { Unread = 0, LastRead = Math.Max(r.LastRead, last) } : r)];
    }

    private async Task FetchNewerAsync(string token, long last, CancellationToken cancellationToken)
    {
        var newer = await OcsAsync(HttpMethod.Get,
            $"{Spreed}v1/chat/{Uri.EscapeDataString(token)}?lookIntoFuture=1&timeout=0&lastKnownMessageId={last}&limit={HistoryLimit}&setReadMarker=0", null, cancellationToken);
        var messages = _messages[token];
        foreach (var message in Parse(token, newer).OrderBy(m => m.Id).Where(m => m.Id > last))
        {
            messages.Add(message);
        }

        if (messages.Count > KeptPerConversation)
        {
            messages.RemoveRange(0, messages.Count - KeptPerConversation);
        }
    }

    private async Task FetchParticipantsAsync(string token, CancellationToken cancellationToken)
    {
        var list = await OcsAsync(HttpMethod.Get, $"{Spreed}v4/room/{Uri.EscapeDataString(token)}/participants?includeStatus=true", null, cancellationToken);
        var people = list.ValueKind != JsonValueKind.Array ? [] : list.EnumerateArray()
            .Select(p => new ChatPerson(Text(p, "actorId") ?? string.Empty, Text(p, "displayName") ?? Text(p, "actorId") ?? "?", null, PresenceOf(Text(p, "status"))))
            .Where(p => p.Id.Length > 0)
            .ToList();
        _participants[token] = (DateTimeOffset.UtcNow, people);
    }

    // Comments only: system messages (joined, call started …) and deleted ones are left out.
    private List<ChatMessage> Parse(string token, JsonElement data)
    {
        var items = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : data.ValueKind == JsonValueKind.Object ? [data] : [];
        var result = new List<ChatMessage>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(Text(item, "systemMessage")) || Text(item, "messageType") is "system" or "command" or "comment_deleted"
                || !item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }

            var sender = Text(item, "actorId") ?? string.Empty;
            if (!_senders.ContainsKey(sender))
            {
                _senders[sender] = new ChatPerson(sender, Text(item, "actorDisplayName") is { Length: > 0 } name ? name : sender, null, Presence.Offline);
            }

            var sentAt = item.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
            var text = TalkText.Render(Text(item, "message") ?? string.Empty, item.TryGetProperty("messageParameters", out var parameters) ? parameters : default);
            result.Add(new ChatMessage(id, token, sender, null, text, sentAt));
        }

        return result;
    }

    // The snapshot the UI reads: group conversations as rooms, one-to-one ones as private conversations.
    private void Publish()
    {
        var people = new Dictionary<string, ChatPerson>(_senders);
        foreach (var (_, participants) in _participants.Values)
        {
            foreach (var person in participants)
            {
                people[person.Id] = person;
            }
        }

        var rooms = new List<ChatRoom>();
        foreach (var room in _rooms.OrderByDescending(r => r.LastActivity))
        {
            var direct = room.Type is TalkRoom.OneToOne or TalkRoom.FormerOneToOne;
            if (direct && room.Type == TalkRoom.OneToOne && room.Name.Length > 0)
            {
                people[room.Name] = new ChatPerson(room.Name, room.DisplayName, null, PresenceOf(room.Status));
            }

            var members = _participants.TryGetValue(room.Token, out var known) ? known.People.Select(p => p.Id).ToList()
                : direct ? [room.Name, _me ?? username] : [];
            rooms.Add(new ChatRoom(room.Token, room.DisplayName, string.IsNullOrWhiteSpace(room.Description) ? null : room.Description, members,
                direct, direct ? PresenceOf(room.Type == TalkRoom.OneToOne ? room.Status : null) : null));
        }

        if (_me is not null)
        {
            people[_me] = new ChatPerson(_me, _meName ?? _me, null, people.TryGetValue(_me, out var self) ? self.Presence : Presence.Available);
        }

        Current = new ChatSnapshot(true, _installed, 0, _me, rooms, [.. people.Values],
            [.. _messages.Values.SelectMany(m => m).OrderBy(m => m.SentAt).ThenBy(m => m.Id)],
            _rooms.ToDictionary(r => ChatConversation.Room(r.Token), r => r.LastRead))
        {
            ServerUnread = _rooms.ToDictionary(r => ChatConversation.Room(r.Token), r => r.Unread),
        };
    }

    private string Signature() =>
        string.Join('|', _rooms.Select(r => r.ToString())) + "#" + string.Join(',', _messages.Select(m => m.Key + ":" + m.Value.Count + ":" + (m.Value.Count > 0 ? m.Value[^1].Id : 0)))
        + "#" + string.Join(',', _participants.Values.SelectMany(p => p.People).Select(p => p.Id + p.Presence)) + "#" + _installed;

    private static string PresenceOf(string? status) => status switch
    {
        "online" => Presence.Available,
        "away" => Presence.Away,
        "dnd" => Presence.DoNotDisturb,
        _ => Presence.Offline,
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // An OCS request with the app password; the "data" of the answer (Undefined for 304: nothing new).
    private async Task<JsonElement> OcsAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(server, path));
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{await password(cancellationToken) ?? string.Empty}")));
        if (body is not null)
        {
            // With a length (not chunked): some web servers in front of PHP drop chunked bodies.
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return default;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ChatException(T("Nextcloud hat die Anmeldung abgelehnt – bitte das Konto unter Einstellungen → Konten neu anmelden."));
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Nextcloud: {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("ocs", out var ocs) && ocs.TryGetProperty("data", out var data) ? data.Clone() : default;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>A Talk conversation as the room list gives it.</summary>
    private sealed record TalkRoom(string Token, int Type, string Name, string DisplayName, string? Description, int Unread, long LastRead,
        long LastMessageId, long LastActivity, string? Status)
    {
        public const int OneToOne = 1;
        public const int Changelog = 4;
        public const int FormerOneToOne = 5;

        public static TalkRoom Parse(JsonElement room)
        {
            static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            var lastMessage = room.TryGetProperty("lastMessage", out var m) && m.ValueKind == JsonValueKind.Object ? Number(m, "id") : 0;
            return new TalkRoom(Text(room, "token") ?? string.Empty, (int)Number(room, "type"), Text(room, "name") ?? string.Empty,
                Text(room, "displayName") ?? Text(room, "name") ?? string.Empty, Text(room, "description"), (int)Number(room, "unreadMessages"),
                Number(room, "lastReadMessage"), lastMessage, Number(room, "lastActivity"), Text(room, "status"));
        }
    }
}

/// <summary>Talk's message text: placeholders like {mention-user1} or {file} replaced by what they stand for.</summary>
public static class TalkText
{
    public static string Render(string message, JsonElement parameters)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return message;
        }

        foreach (var parameter in parameters.EnumerateObject())
        {
            var value = parameter.Value;
            var name = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
            var type = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var shown = type switch
            {
                "user" or "guest" or "call" or "user-group" or "group" or "circle" or "email" or "federated_user" => "@" + name,
                "file" => "📎 " + name,
                _ => name,
            };
            message = message.Replace("{" + parameter.Name + "}", shown, StringComparison.Ordinal);
        }

        return message;
    }
}
