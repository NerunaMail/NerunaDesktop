using System.Text;

namespace Neruna.Core.Contacts;

/// <summary>Escaping and line folding for writing vCards (RFC 6350 §3.2/3.4).</summary>
internal static class VCardWriter
{
    public static string Escape(string? value) => (value ?? string.Empty)
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    // RFC 6350 §3.2: lines longer than 75 octets are folded; never split a UTF-8 sequence.
    public static void Fold(StringBuilder target, string line)
    {
        var octets = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (octets + size > limit)
            {
                target.Append("\r\n ");
                octets = 1;
                limit = 75;
            }

            target.Append(rune.ToString());
            octets += size;
        }

        target.Append("\r\n");
    }

    /// <summary>Joins content lines into a folded vCard text.</summary>
    public static string Join(IEnumerable<string> lines)
    {
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            Fold(result, line);
        }

        return result.ToString();
    }
}
