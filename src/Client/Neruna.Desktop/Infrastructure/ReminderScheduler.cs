using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Neruna.Core.Calendar;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Checks every 30 seconds which reminders are due and shows the reminder window when a new one comes up. Closing the
/// window keeps the reminders; it returns with the next one that becomes due.
/// </summary>
internal sealed class ReminderScheduler(ReminderService reminders, UiLayout layout, TimeProvider clock, ILogger<ReminderScheduler> logger)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly HashSet<string> _announced = new(StringComparer.Ordinal);
    private DispatcherTimer? _timer;
    private ReminderWindowViewModel? _model;
    private ReminderWindow? _window;
    private Func<CalendarOccurrence, Task> _open = _ => Task.CompletedTask;
    private DateOnly _cleanedUp;

    /// <summary>The window currently shown (null when closed).</summary>
    public ReminderWindow? Window => _window;

    /// <param name="open">Opens the event (the calendar's editor).</param>
    public void Start(Func<CalendarOccurrence, Task> open)
    {
        _open = open;
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    /// <summary>Checks now (after a sync brought new or changed events).</summary>
    public async Task CheckAsync()
    {
        try
        {
            var due = await reminders.GetDueAsync();
            var model = _model ??= CreateModel();
            model.Show(due);

            // Snoozed reminders coming back count as new again.
            _announced.IntersectWith(due.Select(r => r.Key + "|" + r.DueAt.ToUnixTimeSeconds()));
            var fresh = due.Where(r => _announced.Add(r.Key + "|" + r.DueAt.ToUnixTimeSeconds())).ToList();
            if (fresh.Count > 0 && _window is null)
            {
                ShowWindow(model);
            }

            if (DateOnly.FromDateTime(clock.GetLocalNow().DateTime) is var today && today != _cleanedUp)
            {
                _cleanedUp = today;
                await reminders.CleanUpAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Checking reminders failed");
        }
    }

    private ReminderWindowViewModel CreateModel()
    {
        var model = new ReminderWindowViewModel(
            r => reminders.DismissAsync(r),
            (r, until) => reminders.SnoozeAsync(r, until),
            r => _open(r.Occurrence),
            clock);
        model.Emptied += (_, _) => _window?.Close();
        return model;
    }

    private void ShowWindow(ReminderWindowViewModel model)
    {
        var window = new ReminderWindow { DataContext = model };
        layout.TrackWindow(window, "window.reminders", withPosition: true);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
    }
}
