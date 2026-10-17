using Avalonia.Input;

namespace Neruna.Desktop.Infrastructure;

/// <summary>Key combinations as quick actions store them ("Ctrl+M", "Ctrl+Shift+D1", "F7").</summary>
internal static class Shortcuts
{
    /// <summary>
    /// The combination of a key press, or null for what is no shortcut: a modifier alone, or a plain key without Ctrl
    /// or Alt (that is typing) – except the function keys F2–F12.
    /// </summary>
    public static string? Of(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.None or Key.System)
        {
            return null;
        }

        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta);
        var functionKey = e.Key is >= Key.F2 and <= Key.F12;
        if (!functionKey && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
        {
            return null;
        }

        return new KeyGesture(e.Key, modifiers).ToString();
    }
}
