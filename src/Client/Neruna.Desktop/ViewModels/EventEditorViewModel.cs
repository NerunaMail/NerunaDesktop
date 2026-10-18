using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Core.Meetings;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

internal sealed record RecurrenceOption(RecurrenceKind Kind, string Label)
{
    public static IReadOnlyList<RecurrenceOption> All { get; } =
    [
        new(RecurrenceKind.None, T("Keine")),
        new(RecurrenceKind.Daily, T("Täglich")),
        new(RecurrenceKind.Weekly, T("Wöchentlich")),
        new(RecurrenceKind.Monthly, T("Monatlich")),
        new(RecurrenceKind.Yearly, T("Jährlich")),
        new(RecurrenceKind.Custom, T("Benutzerdefiniert (unverändert)")),
    ];
}

/// <summary>"Anzeigen als": busy (red), free (green), tentative (orange), away (purple) – the colours of the planning view.</summary>
internal sealed record ShowAsOption(ShowAs Value, string Label, Avalonia.Media.IBrush Brush)
{
    public static IReadOnlyList<ShowAsOption> All { get; } =
    [
        new(ShowAs.Busy, T("Beschäftigt"), ShowAsColors.Brush(ShowAs.Busy)),
        new(ShowAs.Free, T("Frei"), ShowAsColors.Brush(ShowAs.Free)),
        new(ShowAs.Tentative, T("Mit Vorbehalt"), ShowAsColors.Brush(ShowAs.Tentative)),
        new(ShowAs.OutOfOffice, T("Abwesend"), ShowAsColors.Brush(ShowAs.OutOfOffice)),
    ];

    public static ShowAsOption Of(ShowAs value) => All.First(o => o.Value == value);
}

/// <summary>One colour per show-as, the same in the editor and the planning view.</summary>
internal static class ShowAsColors
{
    public static Avalonia.Media.IBrush Brush(ShowAs value) => Avalonia.Media.Brush.Parse(value switch
    {
        ShowAs.Free => "#2E9E5B",
        ShowAs.Tentative => "#E08A1E",
        ShowAs.OutOfOffice => "#7A4FB5",
        _ => "#D13438",
    });
}

internal sealed record ReminderOption(int? Minutes, string Label)
{
    public static IReadOnlyList<ReminderOption> Standard { get; } =
    [
        new(null, T("Keine")),
        new(0, T("Bei Beginn")),
        new(5, T("5 Minuten vorher")),
        new(10, T("10 Minuten vorher")),
        new(15, T("15 Minuten vorher")),
        new(30, T("30 Minuten vorher")),
        new(60, T("1 Stunde vorher")),
        new(120, T("2 Stunden vorher")),
        new(1440, T("1 Tag vorher")),
        new(10080, T("1 Woche vorher")),
    ];

    /// <summary>The standard choices, plus the event's own value if it is none of them (set elsewhere).</summary>
    public static IReadOnlyList<ReminderOption> For(int? minutes) =>
        Standard.Any(o => o.Minutes == minutes) ? Standard : [.. Standard, new(minutes, F("{0} Minuten vorher", minutes))];
}

/// <summary>One invited person with their answer, shown below the attendee field.</summary>
internal sealed record AttendeeStatus(string Text, string State);

/// <summary>Create/edit/delete one event. Recurring events are edited as a whole series.</summary>
internal sealed partial class EventEditorViewModel : ViewModelBase
{
    private readonly CalendarController _calendar;
    private readonly InvitationService _invitations;
    private readonly (CalendarInfo Calendar, string RemoteId)? _existing;
    private readonly Func<CalendarInfo, string?> _ownAddress;
    private readonly IReadOnlyList<EventAttendee> _attendees;
    private readonly (DateTime Start, DateTime End)? _originalTimes;

