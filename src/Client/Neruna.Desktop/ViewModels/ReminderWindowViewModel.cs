using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Calendar;

namespace Neruna.Desktop.ViewModels;

/// <summary>When "Erneut erinnern" shows the reminder again.</summary>
internal sealed record SnoozeOption(string Label, Func<Reminder, DateTimeOffset, DateTimeOffset?> Until)
{
    /// <summary>Relative choices always; "… vor Beginn" only while the event is still ahead.</summary>
    public static IReadOnlyList<SnoozeOption> For(Reminder? reminder, DateTimeOffset now)
    {
        var options = new List<SnoozeOption>
        {
            new("5 Minuten", (_, n) => n.AddMinutes(5)),
            new("10 Minuten", (_, n) => n.AddMinutes(10)),
            new("15 Minuten", (_, n) => n.AddMinutes(15)),
            new("30 Minuten", (_, n) => n.AddMinutes(30)),
            new("1 Stunde", (_, n) => n.AddHours(1)),
            new("2 Stunden", (_, n) => n.AddHours(2)),
            new("4 Stunden", (_, n) => n.AddHours(4)),
            new("1 Tag", (_, n) => n.AddDays(1)),
        };

        if (reminder is { } r && r.Occurrence.Start > now.AddMinutes(5) && !r.Occurrence.IsAllDay)
        {
            options.Insert(0, new("5 Minuten vor Beginn", (x, _) => x.Occurrence.Start.AddMinutes(-5)));
            options.Insert(1, new("Bei Beginn", (x, _) => x.Occurrence.Start));
        }

        return options;
    }
}

/// <summary>One line in the reminder window.</summary>
internal sealed class ReminderItem(Reminder reminder, DateTimeOffset now)
{
    private static CultureInfo Culture => Neruna.Core.Localization.Texts.Culture;

    public Reminder Reminder { get; } = reminder;

    public string Title => Reminder.Occurrence.Summary;

    public string Color => Reminder.Occurrence.Calendar.Color ?? "#0F6CBD";

    /// <summary>"Heute 09:00–10:00 · Sitzungszimmer Rigi".</summary>
    public string When
    {
        get
        {
            var o = Reminder.Occurrence;
            var day = o.Start.Date == now.LocalDateTime.Date ? "Heute"
                : o.Start.Date == now.LocalDateTime.Date.AddDays(1) ? "Morgen"
                : o.Start.ToString("ddd d. MMM", Culture);
            var time = o.IsAllDay ? "ganztägig" : $"{o.Start:HH:mm}–{o.End:HH:mm}";
            return string.IsNullOrWhiteSpace(o.Location) ? $"{day} {time}" : $"{day} {time} · {o.Location}";
        }
    }

    /// <summary>"in 10 Minuten", "jetzt", "seit 5 Minuten" (relative to the start, like in office calendars).</summary>
    public string Due
    {
        get
        {
            var minutes = (int)Math.Round((Reminder.Occurrence.Start - now).TotalMinutes);
            return minutes switch
            {
                > 1440 => $"in {minutes / 1440} Tag(en)",
                > 90 => $"in {Math.Round(minutes / 60.0)} Stunden",
                > 1 => $"in {minutes} Minuten",
                >= -1 => "jetzt",
                > -90 => $"seit {-minutes} Minuten",
                _ => $"seit {Math.Round(-minutes / 60.0)} Stunden",
            };
        }
    }

    public bool IsOverdue => Reminder.Occurrence.Start < now;
}

/// <summary>
/// The reminder window: due reminders with "Schliessen", "Alle schliessen", "Öffnen" and "Erneut erinnern in …".
/// The scheduler fills it; actions go back through the callbacks.
/// </summary>
internal sealed partial class ReminderWindowViewModel(
    Func<Reminder, Task> dismiss,
    Func<Reminder, DateTimeOffset, Task> snooze,
    Func<Reminder, Task> open,
    TimeProvider clock) : ViewModelBase
{
    public ObservableCollection<ReminderItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(DismissCommand), nameof(SnoozeCommand), nameof(OpenCommand))]
    public partial ReminderItem? Selected { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<SnoozeOption> SnoozeOptions { get; set; } = SnoozeOption.For(null, DateTimeOffset.Now);

    [ObservableProperty]
    public partial SnoozeOption? SnoozeChoice { get; set; }

    public bool HasSelection => Selected is not null;

    public string WindowTitle => Items.Count == 1 ? "1 Erinnerung" : $"{Items.Count} Erinnerungen";

    /// <summary>Raised when nothing is left to show.</summary>
    public event EventHandler? Emptied;

    /// <summary>Replaces the list; keeps the selection where possible.</summary>
    public void Show(IReadOnlyList<Reminder> reminders)
    {
        var now = clock.GetUtcNow();
        var selectedKey = Selected?.Reminder.Key;
        Items.Clear();
        foreach (var reminder in reminders)
        {
            Items.Add(new ReminderItem(reminder, now));
        }

        Selected = Items.FirstOrDefault(i => i.Reminder.Key == selectedKey) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(WindowTitle));
        if (Items.Count == 0)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnSelectedChanged(ReminderItem? value)
    {
        var label = SnoozeChoice?.Label;
        SnoozeOptions = SnoozeOption.For(value?.Reminder, clock.GetUtcNow());
        SnoozeChoice = SnoozeOptions.FirstOrDefault(o => o.Label == label) ?? SnoozeOptions.First(o => o.Label == "5 Minuten");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task DismissAsync() => Remove(Selected!, dismiss(Selected!.Reminder));

    [RelayCommand]
    private async Task DismissAllAsync()
    {
        foreach (var item in Items.ToList())
        {
            await dismiss(item.Reminder);
        }

        Show([]);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SnoozeAsync()
    {
        var item = Selected!;
        var until = (SnoozeChoice ?? SnoozeOptions[0]).Until(item.Reminder, clock.GetUtcNow()) ?? clock.GetUtcNow().AddMinutes(5);
        return Remove(item, snooze(item.Reminder, until));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenAsync() => open(Selected!.Reminder);

    private async Task Remove(ReminderItem item, Task action)
    {
        await action;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        OnPropertyChanged(nameof(WindowTitle));
        if (Items.Count == 0)
        {
            Emptied?.Invoke(this, EventArgs.Empty);
        }
    }
}
