using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core.Chat;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Chat: a tab per chat (each Neruna Cloud/Control organisation, Nextcloud Talk per account); in the tab rooms and
/// private conversations on the left, the people of the room below, messages on the right. Asks the servers every few
/// seconds while the chat is in front and every 20 seconds otherwise (badge, online status). The own status (window
/// header) is the same in all chats.
/// </summary>
internal sealed partial class ChatViewModel : ViewModelBase
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(20);

    private readonly ChatController _chat;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, string> _selectedPerTab = [];
    private bool _polling;
    private bool _polled;
    private string? _shownConversation;

    public ChatViewModel(ChatController chat, ILogger<ChatViewModel> logger)
    {
        _chat = chat;
        _logger = logger;
        _timer = new DispatcherTimer { Interval = BackgroundInterval };
        _timer.Tick += async (_, _) => await PollAsync();
        chat.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>"Cloud verbinden": the shell opens Einstellungen → Cloud.</summary>
    public event EventHandler? CloudSetupRequested;

    /// <summary>"Anruf": Nextcloud Talk opens in the browser at this address.</summary>
    public event EventHandler<Uri>? CallRequested;

    /// <summary>The chats as tabs (shown when there is more than one).</summary>
    public ObservableCollection<ChatTabItem> Tabs { get; } = [];

    public bool ShowTabs => Tabs.Count > 1 || AccountChats.Count > 0;

    /// <summary>Nextcloud accounts whose Talk can be shown as a tab of its own (switched on or off here).</summary>
    public ObservableCollection<AccountChatChoice> AccountChats { get; } = [];

    public bool HasAccountChats => AccountChats.Count > 0;

    public ObservableCollection<ChatConversationItem> Rooms { get; } = [];

    public ObservableCollection<ChatConversationItem> Directs { get; } = [];

    /// <summary>The people of the selected room (online first), or the two of a private conversation.</summary>
    public ObservableCollection<ChatPersonItem> People { get; } = [];

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];

    /// <summary>A chat is set up (Neruna Cloud/Control connected, or Nextcloud Talk switched on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotConnected), nameof(ShowNotIncluded), nameof(ShowChat), nameof(ShowPresence))]
    public partial bool IsConnected { get; set; }

    /// <summary>At least one chat may be used (Neruna Cloud: included in the licence).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotIncluded), nameof(ShowChat), nameof(ShowPresence))]
    public partial bool IsAvailable { get; set; }

    public bool ShowNotConnected => !IsConnected;

    public bool ShowNotIncluded => IsConnected && !IsAvailable && _polled;

    public bool ShowChat => IsConnected && IsAvailable;

    /// <summary>The own status counts: a chat to show it in, and its button not hidden.</summary>
    public bool ShowPresence => ShowChat && IsEnabled;

    /// <summary>False while the chat button is hidden (Einstellungen → Design): no polling, shown offline to others.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPresence))]
    public partial bool IsEnabled { get; set; } = true;

    private bool _started;

    async partial void OnIsEnabledChanged(bool value)
    {
        if (!_started)
        {
            return;
        }

        if (value)
        {
            _timer.Start();
            await PollAsync();
        }
        else
        {
            _timer.Stop();
            await _chat.GoOfflineAsync();
        }
    }

    /// <summary>The tab shown (<see cref="IChatService.Key"/>).</summary>
    [ObservableProperty]
    public partial string? SelectedTab { get; set; }

    private IChatService? Service => SelectedTab is { } key ? _chat.Find(key) : null;

    public bool IsTalk => Service?.Kind == ChatKind.NextcloudTalk;

    /// <summary>How long the messages are kept (shown above the conversation).</summary>
    [ObservableProperty]
    public partial string RetentionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RetentionDetails { get; set; } = string.Empty;

    public string RoomsEmptyText => IsTalk
        ? T("Noch keine Unterhaltungen – sie werden in Nextcloud Talk angelegt.")
        : T("Noch keine Räume – sie werden im Portal angelegt.");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))]
    public partial int TotalUnread { get; set; }

    public bool HasUnread => TotalUnread > 0;

    public string UnreadText => TotalUnread > 99 ? "99+" : TotalUnread.ToString(CultureInfo.InvariantCulture);

    /// <summary>The open conversation ("room:…" / "pm:…"); both lists on the left show it as selected.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(CallCommand))]
    public partial string? SelectedKey { get; set; }

    public ChatConversationItem? Selected => Rooms.Concat(Directs).FirstOrDefault(c => c.Key == SelectedKey);

    public bool HasSelection => Selected is not null;

    public string Title => Selected?.Title ?? string.Empty;

    public string? Subtitle => Selected?.Subtitle;

    public string PeopleTitle => Selected is { IsRoom: false } ? T("Unterhaltung") : T("Im Raum");

    /// <summary>Audio and video calls: in Nextcloud Talk (the browser), for its conversations.</summary>
    public bool CanCall => SelectedKey is { } key && Service?.CallUrl(key) is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = string.Empty;

    /// <summary>What went wrong: sending here, or the last refresh of this tab's chat.</summary>
    [ObservableProperty]
    public partial string? Error { get; set; }

    private string? _sendError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceLabel), nameof(PresenceBrush))]
    public partial string Presence { get; set; } = Neruna.Core.Chat.Presence.Available;

    public string PresenceLabel => Neruna.Core.Chat.Presence.Label(Presence);

    public IBrush PresenceBrush => PresenceBrushes.Of(Presence);

    partial void OnPresenceChanged(string value) => Rebuild();

    // ---- The own status: the same in every chat, or one of its own (e.g. offline at work, available in private) ----

    /// <summary>More than one chat: a status can be set for this one only or for all.</summary>
    public bool HasSeveralChats => Tabs.Count > 1;

    /// <summary>What the others see in the open chat.</summary>
    public string TabPresence => SelectedTab is { } key ? _chat.PresenceOverrideOf(key) ?? Presence : Presence;

    public string TabPresenceLabel => Neruna.Core.Chat.Presence.Label(TabPresence);

    public IBrush TabPresenceBrush => PresenceBrushes.Of(TabPresence);

    /// <summary>Sets the status in the open chat only, or in all of them (statuses of their own are dropped).</summary>
    public async Task SetTabPresenceAsync(string presence, bool onlyHere)
    {
        presence = Neruna.Core.Chat.Presence.Normalize(presence);
        try
        {
            if (onlyHere && HasSeveralChats && SelectedTab is { } key)
            {
                await Task.Run(() => _chat.SetPresenceOfAsync(key, presence));
            }
            else
            {
                Presence = presence;
                await Task.Run(() => _chat.SetPresenceForAllAsync(presence));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Presence not saved");
        }

        Rebuild();
    }

    /// <summary>Smileys offered beside the text field (any other emoji can be typed or pasted).</summary>
    public static IReadOnlyList<string> Emojis { get; } =
    [
        "😀", "😄", "😁", "😂", "🙂", "😉", "😊", "😍", "😘", "😎", "🤔", "😐", "😮", "😴", "😢", "😭", "😡", "🙈",
        "👍", "👎", "👌", "👏", "🙏", "💪", "👋", "🤝", "✅", "❌", "⚠️", "❓", "❤️", "🎉", "🔥", "☕", "🍕", "🍰",
        "📞", "📧", "📅", "⏰", "💡", "🚀",
    ];

    public static IReadOnlyList<PresenceChoice> PresenceChoices { get; } =
        Neruna.Core.Chat.Presence.All.Select(p => new PresenceChoice(p, Neruna.Core.Chat.Presence.Label(p), PresenceBrushes.Of(p))).ToList();

    /// <summary>The chat page is in front (and the window active): ask often and mark what is shown as read.</summary>
    public bool IsActive
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            _timer.Interval = value ? ActiveInterval : BackgroundInterval;
            if (value)
            {
                _ = MarkShownReadAsync();
                _ = PollAsync();
                _ = LoadAccountChatsAsync();
            }
        }
    }

    public async Task StartAsync()
    {
        try
        {
            Presence = await _chat.GetPresenceAsync();
            await _chat.LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Local chat not loaded");
        }

        await LoadAccountChatsAsync();
        _started = true;
        if (!IsEnabled)
        {
            await _chat.GoOfflineAsync();
            return;
        }

        _timer.Start();
        await PollAsync();
    }

    /// <summary>Asks the servers now (e.g. right after connecting the cloud).</summary>
    public async Task RefreshNowAsync()
    {
        await PollAsync();
        await LoadAccountChatsAsync();
    }

    private async Task PollAsync()
    {
        if (_polling || !IsEnabled)
        {
            return;
        }

        _polling = true;
        try
        {
            await Task.Run(() => _chat.RefreshAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Each chat keeps what it has and says why (ErrorOf); this is only the list of chats itself.
            _logger.LogWarning(ex, "Chats not refreshed");
        }
        finally
        {
            _polling = false;
            if (!_polled)
            {
                _polled = true;
                OnPropertyChanged(nameof(ShowNotIncluded));
            }
        }
    }

    private async Task LoadAccountChatsAsync()
    {
        try
        {
            var options = await _chat.GetAccountChatsAsync();
            Sync(AccountChats, options.Select(o => new AccountChatChoice(o.ConnectionId, o.Title, o.IsEnabled, Toggle)).ToList());
            OnPropertyChanged(nameof(HasAccountChats));
            OnPropertyChanged(nameof(ShowTabs));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Account chats not listed");
        }
    }

    /// <summary>A new Nextcloud account with "Nextcloud Talk im Chat anzeigen": its tab comes at once.</summary>
    public async Task EnableAccountChatAsync(Neruna.Core.Accounts.Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        try
        {
            foreach (var option in (await _chat.GetAccountChatsAsync()).Where(o => account.Connections.Any(c => c.Id == o.ConnectionId)))
            {
                await Task.Run(() => _chat.SetAccountChatAsync(option.ConnectionId, true));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat of the new account not switched on");
        }

        await LoadAccountChatsAsync();
    }

    // Nextcloud Talk of an account switched on (a tab of its own, shown right away) or off.
    private async void Toggle(AccountChatChoice choice, bool enabled)
    {
        try
        {
            await Task.Run(() => _chat.SetAccountChatAsync(choice.ConnectionId, enabled));
            if (enabled)
            {
                SelectedTab = ChatController.KeyFor(ChatKind.NextcloudTalk, choice.ConnectionId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Account chat not switched");
        }

        await LoadAccountChatsAsync();
        Rebuild();
    }

    // Everything from the chats; the selection, and the messages already shown, stay.
    private void Rebuild()
    {
        // Organisations whose licence leaves the chat out get no tab; Nextcloud Talk always (also to say what is wrong).
        var services = _chat.Services;
        var visible = services.Where(s => s.Current.Available || s.Kind != ChatKind.NerunaCloud).ToList();
        IsConnected = services.Count > 0;
        IsAvailable = visible.Count > 0;
        TotalUnread = visible.Sum(s => s.Current.TotalUnread);

        var tab = SelectedTab;
        if (tab is null || visible.All(s => s.Key != tab))
        {
            tab = visible.FirstOrDefault()?.Key;
        }

        Sync(Tabs, visible.Select(s => new ChatTabItem(s.Key, s.Title, s.Kind, _chat.PresenceOverrideOf(s.Key) ?? Presence)
        {
            Unread = s.Current.TotalUnread,
            IsSelected = s.Key == tab,
            HasError = _chat.ErrorOf(s.Key) is not null,
        }).ToList());
        OnPropertyChanged(nameof(ShowTabs));
        OnPropertyChanged(nameof(HasSeveralChats));
        OnPropertyChanged(nameof(TabPresence));
        OnPropertyChanged(nameof(TabPresenceLabel));
        OnPropertyChanged(nameof(TabPresenceBrush));

        if (tab != SelectedTab)
        {
            SelectedTab = tab; // builds again
            return;
        }

        RebuildTab();
    }

    partial void OnSelectedTabChanged(string? value)
    {
        _shownConversation = null;
        Messages.Clear();
        _sendError = null;
        SelectedKey = value is not null ? _selectedPerTab.GetValueOrDefault(value) : null;
        OnPropertyChanged(nameof(IsTalk));
        OnPropertyChanged(nameof(RoomsEmptyText));
        Rebuild();
    }

    private void RebuildTab()
    {
        var service = Service;
        var snapshot = service?.Current ?? ChatSnapshot.Empty;
        if (service?.Kind == ChatKind.NextcloudTalk)
        {
            RetentionText = T("Nachrichten bleiben auf Ihrer Nextcloud");
            RetentionDetails = T("Die Nachrichten liegen auf Ihrer Nextcloud und werden dort aufbewahrt. Neruna zeigt sie nur an und speichert sie nicht auf diesem Computer.");
        }
        else
        {
            RetentionText = snapshot.RetentionDays == 1 ? T("Nachrichten werden 1 Tag aufbewahrt") : F("Nachrichten werden {0} Tage aufbewahrt", snapshot.RetentionDays);
            RetentionDetails = RetentionText + T(" und danach gelöscht – auf dem Server und auf diesem Computer. ")
                               + T("Die Dauer legt Ihre Organisation im Portal fest. Der Chat ist nicht für vertrauliche Daten gedacht.");
        }

        Error = _sendError ?? (service is null ? null : _chat.ErrorOf(service.Key));

        var people = snapshot.People.ToDictionary(p => p.Id);
        var me = snapshot.Me;
        var selectedKey = SelectedKey;
        var groups = snapshot.Rooms.Where(r => !r.IsDirect).ToList();
        var directRooms = snapshot.Rooms.Where(r => r.IsDirect).ToList();
        // A conversation that is gone (or none chosen yet): the first room.
        if (selectedKey is null || (ChatConversation.IsRoom(selectedKey) && snapshot.Rooms.All(r => ChatConversation.Room(r.Id) != selectedKey)))
        {
            selectedKey = (groups.Count > 0 ? groups : directRooms).Select(r => ChatConversation.Room(r.Id)).FirstOrDefault();
        }

        var rooms = groups.Select(r => new ChatConversationItem(ChatConversation.Room(r.Id), r.Name, r.Description, true, null)
        {
            Unread = snapshot.UnreadOf(ChatConversation.Room(r.Id)),
            IsSelected = ChatConversation.Room(r.Id) == selectedKey,
        }).ToList();

        // Private conversations: Talk's one-to-one conversations; in Neruna Cloud everyone written with (newest first),
        // plus the one just opened.
        var directs = directRooms.Select(r => new ChatConversationItem(ChatConversation.Room(r.Id), r.Name,
            Neruna.Core.Chat.Presence.Label(r.Presence ?? Neruna.Core.Chat.Presence.Offline), false, r.Presence ?? Neruna.Core.Chat.Presence.Offline)
        {
            Unread = snapshot.UnreadOf(ChatConversation.Room(r.Id)),
            IsSelected = ChatConversation.Room(r.Id) == selectedKey,
        }).ToList();

        var directIds = me is null ? [] : snapshot.Messages.Where(m => m.RoomId is null)
            .OrderByDescending(m => m.Id)
            .Select(m => m.SenderId == me ? m.RecipientId : m.SenderId)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (selectedKey is not null && !ChatConversation.IsRoom(selectedKey) && !directIds.Contains(ChatConversation.IdOf(selectedKey)))
        {
            directIds.Insert(0, ChatConversation.IdOf(selectedKey));
        }

        directs.AddRange(directIds.Where(people.ContainsKey).Select(id =>
        {
            var person = people[id];
            return new ChatConversationItem(ChatConversation.Private(id), person.Name, Neruna.Core.Chat.Presence.Label(person.Presence), false, person.Presence)
            {
                Unread = snapshot.UnreadOf(ChatConversation.Private(id)),
                IsSelected = ChatConversation.Private(id) == selectedKey,
            };
        }));

        Sync(Rooms, rooms);
        Sync(Directs, directs);

        if (selectedKey != SelectedKey)
        {
            SelectedKey = selectedKey; // builds again
            return;
        }

        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(PeopleTitle));
        OnPropertyChanged(nameof(CanCall));
        CallCommand.NotifyCanExecuteChanged();
        ShowConversation();
    }

    partial void OnSelectedKeyChanged(string? value)
    {
        if (SelectedTab is { } tab && value is not null)
        {
            _selectedPerTab[tab] = value;
        }

        // Nextcloud Talk loads a conversation's messages when it is opened.
        if (Service is { } service && value is not null)
        {
            _ = OpenAsync(service, value);
        }

        RebuildTab();
    }

    private async Task OpenAsync(IChatService service, string conversation)
    {
        try
        {
            await Task.Run(() => service.OpenAsync(conversation));
        }
        catch (Exception ex) when (ChatController.IsUnreachable(ex))
        {
            _logger.LogDebug(ex, "Conversation not loaded");
        }
    }

    // Clicked: the person looks at it – read, whatever the window activation says (a popup just closed may leave it
    // reported as inactive).
    [RelayCommand]
    private void Select(ChatConversationItem? item)
    {
        if (item is not null)
        {
            SelectedKey = item.Key;
            _ = MarkShownReadAsync();
        }
    }

    [RelayCommand]
    private void SelectTab(ChatTabItem? tab)
    {
        if (tab is not null)
        {
            SelectedTab = tab.Key;
            _ = MarkShownReadAsync();
        }
    }

    private void ShowConversation()
    {
        var snapshot = Service?.Current ?? ChatSnapshot.Empty;
        var key = SelectedKey;
        if (key is null || snapshot.Me is not { } me)
        {
            Messages.Clear();
            People.Clear();
            _shownConversation = null;
            return;
        }

        var people = snapshot.People.ToDictionary(p => p.Id);
        IEnumerable<string> memberIds = ChatConversation.IsRoom(key)
            ? snapshot.Rooms.FirstOrDefault(r => ChatConversation.Room(r.Id) == key)?.MemberIds ?? []
            : [ChatConversation.IdOf(key), me];
        Sync(People, memberIds.Where(people.ContainsKey).Select(id => new ChatPersonItem(people[id], id == me))
            .OrderBy(p => p.IsOffline).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList());

        var messages = snapshot.MessagesOf(key).ToList();
        if (_shownConversation != key || Messages.Count > messages.Count || (Messages.Count > 0 && messages.All(m => m.Id != Messages[^1].Id)))
        {
            Messages.Clear();
        }

        _shownConversation = key;
        var lastShown = Messages.Count == 0 ? 0 : Messages[^1].Id;
        foreach (var message in messages.Where(m => m.Id > lastShown))
        {
            var previous = Messages.Count == 0 ? null : Messages[^1];
            Messages.Add(ChatMessageItem.Create(message, previous, people.GetValueOrDefault(message.SenderId)?.Name ?? T("(ehemalig)"), message.SenderId == me));
        }

        if (IsActive)
        {
            _ = MarkShownReadAsync();
        }
    }

    private async Task MarkShownReadAsync()
    {
        if (Service is not { } service || SelectedKey is not { } key || service.Current.UnreadOf(key) == 0)
        {
            return;
        }

        try
        {
            await service.MarkReadAsync(key);
        }
        catch (Exception ex) when (ChatController.IsUnreachable(ex))
        {
            _logger.LogDebug(ex, "Read marker not sent");
        }
    }

    [RelayCommand]
    private async Task OpenPrivateAsync(ChatPersonItem? person)
    {
        if (person is null || person.IsMe || Service is not { } service)
        {
            return;
        }

        // Not written with yet: the list shows it at the top until there are messages (Talk makes the conversation).
        try
        {
            SelectedKey = await service.OpenPrivateAsync(person.Id);
            _ = MarkShownReadAsync();
        }
        catch (Exception ex) when (ChatController.IsUnreachable(ex))
        {
            _logger.LogWarning(ex, "Private conversation not opened");
            Error = T("Die private Unterhaltung konnte nicht geöffnet werden: ") + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (Service is not { } service || SelectedKey is not { } key || text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        try
        {
            await service.SendAsync(key, text);
            _sendError = null;
            Error = _chat.ErrorOf(service.Key);
        }
        catch (Exception ex) when (ChatController.IsUnreachable(ex))
        {
            _logger.LogWarning(ex, "Chat message not sent");
            Draft = text;
            Error = _sendError = T("Nicht gesendet: ") + ex.Message;
        }
    }

    private bool CanSend() => SelectedKey is not null && !string.IsNullOrWhiteSpace(Draft) && Draft.Length <= 4000;

    [RelayCommand(CanExecute = nameof(CanCall))]
    private void Call()
    {
        if (SelectedKey is { } key && Service?.CallUrl(key) is { } url)
        {
            CallRequested?.Invoke(this, url);
        }
    }

    /// <summary>The same status in every chat.</summary>
    [RelayCommand]
    private Task SetPresenceAsync(string? presence) => SetTabPresenceAsync(presence ?? Neruna.Core.Chat.Presence.Available, onlyHere: false);

    [RelayCommand]
    private void OpenCloudSetup() => CloudSetupRequested?.Invoke(this, EventArgs.Empty);

    // Replaces the items only where they differ, so lists do not flicker or lose their scroll position.
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count)
            {
                if (!Equals(target[i], items[i]))
                {
                    target[i] = items[i];
                }
            }
            else
            {
                target.Add(items[i]);
            }
        }

        while (target.Count > items.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

/// <summary>A chat as a tab: the organisation or the Nextcloud account, with its unread messages.</summary>
/// <param name="Presence">What the others see in this chat (its own status or the header's).</param>
internal sealed record ChatTabItem(string Key, string Title, ChatKind Kind, string Presence)
{
    public IBrush PresenceBrush => PresenceBrushes.Of(Presence);

    public int Unread { get; init; }

    public bool IsSelected { get; init; }

    /// <summary>Its last refresh failed (the tab says why when open).</summary>
    public bool HasError { get; init; }

    public bool HasUnread => Unread > 0;

    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString(CultureInfo.InvariantCulture);

    public bool IsTalk => Kind == ChatKind.NextcloudTalk;

    public string KindText => IsTalk ? T("Nextcloud Talk") : T("Neruna Cloud");
}

/// <summary>A Nextcloud account whose Talk can be switched on as a tab.</summary>
internal sealed class AccountChatChoice(Guid connectionId, string title, bool isEnabled, Action<AccountChatChoice, bool> toggle)
{
    public Guid ConnectionId { get; } = connectionId;

    public string Title { get; } = title;

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (value != isEnabled)
            {
                isEnabled = value;
                toggle(this, value);
            }
        }
    }

    public override bool Equals(object? obj) => obj is AccountChatChoice other && other.ConnectionId == ConnectionId && other.Title == Title && other.IsEnabled == IsEnabled;

    public override int GetHashCode() => HashCode.Combine(ConnectionId, Title, IsEnabled);
}

/// <summary>A room or a private conversation in the list on the left.</summary>
/// <param name="Presence">Of the other person (private conversations).</param>
internal sealed record ChatConversationItem(string Key, string Title, string? Subtitle, bool IsRoom, string? Presence)
{
    public int Unread { get; init; }

    public bool IsSelected { get; init; }

    public bool HasUnread => Unread > 0;

    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString(CultureInfo.InvariantCulture);

    public IBrush PresenceBrush => PresenceBrushes.Of(Presence ?? Neruna.Core.Chat.Presence.Offline);

    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>A person in the room list or the list of everyone; a click opens a private conversation.</summary>
internal sealed record ChatPersonItem(string Id, string Name, string? Position, string Presence, bool IsMe)
{
    public ChatPersonItem(ChatPerson person, bool isMe)
        : this(person.Id, person.Name, person.Position, person.Presence, isMe)
    {
    }

    public bool IsOffline => Presence == Neruna.Core.Chat.Presence.Offline;

    public string DisplayName => IsMe ? Name + " (ich)" : Name;

    public string PresenceLabel => Neruna.Core.Chat.Presence.Label(Presence);

    /// <summary>" · Position", or nothing without one (Nextcloud Talk knows none).</summary>
    public string PositionSuffix => string.IsNullOrWhiteSpace(Position) ? string.Empty : " · " + Position;

    public IBrush PresenceBrush => PresenceBrushes.Of(Presence);

    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>A message; consecutive ones of the same person within a few minutes share one header.</summary>
internal sealed record ChatMessageItem(long Id, string SenderName, string Text, DateTimeOffset SentAt, bool IsMine, bool ShowHeader, string? DayHeader)
{
    public string Time => SentAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public string Initials => string.Concat(SenderName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public bool HasDayHeader => DayHeader is not null;

    public static ChatMessageItem Create(ChatMessage message, ChatMessageItem? previous, string senderName, bool isMine)
    {
        var day = message.SentAt.ToLocalTime().Date;
        var newDay = previous is null || previous.SentAt.ToLocalTime().Date != day;
        var header = newDay || previous!.SenderName != senderName || message.SentAt - previous.SentAt > TimeSpan.FromMinutes(5);
        return new ChatMessageItem(message.Id, senderName, message.Text, message.SentAt, isMine, header, newDay ? DayName(day) : null);
    }

    private static string DayName(DateTime day)
    {
        var today = DateTime.Today;
        return day == today ? T("Heute")
            : day == today.AddDays(-1) ? T("Gestern")
            : day.ToString("dddd, d. MMMM", Neruna.Core.Localization.Texts.Culture);
    }
}

internal sealed record PresenceChoice(string Key, string Label, IBrush Brush);

internal static class PresenceBrushes
{
    private static readonly IBrush Available = new SolidColorBrush(Color.Parse("#13A10E"));
    private static readonly IBrush Away = new SolidColorBrush(Color.Parse("#F7B500"));
    private static readonly IBrush BeRightBack = new SolidColorBrush(Color.Parse("#F7630C"));
    private static readonly IBrush DoNotDisturb = new SolidColorBrush(Color.Parse("#D13438"));
    private static readonly IBrush Offline = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public static IBrush Of(string presence) => presence switch
    {
        Neruna.Core.Chat.Presence.Available => Available,
        Neruna.Core.Chat.Presence.Away => Away,
        Neruna.Core.Chat.Presence.BeRightBack => BeRightBack,
        Neruna.Core.Chat.Presence.DoNotDisturb => DoNotDisturb,
        _ => Offline,
    };
}
