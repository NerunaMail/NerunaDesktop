using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core;
using Neruna.Core.Calendar;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>Creates or edits a task: title, notes, due date (optionally with a time), priority, list, done.</summary>
internal sealed partial class TaskEditorViewModel : ViewModelBase
{
    private readonly CalendarController _calendar;
    private readonly ISettingsStore _settings;
    private readonly TaskItem? _existing;

    public TaskEditorViewModel(CalendarController calendar, ISettingsStore settings, IReadOnlyList<CalendarInfo> lists, TaskDraft draft, TaskItem? existing, CalendarInfo target)
    {
        _calendar = calendar;
        _settings = settings;
        _existing = existing;
        Lists = lists;
        SelectedList = lists.FirstOrDefault(l => l.ConnectionId == target.ConnectionId && l.RemoteId == target.RemoteId) ?? lists.FirstOrDefault();
        Summary = draft.Summary;
        Notes = draft.Notes ?? string.Empty;
        HasDue = draft.Due is not null;
        DueDate = draft.Due?.Date ?? DateTime.Today;
        HasTime = draft.Due is not null && draft.DueHasTime;
        DueTime = draft.Due is { } due && draft.DueHasTime ? due.TimeOfDay : new TimeSpan(9, 0, 0);
        PriorityIndex = (int)draft.Priority;
        IsCompleted = draft.IsCompleted;
    }

    public event EventHandler<bool>? Finished;

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
    public partial bool IsCompleted { get; set; }

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
        var draft = new TaskDraft(Summary, Notes, due, HasDue && HasTime, (TaskPriority)Math.Clamp(PriorityIndex, 0, 3), IsCompleted);
        await RunAsync(async () =>
        {
            await _calendar.SaveTaskAsync(SelectedList!, draft, _existing);
            // New tasks go where the last one went.
            await _settings.SetAsync(SettingKeys.TasksDefaultList, CalendarController.CalendarKey(SelectedList!));
        });
    }

    private bool CanDelete() => !IsBusy && IsExisting && CanEdit;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task DeleteAsync() => RunAsync(() => _calendar.DeleteTaskAsync(_existing!));

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
