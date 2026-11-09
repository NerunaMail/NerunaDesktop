using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Cloud;
using Neruna.Core.Calendar;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>One proposed date in the poll editor: a day, and a time span unless it is the whole day.</summary>
internal sealed partial class PollOptionRow : ObservableObject
{
    public PollOptionRow(string? id, DateTime day, TimeSpan? start, TimeSpan? end, bool allDay)
    {
        Id = id;
        Day = day;
        Start = start;
        End = end;
        IsAllDay = allDay;
    }

    /// <summary>The option's id on the server (kept with its answers); null for a new one.</summary>
    public string? Id { get; }

    [ObservableProperty]
    public partial DateTime? Day { get; set; }

    [ObservableProperty]
    public partial TimeSpan? Start { get; set; }

    [ObservableProperty]
    public partial TimeSpan? End { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTime))]
    public partial bool IsAllDay { get; set; }

    public bool HasTime => !IsAllDay;

    // A later start moves the end along (one hour after by default).
    partial void OnStartChanged(TimeSpan? oldValue, TimeSpan? newValue)
    {
        if (newValue is { } start && (End is null || End <= start || oldValue is null))
        {
            End = start + (oldValue is { } old && End is { } end && end > old ? end - old : TimeSpan.FromHours(1));
        }
    }

    public PollOptionRequest? ToRequest()
    {
        if (Day is not { } day)
        {
            return null;
        }

        if (IsAllDay)
        {
            return new PollOptionRequest(Id, new DateTimeOffset(day.Date, TimeSpan.Zero), null, true);
        }

        var start = day.Date + (Start ?? TimeSpan.FromHours(9));
        DateTimeOffset? end = End is { } e && e > (Start ?? TimeSpan.Zero) ? new DateTimeOffset(day.Date + e) : null;
        return new PollOptionRequest(Id, new DateTimeOffset(start), end, false);
    }
}

/// <summary>
/// Plans a scheduling poll (new or changed): title, place, the proposed dates, who is invited and how answers are
/// shown. From a mail being written the recipients are the invitees and the common link goes into the mail. Connected
/// to several organisations, a new poll asks which one it runs with; a poll keeps its organisation.
/// </summary>
internal sealed partial class PollEditorViewModel : ViewModelBase
{
    private readonly SchedulingPolls _polls;
    private readonly CloudPoll? _existing;