    public EventEditorViewModel(
        CalendarController calendar,
        InvitationService invitations,
        IReadOnlyList<CalendarInfo> writableCalendars,
        EventDraft draft,
        (CalendarInfo Calendar, string RemoteId)? existing,
        Func<CalendarInfo, string?> ownAddress)
    {
        _calendar = calendar;
        _invitations = invitations;
        _existing = existing;
        _ownAddress = ownAddress;
        _attendees = draft.Attendees ?? [];
        _originalTimes = existing is null ? null : (draft.Start, draft.End);
        Organizer = draft.Organizer;

        IsReadOnly = existing?.Calendar.IsReadOnly ?? false;
        Calendars = IsReadOnly ? [existing!.Value.Calendar] : writableCalendars;
        SelectedCalendar = existing is { } e
            ? Calendars.FirstOrDefault(c => c.ConnectionId == e.Calendar.ConnectionId && c.RemoteId == e.Calendar.RemoteId) ?? Calendars.FirstOrDefault()
            : Calendars.FirstOrDefault();

        Title = draft.Summary;
        Location = draft.Location ?? string.Empty;
        Description = draft.Description ?? string.Empty;
        IsAllDay = draft.IsAllDay;
        StartDate = draft.Start.Date;
        StartTime = draft.Start.TimeOfDay;

        // All-day ends are exclusive in iCalendar; the form shows the last day.
        var end = draft.IsAllDay ? draft.End.AddDays(-1) : draft.End;
        EndDate = (end < draft.Start ? draft.Start : end).Date;
        EndTime = draft.End.TimeOfDay;
        Recurrence = RecurrenceOption.All.First(o => o.Kind == draft.Recurrence);
        IsSeries = draft.Recurrence != RecurrenceKind.None;
        ReminderOptions = ReminderOption.For(draft.ReminderMinutes);
        Reminder = ReminderOptions.First(o => o.Minutes == draft.ReminderMinutes);
        ShowAs = ShowAsOption.Of(draft.ShowAs);

        var me = existing is { } own ? ownAddress(own.Calendar) : null;
        AttendeesText = string.Join(", ", _attendees.Where(a => !string.Equals(a.Email, me, StringComparison.OrdinalIgnoreCase))
            .Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Email : $"{a.Name} <{a.Email}>"));
        AttendeeStatuses = _attendees.Where(a => !string.Equals(a.Email, Organizer, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AttendeeStatus(string.IsNullOrWhiteSpace(a.Name) ? a.Email : a.Name, a.Status switch
            {
                Participation.Accepted => T("zugesagt"),
                Participation.Tentative => T("mit Vorbehalt"),
                Participation.Declined => T("abgesagt"),
                _ => T("keine Antwort"),
            }))
            .ToList();
        IsOrganizer = Organizer is null || string.Equals(Organizer, me, StringComparison.OrdinalIgnoreCase);
        Meeting = draft.Meeting;
    }

    /// <summary>Online meetings (Einstellungen → Besprechungen); null in tests without them.</summary>
    public MeetingController? Meetings
    {
        get;
        init
        {
            field = value;
            _ = LoadMeetingChoicesAsync();
        }
    }

    /// <summary>The event's online meeting (Teams, Zoom, Jitsi …); its join text is in <see cref="Description"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeeting), nameof(ShowMeetingRow), nameof(NoMeetingServices), nameof(CanAddMeeting), nameof(MeetingTitle), nameof(MeetingUrl), nameof(IsMeetingPending), nameof(HasMeetingUrl))]
    public partial OnlineMeeting? Meeting { get; set; }

    public bool HasMeeting => Meeting is not null;

    /// <summary>The row shows when there is something to choose or a meeting to show (not for a read-only event without one).</summary>
    public bool ShowMeetingRow => HasMeeting || (CanEdit && Meetings is not null);

    public string MeetingTitle => Meeting is { } m ? F("{0}-Besprechung", m.Name) : string.Empty;

    public string? MeetingUrl => Meeting?.JoinUrl;

    public bool HasMeetingUrl => Meeting?.JoinUrl is not null;

    /// <summary>Teams through Exchange: the link comes with saving.</summary>
    public bool IsMeetingPending => Meeting is { IsPending: true };

    /// <summary>The services for this calendar: its own Teams (Microsoft 365) first, then the set-up ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddMeeting), nameof(NoMeetingServices))]
    public partial IReadOnlyList<MeetingChoice> MeetingChoices { get; set; } = [];

    [ObservableProperty]
    public partial MeetingChoice? SelectedMeetingChoice { get; set; }

    public bool CanAddMeeting => CanEdit && Meeting is null && MeetingChoices.Count > 0;

    /// <summary>Nothing set up yet: the hint where to do it.</summary>
    public bool NoMeetingServices => CanEdit && Meeting is null && MeetingChoices.Count == 0 && Meetings is not null && _choicesLoaded;

    private bool _choicesLoaded;

    [ObservableProperty]
    public partial bool IsCreatingMeeting { get; set; }

    private int _choicesVersion;

