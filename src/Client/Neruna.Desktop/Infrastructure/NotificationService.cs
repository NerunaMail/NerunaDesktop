using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Neruna.Desktop.ViewModels;
using Neruna.Desktop.Views;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Shows desktop notifications stacked in the bottom right corner of the screen Neruna is on.</summary>
internal sealed class NotificationService
{
    private const int MaxVisible = 3;
    private const double Margin = 12;
    private readonly List<NotificationWindow> _open = [];

    /// <summary>The notifications on screen, oldest first.</summary>
    public IReadOnlyList<NotificationWindow> Visible => _open;

    /// <summary>True while the user works in Neruna – then the list itself shows what is new.</summary>
    public static bool IsAppActive => MainWindow is { IsActive: true, WindowState: not WindowState.Minimized };

    private static Window? MainWindow => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public void Show(NotificationViewModel notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        while (_open.Count >= MaxVisible)
        {
            _open[0].Close();
        }

        var window = new NotificationWindow { DataContext = notification };
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Arrange();
        };
        window.SizeChanged += (_, _) => Arrange();
        window.Opened += (_, _) => Arrange();
        _open.Add(window);
        window.Show();
        Arrange();
    }

    /// <summary>Brings Neruna to the front (after a notification was clicked).</summary>
    public static void ActivateMainWindow()
    {
        if (MainWindow is not { } main)
        {
            return;
        }

        if (main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }

        main.Show();
        main.Activate();
    }

    // Newest at the bottom, older ones above it.
    private void Arrange()
    {
        var screens = MainWindow?.Screens ?? _open.FirstOrDefault()?.Screens;
        if (screens is null || _open.Count == 0)
        {
            return;
        }

        var screen = (MainWindow is { } main ? screens.ScreenFromWindow(main) : null) ?? screens.Primary ?? screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var bottom = area.Bottom - (int)(Margin * scale);
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var window = _open[i];
            var height = (int)(Math.Max(window.Bounds.Height, window.DesiredSize.Height) * scale);
            var width = (int)(window.Width * scale);
            bottom -= height;
            window.Position = new PixelPoint(area.Right - width - (int)(Margin * scale), bottom);
            bottom -= (int)(8 * scale);
        }
    }
}
