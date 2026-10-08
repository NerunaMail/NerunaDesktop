using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The bar above a mail that carries an invitation, an answer or a cancellation: what it is about, whether it is in the
/// calendar, collisions, and Annehmen / Vorläufig / Ablehnen.
/// </summary>
internal sealed partial class InvitationBannerViewModel(
    Invitation invitation,
    Func<Participation, Task> respond,
    Func<Task> removeCancelled) : ViewModelBase
{
    public Invitation Invitation { get; } = invitation;

    public string Heading => Invitation.Method switch
    {
        InvitationMethod.Cancel => "Termin abgesagt",
        InvitationMethod.Reply => "Antwort auf Ihre Einladung",
        _ => (Invitation.OrganizerName ?? Invitation.Organizer) is { } organizer ? "Einladung von " + organizer : "Einladung",
    };

    public string Summary => Invitation.Summary;

    public string When => ITip.When(Invitation.Start, Invitation.End, Invitation.IsAllDay) + (Invitation.IsRecurring ? " · Serie" : string.Empty);

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
    private Task AcceptAsync() => RunAsync(() => respond(Participation.Accepted));

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task TentativeAsync() => RunAsync(() => respond(Participation.Tentative));

    [RelayCommand(CanExecute = nameof(CanAnswer))]
    private Task DeclineAsync() => RunAsync(() => respond(Participation.Declined));

    [RelayCommand(CanExecute = nameof(CanRemoveNow))]
    private Task RemoveAsync() => RunAsync(removeCancelled);

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
