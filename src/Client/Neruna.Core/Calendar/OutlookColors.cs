using System.Globalization;

namespace Neruna.Core.Calendar;

/// <summary>
/// The fixed colours of Microsoft 365 and Exchange: 25 for categories ("preset0" … "preset24", in EWS 0 … 24) and a
/// few for calendars. Neruna offers any colour; on the server the nearest of these is stored.
/// </summary>
public static class OutlookColors
{
    /// <summary>Red, orange, brown, yellow, green, teal, olive, blue, purple, cranberry, steel, dark steel, gray,
    /// dark gray, black, then the dark variants of red … cranberry.</summary>
    public static IReadOnlyList<string> Categories { get; } =
    [
        "#D13438", "#F7630C", "#8E562E", "#FCE100", "#13A10E", "#00B7C3", "#6B8E23", "#0078D4", "#8764B8", "#E3008C",
        "#8A9BA8", "#4A5459", "#A0A0A0", "#69797E", "#1B1B1B", "#A4262C", "#CA5010", "#603D30", "#C19C00", "#0B6A0B",
        "#038387", "#4B5320", "#004E8C", "#5C2E91", "#9B0062",
    ];

    /// <summary>Microsoft 365 calendar colours (calendarColor) with a colour each.</summary>
    public static IReadOnlyList<(string Name, string Color)> Calendars { get; } =
    [
        ("lightBlue", "#3A96DD"), ("lightGreen", "#47B24C"), ("lightOrange", "#F7894A"), ("lightGray", "#A0AEB2"), ("lightYellow", "#FCD116"),
        ("lightTeal", "#00B7C3"), ("lightPink", "#E3008C"), ("lightBrown", "#8E562E"), ("lightRed", "#E74856"),
    ];

    /// <summary>The category colour nearest to <paramref name="color"/> (index 0 … 24).</summary>
    public static int NearestCategory(string color) => Nearest(color, Categories);

    /// <summary>The colour of a category index; null for none (-1) or an unknown one.</summary>
    public static string? CategoryColor(int index) => index >= 0 && index < Categories.Count ? Categories[index] : null;

    public static string NearestCalendar(string color) => Calendars[Nearest(color, [.. Calendars.Select(c => c.Color)])].Name;

    private static int Nearest(string color, IReadOnlyList<string> choices)
    {
        var (r, g, b) = Rgb(color);
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < choices.Count; i++)
        {
            var (cr, cg, cb) = Rgb(choices[i]);
            // Weighted as the eye sees it ("redmean"): close enough to tell blue from teal and red from cranberry.
            var mean = (r + cr) / 2.0;
            var distance = ((2 + mean / 256) * (r - cr) * (r - cr)) + (4 * (g - cg) * (g - cg)) + ((2 + ((255 - mean) / 256)) * (b - cb) * (b - cb));
            if (distance < bestDistance)
            {
                (best, bestDistance) = (i, distance);
            }
        }

        return best;
    }

    private static (int R, int G, int B) Rgb(string color)
    {
        var hex = color.TrimStart('#');
        return hex.Length >= 6 && int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? ((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF)
            : (0, 0, 0);
    }
}
