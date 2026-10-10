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
internal sealed partial class TasksViewModel(CalendarController calendar, ISettingsStore settings, ILogger<TasksViewModel> logger) : ViewModelBase
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
            var lists = (await calendar.GetCalendarsAsync()).Where(c => c.HasTasks).ToList();
            var hidden = await calendar.GetHiddenTaskListsAsync();
            var inCalendar = await calendar.GetTaskListsInCalendarAsync();
            var colors = await CalendarViewModel.ColorsAsync(settings, await calendar.GetCalendarsAsync());
            var accounts = (await calendar.GetSourcesAsync()).ToDictionary(s => s.Connection.Id, s => s.Account.Title);
            _all = await Task.Run(() => calendar.GetTasksAsync());

            ListGroups.Clear();
            foreach (var group in lists.GroupBy(l => l.ConnectionId))
            {
                var items = group.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(l => new TaskListItem(l, colors.GetValueOrDefault((l.ConnectionId, l.RemoteId)) ?? CalendarViewModel.Palette[0], _all.Count(t => !t.IsCompleted && Same(t.List, l)))
                    {
                        IsVisible = !hidden.Contains(CalendarController.TaskListKey(l)),
                        InCalendar = inCalendar.Contains(CalendarController.TaskListKey(l)),
                    })
                    .ToList();
                foreach (var item in items)
                {
                    item.PropertyChanged += async (_, e) =>
                    {
                        if (e.PropertyName == nameof(TaskListItem.IsVisible))
                        {
                            await calendar.SetTaskListVisibleAsync(item.Info, item.IsVisible);
                            Rebuild();
                        }
                        else if (e.PropertyName == nameof(TaskListItem.InCalendar))
                        {
                            await calendar.SetTaskListInCalendarAsync(item.Info, item.InCalendar);
                            CalendarChanged?.Invoke(this, EventArgs.Empty);
                        }
                    };
                }

                ListGroups.Add(new TaskListGroup(accounts.GetValueOrDefault(group.Key) ?? string.Empty, items));
            }

            OnPropertyChanged(nameof(HasLists));
            Rebuild();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tasks could not be loaded");
            StatusMessage?.Invoke(this, T("Aufgaben konnten nicht geladen werden"));
        }
    }

    /// <summary>Gives a list an own display name (null: the server's again) – the same name as in the calendar.</summary>
    public async Task RenameAsync(TaskListItem item, string? name)
    {
        ArgumentNullException.ThrowIfNull(item);
        await calendar.SetDisplayNameAsync(item.Info, name);
        await ReloadAsync();
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets a list's colour (null: the server's again) – the same colour as in the calendar.</summary>
    public async Task SetColorAsync(TaskListItem item, string? color)
    {
        ArgumentNullException.ThrowIfNull(item);
        await settings.SetAsync(CalendarViewModel.ColorKey(item.Info), color);
        await ReloadAsync();
        CalendarChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsShown(CalendarInfo list) =>
        ListGroups.SelectMany(g => g.Lists).FirstOrDefault(l => Same(l.Info, list)) is not { IsVisible: false };

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
            .Select(t => new TaskRow(t, colors.GetValueOrDefault((t.List.ConnectionId, t.List.RemoteId)) ?? CalendarViewModel.Palette[0], now, ToggleAsync, EditAsync))
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

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        foreach (var list in ListGroups.SelectMany(g => g.Lists))
        {
            list.OpenCount = _all.Count(t => !t.IsCompleted && Same(t.List, list.Info));
        }
    }

    private async Task ToggleAsync(TaskRow row, bool completed)
    {
        try
        {
            await calendar.SetTaskCompletedAsync(row.Item, completed);
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
            ShowEditor(TaskDraft.FromItem(task), task);
        }
    }

    private IReadOnlyList<CalendarInfo> WritableLists() =>
        [.. ListGroups.SelectMany(g => g.Lists).Select(l => l.Info).Where(l => !l.IsReadOnly)];

    private async Task<CalendarInfo?> DefaultListAsync()
    {
        var writable = WritableLists();
        var chosen = await settings.GetAsync(SettingKeys.TasksDefaultList);
        return writable.FirstOrDefault(l => CalendarController.TaskListKey(l) == chosen)
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
            await calendar.SaveTaskAsync(list, TaskDraft.New(title));
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

        var editor = new TaskEditorViewModel(calendar, settings, lists, draft, existing, target);
        EditorRequested?.Invoke(this, editor);
    }
}

/// <summary>The task lists of one account in the side pane.</summary>
internal sealed class TaskListGroup(string account, IReadOnlyList<TaskListItem> lists)
{
    public string Account { get; } = account;

    public IReadOnlyList<TaskListItem> Lists { get; } = lists;
}

internal sealed partial class TaskListItem(CalendarInfo info, string color, int openCount) : ObservableObject
{
    public CalendarInfo Info { get; } = info;

    public string Color { get; } = color;

    public IBrush Brush { get; } = Avalonia.Media.Brush.Parse(color);

    public string Name => Info.Name;

    /// <summary>The name being edited in the colour/name flyout.</summary>
    [ObservableProperty]
    public partial string EditName { get; set; } = info.Name;

    public string? ServerNameText => Info.ServerName is { } server ? T("Auf dem Server: ") + server : null;

    public bool IsRenamed => Info.ServerName is not null;

    /// <summary>"Kalender + Aufgaben" for mixed CalDAV calendars.</summary>
    public string? Kind => Info.HasEvents ? T("mit Terminen") : null;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial int OpenCount { get; set; } = openCount;

    /// <summary>"Aufgaben im Kalender anzeigen" for this list (default off).</summary>
    [ObservableProperty]
    public partial bool InCalendar { get; set; }
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
    private bool _ready;

    public TaskRow(TaskItem item, string color, DateTimeOffset now, Func<TaskRow, bool, Task> toggle, Func<TaskRow, Task> edit)
    {
        Item = item;
        Brush = Avalonia.Media.Brush.Parse(color);
        IsOverdue = item.IsOverdue(now);
        _toggle = toggle;
        _edit = edit;
        IsCompleted = item.IsCompleted;
        _ready = true;
    }

    public TaskItem Item { get; }

    public IBrush Brush { get; }

    public string Title => Item.Summary.Length > 0 ? Item.Summary : T("(ohne Titel)");

    public bool IsOverdue { get; }

    public bool IsHigh => Item.Priority == TaskPriority.High;

    public bool HasNotes => Item.Notes is not null;

    public string ListName => Item.List.Name;

    public string? DueText => Item.Due is { } due
        ? Item.DueHasTime ? due.LocalDateTime.ToString("ddd d. MMM, HH:mm", Culture) : due.LocalDateTime.ToString("ddd d. MMM", Culture)
        : null;

    public TextDecorationCollection? Decorations => IsCompleted ? TextDecorations.Strikethrough : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Decorations))]
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
}