    private async Task LoadMeetingChoicesAsync()
    {
        if (Meetings is not { } meetings)
        {
            return;
        }

        var version = ++_choicesVersion;
        var calendar = SelectedCalendar;
        try
        {
            var choices = await meetings.GetChoicesAsync(calendar);
            if (version != _choicesVersion)
            {
                return;
            }

            MeetingChoices = choices;
            SelectedMeetingChoice = choices.FirstOrDefault();

            // A Teams meeting that Exchange was to create cannot come from another calendar.
            if (Meeting is { IsPending: true } pending && !choices.Any(c => c.Service is null && c.Kind == pending.Service))
            {
                Meeting = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MeetingChoices = [];
        }

        _choicesLoaded = true;
        OnPropertyChanged(nameof(NoMeetingServices));
    }

    [RelayCommand]
    private async Task AddMeetingAsync()
    {
        if (Meetings is not { } meetings || SelectedMeetingChoice is not { } choice || StartDate is null)
        {
            return;
        }

        Error = null;
        IsCreatingMeeting = true;
        try
        {
            var start = StartDate.Value.Date + (IsAllDay ? TimeSpan.Zero : StartTime ?? TimeSpan.FromHours(9));
            var end = IsAllDay ? (EndDate ?? StartDate).Value.Date.AddDays(1) : (EndDate ?? StartDate).Value.Date + (EndTime ?? TimeSpan.FromHours(10));
            var meeting = await meetings.CreateAsync(choice, new MeetingRequest(string.IsNullOrWhiteSpace(Title) ? T("Besprechung") : Title.Trim(), start, end > start ? end : start.AddHours(1)));
            if (!meeting.IsPending)
            {
                Description = meeting.AddTo(Description);
                if (string.IsNullOrWhiteSpace(Location))
                {
                    Location = F("{0}-Besprechung", meeting.Name);
                }
            }

            Meeting = meeting;
        }
        catch (MeetingException ex)
        {
            Error = ex.SignInRequired
                ? T("Die Anmeldung beim Besprechungsdienst ist abgelaufen. Bitte unter Einstellungen → Besprechungen erneut verbinden.")
                : ex.Message;
        }
        catch (HttpRequestException ex)
        {
            Error = T("Die Besprechung konnte nicht erstellt werden: ") + ex.Message;
        }
        finally
        {
            IsCreatingMeeting = false;
        }
    }

    /// <summary>Takes the meeting out of the event (its join text and, if Neruna set it, the place).</summary>
    [RelayCommand]
    private void RemoveMeeting()
    {
        if (Meeting is not { } meeting)
        {
            return;
        }

        if (meeting.JoinUrl is { } url)
        {
            Description = OnlineMeeting.RemoveFrom(Description, url);
        }

        if (Location.Trim() == F("{0}-Besprechung", meeting.Name))
        {
            Location = string.Empty;
        }

        Meeting = null;
    }

    /// <summary>Contacts suggested while typing the attendees (none in tests).</summary>
    public Infrastructure.RecipientDirectory? Recipients { get; init; }

    /// <summary>The event as saved (calendar, its id there, what was saved) – for the mail it was made from.</summary>
    public (CalendarInfo Calendar, string RemoteId, EventDraft Draft)? Saved { get; private set; }

    /// <summary>"Gespeichert – Einladung an 2 Personen gesendet" for the status bar.</summary>
    public string? ResultMessage { get; private set; }

    public IReadOnlyList<ReminderOption> ReminderOptions { get; }

    [ObservableProperty]
    public partial ReminderOption Reminder { get; set; }

    [ObservableProperty]
    public partial ShowAsOption ShowAs { get; set; }

    /// <summary>Invited people, as typed: "Lea Keller &lt;lea@example.com&gt;, marco@example.com".</summary>
    [ObservableProperty]
    public partial string AttendeesText { get; set; }

    public IReadOnlyList<AttendeeStatus> AttendeeStatuses { get; }

    public bool HasAttendeeStatuses => AttendeeStatuses.Count > 0;

    public string? Organizer { get; }

    /// <summary>Someone else organizes this meeting: the attendee list is theirs, no invitations are sent.</summary>
    public bool IsOrganizer { get; }

    public bool CanEditAttendees => CanEdit && IsOrganizer;

    /// <summary>The second click on "Löschen" says whether mail goes out.</summary>
    public string DeleteConfirmText =>
        !IsOrganizer ? T("Löschen und absagen?")
        : _attendees.Any(a => !string.Equals(a.Email, Organizer, StringComparison.OrdinalIgnoreCase)) ? T("Löschen und Absage an alle?")
        : T("Wirklich löschen?");

    public string OrganizerText => F("Organisiert von {0} – Ihre Antwort geben Sie in der Einladungsmail.", Organizer);

    /// <summary>Raised with true when something was saved or deleted.</summary>
    public event EventHandler<bool>? Finished;

    public IReadOnlyList<CalendarInfo> Calendars { get; }

    public static IReadOnlyList<RecurrenceOption> RecurrenceOptions => RecurrenceOption.All;

    public bool IsNew => _existing is null;

    public bool IsExisting => !IsNew;

    public string Heading => IsReadOnly ? T("Termin (schreibgeschützt)") : IsNew ? T("Neuer Termin") : T("Termin bearbeiten");

    public bool IsReadOnly { get; }

    public bool CanEdit => !IsReadOnly;

    public bool IsSeries { get; }

    public bool NoCalendar => Calendars.Count == 0;

    [ObservableProperty]
    public partial CalendarInfo? SelectedCalendar { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Location { get; set; }

    [ObservableProperty]
    public partial string Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTimes))]
    public partial bool IsAllDay { get; set; }

    public bool ShowTimes => !IsAllDay;

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? StartTime { get; set; }

    [ObservableProperty]
    public partial DateTime? EndDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EndTime { get; set; }

    [ObservableProperty]
    public partial RecurrenceOption Recurrence { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ConfirmDelete { get; set; }

    // Moving the start keeps the duration.
    partial void OnStartDateChanged(DateTime? oldValue, DateTime? newValue)
    {
        if (oldValue is { } old && newValue is { } current && EndDate is { } end)
        {
            EndDate = end + (current - old);
        }

        CheckConflictsLater();
    }

    partial void OnStartTimeChanged(TimeSpan? oldValue, TimeSpan? newValue)
    {
        if (oldValue is { } old && newValue is { } current && EndTime is { } end && StartDate == EndDate)
        {
            EndTime = end + (current - old);
        }

        CheckConflictsLater();
    }

    partial void OnEndDateChanged(DateTime? value) => CheckConflictsLater();

    partial void OnEndTimeChanged(TimeSpan? value) => CheckConflictsLater();

    partial void OnIsAllDayChanged(bool value) => CheckConflictsLater();

    partial void OnAttendeesTextChanged(string value) => CheckConflictsLater();

    partial void OnSelectedCalendarChanged(CalendarInfo? value)
    {
        CheckConflictsLater();
        _ = LoadMeetingChoicesAsync();
    }

    /// <summary>"Lea Keller hat zur gleichen Zeit: Kundentermin 09:00–10:00" – for people whose calendar Neruna knows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflicts))]
    public partial string? ConflictText { get; set; }

    public bool HasConflicts => !string.IsNullOrEmpty(ConflictText);

    private int _conflictVersion;

    // Once typing pauses; a later change wins.
    private void CheckConflictsLater()
    {
        var version = ++_conflictVersion;
        _ = CheckConflictsAsync(version);
    }

    private async Task CheckConflictsAsync(int version)
    {
        await Task.Delay(500);
        if (version != _conflictVersion || !CanEdit || StartDate is null || EndDate is null)
        {
            return;
        }

        var start = StartDate.Value.Date + (IsAllDay ? TimeSpan.Zero : StartTime ?? TimeSpan.FromHours(9));
        var end = IsAllDay ? EndDate.Value.Date.AddDays(1) : EndDate.Value.Date + (EndTime ?? TimeSpan.FromHours(10));
        var people = EventAttendee.FromRecipients(AttendeesText).ToList();
        var me = SelectedCalendar is { } own ? _ownAddress(own) : null;
        if (me is not null && IsOrganizer)
        {
            people.Insert(0, new EventAttendee(me));
        }

        try
        {
            var conflicts = await Task.Run(() => EventConflicts.FindAsync(_calendar, people, start, end, _existing, _originalTimes, SelectedCalendar?.ConnectionId));
            if (version != _conflictVersion)
            {
                return;
            }

            ConflictText = conflicts.Count == 0 ? null : string.Join("\n", conflicts.Take(6).Select(c => F("{0} hat zur gleichen Zeit: {1} ({2})",
                string.Equals(c.Person, me, StringComparison.OrdinalIgnoreCase) ? T("Sie selbst") : c.Name ?? c.Person,
                c.Subject ?? T("beschäftigt"),
                c.IsAllDay ? T("ganztägig") : $"{c.Start.LocalDateTime:HH:mm}–{c.End.LocalDateTime:HH:mm}")))
                + (conflicts.Count > 6 ? "\n…" : string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ConflictText = null; // only a hint; the event can be saved anyway
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        if (SelectedCalendar is null || StartDate is null || EndDate is null)
        {
            Error = T("Bitte Kalender, Beginn und Ende angeben.");
            return;
        }

        var start = StartDate.Value.Date + (IsAllDay ? TimeSpan.Zero : StartTime ?? TimeSpan.FromHours(9));
        var end = IsAllDay ? EndDate.Value.Date.AddDays(1) : EndDate.Value.Date + (EndTime ?? TimeSpan.FromHours(10));
        if (end <= start)
        {
            Error = T("Das Ende muss nach dem Beginn liegen.");
            return;
        }

        if (!TryParseAttendees(out var attendees))
        {
            Error = T("Bitte gültige E-Mail-Adressen bei den Teilnehmern eingeben (mehrere mit Komma trennen).");
            return;
        }

        var target = SelectedCalendar;
        var me = _ownAddress(target);
        var draft = new EventDraft(Title.Trim(), Location.Trim(), Description.Trim(), start, end, IsAllDay, Recurrence.Kind,
            Reminder.Minutes, IsOrganizer ? attendees : null, me)
        {
            ShowAs = (ShowAs ?? ShowAsOption.Of(Neruna.Core.Calendar.ShowAs.Busy)).Value,
            Meeting = Meeting,
        };
        await RunAsync(async () =>
        {
            var previous = _existing is { } e ? (await _calendar.GetObjectAsync(e.Calendar, e.RemoteId))?.ICalendarData : null;
            var saved = await _calendar.SaveEventAsync(target, draft, _existing);
            Saved = (target, saved.RemoteId, draft);
            if (!IsOrganizer)
            {
                return;
            }

            try
            {
                var sent = await _invitations.SendAfterSaveAsync(target, previous, saved.ICalendarData);
                ResultMessage = sent > 0 ? F("Gespeichert – Einladung an {0} Person(en) gesendet.", sent) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ResultMessage = T("Gespeichert, aber die Einladungen konnten nicht gesendet werden: ") + ex.Message;
            }
        });
    }

    // Keeps the known answers of people already invited; new people have not answered yet.
    private bool TryParseAttendees(out List<EventAttendee> attendees)
    {
        attendees = [];
        if (string.IsNullOrWhiteSpace(AttendeesText))
        {
            return true;
        }

        if (!InternetAddressList.TryParse(AttendeesText.Replace(';', ','), out var list))
        {
            return false;
        }

        foreach (var mailbox in list.Mailboxes)
        {
            var known = _attendees.FirstOrDefault(a => string.Equals(a.Email, mailbox.Address, StringComparison.OrdinalIgnoreCase));
            attendees.Add(known ?? new EventAttendee(mailbox.Address, string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name));
        }

        return true;
    }

    private bool CanSave() => !IsBusy && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task DeleteAsync()
    {
        if (_existing is not { } existing)
        {
            return;
        }

        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        await RunAsync(async () =>
        {
            // An attendee who deletes a meeting tells the organizer "abgelehnt".
            if (!IsOrganizer && (await _calendar.GetObjectAsync(existing.Calendar, existing.RemoteId)) is { } invited)
            {
                try
                {
                    ResultMessage = await _invitations.SendDeclineAsync(existing.Calendar, invited.ICalendarData)
                        ? F("Gelöscht – Absage an {0} gesendet.", Organizer)
                        : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error = T("Die Absage an den Organisator konnte nicht gesendet werden: ") + ex.Message;
                    return false;
                }
            }

            // The organizer of a meeting tells the attendees it is cancelled.
            if (IsOrganizer && (await _calendar.GetObjectAsync(existing.Calendar, existing.RemoteId)) is { } current)
            {
                try
                {
                    var sent = await _invitations.SendCancellationAsync(existing.Calendar, current.ICalendarData);
                    ResultMessage = sent > 0 ? F("Gelöscht – Absage an {0} Person(en) gesendet.", sent) : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Error = T("Die Absage konnte nicht gesendet werden: ") + ex.Message;
                    return false;
                }
            }

            await _calendar.DeleteEventAsync(existing.Calendar, existing.RemoteId);
            return true;
        });
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private Task RunAsync(Func<Task> operation) => RunAsync(async () =>
    {
        await operation();
        return true;
    });

    /// <param name="operation">Returns false to stay in the editor (the error is shown).</param>
    private async Task RunAsync(Func<Task<bool>> operation)
    {
        IsBusy = true;
        try
        {
            if (await operation())
            {
                Finished?.Invoke(this, true);
            }
        }
        catch (RemoteConflictException)
        {
            Error = T("Der Termin wurde inzwischen auf dem Server geändert. Bitte synchronisieren (F5) und erneut bearbeiten.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Speichern fehlgeschlagen: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
