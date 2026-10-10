using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The bar above a mail that carries an invitation, an answer or a cancellation: what it is about, whether it is in the
/// calendar, collisions, and Annehmen / Vorläufig / Ablehnen. Answers go through <see cref="InvitationService"/>; the
/// mail page is told when the calendar changed (to refresh it and tidy the mail away).
/// </summary>
/// <param name="done">
/// Answered, a cancellation applied or an answer to my invitation recorded – with what to tell the user (empty when
/// nothing needs saying). Awaited: the bar stays busy until the mail page is done too.
/// </param>
internal sealed partial class InvitationBannerViewModel(Invitation invitation, Account account, InvitationService invitations, Func<string, Task> done) : ViewModelBase
{
    public Invitation Invitation { get; } = invitation;

    public string Heading => Invitation.Method switch
    {
        InvitationMethod.Cancel => T("Termin abgesagt"),
        InvitationMethod.Reply => T("Antwort auf Ihre Einladung"),
        _ => (Invitation.OrganizerName ?? Invitation.Organizer) is { } organizer ? T("Einladung von ") + organizer : T("Einladung"),
    };

    public string Summary => Invitation.Summary;

    public string When => ITip.When(Invitation.Start, Invitation.End, Invitation.IsAllDay) + (Invitation.IsRecurring ? T(" · Serie") : string.Empty);

    public string? Location => Invitation.Location;

    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);

    public bool IsCancel => Invitation.Method == InvitationMethod.Cancel;

    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Where an accepted invitation goes (only offered while it is not in a calendar yet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCalendarChoice))]
    public partial IReadOnlyList<CalendarInfo> Calendars { get; set; } = [];

    [ObservableProperty]
    public partial CalendarInfo? SelectedCalendar { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCalendarChoice))]
    public partial bool IsInCalendar { get; set; }

    public bool ShowCalendarChoice => CanRespond && !IsInCalendar && Calendars.Count > 1;

    [ObservableProperty]
    public partial string? Conflicts { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Annehmen / Vorläufig / Ablehnen are offered (an invitation to me, not outdated).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(TentativeCommand), nameof(DeclineCommand))]
    [NotifyPropertyChangedFor(nameof(ShowCalendarChoice))]
    public partial bool CanRespond { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool CanRemove { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand), nameof(TentativeCommand), nameof(DeclineCommand), nameof(RemoveCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task AcceptAsync() => RunAsync(() => RespondAsync(Participation.Accepted));

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task TentativeAsync() => RunAsync(() => RespondAsync(Participation.Tentative));

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task DeclineAsync() => RunAsync(() => RespondAsync(Participation.Declined));

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private Task RemoveAsync() => RunAsync(async () =>
    {
        await invitations.ApplyCancelAsync(Invitation);
        await RefreshAsync();
        await done(T("Der abgesagte Termin wurde aus dem Kalender entfernt."));
    });

    private async Task RespondAsync(Participation answer)
    {
        await invitations.RespondAsync(Invitation, answer, account, IsInCalendar ? null : SelectedCalendar);
        await RefreshAsync();
        await done(answer switch
        {
            Participation.Accepted => T("Zugesagt – der Termin steht im Kalender."),
            Participation.Tentative => T("Mit Vorbehalt zugesagt – der Termin steht im Kalender."),
            _ => T("Abgesagt."),
        });
    }

    /// <summary>
    /// On opening: an answer to my own invitation is recorded right away (like in office calendars), then status,
    /// collisions and buttons from the calendar as it is now.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (Invitation.Method == InvitationMethod.Reply && await invitations.ApplyReplyAsync(Invitation))
            {
                await done(string.Empty);
            }

            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = T("Der Kalender konnte nicht geprüft werden: ") + ex.Message;
            throw;
        }
    }

    // Status text, collisions and which buttons make sense – from the calendar as it is now.
    private async Task RefreshAsync()
    {
        var state = await invitations.GetStateAsync(Invitation, account);
        var mine = InvitationService.IsMine(Invitation, account.EmailAddress) && Invitation.Method != InvitationMethod.Reply;
        IsInCalendar = state.Item is not null;
        if (Calendars.Count == 0)
        {
            Calendars = await invitations.TargetCalendarsAsync(account);
            SelectedCalendar = Calendars.FirstOrDefault();
        }

        switch (Invitation.Method)
        {
            case InvitationMethod.Reply:
                var answer = Invitation.Attendees.FirstOrDefault();
                var who = answer?.Name ?? answer?.Email ?? T("Jemand");
                Status = answer?.Status switch
                {
                    Participation.Accepted => F("{0} hat zugesagt.", who),
                    Participation.Tentative => F("{0} hat mit Vorbehalt zugesagt.", who),
                    Participation.Declined => F("{0} hat abgesagt.", who),
                    _ => F("{0} hat geantwortet.", who),
                } + (state.Item is null ? T(" Der Termin ist nicht (mehr) in Ihrem Kalender.") : T(" Die Antwort ist in Ihrem Termin eingetragen."));
                CanRespond = false;
                break;

            case InvitationMethod.Cancel:
                Status = state.Item is null ? T("Der Termin ist nicht (mehr) in Ihrem Kalender.") : T("Der Termin steht noch in Ihrem Kalender.");
                CanRemove = state.Item is not null && state.Calendar is { IsReadOnly: false };
                break;

            default:
                Status = mine ? T("Sie sind der Organisator dieses Termins.")
                    : state.IsOutdated ? T("Diese Einladung ist veraltet – im Kalender steht bereits eine neuere Fassung.")
                    : state.MyAnswer switch
                    {
                        Participation.Accepted when state.Item is not null => T("Sie haben zugesagt."),
                        Participation.Tentative when state.Item is not null => T("Sie haben mit Vorbehalt zugesagt."),
                        Participation.Declined => T("Sie haben abgesagt."),
                        _ when state.Item is not null => T("Steht in Ihrem Kalender – noch nicht beantwortet."),
                        _ => T("Noch nicht beantwortet."),
                    };
                CanRespond = !mine && !state.IsOutdated;
                break;
        }

        Conflicts = Invitation.Method == InvitationMethod.Request && state.Conflicts.Count > 0
            ? T("Überschneidet sich mit: ") + string.Join(", ", state.Conflicts.Take(3).Select(c => $"{c.Summary} ({c.Start:HH:mm}–{c.End:HH:mm})"))
            : null;
    }

    private bool CanAnswer() => CanRespond && !IsBusy;

    private bool CanRemoveNow() => CanRemove && !IsBusy;

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
