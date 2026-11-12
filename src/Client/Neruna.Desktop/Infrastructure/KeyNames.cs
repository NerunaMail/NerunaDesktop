using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop;

/// <summary>
/// Key names in menus («Löschen   Entf») in the language shown, as on the toolbar tooltips, instead of the English
/// names Avalonia prints («Delete»). Ctrl, Shift and Alt stay as printed on most keyboards.
/// </summary>
internal static class KeyNames
{
    private static bool _registered;

    public static void Use()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) =>
        {
            if (item.InputGesture is { } gesture && Name(gesture.Key) is { } key
                && e.NameScope.Find<TextBlock>("PART_InputGestureText") is { } text)
            {
                text.Text = Modifiers(gesture.KeyModifiers) + key;
            }
        });
    }

    /// <summary>The key's name in the language shown, for keys whose English name differs on the keyboard.</summary>
    public static string? Name(Key key) => key switch
    {
        Key.Delete => T("Entf"),
        Key.Back => T("Rücktaste"),
        _ => null,
    };

    private static string Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Control) ? "Ctrl+" : "")
        + (modifiers.HasFlag(KeyModifiers.Shift) ? "Shift+" : "")
        + (modifiers.HasFlag(KeyModifiers.Alt) ? "Alt+" : "");
}
