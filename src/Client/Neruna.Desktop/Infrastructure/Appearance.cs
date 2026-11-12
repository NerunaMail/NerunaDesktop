using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Neruna.Core;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Infrastructure;

internal enum ThemeMode
{
    System,
    Light,
    Dark,
}

internal enum ColorScheme
{
    Blue,
    Red,
    Green,
}

/// <summary>
/// Light/dark mode and color scheme (Einstellungen → Design). Changes the Fluent accent (buttons, selection) and
/// Neruna's own accent brushes (header bar, unread marker, today) for both theme variants, at runtime.
/// </summary>
internal static class Appearance
{
    /// <param name="Selection">
    /// Selected list rows (light, dark), when the accent at the usual opacity is too loud: in red the selection would look
    /// like an error, so it is a light red instead.
    /// </param>
    private sealed record Scheme(Color Accent, Color AccentDark, Color HeaderDark, Color TodayLight, Color TodayDark, (Color Light, Color Dark)? Selection = null);

    // Fluent's brushes for selected (and selected + hovered, pressed) list, tree and grid rows.
    private static readonly string[] SelectionKeys =
        ["SystemControlHighlightListAccentLowBrush", "SystemControlHighlightListAccentMediumBrush", "SystemControlHighlightListAccentHighBrush"];

    private static readonly Dictionary<ColorScheme, Scheme> Schemes = new()
    {
        [ColorScheme.Blue] = new(Color.Parse("#0F6CBD"), Color.Parse("#479EF5"), Color.Parse("#0C3B5E"), Color.Parse("#EBF3FC"), Color.Parse("#0C3B5E")),
        [ColorScheme.Red] = new(Color.Parse("#C50F1F"), Color.Parse("#F1707B"), Color.Parse("#6E0811"), Color.Parse("#FDF3F4"), Color.Parse("#4A0A10"),
            (Color.Parse("#F9DADD"), Color.Parse("#5A1A20"))),
        [ColorScheme.Green] = new(Color.Parse("#107C10"), Color.Parse("#54B054"), Color.Parse("#0B5A08"), Color.Parse("#F1FAF1"), Color.Parse("#0A3A0A")),
    };

    public static ColorScheme CurrentScheme { get; private set; } = ColorScheme.Blue;

    public static async Task LoadAsync(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var mode = Enum.TryParse<ThemeMode>(await settings.GetAsync(SettingKeys.ThemeMode), out var m) ? m : ThemeMode.System;
        var scheme = Enum.TryParse<ColorScheme>(await settings.GetAsync(SettingKeys.ColorScheme), out var s) ? s : ColorScheme.Blue;
        Apply(mode, scheme);
    }

    public static void Apply(ThemeMode mode, ColorScheme scheme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        CurrentScheme = scheme;
        var colors = Schemes[scheme];
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
        {
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = colors.Accent };
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = colors.AccentDark };
        }

        if (app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Light, out var light) && light is IResourceDictionary l)
        {
            SetBrushes(l, header: colors.Accent, text: colors.Accent, today: colors.TodayLight, selection: colors.Selection?.Light);
        }

        if (app.Resources.ThemeDictionaries.TryGetValue(ThemeVariant.Dark, out var dark) && dark is IResourceDictionary d)
        {
            SetBrushes(d, header: colors.HeaderDark, text: colors.AccentDark, today: colors.TodayDark, selection: colors.Selection?.Dark);
        }
    }

    private static void SetBrushes(IResourceDictionary resources, Color header, Color text, Color today, Color? selection)
    {
        for (var step = 0; step < SelectionKeys.Length; step++)
        {
            if (selection is { } row)
            {
                // A touch darker when hovered or pressed, so the row still reacts (in dark mode: lighter)
                var shift = (row.R + row.G + row.B > 384 ? -12 : 12) * step;
                resources[SelectionKeys[step]] = new SolidColorBrush(Color.FromRgb(Shift(row.R, shift), Shift(row.G, shift), Shift(row.B, shift)));
            }
            else
            {
                resources.Remove(SelectionKeys[step]); // back to Fluent's accent-based ones
            }
        }

        resources["HeaderBackgroundBrush"] = new SolidColorBrush(header);
        resources["UnreadBarBrush"] = new SolidColorBrush(text);
        resources["AccentTextBrush"] = new SolidColorBrush(text);
        resources["TodayBrush"] = new SolidColorBrush(today);
    }

    private static byte Shift(byte value, int by) => (byte)Math.Clamp(value + by, 0, 255);
}
