using System.Text.RegularExpressions;

namespace Neruna.Core.Chat;

/// <summary>The chat's simple formatting: **bold** and __underlined__ (also nested); everything else as typed.</summary>
public static partial class ChatFormatting
{
    /// <summary>The text in pieces with their formatting; markers without a partner stay as typed.</summary>
    public static IReadOnlyList<(string Text, bool Bold, bool Underline)> Parse(string markup)
    {
        ArgumentNullException.ThrowIfNull(markup);
        var parts = new List<(string, bool, bool)>();
        Collect(markup, false, false, parts);
        return parts;
    }

    private static void Collect(string text, bool bold, bool underline, List<(string, bool, bool)> parts)
    {
        var position = 0;
        foreach (Match match in Formatting().Matches(text))
        {
            if (match.Index > position)
            {
                parts.Add((text[position..match.Index], bold, underline));
            }

            if (match.Groups["bold"].Success)
            {
                Collect(match.Groups["bold"].Value, true, underline, parts);
            }
            else
            {
                Collect(match.Groups["underline"].Value, bold, true, parts);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            parts.Add((text[position..], bold, underline));
        }
    }

    [GeneratedRegex(@"\*\*(?<bold>(?:(?!\*\*).)+?)\*\*|__(?<underline>(?:(?!__).)+?)__", RegexOptions.Singleline)]
    private static partial Regex Formatting();
}
