using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Contracts.Cloud;
using Neruna.Core.Chat;
using Neruna.Core.Cloud;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Chat: rooms and private conversations on the left, the people of the room below, messages on the right. Asks the
/// server every few seconds while the chat is in front and every 20 seconds otherwise (badge, online status).
/// </summary>
internal sealed partial class ChatViewModel : ViewModelBase
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(20);

    private readonly ChatController _chat;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private bool _polling;
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

    public ObservableCollection<ChatConversationItem> Rooms { get; } = [];

    public ObservableCollection<ChatConversationItem> Directs { get; } = [];

    /// <summary>The people of the selected room (online first), or the two of a private conversation.</summary>
    public ObservableCollection<ChatPersonItem> People { get; } = [];

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotConnected), nameof(ShowNotIncluded), nameof(ShowChat), nameof(ShowPresence))]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotIncluded), nameof(ShowChat), nameof(ShowPresence))]
    public partial bool IsAvailable { get; set; }

    public bool ShowNotConnected => !IsConnected;

    public bool ShowNotIncluded => IsConnected && !IsAvailable;

    public bool ShowChat => IsConnected && IsAvailable;

    /// <summary>The status switch in the header: only with a chat to show it in (and its button not hidden).</summary>
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
            await GoOfflineAsync();
        }
    }

    private async Task GoOfflineAsync()
    {
        try
        {
            await _chat.GoOfflineAsync();
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            // Unreachable: the server shows offline anyway after 5 minutes without contact.
            _logger.LogDebug(ex, "Offline status not sent");
        }
    }

    /// <summary>How long the organisation keeps messages (shown above the conversation).</summary>
    [ObservableProperty]
    public partial string RetentionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RetentionDetails { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText))]
    public partial int TotalUnread { get; set; }

    public bool HasUnread => TotalUnread > 0;

    public string UnreadText => TotalUnread > 99 ? "99+" : TotalUnread.ToString(CultureInfo.InvariantCulture);

    /// <summary>The open conversation ("room:…" / "pm:…"); both lists on the left show it as selected.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string? SelectedKey { get; set; }

    public ChatConversationItem? Selected => Rooms.Concat(Directs).FirstOrDefault(c => c.Key == SelectedKey);

    public bool HasSelection => Selected is not null;

    public string Title => Selected?.Title ?? string.Empty;

    public string? Subtitle => Selected?.Subtitle;

    public string PeopleTitle => Selected is { IsRoom: false } ? "Unterhaltung" : "Im Raum";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Draft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceLabel), nameof(PresenceBrush))]
    public partial string Presence { get; set; } = Neruna.Core.Chat.Presence.Available;

    public string PresenceLabel => Neruna.Core.Chat.Presence.Label(Presence);

    public IBrush PresenceBrush => PresenceBrushes.Of(Presence);

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

        _started = true;
        if (!IsEnabled)
        {
            await GoOfflineAsync();
            return;
        }

        _timer.Start();
        await PollAsync();
    }

    /// <summary>Asks the server now (e.g. right after connecting the cloud).</summary>
    public Task RefreshNowAsync() => PollAsync();

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
            Error = null;
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            // Offline or server away: the chat shows what it has and tries again with the next tick.
            Error = "Keine Verbindung zum Chat-Server – neuer Versuch läuft.";
            _logger.LogDebug(ex, "Chat not refreshed");
        }
        finally
        {
            _polling = false;
        }
    }

    // Everything from the snapshot; the selection, and the messages already shown, stay.
    private void Rebuild()
    {
        var snapshot = _chat.Current;
        IsConnected = snapshot.Connected;
        IsAvailable = snapshot.Available;
        RetentionText = snapshot.RetentionDays == 1 ? "Nachrichten werden 1 Tag aufbewahrt" : $"Nachrichten werden {snapshot.RetentionDays} Tage aufbewahrt";
        RetentionDetails = RetentionText + " und danach gelöscht – auf dem Server und auf diesem Computer. "
                           + "Die Dauer legt deine Organisation im Portal fest. Der Chat ist nicht für vertrauliche Daten gedacht.";
        TotalUnread = snapshot.TotalUnread;

        var people = snapshot.People.ToDictionary(p => p.Id);
        var me = snapshot.Me;
        var selectedKey = SelectedKey;
        // A room that is gone (or none chosen yet): the first room.
        if (selectedKey is null || (ChatConversation.IsRoom(selectedKey) && snapshot.Rooms.All(r => ChatConversation.Room(r.Id) != selectedKey)))
        {
            selectedKey = snapshot.Rooms.Count > 0 ? ChatConversation.Room(snapshot.Rooms[0].Id) : null;
        }

        var rooms = snapshot.Rooms.Select(r => new ChatConversationItem(ChatConversation.Room(r.Id), r.Name, r.Description, true, null)
        {
            Unread = snapshot.UnreadOf(ChatConversation.Room(r.Id)),
            IsSelected = ChatConversation.Room(r.Id) == selectedKey,
        }).ToList();

        // Private conversations: everyone written with (newest first), plus the one just opened.
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

        var directs = directIds.Where(people.ContainsKey).Select(id =>
        {
            var person = people[id];
            return new ChatConversationItem(ChatConversation.Private(id), person.Name, Neruna.Core.Chat.Presence.Label(person.Presence), false, person.Presence)
            {
                Unread = snapshot.UnreadOf(ChatConversation.Private(id)),
                IsSelected = ChatConversation.Private(id) == selectedKey,
            };
        }).ToList();

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
        ShowConversation();
    }

    partial void OnSelectedKeyChanged(string? value) => Rebuild();

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

    private void ShowConversation()
    {
        var snapshot = _chat.Current;
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
            Messages.Add(ChatMessageItem.Create(message, previous, people.GetValueOrDefault(message.SenderId)?.Name ?? "(ehemalig)", message.SenderId == me));
        }

        if (IsActive)
        {
            _ = MarkShownReadAsync();
        }
    }

    private async Task MarkShownReadAsync()
    {
        if (SelectedKey is not { } key || _chat.Current.UnreadOf(key) == 0)
        {
            return;
        }

        try
        {
            await _chat.MarkReadAsync(key);
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Read marker not sent");
        }
    }

    [RelayCommand]
    private void OpenPrivate(ChatPersonItem? person)
    {
        if (person is null || person.IsMe)
        {
            return;
        }

        // Not written with yet: the list shows it at the top until there are messages.
        SelectedKey = ChatConversation.Private(person.Id);
        _ = MarkShownReadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Draft.Trim();
        if (SelectedKey is not { } key || text.Length == 0)
        {
            return;
        }

        Draft = string.Empty;
        try
        {
            await _chat.SendAsync(key, text);
            Error = null;
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Chat message not sent");
            Draft = text;
            Error = "Nicht gesendet: " + ex.Message;
        }
    }

    private bool CanSend() => SelectedKey is not null && !string.IsNullOrWhiteSpace(Draft) && Draft.Length <= 4000;

    [RelayCommand]
    private async Task SetPresenceAsync(string? presence)
    {
        Presence = Neruna.Core.Chat.Presence.Normalize(presence);
        try
        {
            await _chat.SetPresenceAsync(Presence);
        }
        catch (Exception ex) when (ex is CloudException or HttpRequestException or TaskCanceledException)
        {
            // Remembered here; sent with the next successful contact.
            _logger.LogWarning(ex, "Presence not sent");
        }
    }

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
    public ChatPersonItem(CloudChatPerson person, bool isMe)
        : this(person.Id, person.Name, person.Position, person.Presence, isMe)
    {
    }

    public bool IsOffline => Presence == Neruna.Core.Chat.Presence.Offline;

    public string DisplayName => IsMe ? Name + " (ich)" : Name;

    public string PresenceLabel => Neruna.Core.Chat.Presence.Label(Presence);

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
        return day == today ? "Heute"
            : day == today.AddDays(-1) ? "Gestern"
            : day.ToString("dddd, d. MMMM", CultureInfo.GetCultureInfo("de-CH"));
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
