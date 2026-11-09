using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Contracts.Cloud;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>One answer in the results table: the symbol and which answer it is (for its colour).</summary>
internal sealed record PollAnswerCell(string Symbol, string Answer, string Tip)
{
    public bool IsYes => Answer == "yes";
    public bool IsIfNeeded => Answer == "ifneeded";
    public bool IsMaybe => Answer == "maybe";
    public bool IsNo => Answer == "no";
}

/// <summary>
/// A proposed date as a column of the table: its head (month, day, weekday, time), how many said yes (and if needed /
/// maybe, when offered or given), and whether it suits best.
/// </summary>
internal sealed record PollOptionResult(CloudPollOption Option, string When, int Yes, int IfNeeded, int Maybe, int No, bool IsBest)
{
    private DateTime From => Option.AllDay ? Option.Start.UtcDateTime : Option.Start.LocalDateTime;

    private DateTime? To => Option.End is not { } end ? null : Option.AllDay ? end.UtcDateTime : end.LocalDateTime;

    // A whole day ends at the next midnight: several days only when it ends later than that.
    private bool SeveralDays => To is { } to && (Option.AllDay ? to.Date > From.Date.AddDays(1) : to.Date > From.Date);

    public string Month => From.ToString("MMM", System.Globalization.CultureInfo.CurrentCulture).ToUpperInvariant();

    public string Day => Option.AllDay && SeveralDays && To is { } to
        ? From.Day + ".–" + (to.TimeOfDay == TimeSpan.Zero ? to.AddDays(-1) : to).Day + "."
        : From.Day.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string Weekday => From.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture);

    public string Time => Option.AllDay ? T("ganztägig")
        : From.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)
          + (To is { } to ? "–" + to.ToString(SeveralDays ? "ddd HH:mm" : "HH:mm", System.Globalization.CultureInfo.CurrentCulture) : string.Empty);

    public string Total => "✓ " + Yes;

    public string More => string.Join(" · ", new[] { (IfNeeded, "(✓)"), (Maybe, "?") }.Where(x => x.Item1 > 0).Select(x => x.Item2 + " " + x.Item1));

    public bool HasMore => IfNeeded + Maybe > 0;

    public string Counts => F("{0} Ja · {1} wenn nötig · {2} vielleicht · {3} nein", Yes, IfNeeded, Maybe, No);
}

/// <summary>Someone in the poll with their answer per option (in the order of the options).</summary>
internal sealed record PollParticipantResult(CloudPollParticipant Participant, string Name, string State, IReadOnlyList<PollAnswerCell> Cells)
{
    public Uri? PersonalLink => Participant.Url;

    public bool HasPersonalLink => Participant.Url is not null;

    public bool HasComment => !string.IsNullOrWhiteSpace(Participant.Comment);
}

/// <summary>A poll in the list on the left, with the organisation it runs with (named when there are several).</summary>
internal sealed record PollListItem(CloudPoll Poll, CloudConnection Connection, bool ShowOrganization = false)
{
    public string Title => Poll.Title;

    public string Info
    {
        get
        {
            var answered = Poll.Participants.Count(p => p.AnsweredAt is not null);
            var info = F("{0} Vorschläge · {1} Antworten", Poll.Options.Count, answered);
            return ShowOrganization ? Connection.OrganizationName + " · " + info : info;
        }
    }
}

/// <summary>
/// "Terminumfragen" (from the calendar): the person's polls with what everyone answered; plan a new one, change one,
/// copy its link, write the mail with it, and fix the date – then the event opens with everyone as attendees, and the
/// poll is deleted in Neruna Cloud once the event is saved. With several organisations, the polls of all of them.
/// </summary>
internal sealed partial class PollsViewModel(SchedulingPolls polls) : ViewModelBase
{
    public event EventHandler? Closed;

    /// <summary>"Termin festlegen": the shell closes this and opens the event (then deletes the poll when saved).</summary>
    public event EventHandler<(CloudPoll Poll, CloudPollOption Option, CloudConnection Connection)>? FixRequested;

