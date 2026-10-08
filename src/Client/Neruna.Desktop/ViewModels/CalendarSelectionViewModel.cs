using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Kalender verwalten": asks every calendar server again which calendars exist (own, shared, subscribed in the
/// web interface) and lets the user choose which ones Neruna shows. Thunderbird offers this only during setup.
/// </summary>
internal sealed partial class CalendarSelectionViewModel(CalendarController calendar) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    public ObservableCollection<CalendarSourceGroup> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RefreshCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasNoSources => !IsBusy && Sources.Count == 0;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoSources));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            Sources.Clear();

            // ICS subscriptions are a single calendar each; there is nothing to choose.
            foreach (var source in (await calendar.GetSourcesAsync()).Where(s => s.Connection.ProviderId != ProviderIds.Ics))
            {
                var group = new CalendarSourceGroup(source);
                Sources.Add(group);
                try
                {
                    var discovery = await calendar.DiscoverWithDetailsAsync(source.Connection);
                    group.Details = discovery.Details;
                    foreach (var candidate in discovery.Calendars)
                    {
                        group.Calendars.Add(new CalendarChoiceItem(candidate));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    group.Error = "Server nicht erreichbar: " + ex.Message;
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            foreach (var group in Sources.Where(g => g.Error is null && g.Calendars.Count > 0))
            {
                await calendar.SetSelectionAsync(
                    group.Source.Connection,
                    group.Calendars.Where(c => c.IsSelected).Select(c => c.Candidate.Calendar.RemoteId).ToList(),
                    group.Calendars.Select(c => c.Candidate.Calendar.RemoteId).ToList());
            }

            Finished?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = "Übernehmen fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Adds the calendar whose address the user pasted (SOGo: Kalender → Eigenschaften → Links).</summary>
    [RelayCommand]
    private async Task AddByUrlAsync(CalendarSourceGroup group)
    {
        group.AddError = null;
        var text = group.NewUrl.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
        {
            group.AddError = "Bitte die vollständige CalDAV-Adresse eingeben (https://…).";
            return;
        }

        group.IsAdding = true;
        try
        {
            var added = await calendar.AddByUrlAsync(group.Source.Connection, url);
            if (added is null)
            {
                group.AddError = "Unter dieser Adresse wurde kein Kalender gefunden, oder er ist für dieses Konto nicht freigegeben.";
                return;
            }

            group.NewUrl = string.Empty;
            var existing = group.Calendars.FirstOrDefault(c => c.Candidate.Calendar.RemoteId == added.RemoteId);
            if (existing is null)
            {
                group.Calendars.Add(new CalendarChoiceItem(new CalendarCandidate(added, true, true, true)));
            }
            else
            {
                existing.IsSelected = true;
            }

            Changed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            group.AddError = "Hinzufügen fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            group.IsAdding = false;
        }
    }

    /// <summary>A calendar was added by address: the calendar view reloads even when the dialog is cancelled.</summary>
    public bool Changed { get; private set; }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, Changed);

    private bool IsIdle() => !IsBusy;
}

internal sealed partial class CalendarSourceGroup(CalendarSource source) : ObservableObject
{
    public CalendarSource Source { get; } = source;

    public string Title => Source.Account.EmailAddress ?? Source.Account.DisplayName;

    public string Server => Source.Connection.Settings.TryGetValue(Neruna.Providers.Dav.DavSettings.UrlKey, out var url) ? url : Source.Connection.ProviderId;

    public ObservableCollection<CalendarChoiceItem> Calendars { get; } = [];

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Where the server was asked (calendar home, user name) – helps when a subscription is missing.</summary>
    [ObservableProperty]
    public partial string? Details { get; set; }

    [ObservableProperty]
    public partial string NewUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? AddError { get; set; }

    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    public bool CanAddByUrl => Source.Connection.ProviderId == ProviderIds.CalDav;
}

internal sealed partial class CalendarChoiceItem(CalendarCandidate candidate) : ObservableObject
{
    public CalendarCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Calendar.Name;

    public string Color => Candidate.Calendar.Color ?? "#0F6CBD";

    public bool IsNew => Candidate.IsNew;

    public bool IsReadOnly => Candidate.Calendar.IsReadOnly;

    public bool IsAddedByUrl => Candidate.IsAddedByUrl;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.IsSelected;
}
