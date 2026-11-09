using Avalonia.Controls;
using Neruna.Desktop.Infrastructure;
using Neruna.Desktop.ViewModels;

namespace Neruna.Desktop.Views;

/// <summary>The calendar in a window of its own (see <see cref="MainWindowViewModel.IsCalendarDetached"/>).</summary>
internal sealed partial class CalendarWindow : Window
{
    public CalendarWindow() => InitializeComponent();

    /// <summary>The calendar window while it is open.</summary>
    internal static CalendarWindow? Current { get; private set; }

    /// <summary>Opens, brings forward and closes the calendar window as the main window asks.</summary>
    public static void Attach(MainWindowViewModel main, UiLayout layout, Window owner)
    {
        main.CalendarWindowRequested += (_, _) =>
        {
            if (Current is { } open)
            {
                if (open.WindowState == WindowState.Minimized)
                {
                    open.WindowState = WindowState.Normal;
                }

                open.Activate();
                return;
            }

            // "Neruna – Kalender" (with another profile: its name too, as in the main window).
            var window = new CalendarWindow { DataContext = main, Title = $"{owner.Title} – {Neruna.Core.Localization.Texts.T("Kalender")}" };
            Current = window;
            layout.TrackWindow(window, "window.calendar", withPosition: true);
            main.IsCalendarWindowActive = () => window.IsActive;
            var byUser = false;
            window.Closing += (_, e) => byUser = e.CloseReason == WindowCloseReason.WindowClosing && !e.Cancel;
            window.Closed += async (_, _) =>
            {
                Current = null;
                main.IsCalendarWindowActive = null;
                await main.CalendarWindowClosedAsync(byUser);
            };

            // Not owned by the main window: it may lie behind it, on another screen.
            window.Show();
        };
        main.CalendarWindowCloseRequested += (_, _) => Current?.Close();
    }
}
