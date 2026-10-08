using System.Text;

namespace Neruna.Providers.Ics;

/// <summary>
/// Splits an ICS feed into one VCALENDAR per UID (event + its recurrence overrides + the time zones it needs).
/// Works on the raw text so the original data is preserved byte for byte.
/// </summary>
internal static class IcsFeed
{
    public sealed record Item(string Uid, string ICalendarData);

    public static string? CalendarName(string ics) =>
        UnfoldedLines(ics).FirstOrDefault(l => l.StartsWith("X-WR-CALNAME", StringComparison.OrdinalIgnoreCase)) is { } line
            ? line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()
            : null;

    public static IReadOnlyList<Item> Split(string ics)
    {
        var lines = ics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var timezones = new StringBuilder();
        var components = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var order = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("BEGIN:VTIMEZONE", StringComparison.OrdinalIgnoreCase))
            {
                AppendBlock(timezones, lines, ref i, "END:VTIMEZONE");
            }
            else if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase) || line.StartsWith("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase))
            {
                var end = "END:" + line[6..].Trim();
                var block = new StringBuilder();
                AppendBlock(block, lines, ref i, end);

                var uid = UidOf(block.ToString()) ?? $"neruna-generated-{order.Count}";
                if (!components.TryGetValue(uid, out var existing))
                {
                    components[uid] = existing = new StringBuilder();
                    order.Add(uid);
                }

                existing.Append(block);
            }
        }

        return order.Select(uid => new Item(uid, Wrap(timezones, components[uid]))).ToList();
    }

    private static void AppendBlock(StringBuilder target, string[] lines, ref int i, string endMarker)
    {
        for (; i < lines.Length; i++)
        {
            target.Append(lines[i].TrimEnd('\r')).Append("\r\n");
            if (lines[i].StartsWith(endMarker, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    private static string? UidOf(string block) =>
        UnfoldedLines(block).FirstOrDefault(l => l.StartsWith("UID", StringComparison.OrdinalIgnoreCase) && l.Length > 3 && l[3] is ':' or ';') is { } line
            ? line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()
            : null;

    private static string Wrap(StringBuilder timezones, StringBuilder components) =>
        new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Neruna//ICS Subscription//EN\r\n")
            .Append(timezones)
            .Append(components)
            .Append("END:VCALENDAR\r\n")
            .ToString();

    private static IEnumerable<string> UnfoldedLines(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (raw.Length > 0 && raw[0] is ' ' or '\t')
            {
                current.Append(raw, 1, raw.Length - 1);
                continue;
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }

            current.Clear().Append(raw);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
