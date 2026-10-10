using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

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

    /// <summary>The account whose calendars are shown on the right (the list on the left only with several accounts).</summary>
    [ObservableProperty]
    public partial CalendarSourceGroup? SelectedSource { get; set; }

    public bool HasSeveralSources => Sources.Count > 1;

    /// <summary>Narrows the calendars of the selected account by name.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    partial void OnFilterChanged(string value) => SelectedSource?.ApplyFilter(value);

    partial void OnSelectedSourceChanged(CalendarSourceGroup? value) => value?.ApplyFilter(Filter);

    /// <summary>"Alle" / "Keine": the calendars of the selected account that the filter shows.</summary>
    [RelayCommand]
    private void SelectAll(bool value)
    {
        foreach (var item in SelectedSource?.Calendars.Where(c => c.IsVisible) ?? [])
        {
            item.IsSelected = value;
        }
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(HasNoSources));

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Error = null;
        var selected = SelectedSource?.Source.Connection.Id;
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
                    group.Error = T("Server nicht erreichbar: ") + ex.Message;
                }

                group.UpdateSummary();
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasSeveralSources));
            SelectedSource = Sources.FirstOrDefault(s => s.Source.Connection.Id == selected) ?? Sources.FirstOrDefault();
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
            Error = T("Übernehmen fehlgeschlagen: ") + ex.Message;
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
            group.AddError = T("Bitte die vollständige CalDAV-Adresse eingeben (https://…).");
            return;
        }

        group.IsAdding = true;
        try
        {
            var added = await calendar.AddByUrlAsync(group.Source.Connection, url);
            if (added is null)
            {
                group.AddError = T("Unter dieser Adresse wurde kein Kalender gefunden, oder er ist für dieses Konto nicht freigegeben.");
                return;
            }

            group.NewUrl = string.Empty;
            var existing = group.Calendars.FirstOrDefault(c => c.Candidate.Calendar.RemoteId == added.RemoteId);
            if (existing is null)
            {
                group.Calendars.Add(new CalendarChoiceItem(new CalendarCandidate(added, true, true, true)));
                group.ApplyFilter(Filter);
            }
            else
            {
                existing.IsSelected = true;
            }

            Changed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            group.AddError = T("Hinzufügen fehlgeschlagen: ") + ex.Message;
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

internal sealed partial class CalendarSourceGroup : ObservableObject
{
    public CalendarSourceGroup(CalendarSource source)
    {
        Source = source;
        Calendars.CollectionChanged += (_, e) =>
        {
            foreach (CalendarChoiceItem item in e.NewItems ?? Array.Empty<CalendarChoiceItem>())
            {
                item.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(CalendarChoiceItem.IsSelected))
                    {
                        UpdateSummary();
                    }
                };
            }

            UpdateSummary();
        };
    }

    public CalendarSource Source { get; }

    public string Title => Source.Account.Title;

    public string Server => Source.Connection.Settings.TryGetValue(Neruna.Providers.Dav.DavSettings.UrlKey, out var url) ? url : Source.Connection.ProviderId;

    public ObservableCollection<CalendarChoiceItem> Calendars { get; } = [];

    /// <summary>"3 von 12 angezeigt" in the account list.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMatches { get; set; } = true;

    public void UpdateSummary()
    {
        Summary = Error is not null ? T("nicht erreichbar")
            : Calendars.Count == 0 ? T("keine Kalender")
            : F("{0} von {1} angezeigt", Calendars.Count(c => c.IsSelected), Calendars.Count);
    }

    public void ApplyFilter(string filter)
    {
        foreach (var item in Calendars)
        {
            item.IsVisible = filter.Length == 0 || item.Name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase);
        }

        HasMatches = Calendars.Count == 0 || Calendars.Any(c => c.IsVisible);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

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

    /// <summary>"Aufgaben" for a pure task list (it appears under "Aufgaben", not in the calendar).</summary>
    public string? ContentText => Candidate.Calendar.Content switch
    {
        Neruna.Core.Calendar.CalendarContent.Tasks => T("Aufgaben"),
        _ => null,
    };

    public string Color => Candidate.Calendar.Color ?? "#0F6CBD";

    public bool IsNew => Candidate.IsNew;

    public bool IsReadOnly => Candidate.Calendar.IsReadOnly;

    public bool IsAddedByUrl => Candidate.IsAddedByUrl;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.IsSelected;

    /// <summary>Matches the search field.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}
