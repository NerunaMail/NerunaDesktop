using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Creates or edits a task: title, notes, start, due date (optionally with a time), reminder, repetition, priority,
/// state, place, privacy, link, link attachments, categories and list. Fields the list's service cannot keep (Microsoft
/// To Do, Google Tasks) are hidden.
/// </summary>
internal sealed partial class TaskEditorViewModel : ViewModelBase
{
    private readonly TaskController _tasks;
    private readonly ISettingsStore _settings;
    private readonly TaskItem? _existing;
    private readonly CalendarController _calendar;
    private readonly IReadOnlyList<string> _categories;

    public TaskEditorViewModel(TaskController tasks, CalendarController calendar, ISettingsStore settings, IReadOnlyList<CalendarInfo> lists, TaskDraft draft,
        TaskItem? existing, CalendarInfo target, IReadOnlyList<CalendarCategory> knownCategories)
    {
        _tasks = tasks;
        _calendar = calendar;
        _settings = settings;
        _existing = existing;
        var details = draft.Details ?? new TaskDetails();
        _categories = details.Categories;
        Lists = lists;
        SelectedList = lists.FirstOrDefault(l => l.ConnectionId == target.ConnectionId && l.RemoteId == target.RemoteId) ?? lists.FirstOrDefault();
        Summary = draft.Summary;
        Notes = draft.Notes ?? string.Empty;
        HasDue = draft.Due is not null;
        DueDate = draft.Due?.Date ?? DateTime.Today;
        HasTime = draft.Due is not null && draft.DueHasTime;
        DueTime = draft.Due is { } due && draft.DueHasTime ? due.TimeOfDay : new TimeSpan(9, 0, 0);
        PriorityIndex = (int)draft.Priority;
        StateIndex = draft.IsCompleted ? 2 : details.State switch { TaskState.InProgress => 1, TaskState.Cancelled => 3, _ => 0 };
        HasStart = details.Start is not null;
        StartDate = details.Start ?? DateTime.Today;
        HasReminder = details.ReminderAt is not null;
        ReminderDate = details.ReminderAt?.Date ?? draft.Due?.Date ?? DateTime.Today;
        ReminderTime = details.ReminderAt?.TimeOfDay ?? new TimeSpan(9, 0, 0);
        Repeat = new RecurrenceEditorViewModel(details.Recurrence, details.Repeat, details.RecurrenceUntil, SeriesStart);
        Location = details.Location ?? string.Empty;
        Privacy = PrivacyOption.Of(details.Privacy);
        Url = details.Url ?? string.Empty;
        LinksText = string.Join("\n", details.Links.Select(l => l.OriginalString));

        foreach (var category in knownCategories)
        {
            CategoryChoices.Add(new CategoryChoice(category.Name, category.Color, _categories.Any(category.Matches)));
        }

        foreach (var other in _categories.Where(c => !knownCategories.Any(k => k.Matches(c))))
        {
            CategoryChoices.Add(new CategoryChoice(other, null, true));
        }

        foreach (var choice in CategoryChoices)
        {
            choice.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCategories));
        }

        _ = LoadFieldsAsync();
    }

    // The day a repetition counts from: the start, else the due day.
    private DateTime SeriesStart() => TaskDetails.SeriesStart(HasStart ? StartDate : null, HasDue ? DueDate : null);

    /// <summary>Which fields the chosen list keeps; until known, all are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStart), nameof(ShowReminder), nameof(ShowRecurrence), nameof(ShowCategories), nameof(ShowLocation),
        nameof(ShowPrivacy), nameof(ShowLinks), nameof(States), nameof(StateChoice), nameof(HasFewFields))]
    public partial TaskFields Fields { get; set; } = TaskFields.All;

    public bool ShowStart => Fields.HasFlag(TaskFields.Start);

    public bool ShowReminder => Fields.HasFlag(TaskFields.Reminder);

    public bool ShowRecurrence => Fields.HasFlag(TaskFields.Recurrence);

    public bool ShowCategories => Fields.HasFlag(TaskFields.Categories) && CategoryChoices.Count > 0;

    public bool ShowLocation => Fields.HasFlag(TaskFields.Location);

    public bool ShowPrivacy => Fields.HasFlag(TaskFields.Privacy);

    public bool ShowLinks => Fields.HasFlag(TaskFields.Links);

    /// <summary>The list's service keeps fewer fields: say so, so nobody looks for the others.</summary>
    public bool HasFewFields => Fields != TaskFields.All;

    private async Task LoadFieldsAsync()
    {
        if (SelectedList is { } list)
        {
            Fields = await _calendar.TaskFieldsOfAsync(list);
        }
    }

    partial void OnSelectedListChanged(CalendarInfo? value) => _ = LoadFieldsAsync();

    /// <summary>"Offen", "In Bearbeitung", "Erledigt", "Abgebrochen" (the last only where the list keeps it).</summary>
    public IReadOnlyList<string> States => Fields.HasFlag(TaskFields.Cancelled)
        ? [T("Offen"), T("In Bearbeitung"), T("Erledigt"), T("Abgebrochen")]
        : Fields.HasFlag(TaskFields.InProgress) ? [T("Offen"), T("In Bearbeitung"), T("Erledigt")] : [T("Offen"), T("Erledigt")];

    // The index in the full list of states: Offen, In Bearbeitung, Erledigt, Abgebrochen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateChoice))]
    public partial int StateIndex { get; set; }

    /// <summary>The state as chosen in <see cref="States"/> (which may lack "In Bearbeitung").</summary>
    public int StateChoice
    {
        get => States.Count == 2 ? (StateIndex == 2 ? 1 : 0) : StateIndex;
        set => StateIndex = States.Count == 2 ? (value == 1 ? 2 : 0) : value;
    }

    [ObservableProperty]
    public partial bool HasStart { get; set; }

    [ObservableProperty]
    public partial DateTime? StartDate { get; set; }

    partial void OnStartDateChanged(DateTime? value) => Repeat?.StartChanged();

    partial void OnHasStartChanged(bool value) => Repeat?.StartChanged();

    partial void OnDueDateChanged(DateTime? value) => Repeat?.StartChanged();

    [ObservableProperty]
    public partial bool HasReminder { get; set; }

    [ObservableProperty]
    public partial DateTime? ReminderDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? ReminderTime { get; set; }

    public RecurrenceEditorViewModel Repeat { get; }

    [ObservableProperty]
    public partial string Location { get; set; }

    [ObservableProperty]
    public partial PrivacyOption Privacy { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string LinksText { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<CategoryChoice> CategoryChoices { get; } = [];

    public IReadOnlyList<CategoryChoice> SelectedCategories => [.. CategoryChoices.Where(c => c.IsSelected)];

    public event EventHandler<bool>? Finished;

    /// <summary>For the status bar after saving, when something is left to do (the original of a move).</summary>
    public string? ResultMessage { get; private set; }

    public string Title => _existing is null ? T("Neue Aufgabe") : T("Aufgabe bearbeiten");

    public bool IsExisting => _existing is not null;

    public IReadOnlyList<CalendarInfo> Lists { get; }

    public IReadOnlyList<string> Priorities { get; } = [T("Keine"), T("Niedrig"), T("Normal"), T("Hoch")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial CalendarInfo? SelectedList { get; set; }

    /// <summary>A task of a read-only list (shared without write access) can only be looked at.</summary>
    public bool CanEdit => _existing is null || !_existing.List.IsReadOnly;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial bool HasDue { get; set; }

    [ObservableProperty]
    public partial DateTime? DueDate { get; set; }

    [ObservableProperty]
    public partial bool HasTime { get; set; }

    [ObservableProperty]
    public partial TimeSpan? DueTime { get; set; }

    [ObservableProperty]
    public partial int PriorityIndex { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    private bool CanSave() => !IsBusy && CanEdit && SelectedList is not null && Summary.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        DateTime? due = HasDue && DueDate is { } date ? date.Date + (HasTime ? DueTime ?? TimeSpan.Zero : TimeSpan.Zero) : null;
        DateTime? start = ShowStart && HasStart && StartDate is { } first ? first.Date : null;
        if (start is { } s && due is { } d && s > d.Date)
        {
            Error = T("Die Aufgabe kann nicht nach dem Fälligkeitsdatum beginnen.");
            return;
        }

        var repeat = Repeat.Build();
        if (repeat.Error is not null)
        {
            Error = repeat.Error;
            return;
        }

        if (repeat.Kind != RecurrenceKind.None && start is null && due is null)
        {
            Error = T("Eine wiederkehrende Aufgabe braucht ein Beginn- oder Fälligkeitsdatum.");
            return;
        }

        var links = new List<Uri>();
        foreach (var line in (ShowLinks ? LinksText : string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (!Uri.TryCreate(line.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + line : line, UriKind.Absolute, out var link))
            {
                Error = F("«{0}» ist keine gültige Web-Adresse.", line);
                return;
            }

            links.Add(link);
        }

        var url = ShowLinks ? Url.Trim() : string.Empty;
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Error = F("«{0}» ist keine gültige Web-Adresse.", url);
            return;
        }

        var details = new TaskDetails
        {
            Start = start,
            ReminderAt = ShowReminder && HasReminder && ReminderDate is { } day ? day.Date + (ReminderTime ?? new TimeSpan(9, 0, 0)) : null,
            Recurrence = repeat.Kind,
            Repeat = repeat.Spec,
            RecurrenceUntil = repeat.Until,
            Location = ShowLocation ? Location.Trim() : null,
            Privacy = ShowPrivacy ? (Privacy ?? PrivacyOption.Of(EventPrivacy.Public)).Value : EventPrivacy.Public,
            Url = url.Length > 0 ? url : null,
            Links = links,
            // Without a list (no categories set up) the task keeps its own.
            Categories = CategoryChoices.Count > 0 ? [.. SelectedCategories.Select(c => c.Name)] : _categories,
            State = StateIndex switch { 1 => TaskState.InProgress, 3 => TaskState.Cancelled, _ => TaskState.Open },
        };
        var draft = new TaskDraft(Summary, Notes, due, HasDue && HasTime, (TaskPriority)Math.Clamp(PriorityIndex, 0, 3), StateIndex == 2)
        {
            Details = details,
        };
        await RunAsync(async () =>
        {
            try
            {
                await _tasks.SaveAsync(SelectedList!, draft, _existing);
            }
            catch (MoveIncompleteException ex)
            {
                // Saved in the new list: closing is right (saving again would add another copy).
                ResultMessage = ex.Message;
            }

            // New tasks go where the last one went.
            await _settings.SetAsync(SettingKeys.TasksDefaultList, CalendarController.CalendarKey(SelectedList!));
        });
    }

    private bool CanDelete() => !IsBusy && IsExisting && CanEdit;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (await Infrastructure.DeleteConfirmation.AskAsync(Infrastructure.DeleteKind.Task,
                F("«{0}» löschen", string.IsNullOrWhiteSpace(Summary) ? T("Aufgabe") : Summary.Trim()),
                T("Die Aufgabe wird auch auf dem Server gelöscht und lässt sich nicht wiederherstellen.")))
        {
            await RunAsync(() => _tasks.DeleteAsync(_existing!));
        }
    }

    [RelayCommand]
    private void Cancel() => Finished?.Invoke(this, false);

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Error = null;
        try
        {
            await action();
            Finished?.Invoke(this, true);
        }
        catch (RemoteConflictException)
        {
            Error = T("Die Aufgabe wurde inzwischen auf dem Server geändert. Bitte synchronisieren und erneut versuchen.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = F("Aufgabe konnte nicht gespeichert werden: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