    /// <summary>"Mail schreiben": a new mail to the invitees with the link.</summary>
    public event EventHandler<(CloudPoll Poll, string? Password)>? MailRequested;

    /// <summary>A link to copy (the view puts it on the clipboard).</summary>
    public event EventHandler<string>? CopyRequested;

    public ObservableCollection<PollListItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(Results), nameof(Participants), nameof(LinkText), nameof(HasLink), nameof(Details), nameof(HasParticipants),
        nameof(Comments), nameof(HasComments), nameof(Legend))]
    [NotifyCanExecuteChangedFor(nameof(EditCommand), nameof(DeleteCommand), nameof(CopyLinkCommand), nameof(WriteMailCommand))]
    public partial PollListItem? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor))]
    public partial PollEditorViewModel? Editor { get; set; }

    public bool HasEditor => Editor is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is not null;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoaded { get; set; }

    public bool IsEmpty => IsLoaded && Items.Count == 0 && Message is null;

    /// <summary>The password set in this session per poll (the server keeps only its hash), for the mail.</summary>
    private readonly Dictionary<string, string> _passwords = [];

    private IReadOnlyList<CloudConnection> _connections = [];

    public string? LinkText => Selected?.Poll.Url?.AbsoluteUri;

    public bool HasLink => LinkText is not null;

    /// <summary>"Ort · Antworten bis … · Passwortschutz · Sichtbarkeit".</summary>
    public string Details
    {
        get
        {
            if (Selected?.Poll is not { } poll)
            {
                return string.Empty;
            }

            var parts = new List<string?>
            {
                poll.Location,
                poll.Deadline is { } d ? F("Antworten bis {0:d}", d.LocalDateTime) : null,
                poll.HasPassword ? T("mit Passwort") : null,
                poll.OpenLink ? null : T("nur eigene Links"),
                poll.Visibility switch
                {
                    "anonymous" => T("andere sehen nur die Anzahl"),
                    "own" => T("andere Antworten verborgen"),
                    _ => T("alle sehen alle Antworten"),
                },
                F("gelöscht am {0:d}, falls nicht festgelegt", poll.ExpiresAt.LocalDateTime),
            };
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public IReadOnlyList<PollOptionResult> Results
    {
        get
        {
            if (Selected?.Poll is not { } poll)
            {
                return [];
            }

            var best = SchedulingPolls.Best(poll).Select(o => o.Id).ToHashSet();
            return poll.Options.Select(o =>
            {
                var counts = SchedulingPolls.Count(poll, o);
                return new PollOptionResult(o, SchedulingPolls.When(o), counts["yes"], counts["ifneeded"], counts["maybe"], counts["no"], best.Contains(o.Id));
            }).ToList();
        }
    }

    public IReadOnlyList<PollParticipantResult> Participants
    {
        get
        {
            if (Selected?.Poll is not { } poll)
            {
                return [];
            }

            return poll.Participants.Select(p => new PollParticipantResult(
                p,
                string.IsNullOrWhiteSpace(p.Name) ? p.Email ?? T("Ohne Namen") : p.Name,
                p.AnsweredAt is null ? T("noch keine Antwort") : p.Invited ? T("eingeladen") : T("über den Link"),
                poll.Options.Select(o => p.Answers.TryGetValue(o.Id, out var a)
                    ? new PollAnswerCell(Symbol(a), a, SchedulingPolls.AnswerText(a))
                    : new PollAnswerCell("–", string.Empty, string.Empty)).ToList())).ToList();
        }
    }

    /// <summary>"✓ Ja · ? Vielleicht · ✗ Nein · – keine Angabe": the answers this poll offers.</summary>
    public string Legend => Selected?.Poll is not { } poll ? string.Empty
        : string.Join(" · ", SchedulingPolls.Choices(poll).Select(a => Symbol(a) + " " + SchedulingPolls.AnswerText(a))) + " · – " + T("keine Angabe");

    public bool HasParticipants => Selected?.Poll.Participants.Count > 0;

    /// <summary>What participants wrote in "Bemerkung" on the page (only the planner sees it).</summary>
    public IReadOnlyList<PollParticipantResult> Comments => Participants.Where(p => p.HasComment).ToList();

    public bool HasComments => Selected?.Poll.Participants.Any(p => !string.IsNullOrWhiteSpace(p.Comment)) == true;

    private static string Symbol(string answer) => answer switch
    {
        "yes" => "✓",
        "ifneeded" => "(✓)",
        "maybe" => "?",
        "no" => "✗",
        _ => "–",
    };

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Message = null;
        var selectedId = Selected?.Poll.Id;
        try
        {
            _connections = await polls.ConnectionsAsync();
            if (_connections.Count == 0)
            {
                Message = T("Terminumfragen gibt es mit einer Verbindung zu Neruna Cloud (Einstellungen → Cloud).");
                Items.Clear();
                return;
            }

            // Each organisation on its own: one that is not reachable (or does not know polls yet) keeps the others.
            var found = new List<PollListItem>();
            var usable = new List<CloudConnection>();
            var errors = new List<string>();
            foreach (var connection in _connections)
            {
                try
                {
                    if (await polls.ListAsync(connection) is { } list)
                    {
                        usable.Add(connection);
                        found.AddRange(list.Select(p => new PollListItem(p, connection, _connections.Count > 1)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add(_connections.Count > 1 ? connection.OrganizationName + ": " + ex.Message : ex.Message);
                }
            }

            _connections = usable.Count > 0 ? usable : _connections;
            Items.Clear();
            foreach (var item in found)
            {
                Items.Add(item);
            }

            if (errors.Count > 0)
            {
                Message = T("Die Terminumfragen konnten nicht geladen werden: ") + string.Join("; ", errors);
            }
            else if (usable.Count == 0)
            {
                Message = T("Der Server dieser Organisation kennt noch keine Terminumfragen.");
            }

            Selected = Items.FirstOrDefault(i => i.Poll.Id == selectedId) ?? Items.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = T("Die Terminumfragen konnten nicht geladen werden: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsLoaded = true;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private void New() => OpenEditor(new PollEditorViewModel(polls, _connections, null));

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Edit()
    {
        if (Selected is { } item)
        {
            OpenEditor(new PollEditorViewModel(polls, [item.Connection], item.Poll));
        }
    }

    private void OpenEditor(PollEditorViewModel editor)
    {
        editor.Finished += async (_, saved) =>
        {
            Editor = null;
            if (saved && editor.Saved is { } poll)
            {
                if (editor.SavedPassword is { } password)
                {
                    _passwords[poll.Id] = password;
                }

                await RefreshAsync();
                Selected = Items.FirstOrDefault(i => i.Poll.Id == poll.Id) ?? Selected;
            }
        };
        Editor = editor;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void CopyLink()
    {
        if (LinkText is { } link)
        {
            CopyRequested?.Invoke(this, link);
            Message = null;
        }
    }

    [RelayCommand]
    private void CopyPersonalLink(PollParticipantResult participant)
    {
        if (participant.PersonalLink is { } link)
        {
            CopyRequested?.Invoke(this, link.AbsoluteUri);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void WriteMail()
    {
        if (Selected?.Poll is { } poll)
        {
            MailRequested?.Invoke(this, (poll, _passwords.GetValueOrDefault(poll.Id)));
        }
    }

    [RelayCommand]
    private void Fix(PollOptionResult result)
    {
        if (Selected is { } item)
        {
            FixRequested?.Invoke(this, (item.Poll, result.Option, item.Connection));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is not { Poll: var poll } item
            || !await Views.ChoiceDialog.ShowInFrontAsync(F("«{0}» löschen", poll.Title),
                T("Die Umfrage und alle Antworten werden in Neruna Cloud gelöscht. Der Link funktioniert dann nicht mehr."),
                (T("Löschen"), true, false), (T("Abbrechen"), false, true)))
        {
            return;
        }

        try
        {
            await polls.DeleteAsync(item.Connection, poll.Id);
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = T("Löschen fehlgeschlagen: ") + ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);
}