    /// <param name="connections">The organisations to choose from (for a new poll); the first is preselected.</param>
    /// <param name="forMail">Made while writing a mail: everyone gets the same link (no hidden poll).</param>
    public PollEditorViewModel(SchedulingPolls polls, IReadOnlyList<CloudConnection> connections, CloudPoll? existing, string? title = null,
        IReadOnlyList<EventAttendee>? invitees = null, bool forMail = false)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _polls = polls;
        _existing = existing;
        IsForMail = forMail;
        Connections = connections;
        Connection = connections.FirstOrDefault();
        if (existing is not null)
        {
            Title = existing.Title;
            Location = existing.Location ?? string.Empty;
            Description = existing.Description ?? string.Empty;
            VisibilityIndex = Math.Max(0, Visibilities.IndexOf(existing.Visibility));
            HasPassword = existing.HasPassword;
            OpenLink = existing.OpenLink;
            var choices = SchedulingPolls.Choices(existing);
            OffersIfNeeded = choices.Contains("ifneeded");
            OffersMaybe = choices.Contains("maybe");
            OffersNo = choices.Contains("no");
            Deadline = existing.Deadline?.LocalDateTime.Date;
            foreach (var option in existing.Options)
            {
                Options.Add(option.AllDay
                    ? new PollOptionRow(option.Id, option.Start.UtcDateTime.Date, null, null, true)
                    : new PollOptionRow(option.Id, option.Start.LocalDateTime.Date, option.Start.LocalDateTime.TimeOfDay, option.End?.LocalDateTime.TimeOfDay, false));
            }

            _invitees = existing.Participants.Where(p => p.Invited).ToList();
            InviteesText = string.Join(", ", _invitees.Select(p => Address(p.Name, p.Email)).Where(a => a.Length > 0));
        }
        else
        {
            Title = title?.Trim() ?? string.Empty;
            InviteesText = string.Join(", ", (invitees ?? []).Select(a => Address(a.Name, a.Email)));
            var first = DateTime.Today.AddDays(1);
            Options.Add(new PollOptionRow(null, first, TimeSpan.FromHours(9), TimeSpan.FromHours(10), false));
            Options.Add(new PollOptionRow(null, first.AddDays(1), TimeSpan.FromHours(9), TimeSpan.FromHours(10), false));
        }
    }

    private static readonly List<string> Visibilities = ["all", "anonymous", "own"];

    private readonly IReadOnlyList<CloudPollParticipant> _invitees = [];

    public event EventHandler<bool>? Finished;

    public string Heading => _existing is null ? T("Neue Terminumfrage") : T("Terminumfrage bearbeiten");

    public bool IsForMail { get; }

    public bool CanChooseOpenLink => !IsForMail;

    /// <summary>The organisations this computer is connected to.</summary>
    public IReadOnlyList<CloudConnection> Connections { get; }

    /// <summary>The organisation whose Neruna Cloud runs the poll.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial CloudConnection? Connection { get; set; }

    /// <summary>Several organisations and a new poll: which one it runs with.</summary>
    public bool CanChooseConnection => _existing is null && Connections.Count > 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    public ObservableCollection<PollOptionRow> Options { get; } = [];

    /// <summary>Recipients as in a mail ("Anna Muster &lt;anna@…&gt;, lea@…").</summary>
    [ObservableProperty]
    public partial string InviteesText { get; set; } = string.Empty;

    public IReadOnlyList<string> VisibilityChoices { get; } =
    [
        T("Alle sehen alle Antworten mit Namen"),
        T("Alle sehen nur die Anzahl der Antworten"),
        T("Jede Person sieht nur ihre eigene Antwort"),
    ];

    [ObservableProperty]
    public partial int VisibilityIndex { get; set; }

    /// <summary>Answers offered besides "Ja" (always): by default "Nein" and "Vielleicht".</summary>
    [ObservableProperty]
    public partial bool OffersNo { get; set; } = true;

    [ObservableProperty]
    public partial bool OffersMaybe { get; set; } = true;

    [ObservableProperty]
    public partial bool OffersIfNeeded { get; set; }

    /// <summary>The poll asks for a password on the common link (personal links need none).</summary>
    [ObservableProperty]
    public partial bool HasPassword { get; set; }

    /// <summary>A new password; empty keeps the one set before.</summary>
    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    public bool HadPassword => _existing?.HasPassword == true;

    /// <summary>Everyone with the link may answer; off: only the invitees with their personal links.</summary>
    [ObservableProperty]
    public partial bool OpenLink { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadline))]
    public partial DateTime? Deadline { get; set; }

    public bool HasDeadline => Deadline is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The poll as saved (with its links); set when <see cref="Finished"/> reports true.</summary>
    public CloudPoll? Saved { get; private set; }

    /// <summary>The password just set (to put into the mail); null when unchanged or none.</summary>
    public string? SavedPassword { get; private set; }

    [RelayCommand]
    private void AddOption()
    {
        var last = Options.LastOrDefault();
        var day = (last?.Day ?? DateTime.Today).AddDays(1);
        Options.Add(new PollOptionRow(null, day, last?.Start ?? TimeSpan.FromHours(9), last?.End ?? TimeSpan.FromHours(10), last?.IsAllDay ?? false));
    }

    /// <summary>Another time on the same day.</summary>
    [RelayCommand]
    private void AddTime(PollOptionRow row)
    {
        var index = Options.IndexOf(row);
        var length = row.End is { } e && row.Start is { } s && e > s ? e - s : TimeSpan.FromHours(1);
        var start = (row.End ?? row.Start ?? TimeSpan.FromHours(9)) + TimeSpan.FromHours(1);
        if (start + length >= TimeSpan.FromDays(1))
        {
            start = TimeSpan.FromHours(14);
        }

        Options.Insert(index + 1, new PollOptionRow(null, row.Day ?? DateTime.Today, start, start + length, false));
    }

    [RelayCommand]
    private void RemoveOption(PollOptionRow row)
    {
        if (Options.Count > 1)
        {
            Options.Remove(row);
        }
    }

    [RelayCommand]
    private void ClearDeadline() => Deadline = null;

    [RelayCommand]
    private void SetDeadline() => Deadline = (Options.Select(o => o.Day).Where(d => d is not null).Min() ?? DateTime.Today.AddDays(7)).AddDays(-1);

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private bool CanSave() => !IsBusy && Connection is not null && !string.IsNullOrWhiteSpace(Title);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        if (Connection is not { } connection)
        {
            return;
        }

        var options = Options.Select(o => o.ToRequest()).OfType<PollOptionRequest>().ToList();
        if (options.Count == 0)
        {
            Error = T("Bitte mindestens einen Termin vorschlagen.");
            return;
        }

        if (HasPassword && string.IsNullOrEmpty(Password) && !HadPassword)
        {
            Error = T("Bitte ein Passwort eingeben oder den Passwortschutz ausschalten.");
            return;
        }

        // Invitees: those already there keep their id (and personal link); matched by address.
        var invitees = new List<PollInviteeRequest>();
        foreach (var person in EventAttendee.FromRecipients(InviteesText))
        {
            var known = _invitees.FirstOrDefault(p => string.Equals(p.Email, person.Email, StringComparison.OrdinalIgnoreCase));
            invitees.Add(new PollInviteeRequest(known?.Id, person.Name ?? known?.Name, person.Email));
        }

        var password = !HasPassword ? (HadPassword ? string.Empty : null) : string.IsNullOrEmpty(Password) ? null : Password;
        var request = new PollRequest(Title.Trim(), NullIfEmpty(Location), NullIfEmpty(Description), SchedulingPolls.LocalTimeZoneId(),
            Visibilities[Math.Clamp(VisibilityIndex, 0, Visibilities.Count - 1)], password, IsForMail || OpenLink,
            Deadline is { } deadline ? new DateTimeOffset(deadline.Date.AddDays(1).AddSeconds(-1)) : null, options, invitees, ChosenAnswers());

        IsBusy = true;
        try
        {
            Saved = await _polls.SaveAsync(connection, _existing?.Id, request);
            SavedPassword = string.IsNullOrEmpty(password) ? null : password;
            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Die Terminumfrage konnte nicht gespeichert werden: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private List<string> ChosenAnswers() =>
        SchedulingPolls.Answers.Where(a => a switch
        {
            "ifneeded" => OffersIfNeeded,
            "maybe" => OffersMaybe,
            "no" => OffersNo,
            _ => true,
        }).ToList();

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Address(string? name, string? email) =>
        string.IsNullOrWhiteSpace(email) ? name ?? string.Empty
        : string.IsNullOrWhiteSpace(name) ? email
        : new MimeKit.MailboxAddress(name, email).ToString();
}
