using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Which tasks the list shows.</summary>
internal enum TaskFilter
{
    Open,
    Due,
    Completed,
    All,
}

/// <summary>
/// "Aufgaben": the tasks (VTODO) of all task lists – CalDAV calendars that hold tasks and Microsoft To Do. Each list can
/// be shown or hidden on its own (hidden lists are also left out of the calendar); grouped by due date.
/// </summary>
internal sealed partial class TasksViewModel(CalendarController calendar, TaskController tasks, ISettingsStore settings, Neruna.Core.Workspaces.WorkspaceService workspaces,
    CalendarCategoryService categories, ILogger<TasksViewModel> logger) : ViewModelBase, ICollectionListHost
{
    private IReadOnlyList<TaskItem> _all = [];

    /// <summary>Opens the task editor in the shell (overlay).</summary>
    public event EventHandler<TaskEditorViewModel>? EditorRequested;

    public event EventHandler<string>? StatusMessage;

    /// <summary>A list's colour or "Aufgaben im Kalender anzeigen" was changed here: the calendar shows both.</summary>
    public event EventHandler? CalendarChanged;

    public ObservableCollection<TaskListGroup> ListGroups { get; } = [];

    public ObservableCollection<TaskGroup> Groups { get; } = [];

    public bool HasLists => ListGroups.Count > 0;

    public bool IsEmpty => HasLists && Groups.Count == 0;

    /// <summary>The task in the reading pane (a click in the list; a double click opens the editor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial TaskRow? SelectedTask { get; set; }

    public bool HasSelection => SelectedTask is not null;

    partial void OnSelectedTaskChanged(TaskRow? oldValue, TaskRow? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    public IReadOnlyList<string> Filters { get; } = [T("Offen"), T("Fällig: heute und überfällig"), T("Erledigt"), T("Alle")];

    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    /// <summary>Typed into the quick-add field; Enter adds it to the default list.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(QuickAddCommand))]
    public partial string NewTitle { get; set; } = string.Empty;

    public string Summary => F("{0} offen", _all.Count(t => !t.IsCompleted && IsShown(t.List)));

    partial void OnFilterIndexChanged(int value) => Rebuild();

    partial void OnSearchChanged(string value) => Rebuild();

    public async Task ReloadAsync()
    {
        try
        {
            // Only the lists the open workspace shows.
            var sources = await calendar.GetSourcesAsync();
            var accountOf = sources.ToDictionary(s => s.Connection.Id, s => s.Account.Id);
            var lists = (await calendar.GetCalendarsAsync())
                .Where(c => c.HasTasks && workspaces.Current.ShowsCalendar(c, accountOf.TryGetValue(c.ConnectionId, out var a) ? a : null))
                .ToList();
            var hidden = await tasks.GetHiddenListsAsync();
            var inCalendar = await tasks.GetListsInCalendarAsync();
            var colors = await CalendarViewModel.ColorsAsync(settings, await calendar.GetCalendarsAsync());
            var accounts = sources.ToDictionary(s => s.Connection.Id, s => s.Account.Title);
            var shownTasks = (await Task.Run(() => tasks.GetTasksAsync())).Where(t => lists.Any(l => Same(t.List, l)));
            _all = [.. shownTasks];

            ListGroups.Clear();
            foreach (var group in lists.GroupBy(l => l.ConnectionId))
            {
                var items = group.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(l => new TaskListItem(l, colors.GetValueOrDefault((l.ConnectionId, l.RemoteId)) ?? CalendarViewModel.Palette[0], _all.Count(t => !t.IsCompleted && Same(t.List, l)))
                    {
                        IsVisible = !hidden.Contains(CalendarController.CalendarKey(l)),
                        ShowTasks = inCalendar.Contains(CalendarController.CalendarKey(l)),
                    })
                    .ToList();
                foreach (var item in items)
                {
                    _solo.Apply(item);
                    item.PropertyChanged += async (_, e) =>
                    {
                        if (e.PropertyName == nameof(TaskListItem.IsVisible))
                        {
                            // Ticking a list ends "only this list": the ticks are the selection again.
                            _solo.Set(null, AllLists);
                            await tasks.SetListVisibleAsync(item.Info, item.IsVisible);
                            await RefreshTargetsAsync();
                            Rebuild();
                        }
                        else if (e.PropertyName == nameof(TaskListItem.ShowTasks))
                        {
                            await tasks.SetListInCalendarAsync(item.Info, item.ShowTasks);
                            CalendarChanged?.Invoke(this, EventArgs.Empty);
                        }
                    };
                }

                ListGroups.Add(new TaskListGroup(accounts.GetValueOrDefault(group.Key) ?? string.Empty, items));
            }

            OnPropertyChanged(nameof(HasLists));
            _accounts = accounts;
            await RefreshTargetsAsync();
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tasks could not be loaded");
            StatusMessage?.Invoke(this, T("Aufgaben konnten nicht geladen werden"));
        }
    }

    /// <summary>Gives a list an own display name (null: the server's again) – the same name as in the calendar.</summary>
    public async Task RenameAsync(CollectionListItem item, string? name)
    {
        ArgumentNullException.ThrowIfNull(item);
        await calendar.SetDisplayNameAsync(item.Info, name);
        await ReloadAsync();
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets a list's colour (null: the server's again) – the same colour as in the calendar.</summary>
    public async Task SetColorAsync(CollectionListItem item, string? color)
    {
        ArgumentNullException.ThrowIfNull(item);
        await settings.SetAsync(CalendarViewModel.ColorKey(item.Info), color);
        await ReloadAsync();
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly SoloFilter _solo = new();

    private IEnumerable<TaskListItem> AllLists => ListGroups.SelectMany(g => g.Lists);

    [RelayCommand]
    private void ToggleSolo(TaskListItem item)
    {
        _solo.Toggle(item, AllLists);
        _ = RefreshTargetsAsync();
        Rebuild();
    }

    private bool IsShown(CalendarInfo list) =>
        _solo.Shows(list, AllLists.FirstOrDefault(l => Same(l.Info, list)) is not { IsVisible: false });

    private static bool Same(CalendarInfo a, CalendarInfo b) => a.ConnectionId == b.ConnectionId && a.RemoteId == b.RemoteId;

    /// <summary>Filters, searches and groups by due date: overdue, today, tomorrow, this week, later, no date – or done.</summary>
    private void Rebuild()
    {
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var filter = (TaskFilter)FilterIndex;
        var query = Search.Trim();
        var colors = ListGroups.SelectMany(g => g.Lists).ToDictionary(l => (l.Info.ConnectionId, l.Info.RemoteId), l => l.Color);
        var shown = _all.Where(t => IsShown(t.List))
            .Where(t => filter switch
            {
                TaskFilter.Open => !t.IsCompleted,
                TaskFilter.Due => !t.IsCompleted && t.Due is { } due && due.LocalDateTime.Date <= today,
                TaskFilter.Completed => t.IsCompleted,
                _ => true,
            })
            .Where(t => query.Length == 0 || t.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                          || (t.Notes?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .Select(t => NewRow(t, colors.GetValueOrDefault((t.List.ConnectionId, t.List.RemoteId)) ?? CalendarViewModel.Palette[0], now))
            .ToList();

        Groups.Clear();
        void Add(string title, IEnumerable<TaskRow> rows, bool warn = false)
        {
            var list = rows.ToList();
            if (list.Count > 0)
            {
                Groups.Add(new TaskGroup(title, list, warn));
            }
        }

        var open = shown.Where(r => !r.Item.IsCompleted).OrderBy(r => r.Item.Due ?? DateTimeOffset.MaxValue).ThenByDescending(r => r.Item.Priority).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        DateTime? Day(TaskRow r) => r.Item.Due?.LocalDateTime.Date;
        Add(T("Überfällig"), open.Where(r => r.IsOverdue), warn: true);
        Add(T("Heute"), open.Where(r => !r.IsOverdue && Day(r) == today));
        Add(T("Morgen"), open.Where(r => Day(r) == today.AddDays(1)));
        Add(T("Nächste 7 Tage"), open.Where(r => Day(r) > today.AddDays(1) && Day(r) <= today.AddDays(7)));
        Add(T("Später"), open.Where(r => Day(r) > today.AddDays(7)));
        Add(T("Ohne Datum"), open.Where(r => Day(r) is null));
        Add(T("Erledigt"), shown.Where(r => r.Item.IsCompleted).OrderByDescending(r => r.Item.CompletedAt ?? DateTimeOffset.MinValue).Take(300));

        // The task read before stays in the pane (as it is now), also when the filter no longer lists it.
        if (SelectedTask is { } selected)
        {
            SelectedTask = shown.FirstOrDefault(r => SameTask(r.Item, selected.Item))
                ?? (_all.FirstOrDefault(t => SameTask(t, selected.Item)) is { } still ? NewRow(still, selected.Color, now) : null);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        foreach (var list in ListGroups.SelectMany(g => g.Lists))
        {
            list.OpenCount = _all.Count(t => !t.IsCompleted && Same(t.List, list.Info));
        }
    }

    private TaskRow NewRow(TaskItem task, string color, DateTimeOffset now) =>
        new(task, color, now, ToggleAsync, EditAsync, row => SelectedTask = row, DeleteAsync);

    private static bool SameTask(TaskItem a, TaskItem b) => Same(a.List, b.List) && a.ObjectRemoteId == b.ObjectRemoteId;

    private async Task ToggleAsync(TaskRow row, bool completed)
    {
        try
        {
            await tasks.SetCompletedAsync(row.Item, completed);
            await ReloadAsync();
            StatusMessage?.Invoke(this, completed ? F("«{0}» erledigt", row.Title) : F("«{0}» wieder offen", row.Title));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task {Uid} could not be saved", row.Item.Uid);
            StatusMessage?.Invoke(this, F("Aufgabe konnte nicht gespeichert werden: {0}", ex.Message));
            await ReloadAsync();
        }
    }

    private async Task DeleteAsync(TaskRow row)
    {
        if (!await Infrastructure.DeleteConfirmation.AskAsync(Infrastructure.DeleteKind.Task, F("«{0}» löschen", row.Title),
                T("Die Aufgabe wird auch auf dem Server gelöscht und lässt sich nicht wiederherstellen.")))
        {
            return;
        }

        try
        {
            await tasks.DeleteAsync(row.Item);
            if (SelectedTask == row)
            {
                SelectedTask = null;
            }

            await ReloadAsync();
            StatusMessage?.Invoke(this, F("«{0}» gelöscht", row.Title));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task {Uid} could not be deleted", row.Item.Uid);
            StatusMessage?.Invoke(this, F("Aufgabe konnte nicht gelöscht werden: {0}", ex.Message));
            await ReloadAsync();
        }
    }

    private Task EditAsync(TaskRow row)
    {
        ShowEditor(TaskDraft.FromItem(row.Item), row.Item);
        return Task.CompletedTask;
    }

    /// <summary>A task shown in the calendar (or found elsewhere) opens here.</summary>
    public async Task OpenAsync(CalendarInfo list, string objectRemoteId)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (_all.Count == 0)
        {
            await ReloadAsync();
        }

        if (_all.FirstOrDefault(t => Same(t.List, list) && t.ObjectRemoteId == objectRemoteId) is { } task)
        {
            var color = ListGroups.SelectMany(g => g.Lists).FirstOrDefault(l => Same(l.Info, list))?.Color ?? CalendarViewModel.Palette[0];
            SelectedTask = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => SameTask(r.Item, task)) ?? NewRow(task, color, DateTimeOffset.Now);
        }
    }

    /// <summary>The lists a new task can go into (beside the quick-add box), with their account.</summary>
    public ObservableCollection<TaskTarget> Targets { get; } = [];

    // While the choices are refilled, the selection is not a choice of the user's.
    private bool _refreshingTargets;

    /// <summary>Where new tasks go (quick add and "Neue Aufgabe"); kept as the default list.</summary>
    [ObservableProperty]
    public partial TaskTarget? Target { get; set; }

    partial void OnTargetChanged(TaskTarget? value)
    {
        if (!_refreshingTargets && value is not null)
        {
            _ = settings.SetAsync(SettingKeys.TasksDefaultList, CalendarController.CalendarKey(value.Info));
        }
    }

    private IReadOnlyDictionary<Guid, string> _accounts = new Dictionary<Guid, string>();

    // Only lists shown on the left: a new task never lands where it cannot be seen (all, when none is shown).
    private async Task RefreshTargetsAsync()
    {
        _refreshingTargets = true;
        try
        {
            var chosen = await DefaultListAsync(fromChoice: false);
            var writable = AllLists.Where(l => !l.Info.IsReadOnly).ToList();
            var shown = writable.Where(l => IsShown(l.Info)).ToList();
            Targets.Clear();
            foreach (var item in shown.Count > 0 ? shown : writable)
            {
                Targets.Add(new TaskTarget(item.Info, item.Brush, _accounts.GetValueOrDefault(item.Info.ConnectionId) ?? string.Empty));
            }

            Target = Targets.FirstOrDefault(t => chosen is not null && Same(t.Info, chosen)) ?? Targets.FirstOrDefault();
        }
        finally
        {
            _refreshingTargets = false;
        }
    }

    private IReadOnlyList<CalendarInfo> WritableLists() =>
        [.. ListGroups.SelectMany(g => g.Lists).Select(l => l.Info).Where(l => !l.IsReadOnly)];

    private async Task<CalendarInfo?> DefaultListAsync(bool fromChoice = true)
    {
        if (fromChoice && Target is { } target)
        {
            return target.Info;
        }

        var writable = WritableLists();
        var chosen = await settings.GetAsync(SettingKeys.TasksDefaultList);
        return writable.FirstOrDefault(l => CalendarController.CalendarKey(l) == chosen && IsShown(l))
               ?? writable.FirstOrDefault(IsShown)
               ?? writable.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanQuickAdd))]
    private async Task QuickAddAsync()
    {
        if (await DefaultListAsync() is not { } list)
        {
            StatusMessage?.Invoke(this, T("Keine beschreibbare Aufgabenliste vorhanden."));
            return;
        }

        var title = NewTitle.Trim();
        NewTitle = string.Empty;
        try
        {
            await tasks.SaveAsync(list, TaskDraft.New(title));
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Task could not be created");
            StatusMessage?.Invoke(this, F("Aufgabe konnte nicht gespeichert werden: {0}", ex.Message));
            NewTitle = title;
        }
    }

    private bool CanQuickAdd() => NewTitle.Trim().Length > 0;

    [RelayCommand]
    private async Task NewTaskAsync()
    {
        if (await DefaultListAsync() is null)
        {
            StatusMessage?.Invoke(this, T("Keine beschreibbare Aufgabenliste vorhanden."));
            return;
        }

        ShowEditor(TaskDraft.New(NewTitle.Trim()), null);
        NewTitle = string.Empty;
    }

    private async void ShowEditor(TaskDraft draft, TaskItem? existing)
    {
        var lists = WritableLists();
        if (existing is not null && lists.All(l => !Same(l, existing.List)))
        {
            lists = [existing.List, .. lists]; // read-only list: shown, but not saved
        }

        var target = existing?.List ?? await DefaultListAsync() ?? lists.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        var editor = new TaskEditorViewModel(tasks, calendar, settings, lists, draft, existing, target, await categories.GetAllAsync());
        EditorRequested?.Invoke(this, editor);
    }
}

/// <summary>A list new tasks can go into, for the choice beside the quick-add box.</summary>
internal sealed record TaskTarget(CalendarInfo Info, Avalonia.Media.IBrush Brush, string Account)
{
    public string Name => Info.Name;

    public override string ToString() => Info.Name;
}

/// <summary>The task lists of one account in the side pane.</summary>
internal sealed class TaskListGroup(string account, IReadOnlyList<TaskListItem> lists)
{
    public string Account { get; } = account;

    public IReadOnlyList<TaskListItem> Lists { get; } = lists;
}

internal sealed partial class TaskListItem(CalendarInfo info, string color, int openCount) : CollectionListItem(info, color)
{
    /// <summary>"mit Terminen" for mixed CalDAV calendars.</summary>
    public string? Kind => Info.HasEvents ? T("mit Terminen") : null;

    [ObservableProperty]
    public partial int OpenCount { get; set; } = openCount;

    public override string SoloTip => IsSolo ? T("Wieder alle ausgewählten Listen anzeigen") : T("Nur diese Liste anzeigen");
}

internal sealed class TaskGroup(string title, IReadOnlyList<TaskRow> rows, bool isWarning)
{
    public string Title { get; } = title;

    public IReadOnlyList<TaskRow> Rows { get; } = rows;

    public bool IsWarning { get; } = isWarning;

    public string CountText => Rows.Count.ToString(Culture);
}

internal sealed partial class TaskRow : ObservableObject
{
    private readonly Func<TaskRow, bool, Task> _toggle;
    private readonly Func<TaskRow, Task> _edit;
    private readonly Action<TaskRow> _select;
    private readonly Func<TaskRow, Task> _delete;
    private bool _ready;

    public TaskRow(TaskItem item, string color, DateTimeOffset now, Func<TaskRow, bool, Task> toggle, Func<TaskRow, Task> edit, Action<TaskRow> select, Func<TaskRow, Task> delete)
    {
        _delete = delete;
        Item = item;
        Color = color;
        _select = select;
        Brush = Avalonia.Media.Brush.Parse(color);
        IsOverdue = item.IsOverdue(now);
        _toggle = toggle;
        _edit = edit;
        IsCompleted = item.IsCompleted;
        _ready = true;
    }

    public TaskItem Item { get; }

    public string Color { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    // ---- The reading pane ----

    public string? Notes => string.IsNullOrWhiteSpace(Item.Notes) ? null : Item.Notes.Trim();

    public string? DueLongText => Item.Due is { } due
        ? Item.DueHasTime ? due.LocalDateTime.ToString("dddd, d. MMMM yyyy, HH:mm", Culture) : due.LocalDateTime.ToString("dddd, d. MMMM yyyy", Culture)
        : null;

    public bool HasDue => Item.Due is not null;

    public string? PriorityText => Item.Priority switch
    {
        TaskPriority.High => T("Hoch"),
        TaskPriority.Normal => T("Normal"),
        TaskPriority.Low => T("Niedrig"),
        _ => null,
    };

    public bool HasPriority => PriorityText is not null;

    private TaskDetails Details => Item.Details;

    public string? StartText => Details.Start?.ToString("dddd, d. MMMM yyyy", Culture);

    public bool HasStart => StartText is not null;

    public string? ReminderText => Details.ReminderAt?.ToString("dddd, d. MMMM yyyy, HH:mm", Culture);

    public bool HasReminder => ReminderText is not null;

    /// <summary>"Wöchentlich am Mo bis …"; for a rule Neruna cannot show in detail: "Wiederholt sich".</summary>
    public string? RepeatsText => Details.Recurrence == RecurrenceKind.None
        ? null
        : Details.Repeat is { } spec && Details.Recurrence != RecurrenceKind.Custom
            ? spec.Describe(TaskDetails.SeriesStart(Details.Start, Item.Due?.LocalDateTime), Details.RecurrenceUntil)
            : T("Wiederholt sich");

    public bool HasRepeats => RepeatsText is not null;

    /// <summary>"In Bearbeitung" or "Abgebrochen"; nothing for open and done tasks.</summary>
    public string? StateText => Item.IsCompleted ? null : Details.State switch
    {
        TaskState.InProgress => T("In Bearbeitung"),
        TaskState.Cancelled => T("Abgebrochen"),
        _ => null,
    };

    public bool HasState => StateText is not null;

    /// <summary>The state chip in the list: "In Bearbeitung · 40 %".</summary>
    public string? StateLabel => StateText is null ? null : HasPercent ? $"{StateText} · {Percent} %" : StateText;

    public bool IsCancelledState => !Item.IsCompleted && Details.State == TaskState.Cancelled;

    /// <summary>How far along (0–100); the row is filled that far.</summary>
    public int Percent => Item.IsCompleted ? 0 : Details.Percent;

    public bool HasPercent => Percent > 0;

    public string? PercentText => HasPercent ? F("{0} % erledigt", Percent) : null;

    public string? Location => Details.Location;

    public bool HasLocation => Location is not null;

    public string? PrivacyText => Details.Privacy switch
    {
        EventPrivacy.Private => T("Privat"),
        EventPrivacy.Confidential => T("Vertraulich"),
        _ => null,
    };

    public bool HasPrivacy => PrivacyText is not null;

    public string? Url => Details.Url;

    public bool HasUrl => Url is not null;

    public string? Links => Details.Links.Count > 0 ? string.Join("\n", Details.Links.Select(l => l.OriginalString)) : null;

    public bool HasLinks => Links is not null;

    public string? CategoriesText => Details.Categories.Count > 0 ? string.Join(", ", Details.Categories) : null;

    public bool HasCategories => CategoriesText is not null;

    public string? CompletedText => Item.IsCompleted
        ? Item.CompletedAt is { } at ? F("Erledigt am {0}", at.LocalDateTime.ToString("d. MMMM yyyy", Culture)) : T("Erledigt")
        : null;

    public bool IsReadOnly => Item.List.IsReadOnly;

    public bool CanEdit => !IsReadOnly;

    public string ToggleText => IsCompleted ? T("Wieder öffnen") : T("Als erledigt markieren");

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private void Toggle() => IsCompleted = !IsCompleted;

    public IBrush Brush { get; }

    public string Title => Item.Summary.Length > 0 ? Item.Summary : T("(ohne Titel)");

    public bool IsOverdue { get; }

    public bool IsHigh => Item.Priority == TaskPriority.High;

    public bool HasNotes => Notes is not null;

    /// <summary>The first line of the notes, under the title (as the preview of a mail).</summary>
    public string? NotePreview => Notes?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    public string ListName => Item.List.Name;

    public string? DueText => Item.Due is { } due
        ? Item.DueHasTime ? due.LocalDateTime.ToString("ddd d. MMM, HH:mm", Culture) : due.LocalDateTime.ToString("ddd d. MMM", Culture)
        : null;

    public TextDecorationCollection? Decorations => IsCompleted ? TextDecorations.Strikethrough : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Decorations), nameof(ToggleText))]
    public partial bool IsCompleted { get; set; }

    partial void OnIsCompletedChanged(bool value)
    {
        if (_ready)
        {
            _ = _toggle(this, value);
        }
    }

    [RelayCommand]
    private Task EditAsync() => _edit(this);

    [RelayCommand]
    private Task DeleteAsync() => _delete(this);
}
