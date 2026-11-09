using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// The calendar in a window of its own («Eigenes Fenster»), e.g. on the second screen while the mails stay in the main
/// window. Same page, same data – only shown elsewhere; the calendar button in the main window brings that window to
/// the front. Dialogs opened from the calendar window appear there. Closing the window puts the calendar back; whether
/// it was on its own is remembered for the next start.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private const string CalendarDetachedKey = "calendar.detached";

    private ViewModelBase? _pageBeforeCalendar;

    /// <summary>Open the calendar window, or bring it to the front.</summary>
    public event EventHandler? CalendarWindowRequested;

    /// <summary>Close it (the calendar goes back into the main window).</summary>
    public event EventHandler? CalendarWindowCloseRequested;

    [ObservableProperty]
    public partial bool IsCalendarDetached { get; set; }

    /// <summary>The dialog shown right now belongs to the calendar window (it was active when it opened).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay), nameof(HasCalendarOverlay))]
    public partial bool OverlayInCalendarWindow { get; set; }

    public bool HasCalendarOverlay => Overlay is not null && OverlayInCalendarWindow;

    /// <summary>Set by the calendar window: whether it is the active one.</summary>
    public Func<bool>? IsCalendarWindowActive { get; set; }

    private async Task ToggleCalendarWindowAsync()
    {
        if (IsCalendarDetached)
        {
            CalendarWindowCloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        await DetachCalendarAsync(remember: true);
    }

    private async Task DetachCalendarAsync(bool remember)
    {
        IsCalendarDetached = true;
        CalendarPage.IsDetached = true;
        if (CurrentPage == CalendarPage)
        {
            CurrentPage = _pageBeforeCalendar ?? MailPage;
        }

        CalendarWindowRequested?.Invoke(this, EventArgs.Empty);
        if (remember)
        {
            await _settings.SetBoolAsync(CalendarDetachedKey, true);
        }
    }

    /// <summary>From the calendar window when it closed; <paramref name="byUser"/>: not because Neruna is quitting.</summary>
    public async Task CalendarWindowClosedAsync(bool byUser)
    {
        IsCalendarDetached = false;
        CalendarPage.IsDetached = false;
        OverlayInCalendarWindow = false;
        if (byUser)
        {
            await _settings.SetBoolAsync(CalendarDetachedKey, false);
            CurrentPage = CalendarPage;
        }
    }

    /// <summary>At the start: the calendar in its window again, if it was there when Neruna was closed.</summary>
    private async Task RestoreCalendarWindowAsync()
    {
        if (await _settings.GetBoolAsync(CalendarDetachedKey))
        {
            await DetachCalendarAsync(remember: false);
        }
    }

    // The calendar is in its own window: going to it (button, reminder, invitation …) brings that window forward
    // and the main window stays where it was.
    private void FollowCalendarWindow(ViewModelBase page)
    {
        if (page != CalendarPage)
        {
            _pageBeforeCalendar = page;
            return;
        }

        if (IsCalendarDetached)
        {
            Dispatcher.UIThread.Post(() =>
            {
                CurrentPage = _pageBeforeCalendar ?? MailPage;
                CalendarWindowRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }
}
