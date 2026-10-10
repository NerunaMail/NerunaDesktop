using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neruna.Core.Mail;

namespace Neruna.Providers.Imap.Sieve;

/// <summary>
/// The out-of-office reply as a block in the user's active Sieve script. Neruna never replaces a script: filters the
/// user made elsewhere (webmail) stay; only the marked block is written, changed or removed. Its settings travel along
/// as a comment, so Neruna reads back exactly what it wrote (the message also stays when the reply is off).
/// </summary>
internal static class SieveVacation
{
    private const string Begin = "# BEGIN Neruna-Abwesenheit";
    private const string End = "# END Neruna-Abwesenheit";
    private const string RequireTag = "# Neruna-Abwesenheit";

    private sealed record Stored(
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("start")] string? Start,
        [property: JsonPropertyName("end")] string? End,
        [property: JsonPropertyName("subject")] string? Subject);

    /// <summary>What Neruna wrote into the script; off when there is no block.</summary>
    public static AutoReply Read(string? script)
    {
        foreach (var line in Lines(script))
        {
            if (line.StartsWith(Begin, StringComparison.Ordinal) && line.Length > Begin.Length)
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<Stored>(line[Begin.Length..].Trim());
                    if (stored is not null)
                    {
                        return new AutoReply(stored.Enabled, stored.Message, DateOf(stored.Start), DateOf(stored.End), stored.Subject);
                    }
                }
                catch (JsonException)
                {
                    // A block edited by hand: treated as off; writing replaces it.
                }
            }
        }

        return AutoReply.Off;
    }

    /// <summary>The script with Neruna's block for <paramref name="reply"/> (or without one, if there was no script and it is off).</summary>
    /// <param name="addresses">Mail to these addresses is answered (the account and its aliases).</param>
    /// <param name="withDates">The server has the "date" and "relational" extensions (needed for a period).</param>
    public static string Write(string? script, AutoReply reply, IReadOnlyList<string> addresses, bool withDates)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(addresses);
        if (reply.IsEnabled && reply.IsScheduled && !withDates)
        {
            throw new ArgumentException("A period needs the Sieve extensions \"date\" and \"relational\".", nameof(reply));
        }

        var rest = Remove(script ?? string.Empty);
        var block = new StringBuilder();
        var stored = new Stored(reply.IsEnabled, reply.Message, DateText(reply.Start), DateText(reply.End), reply.Subject);
        block.Append(Begin).Append(' ').Append(JsonSerializer.Serialize(stored)).Append("\r\n");
        if (reply.IsEnabled)
        {
            var vacation = new StringBuilder("vacation :days 1");
            if (!string.IsNullOrWhiteSpace(reply.Subject))
            {
                vacation.Append(" :subject ").Append(Quote(reply.Subject.Trim()));
            }

            if (addresses.Count > 0)
            {
                vacation.Append(" :addresses [").Append(string.Join(", ", addresses.Select(Quote))).Append(']');
            }

            vacation.Append(' ').Append(Quote(reply.Message)).Append(';');
            if (reply.IsScheduled)
            {
                // Days in the user's time zone; the end is exclusive (the first day back).
                var zone = Zone(reply.Start!.Value.Offset);
                block.Append(CultureInfo.InvariantCulture,
                        $"if allof (currentdate :zone \"{zone}\" :value \"ge\" \"date\" \"{DateText(reply.Start)}\", currentdate :zone \"{zone}\" :value \"lt\" \"date\" \"{DateText(reply.End)}\") {{\r\n")
                    .Append("  ").Append(vacation).Append("\r\n}\r\n");
            }
            else
            {
                block.Append(vacation).Append("\r\n");
            }
        }

        block.Append(End).Append("\r\n");

        // Commands must follow every "require": the block goes after them, its own require on top.
        var insertAt = EndOfRequires(rest);
        var result = new StringBuilder();
        if (reply.IsEnabled)
        {
            var extensions = reply.IsScheduled ? "\"vacation\", \"date\", \"relational\"" : "\"vacation\"";
            result.Append("require [").Append(extensions).Append("]; ").Append(RequireTag).Append("\r\n");
        }

        result.Append(rest, 0, insertAt);
        if (insertAt > 0 && rest[insertAt - 1] != '\n')
        {
            result.Append("\r\n");
        }

        result.Append(block).Append(rest, insertAt, rest.Length - insertAt);
        return Normalize(result.ToString());
    }

    /// <summary>A vacation rule outside Neruna's block (made in the webmail, for example).</summary>
    public static bool HasOtherVacation(string? script) =>
        Lines(Remove(script ?? string.Empty)).Any(l => !l.TrimStart().StartsWith('#') && l.Contains("vacation", StringComparison.OrdinalIgnoreCase) && !l.TrimStart().StartsWith("require", StringComparison.OrdinalIgnoreCase));

    // The script without Neruna's block and require line.
    private static string Remove(string script)
    {
        var result = new StringBuilder();
        var inBlock = false;
        foreach (var line in Lines(script))
        {
            if (line.StartsWith(Begin, StringComparison.Ordinal))
            {
                inBlock = true;
            }
            else if (inBlock && line.StartsWith(End, StringComparison.Ordinal))
            {
                inBlock = false;
            }
            else if (!inBlock && !line.TrimEnd().EndsWith(RequireTag, StringComparison.Ordinal))
            {
                result.Append(line).Append("\r\n");
            }
        }

        return result.ToString();
    }

    // Where the leading comments and "require" statements end.
    private static int EndOfRequires(string script)
    {
        var i = 0;
        while (i < script.Length)
        {
            var start = i;
            while (i < script.Length && char.IsWhiteSpace(script[i]))
            {
                i++;
            }

            if (i < script.Length && script[i] == '#')
            {
                i = LineEnd(script, i);
            }
            else if (i + 1 < script.Length && script[i] == '/' && script[i + 1] == '*')
            {
                var close = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? script.Length : LineEnd(script, close + 2);
            }
            else if (string.Compare(script, i, "require", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var inString = false;
                while (i < script.Length && (inString || script[i] != ';'))
                {
                    if (script[i] == '"' && script[i - 1] != '\\')
                    {
                        inString = !inString;
                    }

                    i++;
                }

                i = LineEnd(script, Math.Min(i + 1, script.Length));
            }
            else
            {
                return start;
            }
        }

        return script.Length;
    }

    private static int LineEnd(string text, int from)
    {
        var newline = text.IndexOf('\n', from);
        return newline < 0 ? text.Length : newline + 1;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\"";

    private static string Zone(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    private static string? DateText(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset? DateOf(string? text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))
            : null;

    private static IEnumerable<string> Lines(string? script)
    {
        var text = (script ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Split('\n');
        var count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        return text.Length == 0 ? [] : lines.Take(count);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
}
