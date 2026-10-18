using Avalonia;
using Avalonia.Controls;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Keeps every window on its screen when it opens: never larger than the working area (without the taskbar), the
/// title bar always reachable. On a small screen the main window (1440 × 880) and a mail opened by double-click
/// (980 × 780) were centred beyond the edges – the title bar above the screen, the bottom behind the taskbar, so the
/// window could neither be moved nor made smaller.
/// </summary>
internal static class ScreenFit
{
    // Title bar and borders, until the window knows its own (FrameSize is set once it is shown).
    private static readonly Size AssumedDecorations = new(16, 40);

    public static void Install() =>
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Fit(window));

    public static void Fit(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.WindowState != WindowState.Normal
            || (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is not { } screen)
        {
            return;
        }

        var area = screen.WorkingArea;
        var scale = window.DesktopScaling;
        var decorations = window.FrameSize is { } frame ? frame - window.ClientSize : AssumedDecorations;
        var maxWidth = Math.Floor(area.Width / scale - decorations.Width);
        var maxHeight = Math.Floor(area.Height / scale - decorations.Height);
        var width = window.ClientSize.Width;
        var height = window.ClientSize.Height;
        if (width > maxWidth)
        {
            window.MinWidth = Math.Min(window.MinWidth, maxWidth);
            window.Width = width = maxWidth;
        }

        if (height > maxHeight)
        {
            window.MinHeight = Math.Min(window.MinHeight, maxHeight);
            window.Height = height = maxHeight;
        }

        // The whole frame inside the working area (top-left first, if it is still larger than the area).
        var size = PixelSize.FromSize(new Size(width + decorations.Width, height + decorations.Height), scale);
        var x = Math.Max(area.X, Math.Min(window.Position.X, area.Right - size.Width));
        var y = Math.Max(area.Y, Math.Min(window.Position.Y, area.Bottom - size.Height));
        if (x != window.Position.X || y != window.Position.Y)
        {
            window.Position = new PixelPoint(x, y);
        }
    }
}
