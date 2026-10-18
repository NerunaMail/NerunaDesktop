using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Auth;
using Neruna.Core.Calendar;
using Neruna.Core.Meetings;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>A Microsoft 365 account and whether its calendar offers Teams by itself.</summary>
internal sealed record MeetingAccountRow(string Email, string State, bool HasTeams);

/// <summary>
/// Settings → Besprechungen: the meeting services (Teams with a sign-in of its own, Zoom, Jitsi servers) and which
/// Microsoft 365 accounts offer Teams directly. Events get their meeting in the event editor.
/// </summary>
internal sealed partial class MeetingsOptionsViewModel(MeetingController meetings, CalendarController calendars, IBrowserLauncher browser) : ViewModelBase
{
    private Action? _cancelSignIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServices))]
    public partial IReadOnlyList<MeetingService> Services { get; set; } = [];

    public bool HasServices => Services.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    public partial IReadOnlyList<MeetingAccountRow> Accounts { get; set; } = [];

    public bool HasAccounts => Accounts.Count > 0;

    public bool TeamsAvailable => meetings.IsAvailable(MeetingKinds.Teams);

    public bool ZoomAvailable => meetings.IsAvailable(MeetingKinds.Zoom);

    public bool ZoomNotAvailable => !ZoomAvailable;

    /// <summary>Address of a Jitsi server to add; empty = meet.jit.si.</summary>
    [ObservableProperty]
    public partial string JitsiServer { get; set; } = string.Empty;

    public static string JitsiPlaceholder => JitsiMeetingProvider.DefaultServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTeamsCommand), nameof(AddZoomCommand), nameof(AddJitsiCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsSigningIn { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    public async Task LoadAsync()
    {
        Services = await meetings.GetServicesAsync();
        Accounts = await AccountRowsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddTeamsAsync() => AddAsync(MeetingKinds.Teams, null, signIn: true);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task AddZoomAsync() => AddAsync(MeetingKinds.Zoom, null, signIn: true);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddJitsiAsync()
    {
        await AddAsync(MeetingKinds.Jitsi, JitsiServer, signIn: false);
        if (Error is null)
        {
            JitsiServer = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _cancelSignIn?.Invoke();

    [RelayCommand]
    private async Task RemoveAsync(MeetingService? service)
    {
        if (service is null)
        {
            return;
        }

        await meetings.RemoveAsync(service);
        Services = await meetings.GetServicesAsync();
        Status = F("«{0}» entfernt.", service.Name);
    }

    private bool CanAdd() => !IsBusy;

    private async Task AddAsync(string kind, string? server, bool signIn)
    {
        Error = null;
        Status = signIn ? T("Die Anmeldung öffnet sich im Browser …") : null;
        IsBusy = true;
        IsSigningIn = signIn;
        using var cancel = new CancellationTokenSource();
        _cancelSignIn = cancel.Cancel;
        try
        {
            var added = await meetings.AddAsync(kind, server, browser, cancel.Token);
            Services = await meetings.GetServicesAsync();
            Status = F("«{0}» ist eingerichtet.", added.Name);
        }
        catch (OperationCanceledException)
        {
            Status = null;
        }
        catch (MeetingException ex)
        {
            Status = null;
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsSigningIn = false;
            _cancelSignIn = null;
        }
    }

    // Each Microsoft 365 account with its first calendar: does the organisation offer Teams there?
    private async Task<IReadOnlyList<MeetingAccountRow>> AccountRowsAsync()
    {
        var rows = new List<MeetingAccountRow>();
        var all = await calendars.GetCalendarsAsync();
        foreach (var source in (await calendars.GetSourcesAsync()).Where(s => s.Connection.ProviderId == ProviderIds.Graph))
        {
            var calendar = all.FirstOrDefault(c => c.ConnectionId == source.Connection.Id && c.HasEvents && !c.IsReadOnly);
            var teams = calendar is not null && (await calendars.GetOnlineMeetingKindsAsync(calendar)).Contains(MeetingKinds.Teams);
            rows.Add(new MeetingAccountRow(source.Account.EmailAddress ?? source.Account.DisplayName, teams
                ? T("Teams verfügbar – Termine in diesem Kalender erhalten die Besprechung direkt von Exchange, ohne Add-in.")
                : T("Kein Teams für dieses Konto (z. B. ein privates Microsoft-Konto)."), teams));
        }

        return rows;
    }
}
