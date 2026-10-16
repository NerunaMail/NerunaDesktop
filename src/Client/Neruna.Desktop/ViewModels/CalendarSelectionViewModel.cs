using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Calendar;
using Neruna.Core.Providers;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Kalender verwalten": asks every calendar server again which calendars exist (own, shared, subscribed in the
/// web interface) and lets the user choose which ones Neruna shows. Also where new events go: the default calendar,
/// and per account the one its invitations go into – both chosen from the calendars shown, so the lists are current.
/// </summary>
internal sealed partial class CalendarSelectionViewModel(CalendarController calendar, ISettingsStore settings) : ViewModelBase
{
    public event EventHandler<bool>? Finished;

    /// <summary>"Standardkalender für neue Termine": automatic or one of the writable calendars shown, of all accounts.</summary>
    public ObservableCollection<CalendarChoice> DefaultChoices { get; } = [];

    [ObservableProperty]
    public partial CalendarChoice? DefaultCalendar { get; set; }

    private string? _defaultKey;

    partial void OnDefaultCalendarChanged(CalendarChoice? value)
    {
        if (value is not null)
        {
            _defaultKey = value.Key;
        }
    }

    /// <summary>After a calendar was ticked or unticked: the choices follow, the chosen one stays if it still can.</summary>
    private void UpdateChoices()
    {
        var key = _defaultKey;
        DefaultChoices.Clear();
        DefaultChoices.Add(CalendarChoice.Automatic);
        foreach (var group in Sources)
        {
            foreach (var item in group.Writable())
            {
                DefaultChoices.Add(new CalendarChoice(CalendarController.CalendarKey(item.Candidate.Calendar), Sources.Count > 1 ? $"{item.Name} – {group.Title}" : item.Name));
            }
        }

        var choice = DefaultChoices.FirstOrDefault(c => c.Key == key);
        DefaultCalendar = choice ?? CalendarChoice.Automatic;
        // Unticked here: no longer the default. Only on an unreachable server it stays (it is not listed at all).
        _defaultKey = choice is null && Sources.Any(g => g.Lists(key)) ? null : key;
    }

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
            _defaultKey ??= await settings.GetAsync(SettingKeys.CalendarDefaultCalendar);

            // ICS subscriptions are a single calendar each; there is nothing to choose.
            foreach (var source in (await calendar.GetSourcesAsync()).Where(s => s.Connection.ProviderId != ProviderIds.Ics))
            {
                var group = new CalendarSourceGroup(source, await settings.GetAsync(InvitationService.DefaultCalendarKey(source.Account.Id)));
                group.ChoicesChanged += (_, _) => UpdateChoices();
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

                group.IsLoaded = true;
                group.UpdateSummary();
            }
        }
        finally
        {
            UpdateChoices();
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

            // The keys, not the boxes: a chosen calendar of a server that is not reachable right now is not in the list
            // (the box shows "Automatisch"), but stays the default.
            await settings.SetAsync(SettingKeys.CalendarDefaultCalendar, _defaultKey);
            foreach (var group in Sources)
            {
                await settings.SetAsync(InvitationService.DefaultCalendarKey(group.Source.Account.Id), group.InvitationKey);
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
    private string? _invitationKey;

    public CalendarSourceGroup(CalendarSource source, string? invitationKey = null)
    {
        Source = source;
        _invitationKey = invitationKey;
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

    public string Server => Source.Server.Length > 0 ? Source.Server : Source.Connection.ProviderId;

    public ObservableCollection<CalendarChoiceItem> Calendars { get; } = [];

    /// <summary>"3 von 12 angezeigt" in the account list.</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMatches { get; set; } = true;

    /// <summary>A calendar was ticked or unticked: the default choices of the dialog follow.</summary>
    public event EventHandler? ChoicesChanged;

    /// <summary>The calendars shown that new events can go into (not read-only, not a pure task list).</summary>
    public IEnumerable<CalendarChoiceItem> Writable() =>
        Calendars.Where(c => c.IsSelected && !c.IsReadOnly && c.Candidate.Calendar.Content != Neruna.Core.Calendar.CalendarContent.Tasks);

    public string? InvitationKey => _invitationKey;

    /// <summary>All calendars of the server are in the list (while it fills, the chosen one may still be missing).</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Whether this calendar is among the ones the server listed (shown or not).</summary>
    public bool Lists(string? key) => key is not null && Calendars.Any(c => CalendarController.CalendarKey(c.Candidate.Calendar) == key);

    /// <summary>"Einladungen an dieses Konto": automatic (one of the account's own) or one of its calendars shown.</summary>
    public ObservableCollection<CalendarChoice> InvitationChoices { get; } = [];

    [ObservableProperty]
    public partial CalendarChoice? InvitationCalendar { get; set; }

    partial void OnInvitationCalendarChanged(CalendarChoice? value)
    {
        if (value is not null)
        {
            _invitationKey = value.Key;
        }
    }

    public void UpdateSummary()
    {
        var key = _invitationKey;
        InvitationChoices.Clear();
        InvitationChoices.Add(CalendarChoice.AutomaticForAccount);
        foreach (var item in Writable())
        {
            InvitationChoices.Add(new CalendarChoice(CalendarController.CalendarKey(item.Candidate.Calendar), item.Name));
        }

        var choice = InvitationChoices.FirstOrDefault(c => c.Key == key);
        InvitationCalendar = choice ?? CalendarChoice.AutomaticForAccount;
        // Not one of the account's calendars shown (unticked, or from before: the calendar chosen last, of any account):
        // automatic, as the box says. Only while the server is not reachable the setting stays.
        _invitationKey = choice is null && IsLoaded && Error is null ? null : key;
        ChoicesChanged?.Invoke(this, EventArgs.Empty);

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

    /// <summary>"geteilt von Lea Keller": another person's calendar, found through delegation.</summary>
    public string? SharedByText => Candidate.Calendar.SharedBy is { } owner ? F("geteilt von {0}", owner) : null;

    public bool IsReadOnly => Candidate.Calendar.IsReadOnly;

    public bool IsAddedByUrl => Candidate.IsAddedByUrl;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.IsSelected;

    /// <summary>Matches the search field.</summary>
    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;
}

/// <summary>A calendar in the choice of a default one; the automatic choices have no key.</summary>
internal sealed record CalendarChoice(string? Key, string Label)
{
    public static CalendarChoice Automatic { get; } = new(null, T("Automatisch (der erste beschreibbare Kalender)"));

    public static CalendarChoice AutomaticForAccount { get; } = new(null, T("Automatisch (ein eigener Kalender des Kontos)"));

    public override string ToString() => Label;
}
